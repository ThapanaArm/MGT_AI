using System.Text;
using System.Text.Json;
using MgtAiAuthen.Api.Contracts;
using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MgtAiAuthen.Api.Services.DataSources;

/// <summary>
/// Calls OpenAI's embeddings endpoint — used both to embed a knowledge base's chunks once at
/// import time, and to embed the user's own question on every message (see
/// ChatService.ResolvePerMessageOverrideAsync). Deliberately the SAME provider/model for both
/// sides: cosine similarity is only meaningful between vectors from one embedding space, so a
/// corpus embedded with a different model (e.g. the multilingual-e5-base the source files'
/// own scripts/generate_embeddings.py would have used) must be re-embedded here on import rather
/// than reusing whatever vector it originally shipped with.
/// </summary>
public interface IEmbeddingService
{
    bool IsConfigured { get; }
    string Model { get; }

    /// <summary>List price in USD per 1M input tokens — used to show what an import cost.</summary>
    decimal UsdPerMTok { get; }

    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default);

    /// <summary>Same as <see cref="EmbedAsync"/>, plus the number of tokens the provider billed for.</summary>
    Task<(IReadOnlyList<float[]> Vectors, int Tokens)> EmbedWithUsageAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}

public class OpenAiEmbeddingService : IEmbeddingService
{
    /// <summary>1536 dimensions, $0.02 / 1M tokens — cheap enough that embedding cost is a rounding error next to the chat calls it feeds.</summary>
    public const string ModelName = "text-embedding-3-small";

    private readonly OpenAiOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenAiEmbeddingService> _logger;
    private readonly string _apiKey;

    public OpenAiEmbeddingService(
        IOptions<OpenAiOptions> options, IHttpClientFactory httpClientFactory, ILogger<OpenAiEmbeddingService> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        _apiKey = !string.IsNullOrWhiteSpace(_options.ApiKey)
            ? _options.ApiKey
            : Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public string Model => ModelName;

    public decimal UsdPerMTok => 0.02m;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        => (await EmbedWithUsageAsync(texts, ct)).Vectors;

    public async Task<(IReadOnlyList<float[]> Vectors, int Tokens)> EmbedWithUsageAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            throw new AiUnavailableException(
                "The OpenAI API key is not configured (OpenAI:ApiKey or the OPENAI_API_KEY " +
                "environment variable) — a Rag data source needs it for embeddings, even if chat " +
                "itself is answered by a different provider.", AiProviders.OpenAi);
        }

        if (texts.Count == 0)
        {
            return ([], 0);
        }

        object payload = new { model = ModelName, input = texts };

        HttpClient http = _httpClientFactory.CreateClient(OpenAiClient.HttpClientName);
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/embeddings")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Authorization", $"Bearer {_apiKey}");

        using HttpResponseMessage response = await http.SendAsync(request, ct);
        string body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("OpenAI embeddings call failed ({Status}): {Body}",
                (int)response.StatusCode, body.Length > 500 ? body[..500] : body);

            throw new AiInvalidInputException(
                $"The embeddings request failed (HTTP {(int)response.StatusCode}). Check the " +
                "OpenAI API key and that this account has access to the embeddings endpoint.",
                new InvalidOperationException(body), AiProviders.OpenAi);
        }

        using JsonDocument doc = JsonDocument.Parse(body);

        // "data" is documented as returned in the same order as the "input" array was sent, so
        // index position (not an "index" field lookup) is enough to zip results back to inputs.
        List<float[]> vectors = doc.RootElement.GetProperty("data")
            .EnumerateArray()
            .Select(item => item.GetProperty("embedding")
                .EnumerateArray()
                .Select(v => (float)v.GetDouble())
                .ToArray())
            .ToList();

        int tokens = doc.RootElement.TryGetProperty("usage", out JsonElement usage)
                     && usage.ValueKind == JsonValueKind.Object
                     && usage.TryGetProperty("total_tokens", out JsonElement total)
                     && total.TryGetInt32(out int counted)
            ? counted
            : 0;

        return (vectors, tokens);
    }
}

/// <summary>Packing a float[] to/from the VARBINARY column RagChunk.Embedding stores it as, plus the similarity measure retrieval ranks by.</summary>
internal static class RagEmbedding
{
    public static byte[] Pack(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] Unpack(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }

    /// <summary>Both OpenAI embeddings and this corpus's intended e5 embeddings are already unit-normalised, so a plain dot product equals cosine similarity — but this does the full computation rather than assuming that, since a differently-sourced vector down the line might not be.</summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, magA = 0, magB = 0;
        int len = Math.Min(a.Length, b.Length);

        for (int i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        double denom = Math.Sqrt(magA) * Math.Sqrt(magB);
        return denom <= 0 ? 0f : (float)(dot / denom);
    }
}

/// <summary>
/// "Test connection" for a Rag source means "is there anything imported for it", not a live
/// external call — the data already lives in this app's own database (RagChunks), imported once
/// via RagController rather than queried on demand like every other source type.
/// </summary>
public class RagConnectionTester(AppDbContext db) : IDataSourceConnectionTester
{
    public string SourceType => DataSourceTypes.Rag;

    public async Task<DataSourceTestResult> TestAsync(
        Dictionary<string, string> config, string? secret, CancellationToken ct)
    {
        // config carries no connection details for this type — SourceId comes from the caller's
        // own context, but IDataSourceConnectionTester's signature doesn't pass it, so this reads
        // every Rag source's chunk count the same way DataSourceService already resolves SourceId
        // for the caller before invoking TestAsync. See DataSourceService.TestConnectionAsync.
        int sourceId = config.TryGetValue("__sourceId", out string? raw) && int.TryParse(raw, out int id)
            ? id
            : 0;

        if (sourceId <= 0)
        {
            return new DataSourceTestResult(false, "No source ID was provided to test against.", DateTime.Now);
        }

        int count = await db.RagChunks.AsNoTracking().CountAsync(c => c.SourceId == sourceId, ct);

        return count > 0
            ? new DataSourceTestResult(true, $"{count} chunk(s) imported and ready to search.", DateTime.Now)
            : new DataSourceTestResult(false,
                "No chunks have been imported yet for this source — use \"Import corpus\" on the " +
                "data source registry page to load a prepared JSONL file.", DateTime.Now);
    }
}

/// <summary>
/// Returns whatever ChatService.ResolvePerMessageOverrideAsync already retrieved and handed down
/// through the scopeFilter parameter (the same mechanism Warehouse-mode multi-query selection
/// reuses) — this fetcher does no retrieval of its own, since retrieval needs the live question
/// text, which the generic IDataSourceFetcher.FetchAsync signature has no slot for.
/// </summary>
public class RagFetcher : IDataSourceFetcher
{
    public string SourceType => DataSourceTypes.Rag;

    public Task<DataSourceFetchResult> FetchAsync(
        Dictionary<string, string> config, string? secret, string? scopeFilter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scopeFilter))
        {
            // Reachable from "Refresh" (no question context) or a fresh session with no message
            // sent yet — there is nothing to retrieve against, so say so rather than guessing.
            return Task.FromResult(new DataSourceFetchResult(
                true, "No question to search the knowledge base against yet.", null, 0, false));
        }

        return Task.FromResult(new DataSourceFetchResult(
            true, "Retrieved the most relevant passage(s) from the knowledge base.",
            scopeFilter, scopeFilter.Length, false));
    }
}
