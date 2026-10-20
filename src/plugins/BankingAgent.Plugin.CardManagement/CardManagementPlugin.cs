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
    public override IReadOnlyList<string> TriggerKeywords =>
        ["银行卡", "卡片", "挂失", "解挂", "冻结", "限额", "卡"];

    /// <inheritdoc />
    protected override async Task<AgentResult> HandleAsync(AgentRequest request, CancellationToken ct)
    {
        var intent = IntentOf(request);
        var action = CardStatusResolver.Resolve(request.UserInput);

        // ===== 写操作：挂失 / 解挂 / 冻结 / 解冻 =====
        //
        // 进入写分支的依据是**语义解析器在原文中找到了明确的操作说法**
        // （「丢了」「不见了」「被冻了」等），而不是「模型说要做」——
        // 写操作有副作用，判定必须以用户原话为准，模型只作参考。
        // 代价是：用户若只说「帮我处理一下卡」，我们不知道要做成什么，
        // 这种情况交给下面的追问分支，绝不猜。
        if (action is CardStatusAction.ReportLost or CardStatusAction.UndoLost
            or CardStatusAction.Freeze or CardStatusAction.Unfreeze)
        {
            return await ApplyStatusChangeAsync(request, action, ct);
        }

        if (action == CardStatusAction.AdjustLimit)
        {
            return AgentResult.Fail(
                "LIMIT_ADJUST_NOT_SUPPORTED",
                "单日限额调整暂未在对话入口开放，限额变更需要临柜或手机银行办理。");
        }

        // 模型判定这是「改卡状态」类动作，但原文里没找到具体操作 ——
        // 说明用户的意思不够明确，反问一次比猜一个动作安全。
        if (intent.IsAction("card.status"))
        {
            return AgentResult.Fail(
                "CARD_ACTION_UNSPECIFIED",
                "请说明要对卡片做什么：挂失（丢了 / 不见了）、解挂（找回来了）、冻结、还是解冻？");
        }

        // ===== 只读：查询卡片列表（跨行：一次返回所有银行发行的卡）=====
        var cards = await _coreBank.ListCardsAsync(request.UserId, ct);
        if (cards.Count == 0) return AgentResult.Fail("NO_CARD", "未绑定银行卡");

        var cardList = cards.Select(c => new Dictionary<string, object?>
        {
            // 卡号必须脱敏，这是 L3 数据的强制要求
            ["card_no"] = DataMasker.MaskBankCard(c.CardNo),
            // 发卡行信息：跨行卡片场景下，用户需要知道每张卡是哪家银行发的
            ["bank_code"] = c.BankCode,
            ["bank_name"] = c.BankName,
            ["card_type"] = c.CardType,
            ["status"] = c.Status,
            ["bound_phone"] = DataMasker.MaskPhone(c.BoundPhone),
            ["daily_limit"] = c.DailyLimit
        }).ToList();

        // 按发卡行分组统计，一眼看清"我有几家银行的卡"。
        var byBank = cards
            .GroupBy(c => c.BankName)
            .Select(g => $"{g.Key} {g.Count()} 张")
            .ToList();

        return AgentResult.Ok(
            $"您共有 {cards.Count} 张银行卡，分布在 {byBank.Count} 家银行：{string.Join("，", byBank)}",
            "card.list",
            new Dictionary<string, object?>
            {
                ["card_count"] = cards.Count,
                ["bank_count"] = byBank.Count,
                ["cards"] = cardList
            });
    }

    /// <summary>
    /// 执行卡片状态变更：定位目标卡 → 过合规守卫 → 调核心系统 → 审计留痕。
    /// </summary>
    /// <param name="request">当前请求。</param>
    /// <param name="action">要执行的状态操作。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>变更结果。</returns>
    private async Task<AgentResult> ApplyStatusChangeAsync(
        AgentRequest request, CardStatusAction action, CancellationToken ct)
    {
        var resolution = await ResolveTargetCardAsync(request, ct);
        if (resolution.Card is null)
        {
            // 有多张卡时不能替用户挑一张：把候选连同开户行列出来，让用户指认。
            var options = resolution.Candidates.Count == 0
                ? string.Empty
                : "你名下有：" + string.Join("；", resolution.Candidates.Select(c =>
                    $"{c.BankName} {DataMasker.MaskBankCard(c.CardNo)}（{c.CardType}）"));

            return AgentResult.Fail(
                "CARD_NOT_SPECIFIED",
                "没能确定要处理哪张卡。" + options + "。请指定卡号，或说明是哪个银行的卡。");
        }

        var target = resolution.Card;
        var newStatus = CardStatusResolver.ToCoreStatusText(action)!;

        // 卡片操作同样要过合规守卫：防止越权操作他人卡片
        var compliance = _compliance.Evaluate(new ComplianceContext
        {
            UserId = request.UserId,
            Scenario = "card",
            Intent = "card.status",
            Extra = new Dictionary<string, object?> { ["card_no"] = target.CardNo }
        });

        if (!compliance.Allowed)
        {
            return AgentResult.Fail("COMPLIANCE_REJECTED", compliance.Reason);
        }

        var updated = await _coreBank.UpdateCardStatusAsync(
            target.CardNo, newStatus, "用户通过 AI 助手申请", ct);

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
            Intent = "card.status",
            Decision = "SUCCESS",
            DecisionReason = $"状态变更为 {newStatus}",
            ComplianceRule = "card.ownership.checked",
            Extra = new Dictionary<string, object?>
            {
                ["card_no"] = DataMasker.MaskBankCard(target.CardNo),
                ["bank_code"] = target.BankCode,
                ["requested_action"] = CardStatusResolver.Describe(action)
            }
        }, ct);

        var bankLabel = string.IsNullOrWhiteSpace(target.BankName) ? "" : $"（{target.BankName}）";

        return new AgentResult
        {
            Success = true,
            SideEffectCommitted = true,
            Intent = "card.status.updated",
            Content = $"卡片 {DataMasker.MaskBankCard(target.CardNo)}{bankLabel} 状态已更新为「{newStatus}」",
            Data = new Dictionary<string, object?>
            {
                ["card_no"] = DataMasker.MaskBankCard(target.CardNo),
                ["bank_code"] = target.BankCode,
                ["bank_name"] = target.BankName,
                ["new_status"] = newStatus,
                ["requested_action"] = CardStatusResolver.Describe(action)
            }
        };
    }

    /// <summary>
    /// 定位要操作的卡片。
    ///
    /// 顺序：显式卡号 → 原文中提到的银行 → 唯一的一张卡。
    /// 跨行场景下「我的建行卡丢了」这类说法要能落到正确的那张卡上，
    /// 所以第三步要求「确实只有一张」才自动选，否则请用户指认。
    /// </summary>
    /// <param name="request">当前请求。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>定位结果：命中的卡片，或供用户指认的候选列表。</returns>
    private async Task<CardResolution> ResolveTargetCardAsync(AgentRequest request, CancellationToken ct)
    {
        var cards = await _coreBank.ListCardsAsync(request.UserId, ct);
        if (cards.Count == 0) return CardResolution.Unresolved([]);

        var explicitCardNo = SlotReader.String(request, "card_no");
        if (!string.IsNullOrWhiteSpace(explicitCardNo))
        {
            var byNumber = cards.FirstOrDefault(c =>
                string.Equals(c.CardNo, explicitCardNo.Trim(), StringComparison.Ordinal));
            return byNumber is not null
                ? CardResolution.Resolved(byNumber)
                : CardResolution.Unresolved(cards);
        }

        // 原文里提到了哪家银行？
        var banks = await _coreBank.ListBanksAsync(ct);
        var mentionedBank = banks.FirstOrDefault(bank =>
            !string.IsNullOrWhiteSpace(bank.BankName)
            && request.UserInput.Contains(bank.BankName, StringComparison.Ordinal));

        if (mentionedBank is not null)
        {
            var atBank = cards.Where(c =>
                string.Equals(c.BankCode, mentionedBank.BankCode, StringComparison.OrdinalIgnoreCase)).ToList();
            if (atBank.Count == 1) return CardResolution.Resolved(atBank[0]);
            if (atBank.Count > 1) return CardResolution.Unresolved(atBank);
        }

        // 只有一张卡时才敢自动选。
        return cards.Count == 1
            ? CardResolution.Resolved(cards[0])
            : CardResolution.Unresolved(cards);
    }

    /// <summary>目标卡片的定位结果。</summary>
    /// <param name="Card">命中的卡片；未命中时为 null。</param>
    /// <param name="Candidates">供用户指认的候选卡片。</param>
    private sealed record CardResolution(CardSnapshot? Card, IReadOnlyList<CardSnapshot> Candidates)
    {
        /// <summary>构造命中结果。</summary>
        /// <param name="card">命中的卡片。</param>
        /// <returns>定位结果。</returns>
        public static CardResolution Resolved(CardSnapshot card) => new(card, [card]);

        /// <summary>构造未命中结果。</summary>
        /// <param name="candidates">候选卡片。</param>
        /// <returns>定位结果。</returns>
        public static CardResolution Unresolved(IReadOnlyList<CardSnapshot> candidates) => new(null, candidates);
    }
}
