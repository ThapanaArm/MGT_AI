namespace MgtAiAuthen.Api.Services;

/// <summary>An attachment ready to send to the model (content already loaded from disk).</summary>
public record TurnAttachment(
    string FileName,
    string ContentType,
    string FileKind,
    byte[] Content,
    string? Text);

/// <summary>One conversation turn sent to the model as context.</summary>
public record ChatTurn(
    string Role,
    string Content,
    IReadOnlyList<TurnAttachment>? Attachments = null);

/// <summary>
/// The model's reply, plus the figures we persist to the log. Provider-independent: every
/// provider maps its own usage numbers onto these fields so the cost calculator, the log
/// and the reports never need to know who answered.
/// </summary>
public record AiReply(
    string Text,
    string ModelName,
    /// <summary>
    /// Input tokens charged at the full input rate — cached tokens must NOT be counted here.
    /// Anthropic already reports the two separately; Gemini does not, so its provider
    /// subtracts the cached count before filling this in.
    /// </summary>
    int InputTokens,
    /// <summary>
    /// Output tokens, including any thinking/reasoning tokens. Both providers bill thinking at
    /// the output rate, so folding them in here is what makes the recorded cost match the bill.
    /// </summary>
    int OutputTokens,
    int CacheWriteTokens,
    int CacheReadTokens,
    int LatencyMs,
    bool Refused,
    string? RefusalDetail);

/// <summary>Thrown when a provider has no API key configured — the handler turns this into 503.</summary>
public class AiUnavailableException(string message, string provider = "") : Exception(message)
{
    public string Provider { get; } = provider;
}

/// <summary>
/// Thrown when the provider keeps reporting that it is saturated after all retries
/// (Anthropic 529 overloaded_error, Google 503 UNAVAILABLE). Kept separate from other
/// failures because the user can fix it by simply sending again.
/// </summary>
public class AiOverloadedException(string message, Exception? inner, string provider = "")
    : Exception(message, inner)
{
    public string Provider { get; } = provider;
}

/// <summary>
/// Thrown when the provider returns 400 — usually caused by what the user sent (corrupt or
/// too-small image, PDF with too many pages, request too large). Answering 400 with the reason
/// is better than a 502 that makes it look like the system is broken.
/// </summary>
public class AiInvalidInputException(string message, Exception? inner, string provider = "")
    : Exception(message, inner)
{
    public string Provider { get; } = provider;
}

/// <summary>
/// Thrown when the provider rejects the call for account reasons (out of credit, over quota,
/// revoked or unauthorised key). Must stay separate from <see cref="AiInvalidInputException"/>
/// because the user cannot fix it — saying "your data is invalid" would send admins hunting the
/// wrong cause. Both providers report this on status codes they also use for real input errors,
/// so each provider is responsible for telling the two apart.
/// </summary>
public class AiBillingException(string message, Exception? inner, string provider = "")
    : Exception(message, inner)
{
    public string Provider { get; } = provider;
}

/// <summary>
/// One AI vendor. Adding a provider means adding an implementation and a row in
/// <c>ModelPricing</c> with its <c>Provider</c> — no changes to ChatService.
/// </summary>
public interface IAiProvider
{
    /// <summary>Must match a value from <see cref="Data.AiProviders"/>.</summary>
    string ProviderName { get; }

    /// <summary>False when no API key is configured — chat then returns 503 for this provider only.</summary>
    bool IsConfigured { get; }

    /// <summary>The model this provider uses when the caller does not name one.</summary>
    string DefaultModel { get; }

    /// <summary>
    /// <paramref name="model"/> is the exact model id to call. ChatService validates it against
    /// the models that have active pricing before getting here.
    /// </summary>
    Task<AiReply> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<ChatTurn> turns,
        string? model = null,
        CancellationToken ct = default);
}

/// <summary>Readiness of one provider, for the status endpoint.</summary>
public record AiProviderStatus(string Provider, bool Ready, string DefaultModel);

/// <summary>
/// Routes a call to the provider that owns the chosen model. ChatService depends on this rather
/// than on any single vendor's client.
/// </summary>
public interface IAiClient
{
    /// <summary>True when at least one provider has a key — the chat page is usable.</summary>
    bool IsConfigured { get; }

    /// <summary>The system-wide default model (<c>Claude:Model</c>).</summary>
    string DefaultModel { get; }

    /// <summary>The provider that owns <see cref="DefaultModel"/>.</summary>
    string DefaultProvider { get; }

    IReadOnlyList<AiProviderStatus> Providers { get; }

    /// <summary>Is this provider name known and keyed?</summary>
    bool IsProviderReady(string provider);

    /// <summary>
    /// Sends the turns to <paramref name="provider"/>. An unknown provider name is a
    /// configuration error, not user input, so it throws rather than falling back to another
    /// vendor — nobody should be billed by a provider they did not choose.
    /// </summary>
    Task<AiReply> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<ChatTurn> turns,
        string model,
        string provider,
        CancellationToken ct = default);
}
