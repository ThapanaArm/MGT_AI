using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Options;
using Microsoft.Extensions.Options;

namespace MgtAiAuthen.Api.Services;

/// <summary>
/// OpenAI (ChatGPT) via the Responses API — POST /v1/responses.
///
/// Responses rather than Chat Completions because it is the surface OpenAI recommends for new
/// work, and it carries reasoning models and file attachments in one request shape. Called over
/// plain HTTP for the same reason as Gemini: this app needs four numbers and one string out of
/// the response, which does not justify an extra dependency.
///
/// Registered as a singleton, so nothing here may hold per-request state except through the
/// lock below.
/// </summary>
public class OpenAiClient : IAiProvider
{
    /// <summary>Name used by the factory registration in Program.cs.</summary>
    public const string HttpClientName = "openai";

    private readonly OpenAiOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenAiClient> _logger;
    private readonly string _apiKey;

    /// <summary>
    /// Models that rejected the reasoning parameter, learned at runtime.
    ///
    /// The gpt-5 family accepts reasoning.effort; older models such as gpt-4.1-mini answer
    /// 400 unsupported_parameter. Third time this pattern has earned its keep in this codebase
    /// (Anthropic effort, Gemini thinkingLevel, now this) — a hard-coded capability list would
    /// need editing every time OpenAI ships a model.
    /// </summary>
    private readonly HashSet<string> _reasoningUnsupported = new(StringComparer.OrdinalIgnoreCase);

    // System.Threading.Lock is .NET 9+; this project targets net8.0.
    private readonly object _reasoningLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public OpenAiClient(
        IOptions<OpenAiOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<OpenAiClient> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        _apiKey = !string.IsNullOrWhiteSpace(_options.ApiKey)
            ? _options.ApiKey
            : Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogInformation(
                "OpenAI API key is not configured — ChatGPT models appear in the picker but " +
                "selecting one returns 503 until OpenAI:ApiKey or OPENAI_API_KEY is set");
        }
    }

    public string ProviderName => AiProviders.OpenAi;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public string DefaultModel => _options.Model;

    public async Task<AiReply> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<ChatTurn> turns,
        string? model = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            throw new AiUnavailableException(
                "The OpenAI API key is not configured (OpenAI:ApiKey or the OPENAI_API_KEY " +
                "environment variable). Choose a Claude or Gemini model, or ask your system " +
                "administrator to add the key.", ProviderName);
        }

        string resolvedModel = string.IsNullOrWhiteSpace(model) ? _options.Model : model.Trim();
        bool sendReasoning = SupportsReasoning(resolvedModel);

        var stopwatch = Stopwatch.StartNew();

        (HttpStatusCode status, string body) = await PostAsync(resolvedModel, systemPrompt, turns, sendReasoning, ct);

        // Learn once that this model rejects reasoning, then retry immediately without it.
        if (status == HttpStatusCode.BadRequest && sendReasoning && RejectsReasoning(body))
        {
            RememberReasoningUnsupported(resolvedModel);

            _logger.LogInformation(
                "Model {Model} does not accept the reasoning parameter — retrying without it",
                resolvedModel);

            (status, body) = await PostAsync(resolvedModel, systemPrompt, turns, withReasoning: false, ct);
        }

        stopwatch.Stop();

        if (status != HttpStatusCode.OK)
        {
            throw Translate(status, body, resolvedModel);
        }

        return ReadReply(body, resolvedModel, (int)stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// One HTTP round trip. Returns the status and raw body instead of throwing so the caller can
    /// inspect a 400 and decide whether to retry without the reasoning parameter.
    /// </summary>
    private async Task<(HttpStatusCode Status, string Body)> PostAsync(
        string model,
        string systemPrompt,
        IReadOnlyList<ChatTurn> turns,
        bool withReasoning,
        CancellationToken ct)
    {
        object payload = new
        {
            model,

            // The system prompt goes in "instructions", not as a message — it is not part of the
            // conversation and must not be echoed back as history.
            instructions = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt,

            input = turns.Select(BuildTurn).ToArray(),

            max_output_tokens = _options.MaxTokens,

            reasoning = withReasoning && NormaliseEffort(_options.ReasoningEffort) is { } effort
                ? new { effort }
                : null,

            // Nothing is stored on OpenAI's side: the transcript already lives in our own
            // database, and leaving copies on a third party widens the PDPA surface for no gain.
            store = false,
        };

        HttpClient http = _httpClientFactory.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/responses")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
        };

        request.Headers.Add("Authorization", $"Bearer {_apiKey}");

        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, ct);
            string body = await response.Content.ReadAsStringAsync(ct);
            return (response.StatusCode, body);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as a cancellation. Only the caller's token
            // means the user really went away, so the two must be told apart.
            _logger.LogWarning(ex, "OpenAI call timed out after {Seconds}s", _options.TimeoutSeconds);

            throw new AiOverloadedException(
                $"ChatGPT did not answer within {_options.TimeoutSeconds} seconds. Please send your " +
                "message again (your question has already been logged).", ex, ProviderName);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Could not reach the OpenAI API");

            throw new AiOverloadedException(
                "Could not reach the OpenAI service. Please try again, or choose a Claude or " +
                "Gemini model in the meantime.", ex, ProviderName);
        }
    }

    /// <summary>
    /// Turns one conversation turn into a Responses API input item. Files go *before* the text so
    /// the model reads the documents before the question, matching the other two providers.
    /// </summary>
    private static object BuildTurn(ChatTurn turn)
    {
        bool isAssistant = turn.Role == MessageRoles.Assistant;
        string role = isAssistant ? "assistant" : "user";

        List<object> content = [];

        foreach (TurnAttachment attachment in turn.Attachments ?? [])
        {
            string base64 = Convert.ToBase64String(attachment.Content);

            switch (attachment.FileKind)
            {
                case FileKinds.Image:
                    content.Add(new
                    {
                        type = "input_image",
                        image_url = $"data:{attachment.ContentType};base64,{base64}",
                    });
                    break;

                case FileKinds.Pdf:
                    content.Add(new
                    {
                        type = "input_file",
                        filename = attachment.FileName,
                        file_data = $"data:{attachment.ContentType};base64,{base64}",
                    });
                    break;

                case FileKinds.Text:
                case FileKinds.Spreadsheet:
                    // input_file expects a document format; a plain text file is clearer as a
                    // labelled text part, which also keeps the file name visible to the model.
                    content.Add(new
                    {
                        type = "input_text",
                        text = $"--- Attached file: {attachment.FileName} ---\n" +
                               $"{attachment.Text ?? string.Empty}\n" +
                               $"--- end of {attachment.FileName} ---",
                    });
                    break;
            }
        }

        // An assistant turn must use output_text; only user turns may carry input_* parts.
        content.Add(new
        {
            type = isAssistant ? "output_text" : "input_text",
            text = string.IsNullOrWhiteSpace(turn.Content)
                ? "Please analyse the attached file(s) and summarise the key points."
                : turn.Content,
        });

        return new { role, content };
    }

    /// <summary>Reads the reply text and the usage numbers we bill from.</summary>
    private AiReply ReadReply(string body, string requestedModel, int latencyMs)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        string modelName = ReadString(root, "model") is { } echoed && !string.IsNullOrWhiteSpace(echoed)
            ? echoed
            : requestedModel;

        // --- token usage -----------------------------------------------------------------
        int inputTokens = 0, outputTokens = 0, cachedTokens = 0, reasoningTokens = 0;

        if (TryObject(root, "usage", out JsonElement usage))
        {
            inputTokens = ReadInt(usage, "input_tokens");
            outputTokens = ReadInt(usage, "output_tokens");

            if (TryObject(usage, "input_tokens_details", out JsonElement inDetails))
            {
                cachedTokens = ReadInt(inDetails, "cached_tokens");
            }

            if (TryObject(usage, "output_tokens_details", out JsonElement outDetails))
            {
                reasoningTokens = ReadInt(outDetails, "reasoning_tokens");
            }
        }

        // input_tokens INCLUDES the cached ones — verified against the live API: a 10,027-token
        // prompt resent unchanged reported input_tokens 10,027 with cached_tokens 10,024.
        // Without this subtraction those tokens would be charged at the full input rate instead
        // of the ~10x cheaper cached rate.
        int billedInput = Math.Max(0, inputTokens - cachedTokens);

        // NOTE the asymmetry with Gemini: OpenAI's output_tokens ALREADY includes
        // reasoning_tokens (measured: out 18 of which reasoning 11, and total == in + out), so
        // reasoning must NOT be added again here. Gemini reports thoughts separately and there
        // they DO have to be added. Copying either provider's line into the other double-counts
        // or under-counts the output bill.
        int billedOutput = outputTokens;

        // --- the answer itself -----------------------------------------------------------
        var text = new StringBuilder();
        bool refused = false;
        string? refusalDetail = null;

        if (root.TryGetProperty("output", out JsonElement output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("content", out JsonElement parts)
                    || parts.ValueKind != JsonValueKind.Array)
                {
                    // Reasoning items carry no content array — their tokens are already counted.
                    continue;
                }

                foreach (JsonElement part in parts.EnumerateArray())
                {
                    string? type = ReadString(part, "type");

                    if (type == "output_text" && ReadString(part, "text") is { } chunk)
                    {
                        text.Append(chunk);
                    }
                    else if (type == "refusal")
                    {
                        refused = true;
                        refusalDetail = ReadString(part, "refusal") ?? "refusal";
                    }
                }
            }
        }

        string? status = ReadString(root, "status");

        // "incomplete_details": null on a completed response — TryObject filters that out.
        string? incompleteReason = TryObject(root, "incomplete_details", out JsonElement incomplete)
            ? ReadString(incomplete, "reason")
            : null;

        bool hitOutputLimit = string.Equals(incompleteReason, "max_output_tokens", StringComparison.OrdinalIgnoreCase);
        string answer = text.ToString();

        if (refused)
        {
            _logger.LogWarning("OpenAI refused the request: {Detail}", refusalDetail);
            answer = "The AI declined to answer this question for safety reasons. " +
                     "Please try rephrasing it.";
        }
        else if (string.IsNullOrWhiteSpace(answer))
        {
            // A reasoning model can spend the whole budget thinking and return status
            // "incomplete" with no text at all — verified live. "Try again" would be useless
            // advice for that, so the two empty cases get different messages.
            answer = hitOutputLimit
                ? "The AI ran out of output budget before it produced an answer " +
                  $"(the limit is {_options.MaxTokens} tokens, and reasoning counts towards it). " +
                  "Try asking for a shorter answer, or ask an administrator to raise OpenAI:MaxTokens."
                : $"The AI returned no message (status {status ?? "unknown"}). Please try again.";
        }
        else if (hitOutputLimit)
        {
            // Truncation must be visible: a cut-off answer that looks complete is worse than one
            // that says it was cut off.
            answer += "\n\n_[The answer was cut off at the output limit " +
                      $"({_options.MaxTokens} tokens). Ask a narrower question to see the rest.]_";
        }

        return new AiReply(
            Text: answer,
            ModelName: modelName,
            InputTokens: billedInput,
            OutputTokens: billedOutput,
            // OpenAI caches automatically and does not charge to populate the cache, so there is
            // never a write figure to record — only reads.
            CacheWriteTokens: 0,
            CacheReadTokens: cachedTokens,
            LatencyMs: latencyMs,
            Refused: refused,
            RefusalDetail: refusalDetail);
    }

    private static int ReadInt(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out JsonElement value)
           && value.TryGetInt32(out int result)
            ? result
            : 0;

    /// <summary>
    /// Descends into a child object only when it really is one.
    ///
    /// The API sends explicit nulls for fields that do not apply — a completed response carries
    /// <c>"incomplete_details": null</c> — and calling TryGetProperty on a JSON null throws
    /// InvalidOperationException, which turned a perfectly good answer into a 502.
    /// </summary>
    private static bool TryObject(JsonElement parent, string name, out JsonElement child)
    {
        child = default;

        return parent.ValueKind == JsonValueKind.Object
               && parent.TryGetProperty(name, out JsonElement found)
               && found.ValueKind == JsonValueKind.Object
               && (child = found).ValueKind == JsonValueKind.Object;
    }

    /// <summary>Reads a string property, tolerating a missing or null value.</summary>
    private static string? ReadString(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Turns a non-200 into the same four categories the other providers use, so the controller
    /// and the frontend behave identically whoever answered.
    /// </summary>
    private Exception Translate(HttpStatusCode status, string body, string model)
    {
        (string message, string? code) = ReadError(body);

        switch (status)
        {
            case HttpStatusCode.Unauthorized:
            case HttpStatusCode.Forbidden:
                _logger.LogError(
                    "OpenAI rejected the API key ({Status}): {Detail} — an administrator must check " +
                    "OpenAI:ApiKey and the project's access", (int)status, message);

                return new AiBillingException(
                    "The AI service is unavailable because OpenAI rejected the API key (invalid, " +
                    "revoked, or without access to this model). Please ask an administrator to " +
                    "check it — this is not caused by your message or files.",
                    new InvalidOperationException(message), ProviderName);

            case HttpStatusCode.TooManyRequests:
                // OpenAI uses 429 for both a per-minute rate limit (retrying works) and an
                // exhausted quota or unpaid balance (retrying cannot work). Different people have
                // to act on them, so they must not share one message.
                if (string.Equals(code, "insufficient_quota", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("quota", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("billing", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("OpenAI quota or billing problem: {Detail}", message);

                    return new AiBillingException(
                        "The AI service is unavailable because the OpenAI account is out of credit " +
                        "or over quota. Please ask an administrator to check the balance and " +
                        "billing — this is not caused by your message or files.",
                        new InvalidOperationException(message), ProviderName);
                }

                _logger.LogWarning("OpenAI rate limit hit: {Detail}", message);

                return new AiOverloadedException(
                    "ChatGPT is receiving too many requests right now. Please send your message " +
                    "again in a moment (your question has already been logged).",
                    new InvalidOperationException(message), ProviderName);

            case HttpStatusCode.NotFound:
                // The admin priced a model id this account cannot reach. Naming it saves whoever
                // reads this from guessing.
                _logger.LogError("OpenAI has no model named {Model} for this key: {Detail}", model, message);

                return new AiInvalidInputException(
                    $"OpenAI has no model called \"{model}\", or this account cannot use it. Please " +
                    "ask an administrator to correct or deactivate it on the model pricing page, " +
                    "and choose another model in the meantime.",
                    new InvalidOperationException(message), ProviderName);

            case HttpStatusCode.InternalServerError:
            case HttpStatusCode.BadGateway:
            case HttpStatusCode.ServiceUnavailable:
            case HttpStatusCode.GatewayTimeout:
                _logger.LogWarning("OpenAI returned {Status}: {Detail}", (int)status, message);

                return new AiOverloadedException(
                    "The ChatGPT service is temporarily unavailable. Please send your message again " +
                    "in a moment, or choose a Claude or Gemini model.",
                    new InvalidOperationException(message), ProviderName);

            default:
                _logger.LogWarning("OpenAI rejected the request ({Status}): {Detail}", (int)status, message);
                return new AiInvalidInputException(
                    DescribeBadRequest(message), new InvalidOperationException(message), ProviderName);
        }
    }

    /// <summary>Turns a 400 from OpenAI into a message that tells the user what to do next.</summary>
    private static string DescribeBadRequest(string detail)
    {
        if (detail.Contains("context_length", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("maximum context", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("too long", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("too large", StringComparison.OrdinalIgnoreCase))
        {
            return "The content sent (including attachments and conversation history) exceeds the " +
                   "model limit. Please start a new conversation or reduce the size or number of " +
                   "attachments.";
        }

        if (detail.Contains("image", StringComparison.OrdinalIgnoreCase))
        {
            return "The AI could not read the attached image — the file may be corrupt, too small, " +
                   "or in an unsupported format. Try re-saving it as PNG/JPG and attaching it again.";
        }

        if (detail.Contains("file", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("mime", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("unsupported", StringComparison.OrdinalIgnoreCase))
        {
            return "ChatGPT cannot read one of the attached files. Try re-saving it as PDF, PNG or " +
                   "JPG, or send the content as a text file.";
        }

        return "The AI rejected this request because the data sent was invalid. " +
               "Please check your attachments and message, then try again.";
    }

    /// <summary>Pulls error.message and error.code out of OpenAI's error envelope.</summary>
    private static (string Message, string? Code) ReadError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return ("(no response body)", null);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);

            if (TryObject(doc.RootElement, "error", out JsonElement error))
            {
                return (ReadString(error, "message") ?? body, ReadString(error, "code"));
            }
        }
        catch (JsonException)
        {
            // An HTML error page from a proxy, for instance — the raw text is still the best clue.
        }

        return (body.Length > 500 ? body[..500] : body, null);
    }

    /// <summary>
    /// The 400 that means "drop the reasoning parameter and try again".
    ///
    /// Matched on OpenAI's own error code and parameter name rather than prose, which is both
    /// exact and stable: the live response is
    /// code "unsupported_parameter", param "reasoning.effort".
    /// </summary>
    private static bool RejectsReasoning(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);

            if (TryObject(doc.RootElement, "error", out JsonElement error))
            {
                string? param = ReadString(error, "param");
                string? code = ReadString(error, "code");
                string message = ReadString(error, "message") ?? string.Empty;

                if (param?.StartsWith("reasoning", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return true;
                }

                return string.Equals(code, "unsupported_parameter", StringComparison.OrdinalIgnoreCase)
                       && message.Contains("reasoning", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (JsonException)
        {
            // fall through to the text check below
        }

        return body.Contains("reasoning", StringComparison.OrdinalIgnoreCase)
               && body.Contains("unsupported", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Blank or unrecognised leaves the parameter out so the model uses its own default.</summary>
    private static string? NormaliseEffort(string? effort) => effort?.Trim().ToLowerInvariant() switch
    {
        "minimal" => "minimal",
        "low" => "low",
        "medium" => "medium",
        "high" => "high",
        _ => null,
    };

    private bool SupportsReasoning(string model)
    {
        if (NormaliseEffort(_options.ReasoningEffort) is null)
        {
            return false;
        }

        lock (_reasoningLock)
        {
            return !_reasoningUnsupported.Contains(model);
        }
    }

    private void RememberReasoningUnsupported(string model)
    {
        lock (_reasoningLock)
        {
            _reasoningUnsupported.Add(model);
        }
    }
}
