// ===== 领域模型：银行账户主档（储蓄账户 / 信用账户）=====

namespace MockBank.Api.Domain;

/// <summary>账户类型。</summary>
public enum AccountType
{
    /// <summary>储蓄账户（一卡通 / 借记账户）。</summary>
    Savings = 1,

    /// <summary>信用账户（信用卡账户）。</summary>
    Credit = 2,
}

/// <summary>账户状态。</summary>
public enum AccountStatus
{
    /// <summary>正常，可正常办理业务。</summary>
    Normal = 1,

    /// <summary>冻结，被风控或司法冻结。</summary>
    Frozen = 2,

    /// <summary>已销户。</summary>
    Closed = 3,
}

/// <summary>银行账户实体，模拟核心系统的账户表。</summary>
public sealed record Account
{
    /// <summary>账号，19 位，形如 6222020200000001。</summary>
    public required string AccountNo { get; init; }

    /// <summary>账户归属客户号。</summary>
    public required string UserId { get; init; }

    /// <summary>账户类型（储蓄 / 信用）。</summary>
    public required AccountType AccountType { get; init; }

    /// <summary>产品名称，如“薪金卡”“白金信用卡”。</summary>
    public string ProductName { get; init; } = "储蓄卡";

    /// <summary>账户余额。储蓄账户为正数存款余额；信用账户为已用额度的负值。</summary>
    public decimal Balance { get; init; }

    /// <summary>信用额度，储蓄账户固定为 0。</summary>
    public decimal CreditLimit { get; init; }

    /// <summary>已用信用额度，储蓄账户固定为 0。</summary>
    public decimal CreditUsed { get; init; }

    /// <summary>单日交易限额。</summary>
    public decimal DailyLimit { get; init; }

    /// <summary>账户状态。</summary>
    public AccountStatus Status { get; init; } = AccountStatus.Normal;

    /// <summary>币种，默认人民币。</summary>
    public string Currency { get; init; } = "CNY";

    /// <summary>开户日期。</summary>
    public DateTimeOffset OpenDate { get; init; }

    /// <summary>开户网点。</summary>
    public string BranchName { get; init; } = "模拟银行营业部";

    /// <summary>可用余额：储蓄账户等于 <see cref="Balance"/>，信用账户等于剩余可用额度。</summary>
    public decimal AvailableBalance =>
        AccountType == AccountType.Credit ? CreditLimit - CreditUsed : Balance;

    /// <summary>账户类型的中文描述。</summary>
    public string AccountTypeText =>
        AccountType == AccountType.Credit ? "信用账户" : "储蓄账户";

    /// <summary>账户状态的中文描述。</summary>
    public string StatusText =>
        Status switch
        {
            AccountStatus.Normal => "正常",
            AccountStatus.Frozen => "冻结",
            AccountStatus.Closed => "销户",
            _ => "未知",
        };
}

/// <summary>账户类型与状态的文本解析辅助方法。</summary>
public static class AccountEnums
{
    /// <summary>把 "储蓄" / "Credit" 之类的文本解析为 <see cref="AccountType"/>。</summary>
    /// <param name="value">待解析文本。</param>
    /// <param name="type">解析成功时输出账户类型。</param>
    /// <returns>解析成功返回 true。</returns>
    public static bool TryParseAccountType(string? value, out AccountType type)
    {
        type = AccountType.Savings;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "savings":
            case "debit":
            case "储蓄":
            case "借记":
                type = AccountType.Savings;
                return true;
            case "credit":
            case "信用卡":
            case "信用":
                type = AccountType.Credit;
                return true;
            default:
                return false;
        }
    }
}
