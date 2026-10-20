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
    /// <summary>
    /// 列出核心系统所接入的银行。模拟系统里有多个彼此独立的银行核心系统，
    /// 插件需要知道每张卡是哪家银行发的，才能正确展示「跨行卡片」。
    /// </summary>
    Task<IReadOnlyList<BankSnapshot>> ListBanksAsync(CancellationToken ct = default);
    /// <summary>
    /// 查询公募基金代销清单。公募基金是代销产品，筛选条件全部可空，空表示不限。
    /// </summary>
    Task<IReadOnlyList<FundSnapshot>> ListFundsAsync(
        FundQuery query, CancellationToken ct = default);
    /// <summary>按基金代码查询单只公募基金详情。基金不存在时返回 null。</summary>
    Task<FundSnapshot?> GetFundAsync(string fundCode, CancellationToken ct = default);
    /// <summary>按基金代码查询净值历史，按日期升序返回。</summary>
    Task<IReadOnlyList<FundNavPointSnapshot>> GetFundNavAsync(
        string fundCode, int days, CancellationToken ct = default);
    /// <summary>
    /// 查询客户在各开户行的客户档案。
    ///
    /// 客户风险等级是**行内**评级（同一客户在不同银行可以是不同等级），
    /// 理财场景必须靠它做风险匹配 —— R2 客户不能买 R4 产品。
    /// </summary>
    /// <param name="userId">客户号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>各行客户档案；查不到时返回空集合。</returns>
    Task<IReadOnlyList<CustomerSnapshot>> GetCustomersAsync(
        string userId, CancellationToken ct = default);
}

/// <summary>银行快照。</summary>
public sealed record BankSnapshot
{
    /// <summary>行代码（如 ICBC）。</summary>
    public required string BankCode { get; init; }

    /// <summary>银行全称（如 中国工商银行）。</summary>
    public required string BankName { get; init; }

    /// <summary>银行简称（如 工商银行）。</summary>
    public required string ShortName { get; init; }
}

/// <summary>账户快照。</summary>
public sealed record AccountSnapshot
{
    public required string AccountNo { get; init; }
    public required string UserId { get; init; }
    public required string HolderName { get; init; }

    /// <summary>开户行代码（如 ICBC）。跨行账户时用于区分资金归属行。</summary>
    public string BankCode { get; init; } = "";

    /// <summary>开户行全称。</summary>
    public string BankName { get; init; } = "";

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

    /// <summary>发卡行代码（如 ICBC）。跨行卡片的关键标识。</summary>
    public string BankCode { get; init; } = "";

    /// <summary>发卡行全称（如 中国工商银行）。</summary>
    public string BankName { get; init; } = "";

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

    /// <summary>开户行代码（如 ICBC）。同名收款人在不同银行会有不同的 BankCode。</summary>
    public string BankCode { get; init; } = "";

    /// <summary>开户行全称。</summary>
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

/// <summary>基金排行维度。插件侧用来决定按哪个指标排序。</summary>
public enum FundRankingMetric
{
    /// <summary>按最新一日增长率排序。</summary>
    Daily = 1,

    /// <summary>按近一月收益率排序。</summary>
    Monthly = 2,

    /// <summary>按近一年收益率排序。</summary>
    Yearly = 3,
}

/// <summary>
/// 公募基金快照。
/// 只放插件真正需要的字段：插件要回答「有哪些基金、风险等级多少、净值多少、谁在代销」，
/// 投资范围、业绩比较基准这类长文本不进快照，避免把核心系统的报文原样搬进插件上下文。
/// </summary>
public sealed record FundSnapshot
{
    /// <summary>基金代码，6 位数字。</summary>
    public required string FundCode { get; init; }

    /// <summary>基金简称。</summary>
    public required string FundName { get; init; }

    /// <summary>基金全称。</summary>
    public string FundFullName { get; init; } = "";

    /// <summary>基金类型中文名（货币型 / 债券型 / 混合型 / 股票型 / 指数型 / QDII / FOF）。</summary>
    public required string Category { get; init; }

    /// <summary>基金风险等级（R1-R5）。</summary>
    public required string RiskLevel { get; init; }

    /// <summary>基金管理人。</summary>
    public string FundCompany { get; init; } = "";

    /// <summary>现任基金经理。</summary>
    public string FundManager { get; init; } = "";

    /// <summary>最新净值日期（自然日）。</summary>
    public DateTime NavDate { get; init; }

    /// <summary>单位净值。</summary>
    public decimal UnitNav { get; init; }

    /// <summary>累计净值。</summary>
    public decimal AccumulatedNav { get; init; }

    /// <summary>最新一日单位净值增长率（百分数）。</summary>
    public decimal DailyGrowthRate { get; init; }

    /// <summary>近一月收益率（百分数）。</summary>
    public decimal MonthlyReturn { get; init; }

    /// <summary>近一年收益率（百分数）。</summary>
    public decimal YearlyReturn { get; init; }

    /// <summary>成立以来涨幅（百分数）。</summary>
    public decimal SinceInceptionReturn { get; init; }

    /// <summary>基金规模（亿元）。</summary>
    public decimal FundScale { get; init; }

    /// <summary>申赎状态中文名。</summary>
    public string SubscriptionStatus { get; init; } = "";

    /// <summary>起购金额（元）。</summary>
    public decimal MinPurchaseAmount { get; init; }

    /// <summary>代销该基金的银行行代码，逗号分隔。插件据此判断「客户所在的行有没有代销」。</summary>
    public string DistributorBankCodes { get; init; } = "";

    /// <summary>风险提示。</summary>
    public string RiskNote { get; init; } = "";
}

/// <summary>基金净值历史点快照。</summary>
public sealed record FundNavPointSnapshot
{
    /// <summary>基金代码。</summary>
    public required string FundCode { get; init; }

    /// <summary>净值日期（自然日）。</summary>
    public required DateTime NavDate { get; init; }

    /// <summary>单位净值。</summary>
    public decimal UnitNav { get; init; }

    /// <summary>累计净值。</summary>
    public decimal AccumulatedNav { get; init; }

    /// <summary>当日单位净值增长率（百分数）。</summary>
    public decimal DailyGrowthRate { get; init; }
}

/// <summary>客户档案快照。</summary>
public sealed record CustomerSnapshot
{
    /// <summary>客户号。</summary>
    public required string UserId { get; init; }

    /// <summary>开户行代码（如 ICBC）。客户评级是行内评级，因此必须带行代码。</summary>
    public required string BankCode { get; init; }

    /// <summary>开户行全称。</summary>
    public string BankName { get; init; } = "";

    /// <summary>客户姓名。</summary>
    public string Name { get; init; } = "";

    /// <summary>行内风险等级（R1-R4）。理财风险匹配的依据。</summary>
    public string RiskLevel { get; init; } = "";

    /// <summary>客户等级（VIP / 金卡 / 普通等）。</summary>
    public string CustomerLevel { get; init; } = "";
}

/// <summary>公募基金查询条件。空值表示该维度不限。</summary>
public sealed record FundQuery
{
    /// <summary>基金类型（货币型 / 债券型 / 混合型 / 股票型 / 指数型 / QDII / FOF）。</summary>
    public string? Category { get; init; }

    /// <summary>基金风险等级（R1-R5）。</summary>
    public string? RiskLevel { get; init; }

    /// <summary>基金公司（模糊匹配）。</summary>
    public string? Company { get; init; }

    /// <summary>基金代码或简称（模糊匹配）。</summary>
    public string? Keyword { get; init; }

    /// <summary>限定代销行代码；为空表示不限。</summary>
    public string? BankCode { get; init; }

    /// <summary>最多返回多少只。核心系统有上限，插件不要传过大的值。</summary>
    public int Limit { get; init; } = 20;
}
