using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Options;
using Microsoft.Extensions.Options;

namespace MgtAiAuthen.Api.Services;

/// <summary>
/// Alibaba Cloud Model Studio (DashScope) — Qwen models, called via its OpenAI-*compatible*
/// chat completions endpoint (POST {BaseUrl}/chat/completions). This is the classic
/// role/content/choices[0].message shape, not the newer Responses API <see cref="OpenAiClient"/>
/// uses for ChatGPT — the two providers happen to share a vendor family but not a wire format.
///
/// A Model Studio API key is bound to the region/workspace it was created in; the wrong BaseUrl
/// for that key answers 401 even though the key itself is valid. See QwenOptions.BaseUrl.
///
/// Registered as a singleton — no per-request state.
/// </summary>
public class QwenClient : IAiProvider
{
    /// <summary>Name used by the factory registration in Program.cs.</summary>
    public const string HttpClientName = "qwen";

    private readonly QwenOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<QwenClient> _logger;
    private readonly string _apiKey;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public QwenClient(
        IOptions<QwenOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<QwenClient> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        _apiKey = !string.IsNullOrWhiteSpace(_options.ApiKey)
            ? _options.ApiKey
            : Environment.GetEnvironmentVariable("QWEN_API_KEY") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogInformation(
                "Qwen API key is not configured — Qwen models appear in the picker but " +
                "selecting one returns 503 until Qwen:ApiKey or QWEN_API_KEY is set");
        }
    }

    public string ProviderName => AiProviders.Qwen;

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
                "The Qwen API key is not configured (Qwen:ApiKey or the QWEN_API_KEY " +
                "environment variable). Choose a Claude, Gemini, or ChatGPT model, or ask your " +
                "system administrator to add the key.", ProviderName);
        }

        string resolvedModel = string.IsNullOrWhiteSpace(model) ? _options.Model : model.Trim();
        var stopwatch = Stopwatch.StartNew();

        (HttpStatusCode status, string body) = await PostAsync(resolvedModel, systemPrompt, turns, ct);
        stopwatch.Stop();

        if (status != HttpStatusCode.OK)
        {
            throw Translate(status, body, resolvedModel);
        }

        return ReadReply(body, resolvedModel, (int)stopwatch.ElapsedMilliseconds);
    }

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(
        string model, string systemPrompt, IReadOnlyList<ChatTurn> turns, CancellationToken ct)
    {
        List<object> messages = [];

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new { role = "system", content = systemPrompt });
        }

        messages.AddRange(turns.Select(BuildTurn));

        object payload = new
        {
            model,
            messages,
            max_tokens = _options.MaxTokens,
            stream = false,
        };

        HttpClient http = _httpClientFactory.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/chat/completions")
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
            _logger.LogWarning(ex, "Qwen call timed out after {Seconds}s", _options.TimeoutSeconds);

            throw new AiOverloadedException(
                $"Qwen did not answer within {_options.TimeoutSeconds} seconds. Please send your " +
                "message again (your question has already been logged).", ex, ProviderName);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Could not reach the Qwen API");

            throw new AiOverloadedException(
                "Could not reach the Qwen service. Please try again, or choose a Claude, Gemini, " +
                "or ChatGPT model in the meantime.", ex, ProviderName);
        }
    }

    /// <summary>
    /// Turns one conversation turn into a Chat Completions message. Only FileKinds.Image maps to
    /// a real multimodal part (image_url, the same shape OpenAI's classic Chat Completions API
    /// uses) — Qwen's OpenAI-compatible endpoint has no equivalent of the Responses API's
    /// "input_file" for native PDF upload, so PDFs fall back to a plain-text note; text and
    /// spreadsheet attachments are inlined as labelled text, same as the other providers.
    /// </summary>
    private static object BuildTurn(ChatTurn turn)
    {
        bool isAssistant = turn.Role == MessageRoles.Assistant;
        string role = isAssistant ? "assistant" : "user";

        List<object> parts = [];

        foreach (TurnAttachment attachment in turn.Attachments ?? [])
        {
            switch (attachment.FileKind)
            {
                case FileKinds.Image:
                    string base64 = Convert.ToBase64String(attachment.Content);
                    parts.Add(new
                    {
                        type = "image_url",
                        image_url = new { url = $"data:{attachment.ContentType};base64,{base64}" },
                    });
                    break;

                case FileKinds.Pdf:
                    parts.Add(new
                    {
                        type = "text",
                        text = string.IsNullOrWhiteSpace(attachment.Text)
                            ? $"--- Attached file: {attachment.FileName} ---\n" +
                              "[This model cannot read PDF files directly — choose Claude or " +
                              "Gemini for PDF attachments.]\n" +
                              $"--- end of {attachment.FileName} ---"
                            : $"--- Attached file: {attachment.FileName} ---\n" +
                              $"{attachment.Text}\n--- end of {attachment.FileName} ---",
                    });
                    break;

                case FileKinds.Text:
                case FileKinds.Spreadsheet:
                    parts.Add(new
                    {
                        type = "text",
                        text = $"--- Attached file: {attachment.FileName} ---\n" +
                               $"{attachment.Text ?? string.Empty}\n" +
                               $"--- end of {attachment.FileName} ---",
                    });
                    break;
            }
        }

        parts.Add(new
        {
            type = "text",
            text = string.IsNullOrWhiteSpace(turn.Content)
                ? "Please analyse the attached file(s) and summarise the key points."
                : turn.Content,
        });

        // A plain string body (no parts array) is valid and slightly cheaper to parse, but mixing
        // attachments and text needs the multi-part array form — always using the array form
        // keeps this method's output shape uniform regardless of whether there are attachments.
        return new { role, content = parts };
    }

    private AiReply ReadReply(string body, string requestedModel, int latencyMs)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        string modelName = ReadString(root, "model") is { } echoed && !string.IsNullOrWhiteSpace(echoed)
            ? echoed
            : requestedModel;

        int promptTokens = 0, completionTokens = 0, cachedTokens = 0;

        if (TryObject(root, "usage", out JsonElement usage))
        {
            promptTokens = ReadInt(usage, "prompt_tokens");
            completionTokens = ReadInt(usage, "completion_tokens");

            if (TryObject(usage, "prompt_tokens_details", out JsonElement promptDetails))
            {
                cachedTokens = ReadInt(promptDetails, "cached_tokens");
            }
        }

        // Mirrors OpenAI's own accounting (prompt_tokens already includes any cached tokens) —
        // Alibaba's compatible-mode usage block follows the same convention when present.
        int billedInput = Math.Max(0, promptTokens - cachedTokens);

        string answer = string.Empty;
        bool refused = false;
        string? refusalDetail = null;
        string? finishReason = null;

        if (root.TryGetProperty("choices", out JsonElement choices)
            && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            JsonElement first = choices[0];
            finishReason = ReadString(first, "finish_reason");

            if (TryObject(first, "message", out JsonElement message))
            {
                answer = ReadString(message, "content") ?? string.Empty;
            }
        }

        if (string.Equals(finishReason, "content_filter", StringComparison.OrdinalIgnoreCase))
        {
            refused = true;
            refusalDetail = "content_filter";
            answer = "The AI declined to answer this question for safety reasons. " +
                     "Please try rephrasing it.";
        }
        else if (string.IsNullOrWhiteSpace(answer))
        {
            answer = $"The AI returned no message (finish reason {finishReason ?? "unknown"}). " +
                     "Please try again.";
        }
        else if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            answer += "\n\n_[The answer was cut off at the output limit " +
                      $"({_options.MaxTokens} tokens). Ask a narrower question to see the rest.]_";
        }

        return new AiReply(
            Text: answer,
            ModelName: modelName,
            InputTokens: billedInput,
            OutputTokens: completionTokens,
            // No separate write figure is reported in the compatible-mode usage block.
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

    private static bool TryObject(JsonElement parent, string name, out JsonElement child)
    {
        child = default;

        return parent.ValueKind == JsonValueKind.Object
               && parent.TryGetProperty(name, out JsonElement found)
               && found.ValueKind == JsonValueKind.Object
               && (child = found).ValueKind == JsonValueKind.Object;
    }

    private static string? ReadString(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Turns a non-200 into the same four categories the other providers use.</summary>
    private Exception Translate(HttpStatusCode status, string body, string model)
    {
        (string message, string? code) = ReadError(body);

        switch (status)
        {
            case HttpStatusCode.Unauthorized:
            case HttpStatusCode.Forbidden:
                _logger.LogError(
                    "Qwen rejected the API key ({Status}): {Detail} — an administrator must check " +
                    "Qwen:ApiKey and Qwen:BaseUrl (a Model Studio key only works against the " +
                    "region/workspace endpoint it was created for)", (int)status, message);

                return new AiBillingException(
                    "The AI service is unavailable because Qwen rejected the API key — it may be " +
                    "invalid, revoked, or created for a different region/workspace than the " +
                    "configured Qwen:BaseUrl. Please ask an administrator to check it — this is " +
                    "not caused by your message or files.",
                    new InvalidOperationException(message), ProviderName);

            case HttpStatusCode.TooManyRequests:
                if (message.Contains("quota", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("billing", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("balance", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("Qwen quota or billing problem: {Detail}", message);

                    return new AiBillingException(
                        "The AI service is unavailable because the Qwen account is out of credit " +
                        "or over quota. Please ask an administrator to check the balance and " +
                        "billing — this is not caused by your message or files.",
                        new InvalidOperationException(message), ProviderName);
                }

                _logger.LogWarning("Qwen rate limit hit: {Detail}", message);

                return new AiOverloadedException(
                    "Qwen is receiving too many requests right now. Please send your message " +
                    "again in a moment (your question has already been logged).",
                    new InvalidOperationException(message), ProviderName);

            case HttpStatusCode.NotFound:
                _logger.LogError("Qwen has no model named {Model} for this key: {Detail}", model, message);

                return new AiInvalidInputException(
                    $"Qwen has no model called \"{model}\", or this account cannot use it. Please " +
                    "ask an administrator to correct or deactivate it on the model pricing page, " +
                    "and choose another model in the meantime.",
                    new InvalidOperationException(message), ProviderName);

            case HttpStatusCode.InternalServerError:
            case HttpStatusCode.BadGateway:
            case HttpStatusCode.ServiceUnavailable:
            case HttpStatusCode.GatewayTimeout:
                _logger.LogWarning("Qwen returned {Status}: {Detail}", (int)status, message);

                return new AiOverloadedException(
                    "The Qwen service is temporarily unavailable. Please send your message again " +
                    "in a moment, or choose a Claude, Gemini, or ChatGPT model.",
                    new InvalidOperationException(message), ProviderName);

            default:
                _logger.LogWarning("Qwen rejected the request ({Status}): {Detail} (code {Code})",
                    (int)status, message, code);
                return new AiInvalidInputException(
                    "The AI rejected this request because the data sent was invalid. Please check " +
                    "your attachments and message, then try again.",
                    new InvalidOperationException(message), ProviderName);
        }
    }

    /// <summary>Pulls error.message and error.code out of the OpenAI-style error envelope Qwen's compatible-mode endpoint shares.</summary>
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
            // A non-JSON error page from a proxy, for instance — the raw text is still the best clue.
        }

        return (body.Length > 500 ? body[..500] : body, null);
    }
}
