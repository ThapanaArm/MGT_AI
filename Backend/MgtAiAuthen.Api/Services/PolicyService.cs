using System.Text.RegularExpressions;
using MgtAiAuthen.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MgtAiAuthen.Api.Services;

/// <summary>ผลการคัดกรองข้อความหนึ่งข้อความตามชุด PolicyRules ที่เปิดใช้อยู่</summary>
public record PolicyDecision(string? Flag, int? RuleId, string? RuleName, string? Severity, string? Notice)
{
    public static readonly PolicyDecision Clean = new(null, null, null, null, null);

    public bool IsBlocked => Flag == PolicyFlags.Block;
}

public interface IPolicyService
{
    Task<PolicyDecision> EvaluateAsync(string content, CancellationToken ct = default);

    /// <summary>เรียกหลังจากแก้ไข PolicyRules เพื่อให้ cache โหลดชุดใหม่ทันที</summary>
    void InvalidateCache();
}

public class PolicyService(
    AppDbContext db,
    IMemoryCache cache,
    ILogger<PolicyService> logger) : IPolicyService
{
    private const string CacheKey = "policy-rules:active";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>ยิ่งเลขน้อยยิ่งรุนแรง — ใช้เลือกกฎที่ชนะเมื่อข้อความเข้าหลายกฎ</summary>
    private static int Precedence(string actionType) => actionType switch
    {
        PolicyFlags.Block => 0,
        PolicyFlags.Warn => 1,
        _ => 2,
    };

    public async Task<PolicyDecision> EvaluateAsync(string content, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return PolicyDecision.Clean;
        }

        IReadOnlyList<CompiledRule> rules = await GetRulesAsync(ct);
        CompiledRule? winner = null;

        foreach (CompiledRule rule in rules)
        {
            if (!Matches(rule, content))
            {
                continue;
            }

            if (winner is null || Precedence(rule.Rule.ActionType) < Precedence(winner.Rule.ActionType))
            {
                winner = rule;
            }

            // Block คือระดับรุนแรงสุด ไม่ต้องตรวจกฎที่เหลือ
            if (winner.Rule.ActionType == PolicyFlags.Block)
            {
                break;
            }
        }

        if (winner is null)
        {
            return PolicyDecision.Clean;
        }

        PolicyRule r = winner.Rule;
        return new PolicyDecision(r.ActionType, r.RuleId, r.RuleName, r.Severity, BuildNotice(r));
    }

    public void InvalidateCache() => cache.Remove(CacheKey);

    /// <summary>ข้อความที่แสดงให้ผู้ใช้เห็น — หมวด Audit ติดธงเงียบ ๆ จึงไม่มีข้อความ</summary>
    private static string? BuildNotice(PolicyRule rule) => rule.ActionType switch
    {
        PolicyFlags.Block =>
            $"This message was blocked by company data policy (rule: {rule.RuleName}) " +
            "and was not sent to the AI. Please remove the sensitive data and try again",
        PolicyFlags.Warn =>
            $"This message matched the monitoring rule \"{rule.RuleName}\" — it was sent to the AI " +
            "but has been logged for auditors to review",
        _ => null,
    };

    private bool Matches(CompiledRule rule, string content)
    {
        if (rule.Regex is not null)
        {
            try
            {
                return rule.Regex.IsMatch(content);
            }
            catch (RegexMatchTimeoutException)
            {
                logger.LogWarning(
                    "Rule {RuleId} ({RuleName}) exceeded the {Timeout}ms regex timeout and was skipped",
                    rule.Rule.RuleId, rule.Rule.RuleName, RegexTimeout.TotalMilliseconds);
                return false;
            }
        }

        // เข้าเงื่อนไขเมื่อพบคำใดคำหนึ่งในรายการ (OR) ไม่ใช่ต้องพบทุกคำ
        return rule.Keywords.Any(keyword => content.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Splits a Keyword pattern into terms, separated by "," "|" or a newline.
    /// เพราะผู้ใช้คาดหวังว่าพิมพ์ "คืออะไร, ที่ไหน, อย่างไร" แล้วจะจับทั้งสามคำ
    /// ถ้าต้องการจับคำที่มีจุลภาคอยู่ข้างในจริง ๆ ให้เปลี่ยนไปใช้ MatchType = Regex
    /// </summary>
    private static string[] SplitKeywords(string pattern)
        => pattern
            .Split([',', '|', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private async Task<IReadOnlyList<CompiledRule>> GetRulesAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyList<CompiledRule>? cached) && cached is not null)
        {
            return cached;
        }

        List<PolicyRule> active = await db.PolicyRules
            .AsNoTracking()
            .Where(r => r.IsActive)
            .OrderBy(r => r.RuleId)
            .ToListAsync(ct);

        List<CompiledRule> compiled = [];
        foreach (PolicyRule rule in active)
        {
            if (rule.MatchType == "Regex")
            {
                Regex regex;
                try
                {
                    regex = new Regex(
                        rule.Pattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        RegexTimeout);
                }
                catch (ArgumentException ex)
                {
                    // pattern เสีย: ข้ามกฎนั้นแทนที่จะทำให้ทุกคนแชทไม่ได้
                    logger.LogError(
                        ex, "Rule {RuleId} ({RuleName}) has an invalid regex and was not applied",
                        rule.RuleId, rule.RuleName);
                    continue;
                }

                compiled.Add(new CompiledRule(rule, regex, []));
                continue;
            }

            string[] keywords = SplitKeywords(rule.Pattern);
            if (keywords.Length == 0)
            {
                logger.LogWarning(
                    "Rule {RuleId} ({RuleName}) has no usable keywords and was not applied",
                    rule.RuleId, rule.RuleName);
                continue;
            }

            compiled.Add(new CompiledRule(rule, null, keywords));
        }

        cache.Set(CacheKey, (IReadOnlyList<CompiledRule>)compiled, CacheTtl);
        return compiled;
    }

    /// <summary>
    /// กฎที่เตรียมพร้อมใช้แล้ว — เก็บใน cache เพื่อไม่ต้อง compile regex
    /// หรือแยกคำใหม่ทุกครั้งที่มีคนส่งข้อความ
    /// ใช้ Regex เมื่อ MatchType = Regex, ใช้ Keywords เมื่อเป็น Keyword (อย่างใดอย่างหนึ่ง)
    /// </summary>
    private sealed record CompiledRule(PolicyRule Rule, Regex? Regex, string[] Keywords);
}
