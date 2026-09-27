// ===== 卡片管理插件 =====
// 演示「写操作但非资金」场景：挂失/解挂/冻结需要审计，但不走金额合规规则。
// 同时演示插件间依赖声明（依赖转账插件的版本）。

using BankingAgent.Base.Agents;
using BankingAgent.Base.Security;
using BankingAgent.Base.Security.Audit;
using BankingAgent.Base.Security.Compliance;
using BankingAgent.PluginSdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Plugin.CardManagement;

/// <summary>卡片管理插件入口。</summary>
public sealed class CardManagementPluginEntryPoint : IPluginEntryPoint
{
    /// <inheritdoc />
    public PluginManifest GetManifest() => new()
    {
        Id = new PluginId("banking.card"),
        Name = "卡片管理",
        Description = "银行卡查询、挂失、解挂、冻结、限额调整",
        Version = PluginVersion.Parse("1.0.0"),
        Author = "业务开发组",
        Scenarios = ["card"],
        FeatureFlags = ["card.enabled", "card.limit_adjust"],
        // 卡号属 L3，但绝不返回完整卡号
        MaxDataClassification = DataClassification.L3,
        // 声明依赖：卡片挂失后可能需要同步转账插件的账户状态
        Dependencies =
        [
            new PluginDependency(new PluginId("banking.transfer"), PluginVersion.Parse("1.0.0"))
        ]
    };

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton<IBankingAgent, CardManagementAgent>();
    }
}

/// <summary>卡片管理 Agent。</summary>
public sealed class CardManagementAgent : BankingAgentBase
{
    private readonly ICoreBankClient _coreBank;
    private readonly ComplianceGuard _compliance;

    /// <summary>构造卡片 Agent。</summary>
    public CardManagementAgent(
        ICoreBankClient coreBank,
        ComplianceGuard compliance,
        IAuditLogger audit,
        ILogger<CardManagementAgent> logger)
        : base(audit, logger)
    {
        _coreBank = coreBank;
        _compliance = compliance;
    }

    /// <inheritdoc />
    public override AgentId Id => new("card.agent");

    /// <inheritdoc />
    public override string Name => "卡片管理 Agent";

    /// <inheritdoc />
    public override AgentRole Role => AgentRole.DomainExpert;

    /// <inheritdoc />
    public override IReadOnlyList<string> SupportedIntents => ["card", "card.status"];

    /// <inheritdoc />
    protected override async Task<AgentResult> HandleAsync(AgentRequest request, CancellationToken ct)
    {
        // ===== 只读：查询卡片列表 =====
        if (!request.UserInput.Contains("挂失", StringComparison.Ordinal)
            && !request.UserInput.Contains("冻结", StringComparison.Ordinal)
            && !request.UserInput.Contains("解挂", StringComparison.Ordinal))
        {
            var cards = await _coreBank.ListCardsAsync(request.UserId, ct);
            if (cards.Count == 0) return AgentResult.Fail("NO_CARD", "未绑定银行卡");

            var cardList = cards.Select(c => new Dictionary<string, object?>
            {
                // 卡号必须脱敏，这是 L3 数据的强制要求
                ["card_no"] = DataMasker.MaskBankCard(c.CardNo),
                ["card_type"] = c.CardType,
                ["status"] = c.Status,
                ["bound_phone"] = DataMasker.MaskPhone(c.BoundPhone),
                ["daily_limit"] = c.DailyLimit
            }).ToList();

            return AgentResult.Ok(
                $"您共有 {cards.Count} 张银行卡",
                "card.list",
                new Dictionary<string, object?> { ["cards"] = cardList });
        }

        // ===== 写操作：挂失/冻结/解挂 =====
        var cardNo = SlotReader.String(request, "card_no");
        if (string.IsNullOrEmpty(cardNo))
        {
            return AgentResult.Fail("CARD_MISSING", "请指定要操作的银行卡号");
        }

        var newStatus = request.UserInput.Contains("挂失", StringComparison.Ordinal) ? "挂失"
            : request.UserInput.Contains("冻结", StringComparison.Ordinal) ? "冻结"
            : "正常";

        // 卡片操作同样要过合规守卫：防止越权操作他人卡片
        var compliance = _compliance.Evaluate(new ComplianceContext
        {
            UserId = request.UserId,
            Scenario = "card",
            Intent = "card.status",
            Extra = new Dictionary<string, object?> { ["card_no"] = cardNo }
        });

        if (!compliance.Allowed)
        {
            return AgentResult.Fail("COMPLIANCE_REJECTED", compliance.Reason);
        }

        var updated = await _coreBank.UpdateCardStatusAsync(
            cardNo, newStatus, "用户通过 AI 助手申请", ct);

        if (updated is null)
        {
            return AgentResult.Fail("CARD_NOT_FOUND", "未找到该银行卡");
        }

        // 关键操作必须审计留痕
        await Audit.WriteAsync(new AuditEvent
        {
            AuditId = Guid.NewGuid().ToString("N")[..12],
            Timestamp = DateTimeOffset.UtcNow,
            ActorType = "AGENT",
            ActorId = Id.Value,
            Operation = "card.status.update",
            Scenario = "card",
            Decision = "SUCCESS",
            DecisionReason = $"状态变更为 {newStatus}",
            ComplianceRule = "card.ownership.checked"
        }, ct);

        return new AgentResult
        {
            Success = true,
            SideEffectCommitted = true,
            Intent = "card.status.updated",
            Content = $"卡片 {DataMasker.MaskBankCard(cardNo)} 状态已更新为「{newStatus}」",
            Data = new Dictionary<string, object?>
            {
                ["card_no"] = DataMasker.MaskBankCard(cardNo),
                ["new_status"] = newStatus
            }
        };
    }
}
