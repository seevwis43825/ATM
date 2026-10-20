// ===== 单银行核心系统账本 =====
//
// 一个 BankLedger 就是一家银行的完整核心系统实例：
//   - 拥有自己的 SQLite 数据库文件（客户 / 账户 / 卡 / 流水 / 理财 / 基金）
//   - 拥有自己的写锁（Gate），本行内部的资金变动在此串行执行
//   - 拥有自己的交易流水号序列
//
// ===== 为什么是「内存快照 + SQLite 持久化」=====
//
// 最初这一层是纯内存字典，改成多银行多数据库时直接换成了 EF Core 查询，
// 结果**读路径慢了约 5 倍**：跨行一次卡片查询要打 3 个库文件、3 次建连接、3 条 SQL，
// 而改造前只是 3 次字典查找。实测跨行卡片接口从每秒数千次掉到 48 次。
//
// 于是采用「读内存、写落库」的写穿模型：
//   - **SQLite 仍是持久化的真相来源** —— 每家银行一个独立文件，数据不丢、可审计；
//   - **读路径走内存快照** —— 启动时把本行数据一次性载入，之后查询是字典查找；
//   - **写路径两边都更新** —— 先落库（真相），再更新快照（缓存）。
//
// 写操作在本行 Gate 内串行执行，因此「落库 + 更快照」不会与别的写交错；
// 读路径用 ConcurrentDictionary，与写并发安全。
//
// 跨行的资金变动不在这里处理，而由上层的 MockBankStore 按「先取锁再动账」编排，
// 详见 MockBankStore.AcquireLedgerLocksAsync。

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using MockBank.Api.Domain;

namespace MockBank.Api.Data;

/// <summary>单家银行的账本：把该行数据库中的数据读写出来。</summary>
public sealed class BankLedger
{
    private readonly string _connectionString;
    private readonly ILogger<BankLedger> _logger;
    private long _txSequence;

    // ===== 行内数据的内存快照（读路径走这里；SQLite 是持久化的真相来源）=====
    private readonly ConcurrentDictionary<string, Customer> _customers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Account> _accounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BankCard> _cards = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BankTransaction> _transactions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WealthProduct> _products = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PublicFund> _funds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FundNavPoint[]> _fundNavs = new(StringComparer.Ordinal);

    /// <summary>构造账本并确保数据库结构存在。</summary>
    /// <param name="bank">本行描述符。</param>
    /// <param name="connectionString">本行数据库连接字符串。</param>
    /// <param name="logger">日志记录器。</param>
    public BankLedger(BankDescriptor bank, string connectionString, ILogger<BankLedger> logger)
    {
        Bank = bank;
        _connectionString = connectionString;
        _logger = logger;
    }

    /// <summary>本账本所属的银行。</summary>
    public BankDescriptor Bank { get; }

    /// <summary>本行资金变动互斥闸门。</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>创建一个指向本行数据库的上下文。上下文非线程安全，一次操作一个。</summary>
    /// <returns>数据库上下文，调用方负责释放。</returns>
    public BankDbContext CreateContext() => new(Bank, _connectionString);

    /// <summary>
    /// 确保数据库结构存在（首次运行时建表），并补齐后续新增的表与基金代销清单。
    /// 为什么把「补数据」放在这里：账本工厂只在库为空时才调用 <see cref="Seed"/>，
    /// 而基金表是后加的 —— 历史数据库里结构有了、数据却是空的。
    /// 不在这里补写，基金相关接口会永远返回空列表，而且**不报任何错**（静默失效）。
    /// </summary>
    public void EnsureCreated()
    {
        using var db = CreateContext();
        db.Database.EnsureCreated();
        EnsureSchemaUpToDate(db);
        EnsureFundCatalog(db);
    }

    /// <summary>
    /// 把本行的流水号计数器续接到库中已有流水之后。
    ///
    /// 为什么必须做：流水号里的序号来自内存计数器，重启后从 0 开始，
    /// 而库里已经躺着种子数据写入的流水 —— 两者会撞号，
    /// 表现为 <c>UNIQUE constraint failed: transactions.TxNo</c>，
    /// 也就是「转账接口在重启后必然 500」。
    /// 序号藏在流水号末尾（<c>TX{日期}{行代码}{7 位序号}</c>），
    /// 所以这里解析已有流水的序号并取最大值作为起点。
    /// </summary>
    public void InitializeTransactionSequence()
    {
        using var db = CreateContext();

        var maxSequence = db.Transactions.AsNoTracking()
            .Select(t => t.TxNo)
            .AsEnumerable()
            .Select(ParseSequenceSuffix)
            .DefaultIfEmpty(0)
            .Max();

        _txSequence = maxSequence;
    }

    /// <summary>解析流水号末尾的序号部分。</summary>
    /// <param name="txNo">交易流水号。</param>
    /// <returns>序号；无法解析时返回 0。</returns>
    private static long ParseSequenceSuffix(string? txNo)
    {
        if (string.IsNullOrWhiteSpace(txNo)) return 0;

        var end = txNo.Length;
        while (end > 0 && char.IsDigit(txNo[end - 1])) end--;

        return end == txNo.Length
            ? 0
            : long.TryParse(txNo[end..], out var value) ? value : 0;
    }

    /// <summary>
    /// 检查数据库结构是否与当前模型一致。
    ///
    /// 为什么必须显式检查：<c>EnsureCreated()</c> 只在**整个数据库不存在**时才建表，
    /// 数据库已存在时它什么都不做 —— 既不演进表结构，也不报错。
    /// 于是"模型加了表、旧库里没有"这个最常见的开发期场景，
    /// 会在第一次查询新表时抛出 <c>no such table</c>，
    /// 而且错误信息指向的是某个业务查询，排查方向完全被带偏。
    ///
    /// 做法：把模型声明的表名与库中实际存在的表名求差集。
    /// 缺表即判定为结构漂移，由调用方决定重建策略。
    /// </summary>
    /// <param name="missingTables">输出缺失的表名；结构一致时为空。</param>
    /// <returns>结构与模型一致返回 true。</returns>
    public bool IsSchemaCompatible(out IReadOnlyList<string> missingTables)
    {
        using var db = CreateContext();
        missingTables = MissingModelTables(db);
        return missingTables.Count == 0;
    }

    /// <summary>
    /// 查出「模型声明了、但库里还没有」的表。
    ///
    /// 表名一律从 EF 模型推导，不维护手写清单：
    /// 手写清单（曾用 <c>RequiredTables</c>）必然会随模型演进而漏登记，
    /// 漏一张就又回到"表建不出来、报错指向业务查询"的老问题。
    /// 模型推导是唯一不会漂移的来源。
    /// </summary>
    /// <param name="db">本行数据库上下文。</param>
    /// <returns>缺失的表名列表；结构一致时为空。</returns>
    private static List<string> MissingModelTables(BankDbContext db)
    {
        var existing = ReadActualTableNames(db);

        return db.Model.GetEntityTypes()
            .Select(e => e.GetTableName())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !existing.Contains(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>读取库中实际存在的表名（过滤 SQLite 内部表）。</summary>
    private static HashSet<string> ReadActualTableNames(BankDbContext db)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        db.Database.OpenConnection();

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            // 跳过 sqlite_sequence / sqlite_stat1 等内部表
            if (!name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// 把本行数据库中的全部数据载入内存快照。
    ///
    /// 必须在建表 / 写种子之后调用一次。载入后所有读路径都走内存，
    /// 写路径仍然落库（并同步更新快照）。
    /// </summary>
    public void LoadSnapshot()
    {
        using var db = CreateContext();

        foreach (var customer in db.Customers.AsNoTracking()) _customers[customer.UserId] = customer;
        foreach (var account in db.Accounts.AsNoTracking()) _accounts[account.AccountNo] = account;
        foreach (var card in db.Cards.AsNoTracking()) _cards[card.CardNo] = card;
        foreach (var transaction in db.Transactions.AsNoTracking()) _transactions[transaction.TxNo] = transaction;
        foreach (var product in db.Products.AsNoTracking()) _products[product.Code] = product;
        foreach (var fund in db.PublicFunds.AsNoTracking()) _funds[fund.FundCode] = fund;

        foreach (var group in db.FundNavPoints.AsNoTracking()
                     .AsEnumerable()
                     .GroupBy(p => p.FundCode, StringComparer.Ordinal))
        {
            _fundNavs[group.Key] = group.OrderBy(p => p.NavDate).ToArray();
        }

        _logger.LogInformation(
            "银行 {BankCode} 内存快照已载入：客户 {Customers} / 账户 {Accounts} / 卡 {Cards} / 流水 {Transactions} / 理财 {Products} / 代销基金 {Funds}",
            Bank.BankCode, _customers.Count, _accounts.Count, _cards.Count,
            _transactions.Count, _products.Count, _funds.Count);
    }

    /// <summary>
    /// 本行当前记录数，用于判断是否需要写入种子数据。
    ///
    /// 必须查数据库而不是读内存快照：调用方拿它做「库里是不是空的」判断，
    /// 而这个判断发生在 <see cref="LoadSnapshot"/> **之前**（快照尚未载入），
    /// 一旦改成读快照就永远返回 0，于是每次启动都重播种子，
    /// 直接撞 accounts.AccountNo 的唯一约束，服务起不来。
    /// </summary>
    /// <returns>客户数、账户数、卡片数、流水数、产品数、代销基金数、净值点数。</returns>
    public (int Customers, int Accounts, int Cards, int Transactions, int Products, int Funds, int FundNavPoints) CountAll()
    {
        using var db = CreateContext();

        return (
            db.Customers.Count(),
            db.Accounts.Count(),
            db.Cards.Count(),
            db.Transactions.Count(),
            db.Products.Count(),
            db.PublicFunds.Count(),
            db.FundNavPoints.Count());
    }

    // ---------------------------------------------------------------- 查询
    // 以下查询全部走内存快照：跨行聚合会对每家银行各调一次，
    // 走数据库的话每次请求要开 3 个库、建 3 次连接、发 3 条 SQL，
    // 实测比内存字典慢约 5 倍。

    /// <summary>查询本行全部客户。</summary>
    /// <returns>客户列表，按客户号升序。</returns>
    public IReadOnlyList<Customer> GetCustomers() =>
        _customers.Values.OrderBy(c => c.UserId, StringComparer.Ordinal).ToList();

    /// <summary>按客户号查询本行客户。</summary>
    /// <param name="userId">客户号。</param>
    /// <returns>客户实体；不存在时返回 null。</returns>
    public Customer? GetCustomer(string? userId) =>
        string.IsNullOrWhiteSpace(userId) ? null : _customers.GetValueOrDefault(userId);

    /// <summary>查询本行全部账户。</summary>
    /// <returns>账户列表，按账号升序。</returns>
    public IReadOnlyList<Account> GetAllAccounts() =>
        _accounts.Values.OrderBy(a => a.AccountNo, StringComparer.Ordinal).ToList();

    /// <summary>按账号查询本行账户。</summary>
    /// <param name="accountNo">账号。</param>
    /// <returns>账户实体；不存在时返回 null。</returns>
    public Account? GetAccount(string? accountNo) =>
        string.IsNullOrWhiteSpace(accountNo) ? null : _accounts.GetValueOrDefault(accountNo);

    /// <summary>查询本行某客户名下的全部账户。</summary>
    /// <param name="userId">客户号。</param>
    /// <returns>账户列表，按账号升序。</returns>
    public IReadOnlyList<Account> GetAccountsByUser(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return [];
        return _accounts.Values
            .Where(a => a.UserId == userId)
            .OrderBy(a => a.AccountNo, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>按卡号查询本行卡片。</summary>
    /// <param name="cardNo">卡号。</param>
    /// <returns>卡片实体；不存在时返回 null。</returns>
    public BankCard? GetCard(string? cardNo) =>
        string.IsNullOrWhiteSpace(cardNo) ? null : _cards.GetValueOrDefault(cardNo);

    /// <summary>查询本行全部卡片。</summary>
    /// <returns>卡片列表，按卡号升序。</returns>
    public IReadOnlyList<BankCard> GetAllCards() =>
        _cards.Values.OrderBy(c => c.CardNo, StringComparer.Ordinal).ToList();

    /// <summary>查询本行某客户名下的全部卡片。</summary>
    /// <param name="userId">客户号。</param>
    /// <returns>卡片列表，按卡号升序。</returns>
    public IReadOnlyList<BankCard> GetCardsByUser(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return [];
        return _cards.Values
            .Where(c => c.UserId == userId)
            .OrderBy(c => c.CardNo, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>按账号查询其在本行绑定的卡片。</summary>
    /// <param name="accountNo">账号。</param>
    /// <returns>卡片实体；不存在时返回 null。</returns>
    public BankCard? GetCardByAccount(string? accountNo) =>
        string.IsNullOrWhiteSpace(accountNo)
            ? null
            : _cards.Values.FirstOrDefault(c => c.AccountNo == accountNo);

    /// <summary>按流水号查询本行交易。</summary>
    /// <param name="txNo">交易流水号。</param>
    /// <returns>流水实体；不存在时返回 null。</returns>
    public BankTransaction? GetTransaction(string? txNo) =>
        string.IsNullOrWhiteSpace(txNo) ? null : _transactions.GetValueOrDefault(txNo);

    /// <summary>按条件筛选本行交易流水（时间倒序）。</summary>
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
        TransactionDirection? direction = null)
    {
        IEnumerable<BankTransaction> query = _transactions.Values;

        if (!string.IsNullOrWhiteSpace(userId))
        {
            query = query.Where(t => t.UserId == userId);
        }

        if (!string.IsNullOrWhiteSpace(accountNo))
        {
            query = query.Where(t => t.AccountNo == accountNo);
        }

        if (from.HasValue)
        {
            var fromUtc = from.Value.UtcDateTime;
            query = query.Where(t => t.Timestamp >= fromUtc);
        }

        if (to.HasValue)
        {
            var toUtc = to.Value.UtcDateTime;
            query = query.Where(t => t.Timestamp <= toUtc);
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            var keyword = category.Trim();
            query = query.Where(t => t.Category == keyword);
        }

        if (direction.HasValue)
        {
            query = query.Where(t => t.Direction == direction.Value);
        }

        return query
            .OrderByDescending(t => t.Timestamp)
            .ThenByDescending(t => t.TxNo, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>按代码查询本行理财产品。</summary>
    /// <param name="code">产品代码（忽略大小写）。</param>
    /// <returns>产品实体；不存在时返回 null。</returns>
    public WealthProduct? GetProduct(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : _products.GetValueOrDefault(code.Trim());

    /// <summary>按风险等级与类型筛选本行理财产品。</summary>
    /// <param name="riskLevel">风险等级，可为空表示不限。</param>
    /// <param name="type">产品类型，可为空表示不限。</param>
    /// <returns>命中的产品列表。</returns>
    public IReadOnlyList<WealthProduct> QueryProducts(RiskLevel? riskLevel, ProductType? type)
    {
        IEnumerable<WealthProduct> query = _products.Values;

        if (riskLevel.HasValue)
        {
            query = query.Where(p => p.RiskLevel == riskLevel.Value);
        }

        if (type.HasValue)
        {
            query = query.Where(p => p.Type == type.Value);
        }

        return query.OrderBy(p => p.Code, StringComparer.Ordinal).ToList();
    }

    /// <summary>按条件筛选本行代销的公募基金。</summary>
    /// <param name="category">基金类型，可为空表示不限。</param>
    /// <param name="riskLevel">基金风险等级，可为空表示不限。</param>
    /// <param name="company">基金公司名称（模糊匹配），可为空表示不限。</param>
    /// <param name="keyword">基金代码或简称（模糊匹配），可为空表示不限。</param>
    /// <returns>命中的基金列表，按基金代码升序。</returns>
    public IReadOnlyList<PublicFund> QueryFunds(
        FundCategory? category,
        FundRiskLevel? riskLevel,
        string? company,
        string? keyword)
    {
        IEnumerable<PublicFund> query = _funds.Values;

        if (category.HasValue)
        {
            query = query.Where(f => f.Category == category.Value);
        }

        if (riskLevel.HasValue)
        {
            query = query.Where(f => f.RiskLevel == riskLevel.Value);
        }

        if (!string.IsNullOrWhiteSpace(company))
        {
            var companyName = company.Trim();
            query = query.Where(f => f.FundCompany.Contains(companyName, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var text = keyword.Trim();
            query = query.Where(f =>
                f.FundCode.Contains(text, StringComparison.Ordinal) ||
                f.FundName.Contains(text, StringComparison.Ordinal));
        }

        return query.OrderBy(f => f.FundCode, StringComparer.Ordinal).ToList();
    }

    /// <summary>按基金代码查询本行代销的基金。</summary>
    /// <param name="fundCode">基金代码。</param>
    /// <returns>基金实体；本行未代销或代码不存在时返回 null。</returns>
    public PublicFund? GetFund(string? fundCode) =>
        string.IsNullOrWhiteSpace(fundCode) ? null : _funds.GetValueOrDefault(fundCode.Trim());

    /// <summary>取本行代销基金最近若干天的净值历史。</summary>
    /// <param name="fundCode">基金代码。</param>
    /// <param name="days">回溯天数（含最新一天）。</param>
    /// <returns>净值历史点，按日期升序；基金不存在时返回空列表。</returns>
    public IReadOnlyList<FundNavPoint> GetFundNav(string? fundCode, int days)
    {
        if (string.IsNullOrWhiteSpace(fundCode) || days <= 0) return [];
        if (!_fundNavs.TryGetValue(fundCode.Trim(), out var points) || points.Length == 0) return [];

        // 以本行最新净值日为终点回溯，避免「今天不是交易日」导致少返回数据。
        var newest = points[^1].NavDate;
        var from = newest.AddDays(-(days - 1));
        return points.Where(p => p.NavDate >= from).ToList();
    }

    // ---------------------------------------------------------------- 写入
    //
    // 写路径是「先落库（真相），再更新快照（缓存）」。
    // 调用方需自行持有本行 Gate，保证同一行的写不交错。

    /// <summary>覆盖写入账户（余额 / 状态变更）。调用方需自行持有本行 <see cref="Gate"/>。</summary>
    /// <param name="account">账户实体。</param>
    public void UpdateAccount(Account account)
    {
        using (var db = CreateContext())
        {
            var tracked = db.Accounts.FirstOrDefault(a => a.AccountNo == account.AccountNo);
            if (tracked is null) return;
            db.Entry(tracked).CurrentValues.SetValues(account);
            db.SaveChanges();
        }

        _accounts[account.AccountNo] = account;
    }

    /// <summary>覆盖写入卡片（状态 / 限额变更）。</summary>
    /// <param name="card">卡片实体。</param>
    public void UpdateCard(BankCard card)
    {
        using (var db = CreateContext())
        {
            var tracked = db.Cards.FirstOrDefault(c => c.CardNo == card.CardNo);
            if (tracked is null) return;
            db.Entry(tracked).CurrentValues.SetValues(card);
            db.SaveChanges();
        }

        _cards[card.CardNo] = card;
    }

    /// <summary>覆盖写入理财产品（募集金额变更）。调用方需自行持有本行 <see cref="Gate"/>。</summary>
    /// <param name="product">产品实体。</param>
    public void UpdateProduct(WealthProduct product)
    {
        using (var db = CreateContext())
        {
            var tracked = db.Products.FirstOrDefault(p => p.Code == product.Code);
            if (tracked is null) return;
            db.Entry(tracked).CurrentValues.SetValues(product);
            db.SaveChanges();
        }

        _products[product.Code] = product;
    }

    /// <summary>新增一笔交易流水。</summary>
    /// <param name="transaction">流水实体。</param>
    /// <returns>写入后的流水实体。</returns>
    public BankTransaction AddTransaction(BankTransaction transaction)
    {
        using (var db = CreateContext())
        {
            // 流水号必须在本行内唯一。内存计数器在「同一个库被多个进程打开」时
            // 可能与另一个进程撞号（两边都从自己记忆的起点往后数），
            // 表现为 UNIQUE constraint failed: transactions.TxNo —— 一个 500。
            // 因此写入前先查一次，撞了就顺延。
            var attempts = 0;
            while (db.Transactions.Any(t => t.TxNo == transaction.TxNo))
            {
                if (++attempts > 1000)
                {
                    throw new InvalidOperationException(
                        $"银行 {Bank.BankCode} 连续 1000 次无法生成唯一流水号。");
                }

                transaction = transaction with { TxNo = NextTransactionNo(transaction.Timestamp) };
            }

            db.Transactions.Add(transaction);
            db.SaveChanges();
        }

        _transactions[transaction.TxNo] = transaction;
        return transaction;
    }

    /// <summary>
    /// 生成本行下一个交易流水号，形如 TX20260927ICBC0000001。
    /// 流水号带行代码，因此在跨行聚合后仍然全局唯一。
    /// </summary>
    /// <param name="timestamp">交易时间（取其 yyyyMMdd 部分）。</param>
    /// <returns>交易流水号。</returns>
    public string NextTransactionNo(DateTimeOffset timestamp) =>
        $"TX{timestamp:yyyyMMdd}{Bank.BankCode}{Interlocked.Increment(ref _txSequence):D7}";

    /// <summary>写入本行的种子数据。仅在数据库为空时调用。</summary>
    /// <param name="seed">本行的种子数据定义。</param>
    /// <param name="anchor">时间基准日，种子数据里的相对时间都以此为锚点。</param>
    public void Seed(BankSeedData seed, DateTimeOffset anchor)
    {
        using (var db = CreateContext())
        {
            db.Customers.AddRange(seed.Customers);
            db.Accounts.AddRange(seed.Accounts);
            db.Cards.AddRange(seed.Cards);
            db.Transactions.AddRange(seed.Transactions);
            db.Products.AddRange(seed.Products);
            db.PublicFunds.AddRange(seed.PublicFunds);
            db.FundNavPoints.AddRange(seed.FundNavPoints);
            db.SaveChanges();
        }

        // 同步填入内存快照，避免启动后再从库里全量读一遍。
        foreach (var customer in seed.Customers) _customers[customer.UserId] = customer;
        foreach (var account in seed.Accounts) _accounts[account.AccountNo] = account;
        foreach (var card in seed.Cards) _cards[card.CardNo] = card;
        foreach (var transaction in seed.Transactions) _transactions[transaction.TxNo] = transaction;
        foreach (var product in seed.Products) _products[product.Code] = product;
        foreach (var fund in seed.PublicFunds) _funds[fund.FundCode] = fund;
        foreach (var group in seed.FundNavPoints
                     .GroupBy(p => p.FundCode, StringComparer.Ordinal))
        {
            _fundNavs[group.Key] = group.OrderBy(p => p.NavDate).ToArray();
        }

        _logger.LogInformation(
            "银行 {BankCode} 种子数据写入完成：客户 {Customers} / 账户 {Accounts} / 卡 {Cards} / 流水 {Transactions} / 理财 {Products} / 代销基金 {Funds} / 净值点 {NavPoints}",
            Bank.BankCode, seed.Customers.Count, seed.Accounts.Count, seed.Cards.Count,
            seed.Transactions.Count, seed.Products.Count, seed.PublicFunds.Count, seed.FundNavPoints.Count);
    }

    /// <summary>老库升级时补写基金代销清单与净值历史。</summary>
    /// <param name="db">本行数据库上下文。</param>
    private void EnsureFundCatalog(BankDbContext db)
    {
        // 全新库由 Seed 一次性写入全部种子数据；这里只处理「结构有、数据空」的老库。
        if (!db.Customers.Any() || db.PublicFunds.Any()) return;

        var fundSeed = BankSeedFactory.CreateFundCatalog(Bank, DateTimeOffset.Now);
        db.PublicFunds.AddRange(fundSeed.PublicFunds);
        db.FundNavPoints.AddRange(fundSeed.FundNavPoints);
        db.SaveChanges();

        // 同步内存快照，保证补写的数据立刻可查。
        foreach (var fund in fundSeed.PublicFunds) _funds[fund.FundCode] = fund;
        foreach (var group in fundSeed.FundNavPoints
                     .GroupBy(p => p.FundCode, StringComparer.Ordinal))
        {
            _fundNavs[group.Key] = group.OrderBy(p => p.NavDate).ToArray();
        }

        _logger.LogInformation(
            "银行 {BankCode} 检测到基金代销数据缺失，已补写：基金 {Funds} / 净值点 {NavPoints}",
            Bank.BankCode, fundSeed.PublicFunds.Count, fundSeed.FundNavPoints.Count);
    }

    /// <summary>
    /// 补齐已存在数据库里缺失的表。
    ///
    /// 为什么必须做这一步：DbContext.Database.EnsureCreated() 只在**数据库文件不存在**时才建表，
    /// 库一旦存在就直接返回 false —— 本项目后续新增的表永远建不出来，
    /// 而「表不存在」的报错要等到第一次查询那张表时才炸（no such table: xxx），很难定位。
    ///
    /// 建表语句由 EF 从模型自己生成，因此列定义与模型永远一致；已有的表与已有数据一律不动。
    /// </summary>
    /// <param name="db">本行数据库上下文。</param>
    private void EnsureSchemaUpToDate(BankDbContext db)
    {
        var missingTables = MissingTables(db);
        if (missingTables.Count == 0) return;

        foreach (var statement in CreateStatementsFor(db, missingTables))
        {
            db.Database.ExecuteSqlRaw(statement);
        }

        _logger.LogInformation(
            "银行 {BankCode} 数据库结构已补齐，新增表：{Tables}", Bank.BankCode, string.Join(", ", missingTables));
    }

    /// <summary>查本库里缺少哪些表。</summary>
    /// <param name="db">本行数据库上下文。</param>
    /// <returns>缺失的表名列表。</returns>
    private static List<string> MissingTables(BankDbContext db) => MissingModelTables(db);

    /// <summary>
    /// 从 EF 生成的建表脚本里挑出属于指定表的语句。
    /// 建表语句排在建索引语句前面 —— 索引依赖表，先建表才不会报错。
    /// </summary>
    /// <param name="db">本行数据库上下文。</param>
    /// <param name="tableNames">目标表名。</param>
    /// <returns>可逐条执行的建表 / 建索引语句。</returns>
    private static IEnumerable<string> CreateStatementsFor(BankDbContext db, IReadOnlyList<string> tableNames)
    {
        var script = db.Database.GenerateCreateScript();

        return script
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(statement => tableNames.Any(table =>
                statement.Contains(table, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(statement => statement.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
    }
}
