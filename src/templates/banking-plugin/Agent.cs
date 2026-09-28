// ===== __PLUGIN_NAME__ Agent =====
//
// 继承 BankingAgentBase 即可自动获得：耗时统计、审计留痕、异常兜底。
// 你只需要实现 HandleAsync。

using BankingAgent.Base.Agents;
using BankingAgent.Base.Security;
using BankingAgent.Base.Security.Audit;
using BankingAgent.PluginSdk;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Plugin.Template;

/// <summary>__PLUGIN_NAME__ 的业务 Agent。</summary>
public sealed class __PLUGIN_NAME__Agent : BankingAgentBase
{
    private readonly ICoreBankClient _coreBank;

    /// <summary>构造 Agent。需要的依赖由宿主容器注入。</summary>
    public __PLUGIN_NAME__Agent(
        ICoreBankClient coreBank,
        IAuditLogger audit,
        ILogger<__PLUGIN_NAME__Agent> logger)
        : base(audit, logger)
    {
        _coreBank = coreBank;
    }

    /// <inheritdoc />
    public override AgentId Id => new("__SCENARIO__.agent");

    /// <inheritdoc />
    public override string Name => "__PLUGIN_NAME__ Agent";

    /// <inheritdoc />
    public override AgentRole Role => AgentRole.DomainExpert;

    /// <summary>
    /// 该 Agent 能处理的意图前缀。宿主按「最长前缀优先」路由，
    /// 因此 "wealth" 不会抢占 "wealth.redeem"。
    /// </summary>
    public override IReadOnlyList<string> SupportedIntents => ["__INTENT_PREFIX__"];

    /// <inheritdoc />
    protected override async Task<AgentResult> HandleAsync(AgentRequest request, CancellationToken ct)
    {
        // ===== 1. 从槽位取参数 =====
        // 一律用 SlotReader：它处理了 JSON 反序列化产生的 JsonElement，
        // 直接强转会失败，且金额读成 null 会让校验被静默跳过。
        var accountNo = SlotReader.String(request, "account_no");

        if (string.IsNullOrEmpty(accountNo))
        {
            var accounts = await _coreBank.ListAccountsAsync(request.UserId, ct);
            var first = accounts.FirstOrDefault();
            if (first is null) return AgentResult.Fail("NO_ACCOUNT", "未找到账户");
            accountNo = first.AccountNo;
        }

        // ===== 2. 查询账户 =====
        var account = await _coreBank.GetAccountAsync(accountNo, ct);
        if (account is null)
        {
            return AgentResult.Fail("ACCOUNT_NOT_FOUND", "未找到该账户");
        }

        // ===== 3. 返回结果，L3 及以上字段必须脱敏 =====
        return AgentResult.Ok(
            content: $"账户 {DataMasker.MaskAccount(account.AccountNo)} 当前余额 {account.Balance:N2} {account.Currency}",
            intent: "__INTENT_PREFIX__.query",
            data: new Dictionary<string, object?>
            {
                ["account_no"] = DataMasker.MaskAccount(account.AccountNo),
                ["balance"] = account.Balance,
                ["currency"] = account.Currency
            });
    }

    // ===== 写操作的写法（资金类操作必须遵守，否则不要写成写操作）=====
    //
    // 1) 先过合规守卫，被拒就返回 Fail，不要继续执行：
    //    var decision = _compliance.Evaluate(new ComplianceContext
    //    {
    //        UserId = request.UserId,
    //        Scenario = "__SCENARIO__",
    //        Intent = "__INTENT_PREFIX__.execute"
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