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
/// Manage per-million-token prices for each model plus the exchange rate — Admin only.
///
/// การแก้ราคาที่นี่มีผลกับข้อความที่เกิด "หลังจากนี้" เท่านั้น
/// Costs already recorded are frozen on their rows, so history never changes.
/// </summary>
[ApiController]
[Route("api/admin/model-pricing")]
[Authorize(Roles = UserRoles.Admin)]
public class AdminPricingController(
    AppDbContext db,
    ICostCalculator costCalculator,
    IAuditService audit) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ModelPricingDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ModelPricingDto>>> GetAll(CancellationToken ct)
        => Ok(await db.ModelPricing
            .AsNoTracking()
            .OrderBy(p => p.Provider).ThenBy(p => p.InputUsdPerMTok)
            .Select(p => new ModelPricingDto(
                p.PricingId, p.Provider, p.ModelName,
                p.InputUsdPerMTok, p.OutputUsdPerMTok,
                p.CacheWriteUsdPerMTok, p.CacheReadUsdPerMTok,
                p.UsdToThbRate, p.EffectiveFrom, p.IsActive, p.Notes,
                // ยอดการใช้งานจริงที่คิดด้วยราคาชุดนี้ ช่วยให้เห็นว่าราคาไหนถูกใช้จริง
                db.ChatMessages.Count(m => m.PricingId == p.PricingId),
                db.ChatMessages.Where(m => m.PricingId == p.PricingId).Sum(m => m.TotalCostThb) ?? 0m,
                p.CreatedAt, p.CreatedBy, p.UpdatedAt, p.UpdatedBy))
            .ToListAsync(ct));

    [HttpPost]
    [ProducesResponseType(typeof(ModelPricingDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<ModelPricingDto>> Create(
        ModelPricingUpsertRequest request, CancellationToken ct)
    {
        string modelName = request.ModelName.Trim();

        if (await db.ModelPricing.AnyAsync(p => p.ModelName == modelName, ct))
        {
            throw AppException.Conflict($"Pricing for model \"{modelName}\" already exists — edit it instead");
        }

        var pricing = new ModelPricing
        {
            // Blank means Anthropic — see ModelPricingUpsertRequest.Provider.
            Provider = AiProviders.Normalise(request.Provider),
            ModelName = modelName,
            InputUsdPerMTok = request.InputUsdPerMTok,
            OutputUsdPerMTok = request.OutputUsdPerMTok,
            CacheWriteUsdPerMTok = request.CacheWriteUsdPerMTok,
            CacheReadUsdPerMTok = request.CacheReadUsdPerMTok,
            UsdToThbRate = request.UsdToThbRate,
            Notes = request.Notes?.Trim(),
            IsActive = request.IsActive,
            EffectiveFrom = DateTime.Now,
            CreatedAt = DateTime.Now,
            CreatedBy = User.GetUsername(),
        };

        db.ModelPricing.Add(pricing);
        await db.SaveChangesAsync(ct);
        costCalculator.InvalidateCache();

        await audit.LogAsync(AuditCategories.Admin, AuditActions.PricingCreated,
            User.GetUserId(), User.GetUsername(),
            $"Added pricing for {pricing.Provider} model {pricing.ModelName}: input {pricing.InputUsdPerMTok} / " +
            $"output {pricing.OutputUsdPerMTok} USD per 1M tokens, rate {pricing.UsdToThbRate} THB/USD",
            isSuccess: true, ct);

        return CreatedAtAction(nameof(GetAll), new { id = pricing.PricingId }, ToDto(pricing, 0, 0m));
    }

    [HttpPut("{id:int}")]
    [ProducesResponseType(typeof(ModelPricingDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ModelPricingDto>> Update(
        int id, ModelPricingUpsertRequest request, CancellationToken ct)
    {
        ModelPricing pricing = await db.ModelPricing.FirstOrDefaultAsync(p => p.PricingId == id, ct)
            ?? throw AppException.NotFound("Pricing for this model was not found");

        string modelName = request.ModelName.Trim();
        if (await db.ModelPricing.AnyAsync(p => p.ModelName == modelName && p.PricingId != id, ct))
        {
            throw AppException.Conflict($"Pricing for model \"{modelName}\" already exists");
        }

        string before =
            $"input {pricing.InputUsdPerMTok} / output {pricing.OutputUsdPerMTok} USD, " +
            $"rate {pricing.UsdToThbRate}";

        // Blank means "leave as it is" on update, not "Anthropic". Defaulting here would let any
        // client that predates the provider field silently move a Gemini row to Anthropic, and the
        // model would then fail with no clue why.
        if (!string.IsNullOrWhiteSpace(request.Provider))
        {
            pricing.Provider = AiProviders.Normalise(request.Provider);
        }

        pricing.ModelName = modelName;
        pricing.InputUsdPerMTok = request.InputUsdPerMTok;
        pricing.OutputUsdPerMTok = request.OutputUsdPerMTok;
        pricing.CacheWriteUsdPerMTok = request.CacheWriteUsdPerMTok;
        pricing.CacheReadUsdPerMTok = request.CacheReadUsdPerMTok;
        pricing.UsdToThbRate = request.UsdToThbRate;
        pricing.Notes = request.Notes?.Trim();
        pricing.IsActive = request.IsActive;
        pricing.EffectiveFrom = DateTime.Now;
        pricing.UpdatedAt = DateTime.Now;
        pricing.UpdatedBy = User.GetUsername();

        await db.SaveChangesAsync(ct);
        costCalculator.InvalidateCache();

        await audit.LogAsync(AuditCategories.Admin, AuditActions.PricingUpdated,
            User.GetUserId(), User.GetUsername(),
            $"Updated pricing for {pricing.ModelName} from [{before}] to " +
            $"[input {pricing.InputUsdPerMTok} / output {pricing.OutputUsdPerMTok} USD, " +
            $"rate {pricing.UsdToThbRate}] — applies to messages from now on only",
            isSuccess: true, ct);

        int used = await db.ChatMessages.CountAsync(m => m.PricingId == id, ct);
        decimal spent = await db.ChatMessages
            .Where(m => m.PricingId == id)
            .SumAsync(m => m.TotalCostThb, ct) ?? 0m;

        return Ok(ToDto(pricing, used, spent));
    }

    private static ModelPricingDto ToDto(ModelPricing p, int messageCount, decimal totalThb) => new(
        p.PricingId, p.Provider, p.ModelName,
        p.InputUsdPerMTok, p.OutputUsdPerMTok,
        p.CacheWriteUsdPerMTok, p.CacheReadUsdPerMTok,
        p.UsdToThbRate, p.EffectiveFrom, p.IsActive, p.Notes,
        messageCount, totalThb,
        p.CreatedAt, p.CreatedBy, p.UpdatedAt, p.UpdatedBy);
}
