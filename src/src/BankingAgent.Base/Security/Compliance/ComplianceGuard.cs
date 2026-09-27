// ===== 合规守卫 =====
// 所有进入资金/敏感业务的操作必须先过这道关。
// 对应 docs/05-security-compliance/02-compliance-matrix.md 的控制点实现。

namespace BankingAgent.Base.Security.Compliance;

using BankingAgent.PluginSdk;

/// <summary>合规决策结果。</summary>
public sealed record ComplianceDecision
{
    public required bool Allowed { get; init; }
    public string RuleId { get; init; } = "";
    public string RuleVersion { get; init; } = "v1.0";
    public string Reason { get; init; } = "";
    /// <summary>是否要求人工确认（不阻断，但需人工放行）。</summary>
    public bool RequiresHumanApproval { get; init; }
    public double RiskScore { get; init; }

    public static ComplianceDecision Allow(string ruleId = "") =>
        new() { Allowed = true, RuleId = ruleId };

    public static ComplianceDecision Deny(string ruleId, string reason) =>
        new() { Allowed = false, RuleId = ruleId, Reason = reason };

    public static ComplianceDecision RequireApproval(string ruleId, string reason, double riskScore = 0) =>
        new() { Allowed = true, RuleId = ruleId, Reason = reason, RequiresHumanApproval = true, RiskScore = riskScore };
}

/// <summary>合规校验上下文。</summary>
public sealed record ComplianceContext
{
    public required string UserId { get; init; }
    public required string Scenario { get; init; }
    public string? Intent { get; init; }
    public decimal Amount { get; init; }
    public string? SourceAccount { get; init; }
    public string? TargetAccount { get; init; }
    public string? IdempotencyKey { get; init; }
    public IReadOnlyDictionary<string, object?> Extra { get; init; }
        = new Dictionary<string, object?>();
}

/// <summary>合规规则契约。每条规则独立可测。</summary>
public interface IComplianceRule
{
    string RuleId { get; }
    string RuleVersion { get; }
    /// <summary>该规则适用的场景，空表示适用全部。</summary>
    IReadOnlyList<string> Scenarios { get; }
    /// <summary>返回 null 表示本规则不适用。</summary>
    ComplianceDecision? Evaluate(ComplianceContext ctx);
}

/// <summary>合规守卫。顺序执行全部规则，任一拒绝即阻断。</summary>
public class ComplianceGuard(IEnumerable<IComplianceRule> rules)
{
    private readonly IReadOnlyList<IComplianceRule> _rules = rules.ToList();

    /// <summary>已注册规则清单（供合规后台展示）。</summary>
    public IReadOnlyList<IComplianceRule> Rules => _rules;

    /// <summary>执行全部适用规则。</summary>
    public ComplianceDecision Evaluate(ComplianceContext ctx)
    {
        foreach (var rule in _rules)
        {
            if (rule.Scenarios.Count > 0 &&
                !rule.Scenarios.Contains(ctx.Scenario, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var decision = rule.Evaluate(ctx);
            if (decision is null) continue;

            if (!decision.Allowed)
            {
                return decision;
            }

            if (decision.RequiresHumanApproval)
            {
                return decision;
            }
        }

        return ComplianceDecision.Allow();
    }
}
