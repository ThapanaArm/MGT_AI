using System.Text.Json;
using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MgtAiAuthen.Api.Services.DataSources;

public record RagImportResult(int ChunksImported, string EmbeddingModel, string Message);

/// <summary>
/// Loads a prepared JSONL corpus (one JSON object per line — the shape a tool like a document
/// chunker produces, e.g. MGT_Accounting_Knowledge.jsonl: fields text/chunk_id/source_file/page/
/// page_end/chapter/section/token_count_estimate) into RagChunks for one Rag-type data source.
/// Always replaces that source's existing chunks wholesale rather than merging — a corpus is
/// re-imported as a unit (e.g. after re-chunking upstream), not patched line by line.
/// </summary>
public interface IRagImportService
{
    Task<RagImportResult> ImportAsync(
        int sourceId, Stream jsonlStream, string? importedBy, CancellationToken ct = default);
}

public class RagImportService(
    AppDbContext db, IEmbeddingService embeddings, IAuditService audit, ILogger<RagImportService> logger)
    : IRagImportService
{
    /// <summary>OpenAI accepts far more per request, but batching keeps any one HTTP call small and gives partial progress if a later batch fails.</summary>
    private const int EmbedBatchSize = 50;

    public async Task<RagImportResult> ImportAsync(
        int sourceId, Stream jsonlStream, string? importedBy, CancellationToken ct = default)
    {
        DataSource source = await db.DataSources
            .FirstOrDefaultAsync(s => s.SourceId == sourceId, ct)
            ?? throw AppException.NotFound("Data source not found");

        if (!string.Equals(source.SourceType, DataSourceTypes.Rag, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException($"Source \"{source.SourceName}\" is type {source.SourceType}, not Rag.");
        }

        if (!embeddings.IsConfigured)
        {
            throw new AppException(
                "Cannot import — the OpenAI API key is not configured (OpenAI:ApiKey or " +
                "OPENAI_API_KEY), and this is what generates the embeddings chunks are searched by.");
        }

        List<ParsedChunk> parsed = await ParseJsonlAsync(jsonlStream, ct);

        if (parsed.Count == 0)
        {
            throw new AppException("No chunks were found in that file — check it is JSONL (one JSON object per line) with a non-empty \"text\" field on each line.");
        }

        var newChunks = new List<RagChunk>(parsed.Count);

        for (int offset = 0; offset < parsed.Count; offset += EmbedBatchSize)
        {
            List<ParsedChunk> batch = parsed.Skip(offset).Take(EmbedBatchSize).ToList();
            IReadOnlyList<float[]> vectors = await embeddings.EmbedAsync(batch.Select(c => c.Text).ToList(), ct);

            if (vectors.Count != batch.Count)
            {
                throw new AppException(
                    $"The embeddings call returned {vectors.Count} vector(s) for a batch of " +
                    $"{batch.Count} chunk(s) — aborting the import rather than mismatching text to " +
                    "the wrong vector.");
            }

            for (int i = 0; i < batch.Count; i++)
            {
                ParsedChunk c = batch[i];
                newChunks.Add(new RagChunk
                {
                    SourceId = sourceId,
                    ChunkKey = c.ChunkId,
                    ChunkText = c.Text,
                    SourceFile = c.SourceFile,
                    PageStart = c.Page,
                    PageEnd = c.PageEnd,
                    Chapter = c.Chapter,
                    Section = c.Section,
                    TokenCountEstimate = c.TokenCountEstimate,
                    Embedding = RagEmbedding.Pack(vectors[i]),
                    EmbeddingModel = embeddings.Model,
                    CreatedAt = DateTime.Now,
                });
            }

            logger.LogInformation("Rag import: embedded {Done}/{Total} chunks for source {SourceId}",
                Math.Min(offset + EmbedBatchSize, parsed.Count), parsed.Count, sourceId);
        }

        // Replace wholesale — see class doc comment for why this isn't a merge.
        await db.RagChunks.Where(c => c.SourceId == sourceId).ExecuteDeleteAsync(ct);
        db.RagChunks.AddRange(newChunks);
        await db.SaveChangesAsync(ct);

        string message = $"Imported {newChunks.Count} chunk(s) using {embeddings.Model}.";

        await audit.LogAsync(AuditCategories.Admin, AuditActions.DataSourceUpdated, null, importedBy,
            $"Imported {newChunks.Count} RAG chunk(s) into \"{source.SourceName}\" (#{sourceId}) " +
            $"using {embeddings.Model}", isSuccess: true, ct);

        return new RagImportResult(newChunks.Count, embeddings.Model, message);
    }

    private static async Task<List<ParsedChunk>> ParseJsonlAsync(Stream stream, CancellationToken ct)
    {
        var result = new List<ParsedChunk>();
        using var reader = new StreamReader(stream);

        int lineNumber = 0;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            lineNumber++;
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            JsonElement root;
            try
            {
                root = JsonDocument.Parse(trimmed).RootElement;
            }
            catch (JsonException)
            {
                throw new AppException($"Line {lineNumber} is not valid JSON — check the file is JSONL (one JSON object per line), not a single JSON array.");
            }

            string? text = ReadString(root, "text");
            if (string.IsNullOrWhiteSpace(text)) continue;

            result.Add(new ParsedChunk(
                ChunkId: ReadString(root, "chunk_id") ?? ReadString(root, "id") ?? $"line-{lineNumber}",
                Text: text,
                SourceFile: ReadString(root, "source_file"),
                Page: ReadInt(root, "page"),
                PageEnd: ReadInt(root, "page_end"),
                Chapter: ReadString(root, "chapter"),
                Section: ReadString(root, "section"),
                TokenCountEstimate: ReadInt(root, "token_count_estimate")));
        }

        return result;
    }

    private static string? ReadString(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out JsonElement value)
           && value.TryGetInt32(out int result)
            ? result
            : null;

    private record ParsedChunk(
        string ChunkId, string Text, string? SourceFile, int? Page, int? PageEnd,
        string? Chapter, string? Section, int? TokenCountEstimate);
}
