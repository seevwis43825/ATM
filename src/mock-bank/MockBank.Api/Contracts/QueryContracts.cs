// ===== 契约：账户、流水分页、账单汇总等查询响应契约 =====

using MockBank.Api.Domain;

namespace MockBank.Api.Contracts;

/// <summary>账户查询响应体。</summary>
/// <param name="AccountNo">账号。</param>
/// <param name="UserId">客户号。</param>
/// <param name="AccountType">账户类型中文名（储蓄账户 / 信用账户）。</param>
/// <param name="ProductName">产品名称。</param>
/// <param name="Balance">账户余额（信用账户为已用额度的负值）。</param>
/// <param name="CreditLimit">信用额度。</param>
/// <param name="CreditUsed">已用信用额度。</param>
/// <param name="AvailableBalance">可用余额 / 可用额度。</param>
/// <param name="DailyLimit">单日限额。</param>
/// <param name="Status">账户状态中文名。</param>
/// <param name="Currency">币种。</param>
/// <param name="OpenDate">开户日期。</param>
/// <param name="BranchName">开户网点。</param>
/// <param name="CardNo">绑定的卡号（若已制卡）。</param>
public sealed record AccountResponse(
    string AccountNo,
    string UserId,
    string AccountType,
    string ProductName,
    decimal Balance,
    decimal CreditLimit,
    decimal CreditUsed,
    decimal AvailableBalance,
    decimal DailyLimit,
    string Status,
    string Currency,
    DateTimeOffset OpenDate,
    string BranchName,
    string? CardNo);

/// <summary>账户余额查询响应体。</summary>
/// <param name="AccountNo">账号。</param>
/// <param name="UserId">客户号。</param>
/// <param name="AccountType">账户类型中文名。</param>
/// <param name="Balance">账户余额。</param>
/// <param name="AvailableBalance">可用余额 / 可用额度。</param>
/// <param name="CreditLimit">信用额度。</param>
/// <param name="CreditUsed">已用信用额度。</param>
/// <param name="Currency">币种。</param>
/// <param name="Status">账户状态中文名。</param>
/// <param name="AsOf">余额时间戳。</param>
public sealed record AccountBalanceResponse(
    string AccountNo,
    string UserId,
    string AccountType,
    decimal Balance,
    decimal AvailableBalance,
    decimal CreditLimit,
    decimal CreditUsed,
    string Currency,
    string Status,
    DateTimeOffset AsOf);

/// <summary>账户限额配置响应体。</summary>
/// <param name="AccountNo">账号。</param>
/// <param name="UserId">客户号。</param>
/// <param name="AccountType">账户类型中文名。</param>
/// <param name="DailyLimit">单日交易限额。</param>
/// <param name="CreditLimit">信用额度。</param>
/// <param name="CreditUsed">已用信用额度。</param>
/// <param name="AvailableCredit">剩余可用额度。</param>
/// <param name="SingleTransactionLimit">单笔交易限额（演示取单日限额的十分之一，上限 50000）。</param>
/// <param name="Currency">币种。</param>
/// <param name="Status">账户状态中文名。</param>
public sealed record AccountLimitResponse(
    string AccountNo,
    string UserId,
    string AccountType,
    decimal DailyLimit,
    decimal CreditLimit,
    decimal CreditUsed,
    decimal AvailableCredit,
    decimal SingleTransactionLimit,
    string Currency,
    string Status);

/// <summary>交易流水查询响应体。</summary>
/// <param name="TxNo">交易流水号。</param>
/// <param name="UserId">客户号。</param>
/// <param name="AccountNo">账号。</param>
/// <param name="Direction">资金方向中文名（收入 / 支出）。</param>
/// <param name="Amount">交易金额（正数）。</param>
/// <param name="SignedAmount">带符号金额（收入为正，支出为负）。</param>
/// <param name="Category">交易分类。</param>
/// <param name="Merchant">商户名称。</param>
/// <param name="CounterpartyName">对手方名称。</param>
/// <param name="CounterpartyAccountNo">对手方账号。</param>
/// <param name="Channel">交易渠道中文名。</param>
/// <param name="Remark">交易附言。</param>
/// <param name="Currency">币种。</param>
/// <param name="Timestamp">交易时间。</param>
public sealed record TransactionResponse(
    string TxNo,
    string UserId,
    string AccountNo,
    string Direction,
    decimal Amount,
    decimal SignedAmount,
    string Category,
    string Merchant,
    string CounterpartyName,
    string? CounterpartyAccountNo,
    string Channel,
    string? Remark,
    string Currency,
    DateTimeOffset Timestamp);

/// <summary>
/// 端点 OpenAPI 描述元数据。由端点层通过 <c>WithMetadata</c> 挂载，
/// 供 <see cref="Services.OpenApiDocumentBuilder"/> 生成 OpenAPI 文档。
/// </summary>
/// <param name="OperationId">操作唯一标识。</param>
/// <param name="Summary">一句话摘要。</param>
/// <param name="Description">详细说明。</param>
/// <param name="Tags">标签分组。</param>
/// <param name="RequestBodyType">请求体 CLR 类型；无请求体时为 null。</param>
/// <param name="Hidden">是否为 true 时不输出到 OpenAPI 文档。</param>
public sealed record ApiOperationMetadata(
    string OperationId,
    string Summary,
    string Description,
    string[] Tags,
    Type? RequestBodyType = null,
    bool Hidden = false);

/// <summary>分页查询结果包装体。</summary>
/// <typeparam name="T">列表元素类型。</typeparam>
/// <param name="Items">当前页数据。</param>
/// <param name="Page">当前页码，从 1 开始。</param>
/// <param name="PageSize">每页条数。</param>
/// <param name="Total">符合条件的总条数。</param>
/// <param name="TotalPages">总页数。</param>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int Total,
    int TotalPages)
{
    /// <summary>按列表构造分页结果。</summary>
    /// <param name="items">全量数据。</param>
    /// <param name="page">当前页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <returns>分页结果。</returns>
    public static PagedResult<T> From(IReadOnlyList<T> items, int page, int pageSize)
    {
        var total = items.Count;
        var totalPages = pageSize <= 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize);
        var skip = Math.Max(0, (page - 1) * pageSize);
        var slice = items.Skip(skip).Take(pageSize).ToList();
        return new PagedResult<T>(slice, page, pageSize, total, totalPages);
    }
}

/// <summary>账单分类聚合项。</summary>
/// <param name="Category">分类名称。</param>
/// <param name="Amount">该分类支出金额。</param>
/// <param name="Percent">占总支出的百分比（保留两位小数）。</param>
/// <param name="Count">该分类交易笔数。</param>
public sealed record CategoryAggregate(string Category, decimal Amount, decimal Percent, int Count);

/// <summary>账单日趋势项。</summary>
/// <param name="Date">日期（yyyy-MM-dd）。</param>
/// <param name="Day">当月第几日。</param>
/// <param name="Income">当日收入合计。</param>
/// <param name="Expense">当日支出合计。</param>
/// <param name="Count">当日交易笔数。</param>
public sealed record DailyTrendItem(string Date, int Day, decimal Income, decimal Expense, int Count);

/// <summary>月度账单汇总响应体。</summary>
/// <param name="AccountNo">账号。</param>
/// <param name="UserId">客户号。</param>
/// <param name="Month">账单月份（yyyy-MM）。</param>
/// <param name="Currency">币种。</param>
/// <param name="TotalIncome">当月收入合计。</param>
/// <param name="TotalExpense">当月支出合计。</param>
/// <param name="NetAmount">当月净流入（收入 - 支出）。</param>
/// <param name="TransactionCount">当月交易笔数。</param>
/// <param name="CurrentBalance">账户当前余额（演示账本不追溯历史余额）。</param>
/// <param name="Categories">分类聚合（按支出金额倒序）。</param>
/// <param name="DailyTrend">按日趋势（覆盖整月每一天）。</param>
public sealed record MonthlyStatementResponse(
    string AccountNo,
    string UserId,
    string Month,
    string Currency,
    decimal TotalIncome,
    decimal TotalExpense,
    decimal NetAmount,
    int TransactionCount,
    decimal CurrentBalance,
    IReadOnlyList<CategoryAggregate> Categories,
    IReadOnlyList<DailyTrendItem> DailyTrend);

/// <summary>系统健康检查响应体。</summary>
/// <param name="Status">健康状态，正常为 OK。</param>
/// <param name="Service">服务名。</param>
/// <param name="Version">服务版本。</param>
/// <param name="ServerTime">服务器时间。</param>
/// <param name="UptimeSeconds">进程运行时长（秒）。</param>
public sealed record HealthResponse(
    string Status,
    string Service,
    string Version,
    DateTimeOffset ServerTime,
    double UptimeSeconds);

/// <summary>管理端统计响应体。</summary>
/// <param name="TotalCustomers">客户总数。</param>
/// <param name="TotalAccounts">账户总数。</param>
/// <param name="TotalCards">银行卡总数。</param>
/// <param name="TotalTransactions">交易流水总数。</param>
/// <param name="TotalDepositBalance">储蓄存款余额合计。</param>
/// <param name="TotalCreditUsed">信用已用额度合计。</param>
/// <param name="TotalCreditLimit">信用额度合计。</param>
/// <param name="TotalWealthAum">理财产品募集规模合计。</param>
/// <param name="TotalAssets">总资产（可用存款 + 理财在管规模）。</param>
/// <param name="CurrentMonth">当前自然月（yyyy-MM）。</param>
/// <param name="CurrentMonthTransactionCount">当月交易笔数。</param>
/// <param name="GeneratedAt">统计生成时间。</param>
public sealed record AdminStatsResponse(
    int TotalCustomers,
    int TotalAccounts,
    int TotalCards,
    int TotalTransactions,
    decimal TotalDepositBalance,
    decimal TotalCreditUsed,
    decimal TotalCreditLimit,
    decimal TotalWealthAum,
    decimal TotalAssets,
    string CurrentMonth,
    int CurrentMonthTransactionCount,
    DateTimeOffset GeneratedAt);
