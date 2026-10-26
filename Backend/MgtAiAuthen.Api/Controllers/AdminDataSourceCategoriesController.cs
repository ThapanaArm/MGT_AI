using MgtAiAuthen.Api.Contracts;
using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Infrastructure;
using MgtAiAuthen.Api.Security;
using MgtAiAuthen.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MgtAiAuthen.Api.Controllers;

/// <summary>
/// The Category / Sub Category tree that groups the entries of the Data Source Registry (any source
/// type). Which category a source sits in is set on the source itself (Data Source Registry form).
/// </summary>
[ApiController]
[Route("api/admin/data-source-categories")]
[Authorize(Roles = UserRoles.Admin)]
public class AdminDataSourceCategoriesController(AppDbContext db, IAuditService audit) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DataSourceCategoryDto>>> GetAll(CancellationToken ct)
    {
        List<DataSourceCategory> categories = await db.DataSourceCategories.AsNoTracking()
            .Include(c => c.SubCategories)
            .OrderBy(c => c.CategoryName)
            .ToListAsync(ct);

        var assignments = (await db.DataSources.AsNoTracking()
                .Select(s => new { s.CategoryId, s.SubCategoryId })
                .ToListAsync(ct))
            .ToList();

        return Ok(categories.Select(c => new DataSourceCategoryDto(
            c.CategoryId, c.CategoryName, c.Description, c.IsActive,
            assignments.Count(a => a.CategoryId == c.CategoryId),
            c.SubCategories.OrderBy(s => s.SubCategoryName).Select(s => new DataSourceSubCategoryDto(
                s.SubCategoryId, s.CategoryId, s.SubCategoryName, s.Description, s.IsActive,
                assignments.Count(a => a.SubCategoryId == s.SubCategoryId))).ToList())).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<DataSourceCategoryDto>> Create(DataSourceCategoryRequest request, CancellationToken ct)
    {
        string name = request.Name.Trim();
        if (await db.DataSourceCategories.AnyAsync(c => c.CategoryName == name, ct))
        {
            throw AppException.Conflict($"A category named \"{name}\" already exists");
        }

        var entity = new DataSourceCategory
        {
            CategoryName = name,
            Description = request.Description?.Trim(),
            IsActive = request.IsActive,
            CreatedBy = User.GetUsername(),
        };

        db.DataSourceCategories.Add(entity);
        await db.SaveChangesAsync(ct);
        await LogAsync($"Created data source category #{entity.CategoryId} \"{name}\"", ct);

        return Ok(new DataSourceCategoryDto(entity.CategoryId, entity.CategoryName, entity.Description, entity.IsActive, 0, []));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, DataSourceCategoryRequest request, CancellationToken ct)
    {
        DataSourceCategory entity = await db.DataSourceCategories.FirstOrDefaultAsync(c => c.CategoryId == id, ct)
            ?? throw AppException.NotFound("Category not found");

        string name = request.Name.Trim();
        if (await db.DataSourceCategories.AnyAsync(c => c.CategoryName == name && c.CategoryId != id, ct))
        {
            throw AppException.Conflict($"A category named \"{name}\" already exists");
        }

        entity.CategoryName = name;
        entity.Description = request.Description?.Trim();
        entity.IsActive = request.IsActive;
        entity.UpdatedAt = DateTime.Now;
        entity.UpdatedBy = User.GetUsername();

        await db.SaveChangesAsync(ct);
        await LogAsync($"Updated data source category #{id} \"{name}\"", ct);
        return NoContent();
    }

    /// <summary>
    /// Deleting a category takes its sub categories with it, and any data source filed under either
    /// goes back to "Uncategorised" - the data sources themselves are never touched.
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        DataSourceCategory entity = await db.DataSourceCategories.AsNoTracking().FirstOrDefaultAsync(c => c.CategoryId == id, ct)
            ?? throw AppException.NotFound("Category not found");

        int unfiled = 0;
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            unfiled = await db.DataSources
                .Where(s => s.CategoryId == id)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.CategoryId, (int?)null)
                                          .SetProperty(s => s.SubCategoryId, (int?)null), ct);
            await db.DataSourceSubCategories.Where(s => s.CategoryId == id).ExecuteDeleteAsync(ct);
            await db.DataSourceCategories.Where(c => c.CategoryId == id).ExecuteDeleteAsync(ct);

            await tx.CommitAsync(ct);
        });

        await LogAsync($"Deleted data source category #{id} \"{entity.CategoryName}\" (and its sub categories; {unfiled} data source(s) set to uncategorised)", ct);
        return NoContent();
    }

    [HttpPost("sub")]
    public async Task<ActionResult<DataSourceSubCategoryDto>> CreateSub(DataSourceSubCategoryRequest request, CancellationToken ct)
    {
        if (!await db.DataSourceCategories.AnyAsync(c => c.CategoryId == request.CategoryId, ct))
        {
            throw AppException.NotFound("Category not found");
        }

        string name = request.Name.Trim();
        if (await db.DataSourceSubCategories.AnyAsync(s => s.CategoryId == request.CategoryId && s.SubCategoryName == name, ct))
        {
            throw AppException.Conflict($"This category already has a sub category named \"{name}\"");
        }

        var entity = new DataSourceSubCategory
        {
            CategoryId = request.CategoryId,
            SubCategoryName = name,
            Description = request.Description?.Trim(),
            IsActive = request.IsActive,
            CreatedBy = User.GetUsername(),
        };

        db.DataSourceSubCategories.Add(entity);
        await db.SaveChangesAsync(ct);
        await LogAsync($"Created data source sub category #{entity.SubCategoryId} \"{name}\" under category #{request.CategoryId}", ct);

        return Ok(new DataSourceSubCategoryDto(entity.SubCategoryId, entity.CategoryId, name, entity.Description, entity.IsActive, 0));
    }

    [HttpPut("sub/{id:int}")]
    public async Task<IActionResult> UpdateSub(int id, DataSourceSubCategoryRequest request, CancellationToken ct)
    {
        DataSourceSubCategory entity = await db.DataSourceSubCategories.FirstOrDefaultAsync(s => s.SubCategoryId == id, ct)
            ?? throw AppException.NotFound("Sub category not found");

        string name = request.Name.Trim();
        if (await db.DataSourceSubCategories.AnyAsync(
                s => s.CategoryId == entity.CategoryId && s.SubCategoryName == name && s.SubCategoryId != id, ct))
        {
            throw AppException.Conflict($"This category already has a sub category named \"{name}\"");
        }

        entity.SubCategoryName = name;
        entity.Description = request.Description?.Trim();
        entity.IsActive = request.IsActive;
        entity.UpdatedAt = DateTime.Now;
        entity.UpdatedBy = User.GetUsername();

        await db.SaveChangesAsync(ct);
        await LogAsync($"Updated data source sub category #{id} \"{name}\"", ct);
        return NoContent();
    }

    /// <summary>Data sources filed here keep their category but lose the sub category.</summary>
    [HttpDelete("sub/{id:int}")]
    public async Task<IActionResult> DeleteSub(int id, CancellationToken ct)
    {
        DataSourceSubCategory entity = await db.DataSourceSubCategories.FirstOrDefaultAsync(s => s.SubCategoryId == id, ct)
            ?? throw AppException.NotFound("Sub category not found");

        await db.DataSources
            .Where(s => s.SubCategoryId == id)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.SubCategoryId, (int?)null), ct);

        db.DataSourceSubCategories.Remove(entity);
        await db.SaveChangesAsync(ct);
        await LogAsync($"Deleted data source sub category #{id} \"{entity.SubCategoryName}\"", ct);
        return NoContent();
    }

    private Task LogAsync(string detail, CancellationToken ct)
        => audit.LogAsync(AuditCategories.Admin, AuditActions.DataSourceCategoryChanged,
            User.GetUserId(), User.GetUsername(), detail, isSuccess: true, ct);
}
