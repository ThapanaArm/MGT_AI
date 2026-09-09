using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Options;
using Microsoft.Extensions.Options;

namespace MgtAiAuthen.Api.Services;

/// <summary>
/// Google Gemini via the Generative Language REST API.
///
/// Called over plain HTTP rather than through an SDK because Google publishes no official .NET
/// SDK for the Gemini Developer API (Python/Node/Go/Java only) — a community wrapper would be one
/// more dependency to trust for the four fields this app actually needs.
///
/// Registered as a singleton, so nothing here may hold per-request state except through the
/// lock below.
/// </summary>
public class GeminiClient : IAiProvider
{
    /// <summary>Name used by the factory registration in Program.cs.</summary>
    public const string HttpClientName = "gemini";

    private readonly GeminiOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GeminiClient> _logger;
    private readonly string _apiKey;

    /// <summary>
    /// Models that rejected generationConfig.thinkingLevel, learned at runtime.
    ///
    /// The Gemini 3 family accepts thinkingLevel; the 2.5 family answers 400 for it. Rather than
    /// hard-coding a capability list that goes stale as models come and go, the first rejection is
    /// remembered and the call retried without it — the same approach used for Anthropic's
    /// effort parameter, which earned its keep when Haiku 4.5 started rejecting effort.
    /// </summary>
    private readonly HashSet<string> _thinkingUnsupported = new(StringComparer.OrdinalIgnoreCase);

    // System.Threading.Lock is .NET 9+; this project targets net8.0.
    private readonly object _thinkingLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public GeminiClient(
        IOptions<GeminiOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<GeminiClient> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        _apiKey = !string.IsNullOrWhiteSpace(_options.ApiKey)
            ? _options.ApiKey
            : Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogInformation(
                "Gemini API key is not configured — Gemini models appear in the picker but " +
                "selecting one returns 503 until Gemini:ApiKey or GEMINI_API_KEY is set");
        }
    }

    public string ProviderName => AiProviders.Google;

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
                "The Google Gemini API key is not configured (Gemini:ApiKey or the GEMINI_API_KEY " +
                "environment variable). Choose a Claude model, or ask your system administrator " +
                "to add the key.", ProviderName);
        }

        string resolvedModel = string.IsNullOrWhiteSpace(model) ? _options.Model : model.Trim();
        bool sendThinking = SupportsThinking(resolvedModel);

        var stopwatch = Stopwatch.StartNew();

        (HttpStatusCode status, string body) = await PostAsync(resolvedModel, systemPrompt, turns, sendThinking, ct);

        // Learn once that this model rejects thinkingLevel, then retry immediately without it.
        if (status == HttpStatusCode.BadRequest && sendThinking && RejectsThinking(body))
        {
            RememberThinkingUnsupported(resolvedModel);

            _logger.LogInformation(
                "Model {Model} does not accept generationConfig.thinkingLevel — retrying without it",
                resolvedModel);

            (status, body) = await PostAsync(resolvedModel, systemPrompt, turns, withThinking: false, ct);
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
    /// inspect a 400 and decide whether to retry without thinkingLevel.
    /// </summary>
    private async Task<(HttpStatusCode Status, string Body)> PostAsync(
        string model,
        string systemPrompt,
        IReadOnlyList<ChatTurn> turns,
        bool withThinking,
        CancellationToken ct)
    {
        object payload = new
        {
            systemInstruction = string.IsNullOrWhiteSpace(systemPrompt)
                ? null
                : new { parts = new[] { new { text = systemPrompt } } },

            contents = turns.Select(BuildContent).ToArray(),

            generationConfig = new
            {
                maxOutputTokens = _options.MaxTokens,

                // Nested under thinkingConfig — a bare generationConfig.thinkingLevel is rejected
                // with "Unknown name". Measured on gemini-3.8-flash: without this the model spends
                // ~70 thinking tokens even on a one-word answer, and thinking bills at the output
                // rate, so leaving it out made the answer cost ~70x what it should.
                thinkingConfig = withThinking
                    ? new { thinkingLevel = NormaliseThinkingLevel(_options.ThinkingLevel) }
                    : null,
            },
        };

        HttpClient http = _httpClientFactory.CreateClient(HttpClientName);

        // Model ids can contain characters that must not be taken as path segments.
        string url = $"{_options.BaseUrl.TrimEnd('/')}/models/{Uri.EscapeDataString(model)}:generateContent";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
        };

        // Sent as a header, never as ?key= — a query string ends up in proxy and server logs.
        request.Headers.Add("x-goog-api-key", _apiKey);

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
            _logger.LogWarning(ex, "Gemini call timed out after {Seconds}s", _options.TimeoutSeconds);

            throw new AiOverloadedException(
                $"Gemini did not answer within {_options.TimeoutSeconds} seconds. Please send your " +
                "message again (your question has already been logged).", ex, ProviderName);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Could not reach the Gemini API");

            throw new AiOverloadedException(
                "Could not reach the Google Gemini service. Please try again, or choose a Claude " +
                "model in the meantime.", ex, ProviderName);
        }
    }

    /// <summary>
    /// Turns one conversation turn into a Gemini "content" object. Files go *before* the text so
    /// the model reads the documents before the question, matching what the Anthropic path does.
    /// </summary>
    private static object BuildContent(ChatTurn turn)
    {
        // Gemini calls the assistant role "model".
        string role = turn.Role == MessageRoles.Assistant ? "model" : "user";

        List<object> parts = [];

        foreach (TurnAttachment attachment in turn.Attachments ?? [])
        {
            switch (attachment.FileKind)
            {
                case FileKinds.Image:
                case FileKinds.Pdf:
                    parts.Add(new
                    {
                        inlineData = new
                        {
                            mimeType = attachment.ContentType,
                            data = Convert.ToBase64String(attachment.Content),
                        },
                    });
                    break;

                case FileKinds.Text:
                case FileKinds.Spreadsheet:
                    // Gemini has no titled plain-text document part, so the file name is carried
                    // in a fenced label instead — without it the model cannot tell which file a
                    // figure came from when several are attached.
                    parts.Add(new
                    {
                        text = $"--- Attached file: {attachment.FileName} ---\n" +
                               $"{attachment.Text ?? string.Empty}\n" +
                               $"--- end of {attachment.FileName} ---",
                    });
                    break;
            }
        }

        // A content object with no text is rejected, so supply a default instruction when the
        // user dropped files without typing anything.
        parts.Add(new
        {
            text = string.IsNullOrWhiteSpace(turn.Content)
                ? "Please analyse the attached file(s) and summarise the key points."
                : turn.Content,
        });

        return new { role, parts };
    }

    /// <summary>Reads the reply text and the usage numbers we bill from.</summary>
    private AiReply ReadReply(string body, string requestedModel, int latencyMs)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        string modelName = root.TryGetProperty("modelVersion", out JsonElement version)
                           && version.ValueKind == JsonValueKind.String
                           && !string.IsNullOrWhiteSpace(version.GetString())
            ? version.GetString()!
            : requestedModel;

        // --- token usage -----------------------------------------------------------------
        int promptTokens = 0, candidateTokens = 0, cachedTokens = 0, thoughtTokens = 0;

        if (TryObject(root, "usageMetadata", out JsonElement usage))
        {
            promptTokens = ReadInt(usage, "promptTokenCount");
            candidateTokens = ReadInt(usage, "candidatesTokenCount");
            cachedTokens = ReadInt(usage, "cachedContentTokenCount");
            thoughtTokens = ReadInt(usage, "thoughtsTokenCount");
        }

        // Gemini's promptTokenCount INCLUDES the cached tokens, while Anthropic reports the two
        // apart. Subtracting keeps one meaning for InputTokens across providers — which both the
        // cost calculation (cached reads cost ~1/10) and the TotalTokens column depend on.
        int inputTokens = Math.Max(0, promptTokens - cachedTokens);

        // Thinking tokens are billed at the output rate, so folding them into OutputTokens is
        // what makes the recorded cost match Google's bill. Leaving them out silently
        // under-charges every thinking model.
        int outputTokens = candidateTokens + thoughtTokens;

        // --- the answer itself -----------------------------------------------------------
        // A prompt refused before generation carries no candidate at all, only promptFeedback.
        string? blockReason = null;
        if (TryObject(root, "promptFeedback", out JsonElement feedback)
            && feedback.TryGetProperty("blockReason", out JsonElement reason)
            && reason.ValueKind == JsonValueKind.String)
        {
            blockReason = reason.GetString();
        }

        string? finishReason = null;
        var text = new StringBuilder();

        if (root.TryGetProperty("candidates", out JsonElement candidates)
            && candidates.ValueKind == JsonValueKind.Array
            && candidates.GetArrayLength() > 0)
        {
            JsonElement candidate = candidates[0];

            if (candidate.ValueKind == JsonValueKind.Object
                && candidate.TryGetProperty("finishReason", out JsonElement finish)
                && finish.ValueKind == JsonValueKind.String)
            {
                finishReason = finish.GetString();
            }

            if (TryObject(candidate, "content", out JsonElement content)
                && content.TryGetProperty("parts", out JsonElement parts)
                && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement part in parts.EnumerateArray())
                {
                    // Thinking parts come back flagged; only the answer belongs in the log.
                    if (part.ValueKind == JsonValueKind.Object
                        && part.TryGetProperty("thought", out JsonElement thought)
                        && thought.ValueKind == JsonValueKind.True)
                    {
                        continue;
                    }

                    if (part.ValueKind == JsonValueKind.Object
                        && part.TryGetProperty("text", out JsonElement partText)
                        && partText.ValueKind == JsonValueKind.String)
                    {
                        text.Append(partText.GetString());
                    }
                }
            }
        }

        bool refused = blockReason is not null
                       || string.Equals(finishReason, "SAFETY", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(finishReason, "PROHIBITED_CONTENT", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(finishReason, "BLOCKLIST", StringComparison.OrdinalIgnoreCase);

        string? refusalDetail = refused ? blockReason ?? finishReason : null;
        string answer = text.ToString();

        if (refused)
        {
            _logger.LogWarning("Gemini refused the request: {Detail}", refusalDetail);
            answer = "The AI declined to answer this question for safety reasons. " +
                     "Please try rephrasing it.";
        }
        else if (string.IsNullOrWhiteSpace(answer))
        {
            // A thinking model that hits maxOutputTokens can spend the whole budget on thinking
            // and return an empty answer with finishReason MAX_TOKENS. Saying "try again" for
            // that would be useless advice, so the two empty cases get different messages.
            answer = string.Equals(finishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase)
                ? "The AI ran out of output budget before it produced an answer " +
                  $"(the limit is {_options.MaxTokens} tokens, and thinking counts towards it). " +
                  "Try asking for a shorter answer, or ask an administrator to raise Gemini:MaxTokens."
                : "The AI returned no message. Please try again.";
        }
        else if (string.Equals(finishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase))
        {
            // Truncation must be visible: a cut-off answer that looks complete is worse than one
            // that says it was cut off.
            answer += "\n\n_[The answer was cut off at the output limit " +
                      $"({_options.MaxTokens} tokens). Ask a narrower question to see the rest.]_";
        }

        return new AiReply(
            Text: answer,
            ModelName: modelName,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            // Implicit caching is all this app uses, and Google does not charge to populate it,
            // so there is never a write figure to record — only reads.
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
    /// Descends into a child object only when it really is one — calling TryGetProperty on a
    /// JSON null throws InvalidOperationException, which would turn a good answer into a 502.
    /// (Cost the OpenAI provider exactly that on its first live call, via
    /// <c>"incomplete_details": null</c>.)
    /// </summary>
    private static bool TryObject(JsonElement parent, string name, out JsonElement child)
    {
        child = default;

        return parent.ValueKind == JsonValueKind.Object
               && parent.TryGetProperty(name, out JsonElement found)
               && found.ValueKind == JsonValueKind.Object
               && (child = found).ValueKind == JsonValueKind.Object;
    }

    /// <summary>
    /// Turns a non-200 into the same four categories the Anthropic path uses, so the controller
    /// and the frontend behave identically whoever answered.
    /// </summary>
    private Exception Translate(HttpStatusCode status, string body, string model)
    {
        string detail = ReadErrorMessage(body);

        switch (status)
        {
            // An unauthorised or revoked key, and a project without the API enabled, all land
            // here. None of it is the user's fault, so it must not read as "your data is invalid".
            case HttpStatusCode.Unauthorized:
            case HttpStatusCode.Forbidden:
                _logger.LogError(
                    "Gemini rejected the API key ({Status}): {Detail} — an administrator must check " +
                    "Gemini:ApiKey and that the Generative Language API is enabled", (int)status, detail);

                return new AiBillingException(
                    "The AI service is unavailable because Google rejected the Gemini API key " +
                    "(invalid, revoked, or the API is not enabled for the project). Please ask an " +
                    "administrator to check it — this is not caused by your message or files.",
                    new InvalidOperationException(detail), ProviderName);

            case HttpStatusCode.TooManyRequests:
                // Google uses 429 for both a per-minute rate limit (retrying works) and an
                // exhausted daily/billing quota (retrying cannot work). Different people have to
                // act on them, so they must not share one message.
                if (IsHardQuota(body))
                {
                    _logger.LogError("Gemini quota exhausted: {Detail}", detail);

                    return new AiBillingException(
                        "The AI service is unavailable because the Google Gemini quota is used up " +
                        "for this period. Please ask an administrator to check the quota and " +
                        "billing — this is not caused by your message or files.",
                        new InvalidOperationException(detail), ProviderName);
                }

                _logger.LogWarning("Gemini rate limit hit: {Detail}", detail);

                return new AiOverloadedException(
                    "Gemini is receiving too many requests right now. Please send your message " +
                    "again in a moment (your question has already been logged).",
                    new InvalidOperationException(detail), ProviderName);

            case HttpStatusCode.NotFound:
                // The admin priced a model id that Google does not serve. Naming the model saves
                // whoever reads this from guessing.
                _logger.LogError("Gemini has no model named {Model}: {Detail}", model, detail);

                return new AiInvalidInputException(
                    $"Google has no model called \"{model}\". Please ask an administrator to " +
                    "correct or deactivate it on the model pricing page, and choose another model " +
                    "in the meantime.", new InvalidOperationException(detail), ProviderName);

            case HttpStatusCode.InternalServerError:
            case HttpStatusCode.BadGateway:
            case HttpStatusCode.ServiceUnavailable:
            case HttpStatusCode.GatewayTimeout:
                _logger.LogWarning("Gemini returned {Status}: {Detail}", (int)status, detail);

                return new AiOverloadedException(
                    "The Gemini service is temporarily unavailable. Please send your message again " +
                    "in a moment, or choose a Claude model.",
                    new InvalidOperationException(detail), ProviderName);

            default:
                _logger.LogWarning("Gemini rejected the request ({Status}): {Detail}", (int)status, detail);
                return new AiInvalidInputException(
                    DescribeBadRequest(detail), new InvalidOperationException(detail), ProviderName);
        }
    }

    /// <summary>A daily/lifetime quota or a billing problem — retrying will not help.</summary>
    private static bool IsHardQuota(string body)
        => body.Contains("PerDay", StringComparison.OrdinalIgnoreCase)
           || body.Contains("per day", StringComparison.OrdinalIgnoreCase)
           || body.Contains("billing", StringComparison.OrdinalIgnoreCase)
           || body.Contains("free_tier", StringComparison.OrdinalIgnoreCase)
           || body.Contains("FreeTier", StringComparison.OrdinalIgnoreCase);

    /// <summary>Turns a 400 from Google into a message that tells the user what to do next.</summary>
    private static string DescribeBadRequest(string detail)
    {
        if (detail.Contains("API key not valid", StringComparison.OrdinalIgnoreCase))
        {
            return "The Gemini API key is not valid. Please ask an administrator to check " +
                   "Gemini:ApiKey — this is not caused by your message or files.";
        }

        if (detail.Contains("token count", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("too long", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("exceeds", StringComparison.OrdinalIgnoreCase))
        {
            return "The content sent (including attachments and conversation history) exceeds the " +
                   "model limit. Please start a new conversation or reduce the size or number of " +
                   "attachments.";
        }

        if (detail.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("mime", StringComparison.OrdinalIgnoreCase))
        {
            return "Gemini cannot read one of the attached files. Try re-saving it as PDF, PNG or " +
                   "JPG, or send the content as a text file.";
        }

        if (detail.Contains("image", StringComparison.OrdinalIgnoreCase))
        {
            return "The AI could not read the attached image — the file may be corrupt, too small, " +
                   "or in an unsupported format. Try re-saving it as PNG/JPG and attaching it again.";
        }

        return "The AI rejected this request because the data sent was invalid. " +
               "Please check your attachments and message, then try again.";
    }

    /// <summary>Pulls error.message out of Google's error envelope, falling back to the raw body.</summary>
    private static string ReadErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(no response body)";
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);

            if (TryObject(doc.RootElement, "error", out JsonElement error)
                && error.TryGetProperty("message", out JsonElement message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
            // An HTML error page from a proxy, for instance — the raw text is still the best clue.
        }

        return body.Length > 500 ? body[..500] : body;
    }

    /// <summary>The 400 that means "drop the thinking config and try again".</summary>
    private static bool RejectsThinking(string body)
        => (body.Contains("thinkingLevel", StringComparison.OrdinalIgnoreCase)
            || body.Contains("thinking_level", StringComparison.OrdinalIgnoreCase)
            || body.Contains("thinkingConfig", StringComparison.OrdinalIgnoreCase)
            || body.Contains("thinking_config", StringComparison.OrdinalIgnoreCase))
           && (body.Contains("not support", StringComparison.OrdinalIgnoreCase)
               || body.Contains("Unknown name", StringComparison.OrdinalIgnoreCase)
               || body.Contains("Invalid", StringComparison.OrdinalIgnoreCase)
               || body.Contains("unexpected", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Blank or unrecognised leaves the parameter out so the model uses its own default.
    ///
    /// thinkingBudget is deliberately not offered as an alternative: it is rejected outright by
    /// some models (gemini-3.5-flash-lite answers 400 for thinkingBudget 0) while thinkingLevel
    /// is accepted across the whole Gemini 3 family.
    /// </summary>
    private static string? NormaliseThinkingLevel(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        "low" => "low",
        "medium" => "medium",
        "high" => "high",
        _ => null,
    };

    private bool SupportsThinking(string model)
    {
        if (NormaliseThinkingLevel(_options.ThinkingLevel) is null)
        {
            return false;
        }

        lock (_thinkingLock)
        {
            return !_thinkingUnsupported.Contains(model);
        }
    }

    private void RememberThinkingUnsupported(string model)
    {
        lock (_thinkingLock)
        {
            _thinkingUnsupported.Add(model);
        }
    }
}
