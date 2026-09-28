// ===== 理财 Agent =====
//
// 继承 BankingAgentBase 即可自动获得：耗时统计、审计留痕、异常兜底。
// 你只需要实现 HandleAsync。

using BankingAgent.Base.Agents;
using BankingAgent.Base.Security;
using BankingAgent.Base.Security.Audit;
using BankingAgent.PluginSdk;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Plugin.Wealth;

/// <summary>理财 的业务 Agent。</summary>
public sealed class 理财Agent : BankingAgentBase
{
    private readonly ICoreBankClient _coreBank;

    /// <summary>构造 Agent。需要的依赖由宿主容器注入。</summary>
    public 理财Agent(
        ICoreBankClient coreBank,
        IAuditLogger audit,
        ILogger<理财Agent> logger)
        : base(audit, logger)
    {
        _coreBank = coreBank;
    }

    /// <inheritdoc />
    public override AgentId Id => new("wealth.agent");

    /// <inheritdoc />
    public override string Name => "理财 Agent";

    /// <inheritdoc />
    public override AgentRole Role => AgentRole.DomainExpert;

    /// <summary>
    /// 该 Agent 能处理的意图前缀。宿主按「最长前缀优先」路由，
    /// 因此 "wealth" 不会抢占 "wealth.products"。
    /// </summary>
    public override IReadOnlyList<string> SupportedIntents =>
        ["wealth", "wealth.products", "wealth.balance"];

    /// <inheritdoc />
    public override IReadOnlyList<string> TriggerKeywords =>
        ["理财", "基金", "投资", "产品", "收益", "净值", "余额", "资产"];

    /// <inheritdoc />
    protected override async Task<AgentResult> HandleAsync(AgentRequest request, CancellationToken ct)
    {
        // ===== 1. 余额 / 资产查询（只读，取用户第一个账户）=====
        if (request.UserInput.Contains("余额", StringComparison.Ordinal)
            || request.UserInput.Contains("资产", StringComparison.Ordinal))
        {
            var accounts = await _coreBank.ListAccountsAsync(request.UserId, ct);
            var account = accounts.FirstOrDefault();
            if (account is null) return AgentResult.Fail("NO_ACCOUNT", "未找到账户");

            return AgentResult.Ok(
                content: $"账户 {DataMasker.MaskAccount(account.AccountNo)} 当前可用 {account.AvailableAmount:N2} {account.Currency}",
                intent: "wealth.balance",
                data: new Dictionary<string, object?>
                {
                    // L3 字段必须脱敏，账号只返回掩码
                    ["account_no"] = DataMasker.MaskAccount(account.AccountNo),
                    ["balance"] = account.Balance,
                    ["available_amount"] = account.AvailableAmount,
                    ["currency"] = account.Currency,
                    ["data_classification"] = "L3-masked"
                });
        }

        // ===== 2. 理财产品查询与推荐（只读）=====
        var products = await _coreBank.ListProductsAsync(
            SlotReader.String(request, "risk_level"), ct);

        if (products.Count == 0)
        {
            return AgentResult.Fail("NO_PRODUCT", "暂无在售理财产品");
        }

        var list = products.Select(p => new Dictionary<string, object?>
        {
            ["code"] = p.Code,
            ["name"] = p.Name,
            ["type"] = p.Type,
            ["risk_level"] = p.RiskLevel,
            ["annual_rate"] = p.AnnualRate,
            ["min_amount"] = p.MinInvestment,
            ["term_days"] = p.TermDays,
            ["status"] = p.Status
        }).ToList();

        var best = products.OrderByDescending(p => p.AnnualRate).First();

        return AgentResult.Ok(
            content: $"当前在售 {products.Count} 只理财产品，年化最高为 {best.Name}"
                     + $"（{best.RiskLevel}，{best.AnnualRate:F2}%，{best.TermDays} 天，起投 {best.MinInvestment:N0} 元）",
            intent: "wealth.products",
            data: new Dictionary<string, object?>
            {
                ["product_count"] = products.Count,
                ["products"] = list
            });
    }

    // ===== 写操作的写法（资金类操作必须遵守，否则不要写成写操作）=====
    //
    // 1) 先过合规守卫，被拒就返回 Fail，不要继续执行：
    //    var decision = _compliance.Evaluate(new ComplianceContext
    //    {
    //        UserId = request.UserId,
    //        Scenario = "wealth",
    //        Intent = "wealth.execute"
    //    });
    //    if (!decision.Allowed) return AgentResult.Fail("COMPLIANCE_REJECTED", decision.Reason);
    //
    // 2) 资金操作必须走人工回环，绝不能直接提交副作用：
    //    return AgentResult.PendingApproval("金额 30000 元超过自动放行上限，需人工确认", data);
    //    用户确认后宿主会带 confirmed=true 再次路由到本 Agent，届时才真正提交。
    //
    // 3) 提交成功后置 SideEffectCommitted = true，并用 request.SessionId 作为幂等键，
    //    避免重复点击导致重复扣款。
}