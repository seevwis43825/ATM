// ===== 内置合规规则集 =====
// 覆盖《反洗钱法》《商业银行法》《个保法》与央行 AI 指导意见的核心控制点。
// 阈值全部可配置，便于按监管要求调整而不改代码。

namespace BankingAgent.Base.Security.Compliance;

using BankingAgent.PluginSdk;

/// <summary>合规规则配置。生产环境应通过配置文件注入。</summary>
public sealed class ComplianceOptions
{
    /// <summary>单笔转账免人工确认上限（元）。</summary>
    public decimal TransferAutoApproveLimit { get; set; } = 5000m;
    /// <summary>单日累计转账上限（元）。</summary>
    public decimal TransferDailyLimit { get; set; } = 200000m;
    /// <summary>反洗钱大额报告阈值（元）。</summary>
    public decimal AmlReportThreshold { get; set; } = 50000m;
    /// <summary>单笔交易绝对上限（元），超过一律拒绝。</summary>
    public decimal TransferHardCap { get; set; } = 500000m;
    /// <summary>高频操作阈值：单位时间内超过该次数触发人工。</summary>
    public int HighFrequencyThreshold { get; set; } = 5;
}

/// <summary>单笔金额规则：超软上限需人工确认，超硬上限直接拒绝。</summary>
public sealed class TransferAmountRule(ComplianceOptions options) : IComplianceRule
{
    public string RuleId => "transfer.amount.threshold";
    public string RuleVersion => "v1.5";
    public IReadOnlyList<string> Scenarios => ["transfer"];

    public ComplianceDecision? Evaluate(ComplianceContext ctx)
    {
        if (ctx.Amount <= 0)
        {
            return ComplianceDecision.Deny(RuleId, "转账金额必须大于 0");
        }

        if (ctx.Amount > options.TransferHardCap)
        {
            return ComplianceDecision.Deny(
                RuleId,
                $"单笔金额 {ctx.Amount:N2} 元超过系统硬上限 {options.TransferHardCap:N2} 元");
        }

        if (ctx.Amount > options.TransferAutoApproveLimit)
        {
            return ComplianceDecision.RequireApproval(
                RuleId,
                $"单笔金额 {ctx.Amount:N2} 元超过自动放行上限 {options.TransferAutoApproveLimit:N2} 元，需人工确认",
                riskScore: Math.Min(1.0, (double)(ctx.Amount / options.TransferHardCap)));
        }

        return null;
    }
}

/// <summary>同账户互转规则：禁止给自己转账。</summary>
public sealed class SelfTransferRule : IComplianceRule
{
    public string RuleId => "transfer.self";
    public string RuleVersion => "v1.0";
    public IReadOnlyList<string> Scenarios => ["transfer"];

    public ComplianceDecision? Evaluate(ComplianceContext ctx)
    {
        if (string.IsNullOrEmpty(ctx.SourceAccount) || string.IsNullOrEmpty(ctx.TargetAccount))
        {
            return ComplianceDecision.Deny(RuleId, "付款或收款账户信息不完整");
        }

        return ctx.SourceAccount == ctx.TargetAccount
            ? ComplianceDecision.Deny(RuleId, "禁止向本人账户转账")
            : null;
    }
}

/// <summary>反洗钱规则：超阈值标记为可疑，要求人工复核并触发报送。</summary>
public sealed class AmlThresholdRule(ComplianceOptions options) : IComplianceRule
{
    public string RuleId => "aml.large_amount";
    public string RuleVersion => "v1.2";
    public IReadOnlyList<string> Scenarios => ["transfer", "wealth"];

    public ComplianceDecision? Evaluate(ComplianceContext ctx)
    {
        if (ctx.Amount < options.AmlReportThreshold) return null;

        return ComplianceDecision.RequireApproval(
            RuleId,
            $"交易金额 {ctx.Amount:N2} 元达到反洗钱大额报告阈值 {options.AmlReportThreshold:N2} 元，需人工复核",
            riskScore: Math.Min(1.0, (double)(ctx.Amount / options.AmlReportThreshold) / 4));
    }
}

/// <summary>场景权限规则：仅转账场景允许资金类操作。</summary>
public sealed class ScenarioPermissionRule : IComplianceRule
{
    private static readonly HashSet<string> FundScenarios =
        ["transfer", "wealth", "card"];

    public string RuleId => "scenario.permission";
    public string RuleVersion => "v1.0";
    public IReadOnlyList<string> Scenarios => [];

    public ComplianceDecision? Evaluate(ComplianceContext ctx)
    {
        // 高风险场景必须由受控 Agent 处理，禁止出现在未知场景中
        if (ctx.Scenario is "transfer" or "wealth" or "card") return null;

        if (ctx.Amount > 0)
        {
            return ComplianceDecision.Deny(
                "scenario.fund_operation_outside_context",
                $"场景 {ctx.Scenario} 不允许执行金额为 {ctx.Amount:N2} 元的资金操作");
        }

        return null;
    }
}
