// ===== 领域模型：银行卡主档（借记卡 / 信用卡）与卡状态 =====

namespace MockBank.Api.Domain;

/// <summary>银行卡类型。</summary>
public enum CardType
{
    /// <summary>借记卡（储蓄卡）。</summary>
    Debit = 1,

    /// <summary>信用卡。</summary>
    Credit = 2,
}

/// <summary>银行卡状态。</summary>
public enum CardStatus
{
    /// <summary>正常。</summary>
    Normal = 1,

    /// <summary>已挂失。</summary>
    Lost = 2,

    /// <summary>已冻结。</summary>
    Frozen = 3,
}

/// <summary>银行卡实体，模拟核心系统的卡表，与账户一一对应。</summary>
public sealed record BankCard
{
    /// <summary>卡号，与账号一致。</summary>
    public required string CardNo { get; init; }

    /// <summary>绑定的账号。</summary>
    public required string AccountNo { get; init; }

    /// <summary>卡片归属客户号。</summary>
    public required string UserId { get; init; }

    /// <summary>卡片类型（借记卡 / 信用卡）。</summary>
    public required CardType CardType { get; init; }

    /// <summary>卡片状态（正常 / 挂失 / 冻结）。</summary>
    public CardStatus Status { get; init; } = CardStatus.Normal;

    /// <summary>卡片绑定手机号。</summary>
    public required string BoundPhone { get; init; }

    /// <summary>卡片单日交易限额。</summary>
    public decimal DailyLimit { get; init; }

    /// <summary>发卡日期。</summary>
    public DateTimeOffset IssueDate { get; init; }

    /// <summary>卡片有效期。</summary>
    public DateTimeOffset ExpiryDate { get; init; }

    /// <summary>发卡行名称。</summary>
    public string CardOrg { get; init; } = "模拟银行";

    /// <summary>最近一次状态变更的原因。</summary>
    public string? StatusReason { get; init; }

    /// <summary>最近一次状态变更时间。</summary>
    public DateTimeOffset? StatusChangedAt { get; init; }

    /// <summary>卡片类型的中文描述。</summary>
    public string CardTypeText => CardType == CardType.Credit ? "信用卡" : "借记卡";

    /// <summary>卡片状态的中文描述。</summary>
    public string StatusText =>
        Status switch
        {
            CardStatus.Normal => "正常",
            CardStatus.Lost => "挂失",
            CardStatus.Frozen => "冻结",
            _ => "未知",
        };
}

/// <summary>卡状态文本解析辅助方法。</summary>
public static class CardEnums
{
    /// <summary>支持的卡状态变更指令清单。</summary>
    public static readonly IReadOnlyList<string> SupportedStatusCommands =
        ["正常", "挂失", "解挂", "冻结", "解冻"];

    /// <summary>把 "挂失" / "lost" 之类的文本解析为 <see cref="CardStatus"/>。</summary>
    /// <param name="value">待解析文本。</param>
    /// <param name="status">解析成功时输出卡片状态。</param>
    /// <returns>解析成功返回 true。</returns>
    public static bool TryParseCardStatus(string? value, out CardStatus status)
    {
        status = CardStatus.Normal;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "正常":
            case "normal":
            case "解挂":
            case "unlost":
                status = CardStatus.Normal;
                return true;
            case "挂失":
            case "lost":
            case "reportlost":
                status = CardStatus.Lost;
                return true;
            case "冻结":
            case "frozen":
            case "freeze":
                status = CardStatus.Frozen;
                return true;
            case "解冻":
            case "unfreeze":
            case "unfrozen":
                status = CardStatus.Normal;
                return true;
            default:
                return false;
        }
    }
}
