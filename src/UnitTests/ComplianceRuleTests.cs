// ===== 合规规则单元测试 =====
// 合规规则是资金安全的最后一道闸门，必须有充分的单元测试。
// 参见 docs/05-security-compliance/02-compliance-matrix.md

using BankingAgent.Base.Security.Compliance;
using BankingAgent.PluginSdk;

namespace UnitTests;

public class ComplianceRuleTests
{
    private static ComplianceOptions Default() => new();

    private static ComplianceContext Transfer(decimal amount, string from = "acc_1", string to = "acc_2")
        => new()
        {
            UserId = "u_1",
            Scenario = "transfer",
            Intent = "transfer.execute",
            Amount = amount,
            SourceAccount = from,
            TargetAccount = to
        };

    // ===== 转账金额阈值规则 =====

    [Fact]
    public void TransferAmount_BelowThreshold_NotApplicable()
    {
        var rule = new TransferAmountRule(Default());
        Assert.Null(rule.Evaluate(Transfer(100m)));
    }

    [Fact]
    public void TransferAmount_AboveAutoApproveLimit_RequiresApproval()
    {
        var rule = new TransferAmountRule(Default());
        var decision = rule.Evaluate(Transfer(6000m));

        Assert.NotNull(decision);
        Assert.True(decision!.Allowed);
        Assert.True(decision.RequiresHumanApproval);
        Assert.Equal("transfer.amount.threshold", decision.RuleId);
    }

    [Fact]
    public void TransferAmount_AboveHardCap_Denied()
    {
        var rule = new TransferAmountRule(Default());
        var decision = rule.Evaluate(Transfer(600_000m));

        Assert.NotNull(decision);
        Assert.False(decision!.Allowed);
        Assert.False(decision.RequiresHumanApproval);
        Assert.Contains("硬上限", decision.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-99999)]
    public void TransferAmount_NonPositive_Denied(int amount)
    {
        var rule = new TransferAmountRule(Default());
        var decision = rule.Evaluate(Transfer(amount));

        Assert.NotNull(decision);
        Assert.False(decision!.Allowed);
    }

    [Fact]
    public void TransferAmount_JustBelowThreshold_NoApprovalNeeded()
    {
        // 实现使用严格大于号：金额低于阈值即免确认
        var options = Default();
        var rule = new TransferAmountRule(options);
        Assert.Null(rule.Evaluate(Transfer(options.TransferAutoApproveLimit - 0.01m)));
    }

    [Fact]
    public void TransferAmount_JustAboveThreshold_RequiresApproval()
    {
        var options = Default();
        var rule = new TransferAmountRule(options);
        var decision = rule.Evaluate(Transfer(options.TransferAutoApproveLimit + 0.01m));

        Assert.NotNull(decision);
        Assert.True(decision!.RequiresHumanApproval);
    }

    [Fact]
    public void TransferAmount_RiskScoreGrowsWithAmount()
    {
        var rule = new TransferAmountRule(Default());
        var low = rule.Evaluate(Transfer(6000m));
        var high = rule.Evaluate(Transfer(400_000m));

        Assert.NotNull(low);
        Assert.NotNull(high);
        Assert.True(high!.RiskScore > low!.RiskScore);
    }

    // ===== 禁止自转规则 =====

    [Fact]
    public void SelfTransfer_Denied()
    {
        var rule = new SelfTransferRule();
        var decision = rule.Evaluate(Transfer(100m, "acc_same", "acc_same"));

        Assert.NotNull(decision);
        Assert.False(decision!.Allowed);
        Assert.Equal("transfer.self", decision.RuleId);
    }

    [Fact]
    public void SelfTransfer_DifferentAccounts_NotApplicable()
    {
        var rule = new SelfTransferRule();
        Assert.Null(rule.Evaluate(Transfer(100m)));
    }

    [Theory]
    [InlineData(null, "acc_2")]
    [InlineData("acc_1", null)]
    [InlineData(null, null)]
    public void SelfTransfer_MissingAccountInfo_Denied(string? from, string? to)
    {
        var rule = new SelfTransferRule();
        var ctx = new ComplianceContext
        {
            UserId = "u_1",
            Scenario = "transfer",
            Amount = 100m,
            SourceAccount = from,
            TargetAccount = to
        };
        var decision = rule.Evaluate(ctx);

        Assert.NotNull(decision);
        Assert.False(decision!.Allowed);
    }

    // ===== 反洗钱规则 =====

    [Fact]
    public void Aml_BelowThreshold_NotApplicable()
    {
        var rule = new AmlThresholdRule(Default());
        Assert.Null(rule.Evaluate(Transfer(10_000m)));
    }

    [Fact]
    public void Aml_AboveThreshold_RequiresReview()
    {
        var rule = new AmlThresholdRule(Default());
        var decision = rule.Evaluate(Transfer(80_000m));

        Assert.NotNull(decision);
        Assert.True(decision!.Allowed);
        Assert.True(decision.RequiresHumanApproval);
        Assert.Equal("aml.large_amount", decision.RuleId);
    }

    [Fact]
    public void Aml_RiskScoreCappedAtOne()
    {
        var rule = new AmlThresholdRule(Default());
        var decision = rule.Evaluate(Transfer(10_000_000m));

        Assert.NotNull(decision);
        Assert.InRange(decision!.RiskScore, 0, 1);
    }

    // ===== 场景权限规则 =====

    [Fact]
    public void ScenarioPermission_FundOperationOutsideFundScenario_Denied()
    {
        var rule = new ScenarioPermissionRule();
        var ctx = new ComplianceContext
        {
            UserId = "u_1",
            Scenario = "bill",
            Amount = 5000m
        };
        var decision = rule.Evaluate(ctx);

        Assert.NotNull(decision);
        Assert.False(decision!.Allowed);
    }

    [Fact]
    public void ScenarioPermission_TransferScenario_NotApplicable()
    {
        var rule = new ScenarioPermissionRule();
        Assert.Null(rule.Evaluate(Transfer(5000m)));
    }

    [Fact]
    public void ScenarioPermission_NonFundScenario_Allowed()
    {
        var rule = new ScenarioPermissionRule();
        var ctx = new ComplianceContext { UserId = "u_1", Scenario = "bill", Amount = 0m };
        Assert.Null(rule.Evaluate(ctx));
    }

    // ===== 守卫编排 =====

    [Fact]
    public void Guard_SelfTransfer_RejectedByGuard()
    {
        var guard = new ComplianceGuard(new IComplianceRule[]
        {
            new TransferAmountRule(Default()),
            new SelfTransferRule(),
            new AmlThresholdRule(Default()),
            new ScenarioPermissionRule()
        });

        var decision = guard.Evaluate(Transfer(100m, "same", "same"));

        Assert.False(decision.Allowed);
        Assert.Equal("transfer.self", decision.RuleId);
    }

    [Fact]
    public void Guard_NormalTransfer_Allowed()
    {
        var guard = new ComplianceGuard(new IComplianceRule[]
        {
            new TransferAmountRule(Default()),
            new SelfTransferRule(),
            new AmlThresholdRule(Default()),
            new ScenarioPermissionRule()
        });

        var decision = guard.Evaluate(Transfer(100m));

        Assert.True(decision.Allowed);
        Assert.False(decision.RequiresHumanApproval);
    }

    [Fact]
    public void Guard_LargeAmount_RequiresApproval()
    {
        var guard = new ComplianceGuard(new IComplianceRule[]
        {
            new TransferAmountRule(Default()),
            new SelfTransferRule()
        });

        var decision = guard.Evaluate(Transfer(80_000m));

        Assert.True(decision.Allowed);
        Assert.True(decision.RequiresHumanApproval);
    }

    [Fact]
    public void Guard_HardCapTakesPrecedence_OverApproval()
    {
        // 超硬上限必须直接拒绝，不能只要求人工确认
        var guard = new ComplianceGuard(new IComplianceRule[]
        {
            new TransferAmountRule(Default())
        });

        var decision = guard.Evaluate(Transfer(9_999_999m));

        Assert.False(decision.Allowed);
        Assert.False(decision.RequiresHumanApproval);
    }

    [Fact]
    public void Guard_SceneFiltered_AppliesOnlyMatchingScenarios()
    {
        var rule = new AmlThresholdRule(Default());
        var wealthCtx = new ComplianceContext
        {
            UserId = "u_1", Scenario = "wealth", Amount = 80_000m
        };

        // AML 规则声明了 wealth 场景，应生效
        var guard = new ComplianceGuard(new IComplianceRule[] { rule });
        var decision = guard.Evaluate(wealthCtx);

        Assert.True(decision.RequiresHumanApproval);
    }

    [Fact]
    public void Guard_ThresholdConfigurable()
    {
        var strict = Default();
        strict.TransferAutoApproveLimit = 100m;
        strict.TransferHardCap = 1000m;

        var guard = new ComplianceGuard(new IComplianceRule[] { new TransferAmountRule(strict) });

        // 默认阈值下 500 元免确认，严格阈值下需确认
        Assert.True(guard.Evaluate(Transfer(500m)).RequiresHumanApproval);
        Assert.False(guard.Evaluate(Transfer(1500m)).Allowed);
    }
}
