using System.ComponentModel.DataAnnotations;

namespace MgtAiAuthen.Api.Contracts;

public record DataSourceSubCategoryDto(
    int SubCategoryId, int CategoryId, string SubCategoryName, string? Description, bool IsActive, int SourceCount);

public record DataSourceCategoryDto(
    int CategoryId, string CategoryName, string? Description, bool IsActive, int SourceCount,
    IReadOnlyList<DataSourceSubCategoryDto> SubCategories);

public record DataSourceCategoryRequest
{
    [Required(ErrorMessage = "Category name is required")]
    [StringLength(150)]
    public string Name { get; init; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; init; }

    public bool IsActive { get; init; } = true;
}

public record DataSourceSubCategoryRequest
{
    /// <summary>Required when creating; ignored on update (a sub category is not moved between categories).</summary>
    public int CategoryId { get; init; }

    [Required(ErrorMessage = "Sub category name is required")]
    [StringLength(150)]
    public string Name { get; init; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; init; }

    public bool IsActive { get; init; } = true;
}
