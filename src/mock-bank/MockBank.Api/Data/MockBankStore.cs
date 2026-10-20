// ===== 跨行聚合账本门面 =====
//
// 职责：把 N 家银行（N 个独立数据库）的数据汇总成「一次查询」，
//      并为跨行资金变动提供「先取锁、再动账」的无死锁编排。
//
// 三个必须记住的规则：
//   1. 写操作必须按实体自带的 BankCode 路由到对应的那一行的数据库；
//      绝不能把 A 行的数据写进 B 行的库。
//   2. 跨行加锁按行代码的字典序固定顺序获取，否则两笔方向相反的跨行转账
//      会互相等待，死锁。
//   3. 账号与卡号在跨行层面必须唯一。本系统靠「各家银行使用不同卡号 BIN 段」
//      来保证：工行 6222、建行 6217、招行 6225。

using MockBank.Api.Domain;

namespace MockBank.Api.Data;

/// <summary>模拟银行账本门面：聚合多家银行的独立核心系统。</summary>
public sealed class MockBankStore
{
    /// <summary>模拟平台名称（对外展示用，非某一家银行）。</summary>
    public const string PlatformName = "AI Banking Agent 多银行模拟核心系统";

    private readonly IReadOnlyList<BankLedger> _ledgers;
    private readonly ILogger<MockBankStore> _logger;

    /// <summary>构造门面。</summary>
    /// <param name="ledgers">各银行的账本实例。</param>
    /// <param name="logger">日志记录器。</param>
    public MockBankStore(IEnumerable<BankLedger> ledgers, ILogger<MockBankStore> logger)
    {
        _ledgers = ledgers.ToList();
        _logger = logger;
    }

    /// <summary>被模拟的全部银行。</summary>
    public IReadOnlyList<BankDescriptor> Banks =>
        _ledgers.Select(l => l.Bank).ToList();

    /// <summary>进程本地时区偏移，流水时间统一使用该偏移构造。</summary>
    public TimeSpan LocalOffset { get; } = DateTimeOffset.Now.Offset;

    // ---------------------------------------------------------------- 跨行查询

    /// <summary>获取全部客户（含同名客户在各行的独立主档）。</summary>
    public IReadOnlyList<Customer> Customers =>
        _ledgers.SelectMany(l => l.GetCustomers()).ToList();

    /// <summary>获取全部账户（按账号升序）。</summary>
    public IReadOnlyList<Account> AllAccounts =>
        _ledgers.SelectMany(l => l.GetAllAccounts())
            .OrderBy(a => a.AccountNo, StringComparer.Ordinal)
            .ToList();

    /// <summary>获取全部卡片（按卡号升序）。这是**跨行**卡片全集。</summary>
    public IReadOnlyList<BankCard> AllCards =>
        _ledgers.SelectMany(l => l.GetAllCards())
            .OrderBy(c => c.CardNo, StringComparer.Ordinal)
            .ToList();

    /// <summary>按客户号查询客户。客户号在多家银行都存在时，返回行代码最小的那一行的主档。</summary>
    /// <param name="userId">客户号。</param>
    /// <param name="bankCode">限定银行行代码；为空表示不限。</param>
    /// <returns>客户实体；不存在时返回 null。</returns>
    public Customer? GetCustomer(string? userId, string? bankCode = null)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;

        var ledgers = LedgersFor(bankCode);
        return ledgers
            .Select(l => l.GetCustomer(userId))
            .FirstOrDefault(c => c is not null);
    }

    /// <summary>按客户号查询其在**所有**银行的客户主档。</summary>
    /// <param name="userId">客户号。</param>
    /// <returns>各行客户主档列表。</returns>
    public IReadOnlyList<Customer> GetCustomersByUser(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return [];
        return _ledgers.Select(l => l.GetCustomer(userId)).Where(c => c is not null).Cast<Customer>().ToList();
    }

    /// <summary>按账号查询账户（跨行搜索，账号在全局唯一）。</summary>
    /// <param name="accountNo">账号。</param>
    /// <returns>账户实体；不存在时返回 null。</returns>
    public Account? GetAccount(string? accountNo)
    {
        if (string.IsNullOrWhiteSpace(accountNo)) return null;
        return _ledgers.Select(l => l.GetAccount(accountNo)).FirstOrDefault(a => a is not null);
    }

    /// <summary>查询某客户在**所有**银行的账户（跨行）。</summary>
    /// <param name="userId">客户号。</param>
    /// <returns>账户列表，按账号升序。</returns>
    public IReadOnlyList<Account> GetAccountsByUser(string? userId) =>
        _ledgers.SelectMany(l => l.GetAccountsByUser(userId))
            .OrderBy(a => a.AccountNo, StringComparer.Ordinal)
            .ToList();

    /// <summary>查询某客户在**指定银行**的账户。</summary>
    /// <param name="userId">客户号。</param>
    /// <param name="bankCode">银行行代码。</param>
    /// <returns>账户列表，按账号升序。</returns>
    public IReadOnlyList<Account> GetAccountsByUser(string? userId, string? bankCode) =>
        LedgersFor(bankCode).SelectMany(l => l.GetAccountsByUser(userId))
            .OrderBy(a => a.AccountNo, StringComparer.Ordinal)
            .ToList();

    /// <summary>按卡号查询卡片（跨行搜索，卡号在全局唯一）。</summary>
    /// <param name="cardNo">卡号。</param>
    /// <returns>卡片实体；不存在时返回 null。</returns>
    public BankCard? GetCard(string? cardNo)
    {
        if (string.IsNullOrWhiteSpace(cardNo)) return null;
        return _ledgers.Select(l => l.GetCard(cardNo)).FirstOrDefault(c => c is not null);
    }

    /// <summary>查询某客户在**所有**银行的卡片（跨行卡片）。</summary>
    /// <param name="userId">客户号。</param>
    /// <returns>卡片列表，按卡号升序。</returns>
    public IReadOnlyList<BankCard> GetCardsByUser(string? userId) =>
        _ledgers.SelectMany(l => l.GetCardsByUser(userId))
            .OrderBy(c => c.CardNo, StringComparer.Ordinal)
            .ToList();

    /// <summary>按账号查询其绑定的卡片。卡片必然与账号在同一行。</summary>
    /// <param name="accountNo">账号。</param>
    /// <returns>卡片实体；不存在时返回 null。</returns>
    public BankCard? GetCardByAccount(string? accountNo)
    {
        var account = GetAccount(accountNo);
        if (account is null) return null;
        var ledger = FindLedger(account.BankCode);
        return ledger?.GetCardByAccount(account.AccountNo);
    }

    /// <summary>按流水号查询单笔交易（跨行搜索）。</summary>
    /// <param name="txNo">交易流水号。</param>
    /// <returns>流水实体；不存在时返回 null。</returns>
    public BankTransaction? GetTransaction(string? txNo)
    {
        if (string.IsNullOrWhiteSpace(txNo)) return null;
        return _ledgers.Select(l => l.GetTransaction(txNo)).FirstOrDefault(t => t is not null);
    }

    /// <summary>按条件筛选交易流水（跨行，时间倒序）。</summary>
    /// <param name="userId">客户号，可为空表示不限。</param>
    /// <param name="accountNo">账号，可为空表示不限。</param>
    /// <param name="from">起始时间（含），可为空。</param>
    /// <param name="to">结束时间（含），可为空。</param>
    /// <param name="category">交易分类，可为空表示不限。</param>
    /// <param name="direction">资金方向，可为空表示不限。</param>
    /// <returns>命中的流水列表。</returns>
    public IReadOnlyList<BankTransaction> QueryTransactions(
        string? userId,
        string? accountNo = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? category = null,
        TransactionDirection? direction = null) =>
        _ledgers
            .SelectMany(l => l.QueryTransactions(userId, accountNo, from, to, category, direction))
            .OrderByDescending(t => t.Timestamp)
            .ThenByDescending(t => t.TxNo, StringComparer.Ordinal)
            .ToList();

    /// <summary>按代码查询理财产品（跨行搜索，产品代码在全局唯一）。</summary>
    /// <param name="code">产品代码（忽略大小写）。</param>
    /// <returns>产品实体；不存在时返回 null。</returns>
    public WealthProduct? GetProduct(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        return _ledgers.Select(l => l.GetProduct(code)).FirstOrDefault(p => p is not null);
    }

    /// <summary>按风险等级与类型筛选理财产品（跨行）。</summary>
    /// <param name="riskLevel">风险等级，可为空表示不限。</param>
    /// <param name="type">产品类型，可为空表示不限。</param>
    /// <returns>命中的产品列表。</returns>
    public IReadOnlyList<WealthProduct> QueryProducts(RiskLevel? riskLevel, ProductType? type) =>
        _ledgers.SelectMany(l => l.QueryProducts(riskLevel, type))
            .OrderBy(p => p.Code, StringComparer.Ordinal)
            .ToList();

    // ---------------------------------------------------------------- 公募基金

    /// <summary>
    /// 按条件查询公募基金清单（跨行聚合）。
    /// 公募基金是代销产品：同一只基金可被多家银行同时代销，各行数据库各存一份副本，
    /// 这里按基金代码归并成一只，并记录「哪些行在代销」。
    /// </summary>
    /// <param name="category">基金类型，可为空表示不限。</param>
    /// <param name="riskLevel">基金风险等级，可为空表示不限。</param>
    /// <param name="company">基金公司（模糊匹配），可为空表示不限。</param>
    /// <param name="keyword">基金代码或简称（模糊匹配），可为空表示不限。</param>
    /// <param name="bankCode">限定银行行代码；为空表示全部银行。</param>
    /// <returns>归并后的基金列表，按基金代码升序。</returns>
    public IReadOnlyList<PublicFund> QueryFunds(
        FundCategory? category = null,
        FundRiskLevel? riskLevel = null,
        string? company = null,
        string? keyword = null,
        string? bankCode = null)
    {
        var collected = LedgersFor(bankCode)
            .SelectMany(l => l.QueryFunds(category, riskLevel, company, keyword))
            .ToList();
        return MergeFunds(collected);
    }

    /// <summary>按基金代码查询公募基金（跨行）。</summary>
    /// <param name="fundCode">基金代码。</param>
    /// <returns>基金实体；没有任何行代销该基金时返回 null。</returns>
    public PublicFund? GetFund(string? fundCode)
    {
        if (string.IsNullOrWhiteSpace(fundCode)) return null;
        var trimmed = fundCode.Trim();

        var funds = _ledgers
            .Select(l => l.GetFund(trimmed))
            .Where(f => f is not null)
            .Cast<PublicFund>()
            .ToList();
        if (funds.Count == 0) return null;

        return funds[0] with { DistributorBankCodes = DistributorCodesOf(funds) };
    }

    /// <summary>
    /// 取基金最近若干天的净值历史（跨行）。
    /// 只取**首家代销行**的完整序列：各行副本的净值序列按基金代码确定性生成、内容一致，
    /// 合并多行反而可能把两套不同起点的日期混在一起，让走势图自相矛盾。
    /// </summary>
    /// <param name="fundCode">基金代码。</param>
    /// <param name="days">回溯天数（含最新一天）。</param>
    /// <returns>净值历史点，按日期升序。</returns>
    public IReadOnlyList<FundNavPoint> GetFundNav(string? fundCode, int days)
    {
        if (string.IsNullOrWhiteSpace(fundCode)) return [];
        var trimmed = fundCode.Trim();

        foreach (var ledger in _ledgers)
        {
            var points = ledger.GetFundNav(trimmed, days);
            if (points.Count > 0) return points;
        }

        return [];
    }

    /// <summary>基金排行（跨行，按指定维度倒序）。</summary>
    /// <param name="metric">排行维度（日增长率 / 近一月 / 近一年）。</param>
    /// <param name="limit">最多返回多少只。</param>
    /// <returns>排行后的基金列表。</returns>
    public IReadOnlyList<PublicFund> FundRankings(FundRankingMetric metric, int limit)
    {
        var funds = QueryFunds();
        var ranked = metric switch
        {
            FundRankingMetric.Monthly => funds
                .OrderByDescending(f => f.MonthlyReturn)
                .ThenBy(f => f.FundCode, StringComparer.Ordinal),
            FundRankingMetric.Yearly => funds
                .OrderByDescending(f => f.YearlyReturn)
                .ThenBy(f => f.FundCode, StringComparer.Ordinal),
            _ => funds
                .OrderByDescending(f => f.DailyGrowthRate)
                .ThenBy(f => f.FundCode, StringComparer.Ordinal),
        };

        return ranked.Take(Math.Max(0, limit)).ToList();
    }

    /// <summary>把各行代销的同一只基金归并成一条，并回填代销行代码。</summary>
    /// <param name="funds">各行基金副本。</param>
    /// <returns>归并后的基金列表。</returns>
    private static IReadOnlyList<PublicFund> MergeFunds(IReadOnlyList<PublicFund> funds)
    {
        var representatives = new List<PublicFund>();
        var distributors = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var fund in funds)
        {
            if (!distributors.TryGetValue(fund.FundCode, out var banks))
            {
                banks = [];
                distributors[fund.FundCode] = banks;
                representatives.Add(fund);
            }

            if (!banks.Contains(fund.BankCode, StringComparer.OrdinalIgnoreCase))
            {
                banks.Add(fund.BankCode);
            }
        }

        return representatives
            .OrderBy(f => f.FundCode, StringComparer.Ordinal)
            .Select(f => f with { DistributorBankCodes = string.Join(",", distributors[f.FundCode]) })
            .ToList();
    }

    /// <summary>取出基金在各行的代销代码，逗号分隔。</summary>
    /// <param name="funds">各行基金副本。</param>
    /// <returns>逗号分隔的行代码。</returns>
    private static string DistributorCodesOf(IReadOnlyList<PublicFund> funds) =>
        string.Join(",", funds
            .Select(f => f.BankCode)
            .Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>统计总资产：储蓄存款余额 + 信用已用额度 + 理财在管规模。</summary>
    /// <returns>总资产金额。</returns>
    public decimal TotalAssets() =>
        AllAccounts.Sum(a => a.AvailableBalance >= 0 ? a.AvailableBalance : 0m)
        + QueryProducts(null, null).Sum(p => p.RaisedAmount);

    // ---------------------------------------------------------------- 跨行写入

    /// <summary>覆盖写入账户。调用方需自行持有该行闸门。</summary>
    /// <param name="account">账户实体，其 BankCode 决定写入哪一行的数据库。</param>
    public void UpdateAccount(Account account)
    {
        var ledger = RequireLedger(account.BankCode);
        ledger.UpdateAccount(account);
    }

    /// <summary>覆盖写入卡片。</summary>
    /// <param name="card">卡片实体，其 BankCode 决定写入哪一行的数据库。</param>
    public void UpdateCard(BankCard card)
    {
        var ledger = RequireLedger(card.BankCode);
        ledger.UpdateCard(card);
    }

    /// <summary>覆盖写入理财产品。调用方需自行持有该行闸门。</summary>
    /// <param name="product">产品实体，其 BankCode 决定写入哪一行的数据库。</param>
    public void UpdateProduct(WealthProduct product)
    {
        var ledger = RequireLedger(product.BankCode);
        ledger.UpdateProduct(product);
    }

    /// <summary>新增一笔交易流水。</summary>
    /// <param name="transaction">流水实体，其 BankCode 决定写入哪一行的数据库。</param>
    /// <returns>写入后的流水实体。</returns>
    public BankTransaction AddTransaction(BankTransaction transaction)
    {
        var ledger = RequireLedger(transaction.BankCode);
        return ledger.AddTransaction(transaction);
    }

    /// <summary>生成指定银行下一个交易流水号。</summary>
    /// <param name="bankCode">银行行代码。</param>
    /// <param name="timestamp">交易时间（取其 yyyyMMdd 部分）。</param>
    /// <returns>交易流水号。</returns>
    public string NextTransactionNo(string bankCode, DateTimeOffset timestamp) =>
        RequireLedger(bankCode).NextTransactionNo(timestamp);

    // ---------------------------------------------------------------- 加锁编排

    /// <summary>
    /// 按固定顺序获取涉及行的写锁。跨行转账涉及两行，必须一次性把两把锁都拿到，
    /// 否则可能出现「A 持 A 行锁等 B 行锁、B 持 B 行锁等 A 行锁」的死锁。
    /// </summary>
    /// <param name="bankCodes">涉及的银行行代码；可为空表示不涉及任何行。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>锁句柄，释放时按相反顺序归还。</returns>
    public async Task<IAsyncDisposable> AcquireLedgerLocksAsync(
        IEnumerable<string> bankCodes, CancellationToken ct = default)
    {
        // 字典序固定顺序 = 全局一致的加锁顺序 = 不会死锁
        var targets = bankCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.Ordinal)
            .Select(RequireLedger)
            .ToList();

        var acquired = new List<BankLedger>();
        try
        {
            foreach (var ledger in targets)
            {
                await ledger.Gate.WaitAsync(ct).ConfigureAwait(false);
                acquired.Add(ledger);
            }
        }
        catch
        {
            // 取锁失败时必须把已拿到的锁还回去，否则其他请求会被永久阻塞。
            for (var i = acquired.Count - 1; i >= 0; i--)
            {
                acquired[i].Gate.Release();
            }
            throw;
        }

        return new LedgerLockHandle(acquired);
    }

    /// <summary>一次性拿全行锁，用于跨行查询之外的全局写操作。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>锁句柄。</returns>
    public Task<IAsyncDisposable> AcquireAllLedgerLocksAsync(CancellationToken ct = default) =>
        AcquireLedgerLocksAsync(_ledgers.Select(l => l.Bank.BankCode), ct);

    // ---------------------------------------------------------------- 内部

    /// <summary>按行代码取出参与查询的账本集合。</summary>
    /// <param name="bankCode">行代码；为空表示全部银行。</param>
    /// <returns>账本集合。</returns>
    private IReadOnlyList<BankLedger> LedgersFor(string? bankCode)
    {
        if (string.IsNullOrWhiteSpace(bankCode)) return _ledgers;
        var ledger = FindLedger(bankCode);
        return ledger is null ? [] : [ledger];
    }

    /// <summary>按行代码查找账本。</summary>
    /// <param name="bankCode">行代码。</param>
    /// <returns>账本；未找到时返回 null。</returns>
    private BankLedger? FindLedger(string? bankCode) =>
        _ledgers.FirstOrDefault(l =>
            string.Equals(l.Bank.BankCode, bankCode, StringComparison.OrdinalIgnoreCase));

    /// <summary>按行代码取出账本，找不到时视为配置错误并抛出。</summary>
    /// <param name="bankCode">行代码。</param>
    /// <returns>账本。</returns>
    private BankLedger RequireLedger(string? bankCode)
    {
        var ledger = FindLedger(bankCode);
        if (ledger is not null) return ledger;

        _logger.LogError("未找到银行 {BankCode} 的账本实例", bankCode);
        throw new InvalidOperationException($"未找到银行 {bankCode} 的账本实例。");
    }

    /// <summary>锁句柄：释放时按相反顺序归还所有已获取的锁。</summary>
    private sealed class LedgerLockHandle(IReadOnlyList<BankLedger> acquired) : IAsyncDisposable
    {
        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            for (var i = acquired.Count - 1; i >= 0; i--)
            {
                acquired[i].Gate.Release();
            }
            await Task.CompletedTask;
        }
    }
}
