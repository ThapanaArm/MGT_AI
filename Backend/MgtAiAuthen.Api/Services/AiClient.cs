using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Options;
using Microsoft.Extensions.Options;

namespace MgtAiAuthen.Api.Services;

/// <summary>
/// Sends each call to the provider that owns the chosen model.
///
/// The provider is decided by <c>ModelPricing.Provider</c> (resolved by ChatService together with
/// the model name), not by guessing from the model id. A prefix rule like "starts with gemini-"
/// would quietly send a newly named model to the wrong vendor, and a wrong guess means either a
/// failed call or usage billed to an account nobody chose.
/// </summary>
public class AiClient : IAiClient
{
    private readonly Dictionary<string, IAiProvider> _providers;
    private readonly ClaudeOptions _claudeOptions;
    private readonly ILogger<AiClient> _logger;

    public AiClient(
        IEnumerable<IAiProvider> providers,
        IOptions<ClaudeOptions> claudeOptions,
        ILogger<AiClient> logger)
    {
        _providers = providers.ToDictionary(p => p.ProviderName, StringComparer.OrdinalIgnoreCase);
        _claudeOptions = claudeOptions.Value;
        _logger = logger;

        string[] missing = AiProviders.All.Where(p => !_providers.ContainsKey(p)).ToArray();
        if (missing.Length > 0)
        {
            // A provider the database allows but no implementation claims would only fail once a
            // user picked one of its models. Better to say so at startup.
            _logger.LogWarning(
                "No implementation is registered for AI provider(s) {Missing} — models priced " +
                "under them cannot be used", string.Join(", ", missing));
        }

        _logger.LogInformation("AI providers ready: {Ready}",
            string.Join(", ", _providers.Values.Select(p => $"{p.ProviderName}={(p.IsConfigured ? "yes" : "no key")}")));
    }

    public bool IsConfigured => _providers.Values.Any(p => p.IsConfigured);

    public string DefaultModel => _claudeOptions.Model;

    /// <summary>The default model comes from the Claude section, so its provider is Anthropic.</summary>
    public string DefaultProvider => AiProviders.Anthropic;

    public IReadOnlyList<AiProviderStatus> Providers => _providers.Values
        .OrderBy(p => p.ProviderName, StringComparer.OrdinalIgnoreCase)
        .Select(p => new AiProviderStatus(p.ProviderName, p.IsConfigured, p.DefaultModel))
        .ToList();

    public bool IsProviderReady(string provider)
        => _providers.TryGetValue(provider, out IAiProvider? found) && found.IsConfigured;

    public Task<AiReply> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<ChatTurn> turns,
        string model,
        string provider,
        CancellationToken ct = default)
    {
        if (!_providers.TryGetValue(provider, out IAiProvider? target))
        {
            // Reachable only if a row is priced under a provider this build does not implement.
            // Falling back to another vendor would bill an account the user never chose, so this
            // stays an error.
            throw new AiUnavailableException(
                $"Model \"{model}\" is registered under the AI provider \"{provider}\", which this " +
                "system cannot call. Please ask an administrator to correct or deactivate it on the " +
                "model pricing page.", provider);
        }

        return target.CompleteAsync(systemPrompt, turns, model, ct);
    }
}
