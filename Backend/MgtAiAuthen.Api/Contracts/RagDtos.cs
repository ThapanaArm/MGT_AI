using System.ComponentModel.DataAnnotations;

namespace MgtAiAuthen.Api.Contracts;

public record RagSourceUpdateRequest
{
    [Required(ErrorMessage = "RAG name is required")]
    [StringLength(150)]
    public string Name { get; init; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; init; }

    public bool IsActive { get; init; } = true;
}

/// <summary>One RAG knowledge base (a Rag-type data source) - its category is set in the Data Source Registry.</summary>
public record RagSourceDto(
    int SourceId, string SourceName, string? Description, bool IsActive,
    int ChunkCount, int FileCount, DateTime CreatedAt);

/// <summary>Multipart form for "Create RAG" — files are the PDFs / images to OCR.</summary>
public class RagCreateForm
{
    /// <summary>Append to this existing RAG; when empty a new one named <see cref="SourceName"/> is created.</summary>
    public int? SourceId { get; set; }

    [StringLength(150)]
    public string? SourceName { get; set; }

    [StringLength(500)]
    public string? Description { get; set; }

    /// <summary>The vision-capable model that reads (OCRs) the pages — must have active pricing.</summary>
    [Required(ErrorMessage = "Choose the model that will read the files")]
    public string Model { get; set; } = string.Empty;

    /// <summary>How many PDF pages go to the model in one call (1-10).</summary>
    [Range(1, 10)]
    public int PagesPerBatch { get; set; } = 3;

    public List<IFormFile> Files { get; set; } = [];
}

public record RagJobDto(
    Guid JobId, string State, string Phase,
    int FilesTotal, int FilesDone, int PagesTotal, int PagesDone, int ChunksCreated,
    long InputTokens, long OutputTokens, long CacheTokens, long EmbeddingTokens,
    decimal OcrCostUsd, decimal EmbeddingCostUsd, decimal TotalCostUsd, decimal TotalCostThb, decimal UsdToThbRate,
    int SourceId, string SourceName, string Model,
    IReadOnlyList<string> Warnings, string? Error,
    DateTime StartedAt, DateTime? FinishedAt);

/// <summary>One finished (or interrupted) Create RAG run, with what it cost - kept after the RAG itself is deleted.</summary>
public record RagRunDto(
    Guid RunId, int? SourceId, string SourceName, string Model, string Provider, string State,
    int FilesTotal, int PagesTotal, int ChunksCreated,
    long InputTokens, long OutputTokens, long CacheTokens, long EmbeddingTokens,
    decimal OcrCostUsd, decimal EmbeddingCostUsd, decimal TotalCostUsd, decimal TotalCostThb,
    string? Error, string? StartedBy, DateTime StartedAt, DateTime? FinishedAt);
