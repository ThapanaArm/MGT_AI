using System.Text;
using System.Text.Json;
using MgtAiAuthen.Api.Contracts;
using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Infrastructure;
using MgtAiAuthen.Api.Security;
using MgtAiAuthen.Api.Services;
using MgtAiAuthen.Api.Services.DataSources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MgtAiAuthen.Api.Controllers;

/// <summary>
/// Administrator tools for RAG knowledge bases: "Create RAG" (OCR a PDF or image with an AI model into searchable chunks) and a JSONL
/// export that can be loaded into Microsoft Fabric.
/// </summary>
[ApiController]
[Route("api/admin/rag")]
[Authorize(Roles = UserRoles.Admin)]
public class AdminRagController(
    AppDbContext db, IAuditService audit, IRagCreateService create) : ControllerBase
{
    // ---------------------------------------------------------------- RAG sources

    [HttpGet("sources")]
    public async Task<ActionResult<IReadOnlyList<RagSourceDto>>> GetSources(CancellationToken ct)
    {
        List<DataSource> sources = await db.DataSources.AsNoTracking()
            .Where(s => s.SourceType == DataSourceTypes.Rag)
            .OrderBy(s => s.SourceName)
            .ToListAsync(ct);

        var chunkStats = (await db.RagChunks.AsNoTracking()
                .GroupBy(c => c.SourceId)
                .Select(g => new { SourceId = g.Key, Chunks = g.Count(), Files = g.Select(c => c.SourceFile).Distinct().Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.SourceId);

        return Ok(sources.Select(s => new RagSourceDto(
            s.SourceId, s.SourceName, s.Description, s.IsActive,
            chunkStats.TryGetValue(s.SourceId, out var st) ? st.Chunks : 0,
            chunkStats.TryGetValue(s.SourceId, out var st2) ? st2.Files : 0,
            s.CreatedAt)).ToList());
    }

    [HttpPut("sources/{id:int}")]
    public async Task<IActionResult> UpdateSource(int id, RagSourceUpdateRequest request, CancellationToken ct)
    {
        DataSource source = await db.DataSources.FirstOrDefaultAsync(s => s.SourceId == id && s.SourceType == DataSourceTypes.Rag, ct)
            ?? throw AppException.NotFound("RAG not found");

        string name = request.Name.Trim();
        if (await db.DataSources.AnyAsync(s => s.SourceName == name && s.SourceId != id, ct))
        {
            throw AppException.Conflict($"A data source named \"{name}\" already exists");
        }

        source.SourceName = name;
        source.Description = request.Description?.Trim();
        source.IsActive = request.IsActive;
        source.UpdatedAt = DateTime.Now;
        source.UpdatedBy = User.GetUsername();

        await db.SaveChangesAsync(ct);
        await LogAsync($"Updated RAG #{id} \"{name}\" ({(request.IsActive ? "active" : "inactive")})", ct, AuditActions.DataSourceUpdated);
        return NoContent();
    }

    /// <summary>
    /// Deletes the RAG and every chunk in it. Anything that merely pointed at it is cleaned up
    /// rather than blocking the delete: employee grants are removed, and conversations that were
    /// started on it stay (their messages are untouched) but no longer name a data source.
    /// </summary>
    [HttpDelete("sources/{id:int}")]
    public async Task<IActionResult> DeleteSource(int id, CancellationToken ct)
    {
        DataSource source = await db.DataSources.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SourceId == id && s.SourceType == DataSourceTypes.Rag, ct)
            ?? throw AppException.NotFound("RAG not found");

        int chunks = 0, grants = 0, sessions = 0;
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            await db.ChatSessionDataFetches.Where(f => f.SourceId == id).ExecuteDeleteAsync(ct);
            sessions = await db.ChatSessions.Where(s => s.DataSourceId == id)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.DataSourceId, (int?)null), ct);
            grants = await db.DataSourceGrants.Where(g => g.SourceId == id).ExecuteDeleteAsync(ct);
            chunks = await db.RagChunks.Where(c => c.SourceId == id).ExecuteDeleteAsync(ct);
            await db.DataSources.Where(s => s.SourceId == id).ExecuteDeleteAsync(ct);

            await tx.CommitAsync(ct);
        });

        await LogAsync($"Deleted RAG #{id} \"{source.SourceName}\" — {chunks} chunk(s), {grants} grant(s) removed; {sessions} conversation(s) detached", ct, AuditActions.DataSourceUpdated);
        return NoContent();
    }

    // ---------------------------------------------------------------- Create RAG

    [HttpPost("create")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(200L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 200L * 1024 * 1024)]
    public async Task<ActionResult<RagJobDto>> Create([FromForm] RagCreateForm form, CancellationToken ct)
    {
        var files = new List<RagUploadFile>();

        foreach (IFormFile file in form.Files)
        {
            await using Stream stream = file.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            files.Add(new RagUploadFile(file.FileName, file.ContentType, buffer.ToArray()));
        }

        RagJobDto job = await create.StartAsync(
            new RagCreateInput(
                form.SourceId, form.SourceName, form.Description,
                form.Model, form.PagesPerBatch, files),
            User.GetUsername(), ct);

        return Ok(job);
    }

    /// <summary>Recent Create RAG runs with their token usage and cost, newest first.</summary>
    [HttpGet("runs")]
    public async Task<ActionResult<IReadOnlyList<RagRunDto>>> GetRuns([FromQuery] int take = 50, CancellationToken ct = default)
    {
        List<RagRun> runs = await db.RagRuns.AsNoTracking()
            .OrderByDescending(r => r.StartedAt)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(ct);

        return Ok(runs.Select(r => new RagRunDto(
            r.RunId, r.SourceId, r.SourceName, r.Model, r.Provider,
            // A run still marked Running with no live job behind it was cut off by a backend restart.
            r.State == "Running" && create.GetJob(r.RunId) is null ? "Interrupted" : r.State,
            r.FilesTotal, r.PagesTotal, r.ChunksCreated,
            r.InputTokens, r.OutputTokens, r.CacheTokens, r.EmbeddingTokens,
            r.OcrCostUsd, r.EmbeddingCostUsd, r.TotalCostUsd, r.TotalCostThb,
            r.Error, r.StartedBy, r.StartedAt, r.FinishedAt)).ToList());
    }

    [HttpGet("jobs/{jobId:guid}")]
    public ActionResult<RagJobDto> GetJob(Guid jobId)
        => create.GetJob(jobId) is { } job ? Ok(job) : NotFound();

    // ---------------------------------------------------------------- export for Microsoft Fabric

    /// <summary>
    /// One JSON object per line, in the same shape as the MGT_Accounting_Knowledge corpus, ready to
    /// land in a Fabric Lakehouse (Files/ → read as a Spark/pandas DataFrame). Vectors are left
    /// out unless asked for — Fabric normally embeds with its own model.
    /// </summary>
    [HttpGet("sources/{id:int}/export")]
    public async Task<IActionResult> Export(int id, [FromQuery] bool includeVectors, CancellationToken ct)
    {
        ExportBundle bundle = await BuildExportAsync(id, includeVectors, ct);
        await LogAsync($"Exported RAG #{id} \"{bundle.Source.SourceName}\" as JSONL - {bundle.Chunks.Count} chunk(s){(includeVectors ? " with vectors" : "")}", ct, AuditActions.RagExported);

        return File(bundle.Jsonl, "application/x-ndjson", $"{bundle.SafeName}.jsonl");
    }

    /// <summary>
    /// A ZIP laid out like the MGT_Accounting_Knowledge package: the JSONL, a manifest of what is in
    /// it (files, page ranges, counts, category, embedding model) and a short README on loading it
    /// into a Microsoft Fabric Lakehouse.
    /// </summary>
    [HttpGet("sources/{id:int}/export.zip")]
    public async Task<IActionResult> ExportZip(int id, [FromQuery] bool includeVectors, CancellationToken ct)
    {
        ExportBundle bundle = await BuildExportAsync(id, includeVectors, ct);

        var files = bundle.Chunks
            .GroupBy(c => c.SourceFile ?? "(unknown)")
            .Select(g => new
            {
                source_file = g.Key,
                chunk_count = g.Count(),
                page_start = g.Min(c => c.PageStart),
                page_end = g.Max(c => c.PageEnd ?? c.PageStart),
                char_count = g.Sum(c => c.ChunkText.Length),
            })
            .ToList();

        var manifest = new
        {
            dataset = bundle.Source.SourceName,
            description = bundle.Source.Description,
            category = bundle.Category,
            sub_category = bundle.SubCategory,
            source_id = bundle.Source.SourceId,
            exported_at = DateTime.Now,
            exported_by = User.GetUsername(),
            total_files = files.Count,
            total_chunks = bundle.Chunks.Count,
            total_chars = bundle.Chunks.Sum(c => c.ChunkText.Length),
            language = "original (verbatim from the documents, never translated)",
            jsonl_file = $"{bundle.SafeName}.jsonl",
            includes_vectors = includeVectors,
            embedding_model = includeVectors ? bundle.Chunks.Select(c => c.EmbeddingModel).FirstOrDefault() : null,
            files,
        };

        string readme =
            $"# {bundle.Source.SourceName}\n\n" +
            $"{bundle.Source.Description}\n\n" +
            $"Category: {bundle.Category ?? "-"}{(bundle.SubCategory is null ? "" : " > " + bundle.SubCategory)}\n" +
            $"Chunks: {bundle.Chunks.Count} from {files.Count} file(s). Text is verbatim from the original documents and was not translated.\n\n" +
            "## Files\n\n" +
            $"- `{bundle.SafeName}.jsonl` - one passage per line (UTF-8 JSON Lines)\n" +
            $"- `{bundle.SafeName}_manifest.json` - what the package contains\n\n" +
            "## Fields\n\n" +
            "`dataset`, `category`, `sub_category`, `source_file`, `page`, `page_end`, `pages`, `chapter`, `section`, " +
            "`text`, `char_count`, `token_count_estimate`, `chunk_id`, `id`, `vector` (null unless exported with vectors), `embedding_model`.\n\n" +
            "## Load into Microsoft Fabric\n\n" +
            "1. In your Lakehouse, open **Files** > **Upload** and add the `.jsonl` file.\n" +
            "2. In a notebook: `df = spark.read.json(\"Files/" + bundle.SafeName + ".jsonl\")`\n" +
            "3. Save it as a table: `df.write.mode(\"overwrite\").format(\"delta\").saveAsTable(\"" + SafeTableName(bundle.SafeName) + "\")`\n";

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        using var zipStream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(zip, $"{bundle.SafeName}/{bundle.SafeName}.jsonl", bundle.Jsonl);
            AddEntry(zip, $"{bundle.SafeName}/{bundle.SafeName}_manifest.json",
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, options)));
            AddEntry(zip, $"{bundle.SafeName}/README.md", Encoding.UTF8.GetBytes(readme));
        }

        await LogAsync($"Exported RAG #{id} \"{bundle.Source.SourceName}\" as ZIP - {bundle.Chunks.Count} chunk(s){(includeVectors ? " with vectors" : "")}", ct, AuditActions.RagExported);

        return File(zipStream.ToArray(), "application/zip", $"{bundle.SafeName}.zip");
    }

    private static void AddEntry(System.IO.Compression.ZipArchive zip, string name, byte[] content)
    {
        System.IO.Compression.ZipArchiveEntry entry = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }

    private static string SafeTableName(string name)
    {
        string table = new string(name.Select(ch => char.IsAsciiLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_').ToArray()).Trim('_');
        return string.IsNullOrEmpty(table) || char.IsDigit(table[0]) ? "rag_" + table : table;
    }

    private sealed record ExportBundle(
        DataSource Source, string? Category, string? SubCategory, List<RagChunk> Chunks, byte[] Jsonl, string SafeName);

    private async Task<ExportBundle> BuildExportAsync(int id, bool includeVectors, CancellationToken ct)
    {
        DataSource source = await db.DataSources.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SourceId == id && s.SourceType == DataSourceTypes.Rag, ct)
            ?? throw AppException.NotFound("RAG not found");

        string? category = source.CategoryId is { } c
            ? await db.DataSourceCategories.AsNoTracking().Where(x => x.CategoryId == c).Select(x => x.CategoryName).FirstOrDefaultAsync(ct)
            : null;
        string? subCategory = source.SubCategoryId is { } sc
            ? await db.DataSourceSubCategories.AsNoTracking().Where(x => x.SubCategoryId == sc).Select(x => x.SubCategoryName).FirstOrDefaultAsync(ct)
            : null;

        List<RagChunk> chunks = await db.RagChunks.AsNoTracking()
            .Where(x => x.SourceId == id)
            .OrderBy(x => x.SourceFile).ThenBy(x => x.PageStart).ThenBy(x => x.ChunkId)
            .ToListAsync(ct);

        if (chunks.Count == 0) throw new AppException("This RAG has no chunks yet - there is nothing to export.");

        var jsonOptions = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var sb = new StringBuilder();
        foreach (RagChunk chunk in chunks)
        {
            int[]? pages = chunk.PageStart is { } start
                ? Enumerable.Range(start, Math.Max(1, (chunk.PageEnd ?? start) - start + 1)).ToArray()
                : null;

            sb.Append(JsonSerializer.Serialize(new
            {
                dataset = source.SourceName,
                category,
                sub_category = subCategory,
                source_file = chunk.SourceFile,
                page = chunk.PageStart,
                page_end = chunk.PageEnd,
                pages,
                chapter = chunk.Chapter,
                section = chunk.Section,
                text = chunk.ChunkText,
                char_count = chunk.ChunkText.Length,
                token_count_estimate = chunk.TokenCountEstimate,
                chunk_id = chunk.ChunkKey,
                id = chunk.ChunkKey,
                vector = includeVectors ? RagEmbedding.Unpack(chunk.Embedding) : null,
                embedding_model = includeVectors ? chunk.EmbeddingModel : null,
            }, jsonOptions)).Append('\n');
        }

        string safeName = new string(source.SourceName.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        if (string.IsNullOrEmpty(safeName.Trim('_'))) safeName = $"rag_{id}";

        return new ExportBundle(source, category, subCategory, chunks, Encoding.UTF8.GetBytes(sb.ToString()), safeName);
    }

    private Task LogAsync(string detail, CancellationToken ct, string action = AuditActions.DataSourceUpdated)
        => audit.LogAsync(AuditCategories.Admin, action, User.GetUserId(), User.GetUsername(), detail, isSuccess: true, ct);
}
