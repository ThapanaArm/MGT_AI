using MgtAiAuthen.Api.Contracts;
using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Security;
using MgtAiAuthen.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MgtAiAuthen.Api.Controllers;

/// <summary>
/// Self-service workspaces — any signed-in employee creates and owns these, unlike the
/// admin-only data source registry. Sharing widens who may *use* a project (start a chat under
/// it, see its files); only the owner or an Admin may edit it.
/// </summary>
[ApiController]
[Route("api/projects")]
[Authorize]
public class ProjectsController(IProjectService projects) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ProjectDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ProjectDto>>> GetAll(CancellationToken ct)
        => Ok(await projects.GetAllAsync(User.GetUserId(), User.IsInRole(UserRoles.Admin), ct));

    [HttpPost]
    [ProducesResponseType(typeof(ProjectDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<ProjectDto>> Create(ProjectUpsertRequest request, CancellationToken ct)
    {
        ProjectDto created = await projects.CreateAsync(User.GetUserId(), request, ct);
        return CreatedAtAction(nameof(GetAll), new { id = created.ProjectId }, created);
    }

    [HttpPut("{id:int}")]
    [ProducesResponseType(typeof(ProjectDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ProjectDto>> Update(int id, ProjectUpsertRequest request, CancellationToken ct)
        => Ok(await projects.UpdateAsync(id, User.GetUserId(), User.IsInRole(UserRoles.Admin), request, ct));

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await projects.DeleteAsync(id, User.GetUserId(), User.IsInRole(UserRoles.Admin), ct);
        return NoContent();
    }

    [HttpGet("{id:int}/files")]
    [ProducesResponseType(typeof(IReadOnlyList<ProjectFileDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ProjectFileDto>>> GetFiles(int id, CancellationToken ct)
        => Ok(await projects.GetFilesAsync(id, User.GetUserId(), User.IsInRole(UserRoles.Admin), ct));

    [HttpPost("{id:int}/files")]
    [RequestSizeLimit(64L * 1024 * 1024)]
    [ProducesResponseType(typeof(ProjectFileDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<ProjectFileDto>> UploadFile(
        int id, [FromForm] IFormFile file, CancellationToken ct)
    {
        ProjectFileDto created = await projects.UploadFileAsync(
            id, User.GetUserId(), User.IsInRole(UserRoles.Admin), file, ct);
        return CreatedAtAction(nameof(GetFiles), new { id }, created);
    }

    [HttpGet("files/{fileId:long}")]
    public async Task<IActionResult> DownloadFile(long fileId, CancellationToken ct)
    {
        (ProjectFile meta, byte[] content) = await projects.DownloadFileAsync(
            fileId, User.GetUserId(), User.IsInRole(UserRoles.Admin), ct);

        return File(content, meta.ContentType, meta.FileName);
    }

    [HttpDelete("files/{fileId:long}")]
    public async Task<IActionResult> DeleteFile(long fileId, CancellationToken ct)
    {
        await projects.DeleteFileAsync(fileId, User.GetUserId(), User.IsInRole(UserRoles.Admin), ct);
        return NoContent();
    }
}
