using MgtAiAuthen.Api.Contracts;
using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Security;
using MgtAiAuthen.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MgtAiAuthen.Api.Controllers;

/// <summary>Self-service library of reusable prompt snippets — any signed-in employee.</summary>
[ApiController]
[Route("api/skills")]
[Authorize]
public class SkillsController(ISkillService skills) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<SkillDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SkillDto>>> GetAll(CancellationToken ct)
        => Ok(await skills.GetAllAsync(User.GetUserId(), User.IsInRole(UserRoles.Admin), ct));

    [HttpPost]
    [ProducesResponseType(typeof(SkillDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<SkillDto>> Create(SkillUpsertRequest request, CancellationToken ct)
    {
        SkillDto created = await skills.CreateAsync(User.GetUserId(), request, ct);
        return CreatedAtAction(nameof(GetAll), new { id = created.SkillId }, created);
    }

    [HttpPut("{id:int}")]
    [ProducesResponseType(typeof(SkillDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SkillDto>> Update(int id, SkillUpsertRequest request, CancellationToken ct)
        => Ok(await skills.UpdateAsync(id, User.GetUserId(), User.IsInRole(UserRoles.Admin), request, ct));

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await skills.DeleteAsync(id, User.GetUserId(), User.IsInRole(UserRoles.Admin), ct);
        return NoContent();
    }
}
