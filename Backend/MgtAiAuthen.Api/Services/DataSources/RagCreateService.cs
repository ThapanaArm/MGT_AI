using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MgtAiAuthen.Api.Contracts;
using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace MgtAiAuthen.Api.Services.DataSources;

public record RagUploadFile(string FileName, string ContentType, byte[] Content);

public record RagCreateInput(
    int? SourceId, string? SourceName, string? Description,
    string Model, int PagesPerBatch, IReadOnlyList<RagUploadFile> Files);

/// <summary>
/// "Create RAG": reads PDFs / images with a vision-capable AI model (OCR), chunks the text,
/// embeds it and stores it as RagChunks under a Rag-type data source. Reading a large PDF takes
/// minutes, so the work runs as a background job the admin page polls — job state lives in
/// memory only (a backend restart drops the history, not the chunks already saved).
/// </summary>
public interface IRagCreateService
{
    /// <summary>Validates the request and the target source synchronously, then starts the background work.</summary>
    Task<RagJobDto> StartAsync(RagCreateInput input, string? startedBy, CancellationToken ct = default);

    RagJobDto? GetJob(Guid jobId);
}

public class RagCreateService(IServiceScopeFactory scopes, ILogger<RagCreateService> logger) : IRagCreateService
{
    private const int MaxTotalPages = 600;
    private const int MaxFiles = 30;
    private const string PageMarkerPrefix = "=====PAGE ";

    private static readonly Regex AddedDescription = new(
        @"^\s*\[(?:Figure|Image|Picture|Photo|Chart|Diagram|Logo|Illustration)\b[^\]\n]*\]\s*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PageMarker = new(@"^=====PAGE\s+(\d+)=====\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly string OcrSystemPrompt =
        "You are a precise OCR and document-transcription engine for a knowledge base. Transcribe " +
        "the text printed on the supplied page(s) VERBATIM, in the ORIGINAL language and script of " +
        "the document. NEVER translate, transliterate, summarise, paraphrase, correct or modernise " +
        "anything: Thai stays Thai, English stays English, mixed text stays mixed, with every tone " +
        "mark, number, unit, punctuation mark and spelling exactly as printed. Keep the reading " +
        "order. Do NOT describe images, photos, logos, charts or figures and do NOT write any word " +
        "that is not printed on the page — for a chart or diagram transcribe only its printed " +
        "title, labels, numbers and caption. Render a table as a Markdown table with each cell " +
        "copied verbatim. Keep headings as plain lines (no added # marks, no bold). Skip repeating " +
        "running headers/footers and bare page numbers. No commentary, no preface, no notes.";

    private readonly ConcurrentDictionary<Guid, RagJob> _jobs = new();

    public RagJobDto? GetJob(Guid jobId) => _jobs.TryGetValue(jobId, out RagJob? job) ? job.ToDto() : null;

    public async Task<RagJobDto> StartAsync(RagCreateInput input, string? startedBy, CancellationToken ct = default)
    {
        if (input.Files.Count == 0) throw new AppException("Choose at least one PDF or image file.");
        if (input.Files.Count > MaxFiles) throw new AppException($"At most {MaxFiles} files per run.");

        // Resolve everything the background work depends on now, so a bad request is a 4xx here
        // rather than a job that fails a minute later.
        using IServiceScope scope = scopes.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IAiClient ai = scope.ServiceProvider.GetRequiredService<IAiClient>();
        IEmbeddingService embeddings = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();

        ModelPricing pricing = await db.ModelPricing.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ModelName == input.Model && m.IsActive, ct)
            ?? throw new AppException($"Model \"{input.Model}\" is not available (no active pricing).");

        if (!ai.IsProviderReady(pricing.Provider))
        {
            throw new AppException($"The {pricing.Provider} provider has no API key configured, so \"{input.Model}\" cannot be used.");
        }

        if (!embeddings.IsConfigured)
        {
            throw new AppException("The OpenAI API key is not configured — it is needed to generate embeddings for the RAG.");
        }

        var files = new List<PreparedFile>();
        int totalPages = 0;

        foreach (RagUploadFile upload in input.Files)
        {
            PreparedFile prepared = PrepareFile(upload, pricing.Provider, input.PagesPerBatch);
            totalPages += prepared.Pages;
            files.Add(prepared);
        }

        if (totalPages > MaxTotalPages)
        {
            throw new AppException($"{totalPages} pages in total is more than the {MaxTotalPages}-page limit per run — split it into several runs.");
        }

        DataSource source = await ResolveSourceAsync(db, input, startedBy, ct);

        var job = new RagJob
        {
            JobId = Guid.NewGuid(),
            SourceId = source.SourceId,
            SourceName = source.SourceName,
            Model = pricing.ModelName,
            Provider = pricing.Provider,
            FilesTotal = files.Count,
            PagesTotal = totalPages,
            StartedAt = DateTime.Now,
            StartedBy = startedBy,
            UsdToThbRate = pricing.UsdToThbRate,
        };
        job.Phase = "Queued";

        _jobs[job.JobId] = job;
        TrimOldJobs();

        _ = Task.Run(() => RunAsync(job, files, input.PagesPerBatch, startedBy));

        return job.ToDto();
    }

    private static async Task<DataSource> ResolveSourceAsync(
        AppDbContext db, RagCreateInput input, string? startedBy, CancellationToken ct)
    {
        if (input.SourceId is { } existingId)
        {
            DataSource existing = await db.DataSources.FirstOrDefaultAsync(s => s.SourceId == existingId, ct)
                ?? throw AppException.NotFound("RAG not found");

            if (existing.SourceType != DataSourceTypes.Rag)
            {
                throw new AppException($"\"{existing.SourceName}\" is not a RAG knowledge base.");
            }

            return existing;
        }

        string name = input.SourceName?.Trim() ?? string.Empty;
        if (name.Length == 0) throw new AppException("Give the new RAG a name, or choose an existing RAG to add to.");

        if (await db.DataSources.AnyAsync(s => s.SourceName == name, ct))
        {
            throw AppException.Conflict($"A data source named \"{name}\" already exists — pick it from the list to add to it.");
        }

        var source = new DataSource
        {
            SourceName = name,
            SourceType = DataSourceTypes.Rag,
            Description = input.Description?.Trim(),
            ConfigJson = DataSourceConfig.Serialize(new Dictionary<string, string>()),
            IsActive = true,
            CreatedAt = DateTime.Now,
            CreatedBy = startedBy,
        };

        db.DataSources.Add(source);
        await db.SaveChangesAsync(ct);
        return source;
    }

    // ------------------------------------------------------------------ file preparation

    private sealed record PreparedFile(
        string FileName, string ContentType, bool IsPdf, int Pages, IReadOnlyList<PageBatch> Batches);

    private sealed record PageBatch(int FirstPage, int LastPage, byte[] Content, string ContentType, bool IsPdf);

    private static PreparedFile PrepareFile(RagUploadFile upload, string provider, int pagesPerBatch)
    {
        string name = Path.GetFileName(upload.FileName);
        string ext = Path.GetExtension(name).ToLowerInvariant();
        byte[] bytes = upload.Content;

        if (bytes.Length == 0) throw new AppException($"\"{name}\" is empty.");

        if (ext == ".pdf")
        {
            if (!(bytes.Length > 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46))
            {
                throw new AppException($"\"{name}\" is not a real PDF file.");
            }

            if (provider == AiProviders.Qwen)
            {
                throw new AppException(
                    "Qwen models cannot read PDF files directly — choose a Claude, Gemini or OpenAI model " +
                    "for PDFs (Qwen works for image files).");
            }

            try
            {
                using var input = new MemoryStream(bytes);
                using PdfDocument source = PdfReader.Open(input, PdfDocumentOpenMode.Import);
                int count = source.PageCount;
                var batches = new List<PageBatch>();

                for (int first = 0; first < count; first += pagesPerBatch)
                {
                    int last = Math.Min(first + pagesPerBatch, count) - 1;
                    using var part = new PdfDocument();
                    for (int i = first; i <= last; i++) part.AddPage(source.Pages[i]);

                    using var output = new MemoryStream();
                    part.Save(output, false);
                    batches.Add(new PageBatch(first + 1, last + 1, output.ToArray(), "application/pdf", true));
                }

                return new PreparedFile(name, "application/pdf", true, count, batches);
            }
            catch (Exception ex) when (ex is not AppException)
            {
                throw new AppException($"\"{name}\" could not be read as a PDF ({ex.Message}). Password-protected files must be unlocked first.");
            }
        }

        string? imageType = ext switch
        {
            ".png" when bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 => "image/png",
            ".jpg" or ".jpeg" when bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 => "image/jpeg",
            ".webp" when bytes.Length > 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[8] == 0x57 => "image/webp",
            ".gif" when bytes.Length > 4 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 => "image/gif",
            _ => null,
        };

        if (imageType is null)
        {
            throw new AppException($"\"{name}\" is not a supported file — use PDF, PNG, JPG, WEBP or GIF.");
        }

        return new PreparedFile(name, imageType, false, 1, [new PageBatch(1, 1, bytes, imageType, false)]);
    }

    // ------------------------------------------------------------------ background work

    private async Task RunAsync(RagJob job, IReadOnlyList<PreparedFile> files, int pagesPerBatch, string? startedBy)
    {
        try
        {
            using IServiceScope scope = scopes.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            IAiClient ai = scope.ServiceProvider.GetRequiredService<IAiClient>();
            IEmbeddingService embeddings = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();
            IAuditService audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
            ICostCalculator costs = scope.ServiceProvider.GetRequiredService<ICostCalculator>();

            job.State = "Running";

            db.RagRuns.Add(new RagRun
            {
                RunId = job.JobId,
                SourceId = job.SourceId,
                SourceName = job.SourceName,
                Model = job.Model,
                Provider = job.Provider,
                State = "Running",
                FilesTotal = job.FilesTotal,
                PagesTotal = job.PagesTotal,
                StartedBy = job.StartedBy,
                StartedAt = job.StartedAt,
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            foreach (PreparedFile file in files)
            {
                var pageTexts = new List<RagPageText>();

                foreach (PageBatch batch in file.Batches)
                {
                    job.Phase = $"Reading \"{file.FileName}\" — pages {batch.FirstPage}-{batch.LastPage} of {file.Pages}";

                    try
                    {
                        pageTexts.AddRange(await OcrBatchAsync(ai, job, file, batch));
                    }
                    catch (Exception ex) when (ex is AiBillingException or AiUnavailableException)
                    {
                        // The same failure would hit every later batch — stop instead of burning calls.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "OCR failed for {File} pages {First}-{Last}", file.FileName, batch.FirstPage, batch.LastPage);
                        job.AddWarning($"{file.FileName}: pages {batch.FirstPage}-{batch.LastPage} could not be read ({ex.Message})");
                    }

                    job.PagesDone += batch.LastPage - batch.FirstPage + 1;
                    await RefreshCostAsync(job, costs, embeddings);
                }

                List<RagChunkPiece> pieces = RagChunker.Chunk(pageTexts);

                if (pieces.Count == 0)
                {
                    job.AddWarning($"{file.FileName}: no text was found, nothing was added.");
                    job.FilesDone++;
                    continue;
                }

                job.Phase = $"Embedding {pieces.Count} chunk(s) from \"{file.FileName}\"";
                await SaveChunksAsync(db, embeddings, job, file.FileName, pieces);
                await RefreshCostAsync(job, costs, embeddings);
                job.FilesDone++;
            }

            job.Phase = "Done";
            job.State = job.ChunksCreated > 0 ? "Completed" : "Failed";
            if (job.ChunksCreated == 0 && job.Error is null) job.Error = "No text could be extracted from the files.";
            job.FinishedAt = DateTime.Now;

            await PersistRunAsync(job);

            RagJobDto done = job.ToDto();
            await audit.LogAsync(AuditCategories.Admin, AuditActions.RagCreated, null, startedBy,
                $"Create RAG \"{job.SourceName}\" (#{job.SourceId}): {job.ChunksCreated} chunk(s) from " +
                $"{job.FilesTotal} file(s) / {job.PagesTotal} page(s) using {job.Model} " +
                $"({done.InputTokens:N0} in / {done.OutputTokens:N0} out / {done.CacheTokens:N0} cache / " +
                $"{done.EmbeddingTokens:N0} embedding tokens) - cost ${done.TotalCostUsd:0.0000} " +
                $"({done.TotalCostThb:0.00} THB)",
                isSuccess: job.State == "Completed");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Create RAG job {JobId} failed", job.JobId);
            job.Error = ex is AiBillingException
                ? "The AI account for this model is out of credit or over quota — choose another model or ask an administrator to check billing."
                : ex.Message;
            job.State = "Failed";
            job.Phase = "Failed";
            job.FinishedAt = DateTime.Now;

            try
            {
                await PersistRunAsync(job);
            }
            catch (Exception persistEx)
            {
                logger.LogError(persistEx, "Could not record the failed Create RAG run {JobId}", job.JobId);
            }
        }
    }

    /// <summary>Recomputes the running cost from the token counts so far - OCR at the model's price, embeddings at OpenAI's.</summary>
    private static async Task RefreshCostAsync(RagJob job, ICostCalculator costs, IEmbeddingService embeddings)
    {
        TokenCost ocr = await costs.CalculateAsync(
            job.Model, (int)Math.Min(job.InputTokens, int.MaxValue), (int)Math.Min(job.OutputTokens, int.MaxValue),
            (int)Math.Min(job.CacheWriteTokens, int.MaxValue), (int)Math.Min(job.CacheReadTokens, int.MaxValue));

        decimal embeddingUsd = job.EmbeddingTokens / 1_000_000m * embeddings.UsdPerMTok;
        job.SetCosts(ocr.TotalUsd, embeddingUsd);
    }

    private async Task PersistRunAsync(RagJob job)
    {
        using IServiceScope scope = scopes.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        RagJobDto dto = job.ToDto();
        string? error = dto.Error is { Length: > 1000 } ? dto.Error[..1000] : dto.Error;

        await db.RagRuns.Where(r => r.RunId == job.JobId).ExecuteUpdateAsync(u => u
            .SetProperty(r => r.State, dto.State)
            .SetProperty(r => r.ChunksCreated, dto.ChunksCreated)
            .SetProperty(r => r.InputTokens, dto.InputTokens)
            .SetProperty(r => r.OutputTokens, dto.OutputTokens)
            .SetProperty(r => r.CacheTokens, dto.CacheTokens)
            .SetProperty(r => r.EmbeddingTokens, dto.EmbeddingTokens)
            .SetProperty(r => r.OcrCostUsd, dto.OcrCostUsd)
            .SetProperty(r => r.EmbeddingCostUsd, dto.EmbeddingCostUsd)
            .SetProperty(r => r.TotalCostUsd, dto.TotalCostUsd)
            .SetProperty(r => r.TotalCostThb, dto.TotalCostThb)
            .SetProperty(r => r.UsdToThbRate, dto.UsdToThbRate)
            .SetProperty(r => r.Error, error)
            .SetProperty(r => r.FinishedAt, dto.FinishedAt));
    }

    private async Task<List<RagPageText>> OcrBatchAsync(IAiClient ai, RagJob job, PreparedFile file, PageBatch batch)
    {
        int pageCount = batch.LastPage - batch.FirstPage + 1;

        string instruction = batch.IsPdf
            ? $"The attached PDF holds {pageCount} page(s) of the document \"{file.FileName}\" — document pages " +
              $"{batch.FirstPage} to {batch.LastPage}. Before the text of each page, write a line exactly " +
              $"like {PageMarkerPrefix}{batch.FirstPage}=====  using that page's document number " +
              $"({batch.FirstPage}, then +1 for each following page). Output nothing else."
            : $"The attached image is page {batch.FirstPage} of \"{file.FileName}\". Begin with the line " +
              $"{PageMarkerPrefix}{batch.FirstPage}===== and then the transcription. Output nothing else.";

        var turn = new ChatTurn("user", instruction,
        [
            new TurnAttachment(
                batch.IsPdf ? $"{Path.GetFileNameWithoutExtension(file.FileName)}-{batch.FirstPage}-{batch.LastPage}.pdf" : file.FileName,
                batch.ContentType,
                batch.IsPdf ? FileKinds.Pdf : FileKinds.Image,
                batch.Content,
                null),
        ]);

        AiReply reply = await ai.CompleteAsync(OcrSystemPrompt, [turn], job.Model, job.Provider);

        job.AddUsage(reply.InputTokens, reply.OutputTokens, reply.CacheWriteTokens, reply.CacheReadTokens);

        if (reply.Refused)
        {
            throw new InvalidOperationException(reply.RefusalDetail ?? "the model declined to read this content");
        }

        return ParsePages(reply.Text, batch.FirstPage, batch.LastPage);
    }

    /// <summary>Splits the model's answer on its page markers; falls back to one block if it ignored them.</summary>
    internal static List<RagPageText> ParsePages(string text, int firstPage, int lastPage)
    {
        text = AddedDescription.Replace(text, string.Empty);
        MatchCollection markers = PageMarker.Matches(text);
        var pages = new List<RagPageText>();

        if (markers.Count == 0)
        {
            string whole = text.Trim();
            if (whole.Length > 0) pages.Add(new RagPageText(firstPage, whole));
            return pages;
        }

        for (int i = 0; i < markers.Count; i++)
        {
            int start = markers[i].Index + markers[i].Length;
            int end = i + 1 < markers.Count ? markers[i + 1].Index : text.Length;
            string body = text[start..end].Trim();

            if (body.Length == 0) continue;

            int page = int.TryParse(markers[i].Groups[1].Value, out int parsed) ? parsed : firstPage + i;
            pages.Add(new RagPageText(Math.Clamp(page, firstPage, lastPage), body));
        }

        return pages;
    }

    private static async Task SaveChunksAsync(
        AppDbContext db, IEmbeddingService embeddings, RagJob job, string fileName, List<RagChunkPiece> pieces)
    {
        const int embedBatch = 50;
        var rows = new List<RagChunk>(pieces.Count);
        string keyPrefix = $"rag{job.SourceId}-{Path.GetFileNameWithoutExtension(fileName)}";
        if (keyPrefix.Length > 80) keyPrefix = keyPrefix[..80];

        for (int offset = 0; offset < pieces.Count; offset += embedBatch)
        {
            List<RagChunkPiece> batch = pieces.Skip(offset).Take(embedBatch).ToList();
            (IReadOnlyList<float[]> vectors, int embeddingTokens) =
                await embeddings.EmbedWithUsageAsync(batch.Select(p => p.Text).ToList());
            job.AddEmbeddingTokens(embeddingTokens);

            if (vectors.Count != batch.Count)
            {
                throw new InvalidOperationException("The embeddings call returned a different number of vectors than chunks.");
            }

            for (int i = 0; i < batch.Count; i++)
            {
                RagChunkPiece piece = batch[i];
                rows.Add(new RagChunk
                {
                    SourceId = job.SourceId,
                    ChunkKey = $"{keyPrefix}-{offset + i + 1:00000}",
                    ChunkText = piece.Text,
                    SourceFile = fileName.Length > 260 ? fileName[..260] : fileName,
                    PageStart = piece.PageStart,
                    PageEnd = piece.PageEnd,
                    TokenCountEstimate = (int)Math.Ceiling(piece.Text.Length / 3.0),
                    Embedding = RagEmbedding.Pack(vectors[i]),
                    EmbeddingModel = embeddings.Model,
                    CreatedAt = DateTime.Now,
                });
            }
        }

        // Re-running the same file replaces its earlier chunks instead of duplicating them.
        await db.RagChunks.Where(c => c.SourceId == job.SourceId && c.SourceFile == fileName).ExecuteDeleteAsync();
        db.RagChunks.AddRange(rows);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        job.ChunksCreated += rows.Count;
    }

    private void TrimOldJobs()
    {
        if (_jobs.Count <= 50) return;

        foreach (Guid id in _jobs.Values
                     .Where(j => j.FinishedAt is not null)
                     .OrderBy(j => j.FinishedAt)
                     .Take(_jobs.Count - 50)
                     .Select(j => j.JobId))
        {
            _jobs.TryRemove(id, out _);
        }
    }

    private sealed class RagJob
    {
        private readonly object _lock = new();
        private readonly List<string> _warnings = [];

        public Guid JobId { get; init; }
        public int SourceId { get; init; }
        public string SourceName { get; init; } = string.Empty;
        public string Model { get; init; } = string.Empty;
        public string Provider { get; init; } = string.Empty;
        public int FilesTotal { get; init; }
        public int PagesTotal { get; init; }
        public DateTime StartedAt { get; init; }

        public volatile string State = "Queued";
        public volatile string Phase = "";
        public volatile string? Error;
        public int FilesDone;
        public int PagesDone;
        public int ChunksCreated;
        public long InputTokens;
        public long OutputTokens;
        public long CacheWriteTokens;
        public long CacheReadTokens;
        public long EmbeddingTokens;
        public decimal OcrCostUsd;
        public decimal EmbeddingCostUsd;
        public DateTime? FinishedAt;
        public string? StartedBy { get; init; }
        public decimal UsdToThbRate { get; init; }

        public void AddWarning(string warning)
        {
            lock (_lock) _warnings.Add(warning);
        }

        public void AddUsage(long input, long output, long cacheWrite, long cacheRead)
        {
            lock (_lock)
            {
                InputTokens += input;
                OutputTokens += output;
                CacheWriteTokens += cacheWrite;
                CacheReadTokens += cacheRead;
            }
        }

        public void AddEmbeddingTokens(long tokens)
        {
            lock (_lock) EmbeddingTokens += tokens;
        }

        public void SetCosts(decimal ocrUsd, decimal embeddingUsd)
        {
            lock (_lock)
            {
                OcrCostUsd = ocrUsd;
                EmbeddingCostUsd = embeddingUsd;
            }
        }

        public RagJobDto ToDto()
        {
            lock (_lock)
            {
                decimal totalUsd = OcrCostUsd + EmbeddingCostUsd;
                return new RagJobDto(
                    JobId, State, Phase, FilesTotal, FilesDone, PagesTotal, Math.Min(PagesDone, PagesTotal),
                    ChunksCreated, InputTokens, OutputTokens, CacheWriteTokens + CacheReadTokens, EmbeddingTokens,
                    Math.Round(OcrCostUsd, 8), Math.Round(EmbeddingCostUsd, 8), Math.Round(totalUsd, 8),
                    Math.Round(totalUsd * UsdToThbRate, 6), UsdToThbRate,
                    SourceId, SourceName, Model,
                    _warnings.ToList(), Error, StartedAt, FinishedAt);
            }
        }
    }
}
