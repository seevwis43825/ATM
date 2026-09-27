// ===== 领域模型：账户交易流水（记账明细）=====

namespace MockBank.Api.Domain;

/// <summary>资金流向方向。</summary>
public enum TransactionDirection
{
    /// <summary>收入（贷记），资金流入账户。</summary>
    Income = 1,

    /// <summary>支出（借记），资金流出账户。</summary>
    Expense = 2,
}

/// <summary>交易渠道。</summary>
public enum TransactionChannel
{
    /// <summary>柜面。</summary>
    Counter = 1,

    /// <summary>手机银行。</summary>
    MobileApp = 2,

    /// <summary>网上银行。</summary>
    OnlineBank = 3,

    /// <summary>ATM 自助设备。</summary>
    Atm = 4,

    /// <summary>POS 刷卡消费。</summary>
    Pos = 5,

    /// <summary>快捷支付 / 第三方支付。</summary>
    QuickPay = 6,

    /// <summary>行内转账。</summary>
    InternalTransfer = 7,
}

/// <summary>账户交易流水实体，模拟核心系统的交易明细表。</summary>
public sealed record BankTransaction
{
    /// <summary>交易流水号，形如 TX202609010000001。</summary>
    public required string TxNo { get; init; }

    /// <summary>交易归属客户号。</summary>
    public required string UserId { get; init; }

    /// <summary>交易发生的账号。</summary>
    public required string AccountNo { get; init; }

    /// <summary>资金方向（收入 / 支出）。</summary>
    public required TransactionDirection Direction { get; init; }

    /// <summary>交易金额（恒为正数，方向由 <see cref="Direction"/> 决定）。</summary>
    public decimal Amount { get; init; }

    /// <summary>交易分类，如 餐饮 / 购物 / 工资。</summary>
    public required string Category { get; init; }

    /// <summary>商户名称或摘要。</summary>
    public required string Merchant { get; init; }

    /// <summary>对手方名称，如 海底捞火锅 / 某某公司。</summary>
    public required string CounterpartyName { get; init; }

    /// <summary>对手方账号（转账类交易才有值）。</summary>
    public string? CounterpartyAccountNo { get; init; }

    /// <summary>交易附言。</summary>
    public string? Remark { get; init; }

    /// <summary>交易渠道。</summary>
    public TransactionChannel Channel { get; init; } = TransactionChannel.MobileApp;

    /// <summary>交易币种。</summary>
    public string Currency { get; init; } = "CNY";

    /// <summary>交易发生时间。</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>带符号金额：收入为正，支出为负。</summary>
    public decimal SignedAmount =>
        Direction == TransactionDirection.Income ? Amount : -Amount;

    /// <summary>资金方向的中文描述。</summary>
    public string DirectionText =>
        Direction == TransactionDirection.Income ? "收入" : "支出";

    /// <summary>交易渠道的中文描述。</summary>
    public string ChannelText =>
        Channel switch
        {
            TransactionChannel.Counter => "柜面",
            TransactionChannel.MobileApp => "手机银行",
            TransactionChannel.OnlineBank => "网银",
            TransactionChannel.Atm => "ATM",
            TransactionChannel.Pos => "POS消费",
            TransactionChannel.QuickPay => "快捷支付",
            TransactionChannel.InternalTransfer => "行内转账",
            _ => "其他",
        };
}
