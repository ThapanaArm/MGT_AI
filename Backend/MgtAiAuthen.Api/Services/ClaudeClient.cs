using System.Diagnostics;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Options;
using Microsoft.Extensions.Options;

namespace MgtAiAuthen.Api.Services;

/// <summary>
/// Anthropic Claude via the official C# SDK.
///
/// The shared records, exceptions and the provider interface live in AiContracts.cs — this file
/// holds only what is specific to Anthropic.
/// </summary>
public class ClaudeClient : IAiProvider
{
    private readonly ClaudeOptions _options;
    private readonly ILogger<ClaudeClient> _logger;
    private readonly AnthropicClient? _client;

    /// <summary>
    /// Models that rejected output_config.effort, learned at runtime.
    ///
    /// Not every model accepts the effort parameter — Haiku 4.5, for one, answers
    /// "This model does not support the effort parameter" with a 400. Rather than hard-coding a
    /// capability list that goes stale as models come and go, the first rejection is remembered
    /// and the call retried without effort; later calls for that model skip it outright.
    /// </summary>
    private readonly HashSet<string> _effortUnsupported = new(StringComparer.OrdinalIgnoreCase);

    // System.Threading.Lock is .NET 9+; this project targets net8.0.
    private readonly object _effortLock = new();

    public ClaudeClient(IOptions<ClaudeOptions> options, ILogger<ClaudeClient> logger)
    {
        _options = options.Value;
        _logger = logger;

        string apiKey = !string.IsNullOrWhiteSpace(_options.ApiKey)
            ? _options.ApiKey
            : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning(
                "Claude API key is not configured — sign-in and log viewing still work, but chat returns 503");
        }
        else
        {
            // The SDK applies exponential backoff itself. Raised above the default of 2 because
            // Anthropic returned 529 overloaded_error during back-to-back testing.
            _client = new AnthropicClient { ApiKey = apiKey, MaxRetries = 4 };
        }
    }

    public string ProviderName => AiProviders.Anthropic;

    public bool IsConfigured => _client is not null;

    public string DefaultModel => _options.Model;

    public async Task<AiReply> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<ChatTurn> turns,
        string? model = null,
        CancellationToken ct = default)
    {
        if (_client is null)
        {
            throw new AiUnavailableException(
                "The Claude API key is not configured (Claude:ApiKey or the ANTHROPIC_API_KEY " +
                "environment variable). Please contact your system administrator.", ProviderName);
        }

        List<MessageParam> messages = turns.Select(BuildMessage).ToList();

        // Attachments in the conversation mean the same documents are resent every turn, so turn
        // on prompt caching: later turns read from cache at 0.1x the input price instead of full.
        bool hasAttachments = turns.Any(t => t.Attachments is { Count: > 0 });

        string resolvedModel = string.IsNullOrWhiteSpace(model) ? _options.Model : model.Trim();

        MessageCreateParams BuildParams(bool withEffort) => new()
        {
            Model = resolvedModel,
            MaxTokens = _options.MaxTokens,
            System = systemPrompt,
            Messages = messages,

            // null means the parameter is omitted entirely and the model uses its own default
            // (effort high), which is too slow for live chat — hence the system default of medium.
            OutputConfig = withEffort ? BuildOutputConfig(_options.Effort) : null,

            CacheControl = hasAttachments ? new CacheControlEphemeral() : null,
        };

        bool sendEffort = SupportsEffort(resolvedModel);

        var stopwatch = Stopwatch.StartNew();
        Message response;

        try
        {
            try
            {
                response = await _client.Messages.Create(BuildParams(sendEffort), cancellationToken: ct);
            }
            catch (AnthropicBadRequestException ex) when (sendEffort && RejectsEffort(ex))
            {
                // Learn it once, then retry immediately without effort. Later calls for this model
                // skip the wasted round trip.
                RememberEffortUnsupported(resolvedModel);

                _logger.LogInformation(
                    "Model {Model} does not accept output_config.effort — retrying without it",
                    resolvedModel);

                response = await _client.Messages.Create(BuildParams(withEffort: false), cancellationToken: ct);
            }
        }
        catch (Anthropic5xxException ex) when (IsOverloaded(ex))
        {
            // 529 overloaded_error means Anthropic is momentarily saturated and the SDK's retries
            // were all used up. Separated out so we can tell the user to just send again.
            _logger.LogWarning(ex, "Anthropic returned overloaded (529) after all retries");
            throw new AiOverloadedException(
                "The AI service is temporarily overloaded. Please send your message again in a " +
                "moment (your question has already been logged).", ex, ProviderName);
        }
        catch (AnthropicBadRequestException ex)
        {
            // Anthropic uses 400 invalid_request_error for both bad input and exhausted credit,
            // so the two must be told apart — different people have to act on them.
            if (IsBillingProblem(ex))
            {
                _logger.LogError(ex,
                    "Anthropic rejected the request for an account/credit reason — " +
                    "an administrator must add credit or check the API key");

                throw new AiBillingException(
                    "The AI service is unavailable because the Anthropic account is out of credit " +
                    "or over quota. Please ask an administrator to check the credit balance and " +
                    "API key — this is not caused by your message or files.", ex, ProviderName);
            }

            _logger.LogWarning(ex, "Anthropic rejected the request (400)");
            throw new AiInvalidInputException(DescribeBadRequest(ex), ex, ProviderName);
        }

        stopwatch.Stop();

        string text = string.Concat(
            response.Content.Select(b => b.Value).OfType<TextBlock>().Select(b => b.Text));

        bool refused = response.StopReason == "refusal";
        string? refusalDetail = refused ? response.StopDetails?.ToString() : null;

        if (refused)
        {
            _logger.LogWarning("Claude refused the request: {Detail}", refusalDetail);
            text = "The AI declined to answer this question for safety reasons. " +
                   "Please try rephrasing it.";
        }
        else if (string.IsNullOrWhiteSpace(text))
        {
            text = "The AI returned no message. Please try again.";
        }

        return new AiReply(
            Text: text,
            ModelName: ReadModelName(response),
            InputTokens: (int)response.Usage.InputTokens,
            OutputTokens: (int)response.Usage.OutputTokens,
            // Prompt-cache tokens are priced differently from normal input, so they are tracked apart.
            CacheWriteTokens: (int)(response.Usage.CacheCreationInputTokens ?? 0),
            CacheReadTokens: (int)(response.Usage.CacheReadInputTokens ?? 0),
            LatencyMs: (int)stopwatch.ElapsedMilliseconds,
            Refused: refused,
            RefusalDetail: refusalDetail);
    }

    /// <summary>
    /// Turns one conversation turn into a MessageParam. With attachments the content must be a
    /// list of blocks, and the files go *before* the text as Anthropic recommends, so the model
    /// reads the documents before it sees the question.
    /// </summary>
    private static MessageParam BuildMessage(ChatTurn turn)
    {
        Role role = turn.Role == MessageRoles.Assistant ? Role.Assistant : Role.User;

        if (turn.Attachments is not { Count: > 0 })
        {
            return new MessageParam { Role = role, Content = turn.Content };
        }

        List<ContentBlockParam> blocks = [];

        foreach (TurnAttachment attachment in turn.Attachments)
        {
            switch (attachment.FileKind)
            {
                case FileKinds.Image:
                    blocks.Add(new ImageBlockParam
                    {
                        Source = new Base64ImageSource
                        {
                            Data = Convert.ToBase64String(attachment.Content),
                            MediaType = attachment.ContentType,
                        },
                    });
                    break;

                case FileKinds.Pdf:
                    blocks.Add(new DocumentBlockParam
                    {
                        Source = new Base64PdfSource { Data = Convert.ToBase64String(attachment.Content) },
                        Title = attachment.FileName,
                    });
                    break;

                case FileKinds.Text:
                    blocks.Add(new DocumentBlockParam
                    {
                        Source = new PlainTextSource { Data = attachment.Text ?? string.Empty },
                        Title = attachment.FileName,
                    });
                    break;
            }
        }

        // The text block cannot be empty, so supply a default instruction when the user dropped
        // files without typing anything.
        blocks.Add(new TextBlockParam
        {
            Text = string.IsNullOrWhiteSpace(turn.Content)
                ? "Please analyse the attached file(s) and summarise the key points."
                : turn.Content,
        });

        return new MessageParam { Role = role, Content = blocks };
    }

    /// <summary>
    /// Distinguishes 529 overloaded_error from other 5xx responses. The SDK folds every 5xx into
    /// one exception type and this version exposes no readable status-code property, so the
    /// response body has to be inspected.
    /// </summary>
    private static bool IsOverloaded(Anthropic5xxException ex)
        => ex.Message.Contains("529", StringComparison.Ordinal)
           || ex.Message.Contains("overloaded", StringComparison.OrdinalIgnoreCase);

    /// <summary>Has this model already told us it does not accept output_config.effort?</summary>
    private bool SupportsEffort(string model)
    {
        lock (_effortLock)
        {
            return !_effortUnsupported.Contains(model);
        }
    }

    private void RememberEffortUnsupported(string model)
    {
        lock (_effortLock)
        {
            _effortUnsupported.Add(model);
        }
    }

    /// <summary>The 400 that means "drop the effort parameter and try again".</summary>
    private static bool RejectsEffort(AnthropicBadRequestException ex)
        => ex.Message.Contains("effort", StringComparison.OrdinalIgnoreCase)
           && ex.Message.Contains("does not support", StringComparison.OrdinalIgnoreCase);

    /// <summary>Credit, quota or entitlement problems — nothing an ordinary user can fix.</summary>
    private static bool IsBillingProblem(AnthropicBadRequestException ex)
    {
        string raw = ex.Message;

        return raw.Contains("credit balance", StringComparison.OrdinalIgnoreCase)
               || raw.Contains("Plans & Billing", StringComparison.OrdinalIgnoreCase)
               || raw.Contains("quota", StringComparison.OrdinalIgnoreCase)
               || raw.Contains("billing", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Turns a 400 from Anthropic into a message that tells the user what to do next. With
    /// attachments the common causes are an image the model cannot read and an over-long PDF.
    /// </summary>
    private static string DescribeBadRequest(AnthropicBadRequestException ex)
    {
        string raw = ex.Message;

        if (raw.Contains("Could not process image", StringComparison.OrdinalIgnoreCase))
        {
            return "The AI could not read the attached image — the file may be corrupt, too small, " +
                   "or in an unsupported format. Try re-saving it as PNG/JPG and attaching it again.";
        }

        if (raw.Contains("too many pages", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("page limit", StringComparison.OrdinalIgnoreCase))
        {
            return "The PDF has more pages than the AI can accept. " +
                   "Please split it into smaller files and send them one at a time.";
        }

        if (raw.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("exceed", StringComparison.OrdinalIgnoreCase))
        {
            return "The content sent (including attachments and conversation history) exceeds the " +
                   "model limit. Please start a new conversation or reduce the size or number of " +
                   "attachments.";
        }

        return "The AI rejected this request because the data sent was invalid. " +
               "Please check your attachments and message, then try again.";
    }

    /// <summary>
    /// Maps the Effort setting from appsettings onto the value the SDK expects. An unknown or
    /// blank value returns null so the parameter is omitted and the model uses its own default.
    /// </summary>
    private OutputConfig? BuildOutputConfig(string? effort) => effort?.Trim().ToLowerInvariant() switch
    {
        "low" => new OutputConfig { Effort = Effort.Low },
        "medium" => new OutputConfig { Effort = Effort.Medium },
        "high" => new OutputConfig { Effort = Effort.High },
        "max" => new OutputConfig { Effort = Effort.Max },
        _ => null,
    };

    /// <summary>
    /// The model name the API echoed back. The SDK's Model is a union type whose ToString()
    /// yields JSON (wrapped in quotes), so the quotes must be stripped before logging — otherwise
    /// filters that compare the model name exactly will never match.
    /// </summary>
    private string ReadModelName(Message response)
    {
        string? raw = response.Model.ToString()?.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(raw) ? _options.Model : raw;
    }
}
