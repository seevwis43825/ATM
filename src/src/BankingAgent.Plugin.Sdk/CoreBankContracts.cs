// ===== Core Bank 客户端契约 =====
// 插件只通过此契约访问银行核心系统，绝不直接依赖 Mock Bank 的 DTO，
// 从而保证从模拟环境切换到真实 CBS 时插件零改动。

namespace BankingAgent.PluginSdk;

/// <summary>银行核心系统统一入口。所有资金相关操作必须经由此客户端。</summary>
public interface ICoreBankClient
{
    Task<AccountSnapshot?> GetAccountAsync(string accountNo, CancellationToken ct = default);
    Task<IReadOnlyList<AccountSnapshot>> ListAccountsAsync(string userId, CancellationToken ct = default);
    Task<TransferResult> ExecuteTransferAsync(TransferCommand cmd, CancellationToken ct = default);
    Task<IReadOnlyList<TransactionRecord>> ListTransactionsAsync(
        TransactionQuery query, CancellationToken ct = default);
    Task<MonthlyStatement?> GetMonthlyStatementAsync(
        string accountNo, int year, int month, CancellationToken ct = default);
    Task<IReadOnlyList<CardSnapshot>> ListCardsAsync(string userId, CancellationToken ct = default);
    /// <summary>
    /// 按姓名或手机号检索行内收款人。
    /// 转账场景下用户只会说「给李华转 500」，账号由银行侧解析，
    /// 因此收款人检索属于核心系统能力，而不是插件的私有逻辑。
    /// </summary>
    Task<IReadOnlyList<Beneficiary>> SearchBeneficiariesAsync(
        string keyword, CancellationToken ct = default);
    Task<CardSnapshot?> UpdateCardStatusAsync(
        string cardNo, string newStatus, string reason, CancellationToken ct = default);
    Task<IReadOnlyList<WealthProduct>> ListProductsAsync(
        string? riskLevel = null, CancellationToken ct = default);
    Task<SubscriptionResult> SubscribeWealthAsync(
        string userId, string productCode, decimal amount, CancellationToken ct = default);
}

/// <summary>账户快照。</summary>
public sealed record AccountSnapshot
{
    public required string AccountNo { get; init; }
    public required string UserId { get; init; }
    public required string HolderName { get; init; }
    public string AccountType { get; init; } = "储蓄卡";
    public decimal Balance { get; init; }
    public decimal? CreditLimit { get; init; }
    public decimal CreditUsed { get; init; }
    public string Status { get; init; } = "正常";
    public string Currency { get; init; } = "CNY";
    public decimal DailyTransferLimit { get; init; } = 500000m;

    /// <summary>可用额度：储蓄卡为余额，信用卡为剩余信用额度。</summary>
    public decimal AvailableAmount => AccountType == "信用卡"
        ? Math.Max(0, (CreditLimit ?? 0) - CreditUsed)
        : Balance;
}

/// <summary>转账指令。</summary>
public sealed record TransferCommand
{
    public required string UserId { get; init; }
    public required string FromAccountNo { get; init; }
    public required string ToAccountNo { get; init; }
    public required decimal Amount { get; init; }
    public string Currency { get; init; } = "CNY";
    public string? Remark { get; init; }
    public string? IdempotencyKey { get; init; }
}

/// <summary>转账结果。</summary>
public sealed record TransferResult
{
    public required bool Success { get; init; }
    public string? TransactionNo { get; init; }
    public decimal BalanceAfter { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
}

/// <summary>流水记录。</summary>
public sealed record TransactionRecord
{
    public required string TransactionNo { get; init; }
    public required string AccountNo { get; init; }
    public required string UserId { get; init; }
    public required string Counterparty { get; init; }
    public required decimal Amount { get; init; }
    public required bool IsCredit { get; init; }
    public required string Category { get; init; }
    public string? Merchant { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
}

/// <summary>流水查询条件。</summary>
public sealed record TransactionQuery
{
    public required string UserId { get; init; }
    public string? AccountNo { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public string? Category { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

/// <summary>月度账单汇总。</summary>
public sealed record MonthlyStatement
{
    public required string AccountNo { get; init; }
    public required int Year { get; init; }
    public required int Month { get; init; }
    public decimal TotalIncome { get; init; }
    public decimal TotalExpense { get; init; }
    public int TransactionCount { get; init; }
    public required IReadOnlyList<CategoryBreakdown> Categories { get; init; }
    public required IReadOnlyList<DailyTrendPoint> DailyTrend { get; init; }
}

/// <summary>分类聚合项。</summary>
public sealed record CategoryBreakdown(string Category, decimal Amount, decimal Ratio);

/// <summary>日趋势点。</summary>
public sealed record DailyTrendPoint(int Day, decimal Amount);

/// <summary>卡片快照。</summary>
public sealed record CardSnapshot
{
    public required string CardNo { get; init; }
    public required string UserId { get; init; }
    public required string CardType { get; init; }
    public required string Status { get; init; }
    public string? BoundPhone { get; init; }
    public decimal DailyLimit { get; init; }
    public required string AccountNo { get; init; }
}

/// <summary>行内收款人：转账时可用的对手方账户。</summary>
public sealed record Beneficiary
{
    public required string Name { get; init; }
    public required string AccountNo { get; init; }
    public string AccountType { get; init; } = "储蓄账户";
    public string BankName { get; init; } = "";
}

/// <summary>理财产品。</summary>
public sealed record WealthProduct
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required string RiskLevel { get; init; }
    public required decimal AnnualRate { get; init; }
    public required decimal MinInvestment { get; init; }
    public required int TermDays { get; init; }
    public string Status { get; init; } = "在售";
}

/// <summary>认购结果。</summary>
public sealed record SubscriptionResult
{
    public required bool Success { get; init; }
    public string? SubscriptionNo { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}
