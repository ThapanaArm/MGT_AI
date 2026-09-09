using System.Text.RegularExpressions;
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
/// Manage the question-screening rules applied before sending to the AI (dbo.PolicyRules) — Admin only.
/// </summary>
[ApiController]
[Route("api/admin/policy-rules")]
[Authorize(Roles = UserRoles.Admin)]
public class AdminPolicyController(
    AppDbContext db,
    IPolicyService policy,
    IAuditService audit) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PolicyRuleDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PolicyRuleDto>>> GetAll(CancellationToken ct)
        // ต้อง project แบบ inline — EF แปล static method ที่เรียกใน Select เป็น SQL ไม่ได้
        => Ok(await db.PolicyRules
            .AsNoTracking()
            .OrderBy(r => r.RuleId)
            .Select(r => new PolicyRuleDto(
                r.RuleId, r.RuleName, r.Description, r.MatchType, r.Pattern,
                r.ActionType, r.Severity, r.IsActive, r.CreatedAt, r.CreatedBy, r.UpdatedAt, r.UpdatedBy))
            .ToListAsync(ct));

    [HttpPost]
    [ProducesResponseType(typeof(PolicyRuleDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<PolicyRuleDto>> Create(
        PolicyRuleUpsertRequest request, CancellationToken ct)
    {
        ValidatePattern(request);

        string name = request.RuleName.Trim();
        if (await db.PolicyRules.AnyAsync(r => r.RuleName == name, ct))
        {
            throw AppException.Conflict($"A rule named \"{name}\" already exists");
        }

        var rule = new PolicyRule
        {
            RuleName = name,
            Description = request.Description?.Trim(),
            MatchType = request.MatchType,
            Pattern = request.Pattern,
            ActionType = request.ActionType,
            Severity = request.Severity,
            IsActive = request.IsActive,
            CreatedAt = DateTime.Now,
            CreatedBy = User.GetUsername(),
        };

        db.PolicyRules.Add(rule);
        await db.SaveChangesAsync(ct);
        policy.InvalidateCache();

        await audit.LogAsync(AuditCategories.Policy, AuditActions.PolicyCreated,
            User.GetUserId(), User.GetUsername(),
            $"Created rule #{rule.RuleId} \"{rule.RuleName}\" ({rule.MatchType}/{rule.ActionType}/{rule.Severity})",
            isSuccess: true, ct);

        return CreatedAtAction(nameof(GetAll), new { id = rule.RuleId }, ToDto(rule));
    }

    [HttpPut("{id:int}")]
    [ProducesResponseType(typeof(PolicyRuleDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<PolicyRuleDto>> Update(
        int id, PolicyRuleUpsertRequest request, CancellationToken ct)
    {
        ValidatePattern(request);

        PolicyRule rule = await db.PolicyRules.FirstOrDefaultAsync(r => r.RuleId == id, ct)
            ?? throw AppException.NotFound("Rule not found");

        string name = request.RuleName.Trim();
        if (await db.PolicyRules.AnyAsync(r => r.RuleName == name && r.RuleId != id, ct))
        {
            throw AppException.Conflict($"A rule named \"{name}\" already exists");
        }

        rule.RuleName = name;
        rule.Description = request.Description?.Trim();
        rule.MatchType = request.MatchType;
        rule.Pattern = request.Pattern;
        rule.ActionType = request.ActionType;
        rule.Severity = request.Severity;
        rule.IsActive = request.IsActive;
        rule.UpdatedAt = DateTime.Now;
        rule.UpdatedBy = User.GetUsername();

        await db.SaveChangesAsync(ct);
        policy.InvalidateCache();

        await audit.LogAsync(AuditCategories.Policy, AuditActions.PolicyUpdated,
            User.GetUserId(), User.GetUsername(),
            $"Updated rule #{rule.RuleId} \"{rule.RuleName}\" " +
            $"({rule.MatchType}/{rule.ActionType}/{rule.Severity}, {(rule.IsActive ? "enabled" : "disabled")})",
            isSuccess: true, ct);

        return Ok(ToDto(rule));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        PolicyRule rule = await db.PolicyRules.FirstOrDefaultAsync(r => r.RuleId == id, ct)
            ?? throw AppException.NotFound("Rule not found");

        db.PolicyRules.Remove(rule);
        await db.SaveChangesAsync(ct);
        policy.InvalidateCache();

        await audit.LogAsync(AuditCategories.Policy, AuditActions.PolicyDeleted,
            User.GetUserId(), User.GetUsername(),
            $"Deleted rule #{id} \"{rule.RuleName}\"", isSuccess: true, ct);

        return NoContent();
    }

    /// <summary>
    /// ทดลองยิงข้อความผ่านชุดเงื่อนไขที่เปิดใช้อยู่ โดยไม่เรียก AI และไม่บันทึกลง chat log
    /// ใช้ตรวจว่าเงื่อนไขที่เพิ่งตั้งทำงานตามที่คิดหรือไม่
    /// </summary>
    [HttpPost("test")]
    [ProducesResponseType(typeof(PolicyTestResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PolicyTestResponse>> Test(
        PolicyTestRequest request, CancellationToken ct)
    {
        PolicyDecision decision = await policy.EvaluateAsync(request.Message, ct);

        return Ok(new PolicyTestResponse(
            decision.Flag,
            decision.RuleId,
            decision.RuleName,
            decision.Severity,
            decision.Notice,
            QuestionClassifier.Classify(request.Message) ?? QuestionClassifier.Other));
    }

    private static void ValidatePattern(PolicyRuleUpsertRequest request)
    {
        if (request.MatchType != "Regex")
        {
            return;
        }

        try
        {
            _ = new Regex(request.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException ex)
        {
            throw new AppException($"Invalid regular expression: {ex.Message}");
        }
    }

    private static PolicyRuleDto ToDto(PolicyRule r) => new(
        r.RuleId, r.RuleName, r.Description, r.MatchType, r.Pattern,
        r.ActionType, r.Severity, r.IsActive, r.CreatedAt, r.CreatedBy, r.UpdatedAt, r.UpdatedBy);
}
