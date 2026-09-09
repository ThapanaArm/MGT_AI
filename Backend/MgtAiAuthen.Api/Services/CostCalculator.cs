using MgtAiAuthen.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MgtAiAuthen.Api.Services;

/// <summary>The cost of one message, broken down by token type.</summary>
public record TokenCost(
    decimal InputUsd,
    decimal OutputUsd,
    decimal CacheUsd,
    decimal TotalUsd,
    decimal TotalThb,
    decimal UsdToThbRate,
    int? PricingId)
{
    /// <summary>Used when no pricing exists for the model — tokens are logged but not charged.</summary>
    public static readonly TokenCost Unknown = new(0m, 0m, 0m, 0m, 0m, 0m, null);
}

public interface ICostCalculator
{
    /// <summary>Computes the cost from token counts using the active pricing for the model.</summary>
    Task<TokenCost> CalculateAsync(
        string? modelName,
        int inputTokens,
        int outputTokens,
        int cacheWriteTokens,
        int cacheReadTokens,
        CancellationToken ct = default);

    /// <summary>เรียกหลังแก้ตาราง ModelPricing เพื่อให้ cache โหลดราคาใหม่</summary>
    void InvalidateCache();
}

/// <summary>
/// Computes costs from dbo.ModelPricing (cached for 5 minutes).
///
/// ผลลัพธ์ถูกเก็บลงแถวของข้อความนั้นทันที ไม่ได้คำนวณสดตอนทำรายงาน
/// so historical costs never change when prices or the exchange rate are updated later.
/// </summary>
public class CostCalculator(
    AppDbContext db,
    IMemoryCache cache,
    ILogger<CostCalculator> logger) : ICostCalculator
{
    private const string CacheKey = "model-pricing:active";
    private const decimal TokensPerMillion = 1_000_000m;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public async Task<TokenCost> CalculateAsync(
        string? modelName,
        int inputTokens,
        int outputTokens,
        int cacheWriteTokens,
        int cacheReadTokens,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return TokenCost.Unknown;
        }

        IReadOnlyDictionary<string, ModelPricing> pricing = await GetPricingAsync(ct);

        if (!pricing.TryGetValue(modelName, out ModelPricing? price))
        {
            // The API often echoes a dated snapshot ("claude-haiku-4-5-20251001") instead of the
            // alias we asked for ("claude-haiku-4-5"). Without this fallback that usage would be
            // silently billed as 0. Match the longest priced name the returned name starts with,
            // so any future snapshot of a priced model still costs correctly.
            price = pricing.Values
                .Where(p => modelName.StartsWith(p.ModelName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.ModelName.Length)
                .FirstOrDefault();

            if (price is not null)
            {
                logger.LogDebug(
                    "Model {ModelName} priced from the {Priced} entry (dated snapshot of the same model)",
                    modelName, price.ModelName);
            }
        }

        if (price is null)
        {
            // ไม่ล้ม request เพราะยังไม่มีราคา — บันทึก token ไว้ก่อน แล้วเตือนให้ผู้ดูแลเพิ่มราคา
            logger.LogWarning(
                "No pricing found for model {ModelName} in ModelPricing — cost recorded as 0 " +
                "— please add pricing on the model pricing page", modelName);
            return TokenCost.Unknown;
        }

        decimal inputCost = inputTokens / TokensPerMillion * price.InputUsdPerMTok;
        decimal outputCost = outputTokens / TokensPerMillion * price.OutputUsdPerMTok;
        decimal cacheCost =
            cacheWriteTokens / TokensPerMillion * price.CacheWriteUsdPerMTok
            + cacheReadTokens / TokensPerMillion * price.CacheReadUsdPerMTok;

        decimal totalUsd = inputCost + outputCost + cacheCost;

        return new TokenCost(
            InputUsd: Round8(inputCost),
            OutputUsd: Round8(outputCost),
            CacheUsd: Round8(cacheCost),
            TotalUsd: Round8(totalUsd),
            TotalThb: Math.Round(totalUsd * price.UsdToThbRate, 6, MidpointRounding.AwayFromZero),
            UsdToThbRate: price.UsdToThbRate,
            PricingId: price.PricingId);
    }

    public void InvalidateCache() => cache.Remove(CacheKey);

    private static decimal Round8(decimal value) => Math.Round(value, 8, MidpointRounding.AwayFromZero);

    private async Task<IReadOnlyDictionary<string, ModelPricing>> GetPricingAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyDictionary<string, ModelPricing>? cached) && cached is not null)
        {
            return cached;
        }

        List<ModelPricing> rows = await db.ModelPricing
            .AsNoTracking()
            .Where(p => p.IsActive)
            .ToListAsync(ct);

        // The model name from the API may differ in case from what was stored.
        Dictionary<string, ModelPricing> map = rows
            .GroupBy(p => p.ModelName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.EffectiveFrom).First(),
                StringComparer.OrdinalIgnoreCase);

        cache.Set(CacheKey, (IReadOnlyDictionary<string, ModelPricing>)map, CacheTtl);
        return map;
    }
}
