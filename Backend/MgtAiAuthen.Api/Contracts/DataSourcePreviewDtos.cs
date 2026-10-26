using System.ComponentModel.DataAnnotations;

namespace MgtAiAuthen.Api.Contracts;

/// <summary>Run one read-only query against a source and look at the first rows - for testing the queries an admin writes.</summary>
public record DataSourcePreviewRequest
{
    [Required(ErrorMessage = "Query is required")]
    [StringLength(20000)]
    public string Query { get; init; } = string.Empty;

    /// <summary>How many rows to bring back (1-200, default 30).</summary>
    [Range(1, 200)]
    public int? MaxRows { get; init; }
}

public record DataSourcePreviewResult(bool Success, string Message, string? Text, int? Rows, bool Truncated);
