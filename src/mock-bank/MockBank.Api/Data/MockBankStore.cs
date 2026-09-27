// ===== 模拟银行账本：基于 ConcurrentDictionary 的线程安全内存存储 + 启动种子数据 =====

using System.Collections.Concurrent;
using MockBank.Api.Domain;

namespace MockBank.Api.Data;

/// <summary>
/// 模拟银行核心系统的内存账本。所有数据保存在进程内存中，重启即重置。
/// 读操作无锁；涉及余额变更的写操作由 <see cref="LedgerGate"/> 串行化，保证账实一致。
/// </summary>
public sealed class MockBankStore
{
    /// <summary>模拟银行名称。</summary>
    public const string BankName = "模拟银行（AI Banking Agent 演示核心系统）";

    private const string DefaultBranch = "模拟银行营业部";
    private const string DefaultCurrency = "CNY";

    private readonly ConcurrentDictionary<string, Customer> _customers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Account> _accounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BankCard> _cards = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BankTransaction> _transactions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WealthProduct> _products = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<MockBankStore> _logger;
    private long _txSequence;

    /// <summary>账本写操作互斥闸门，转账 / 理财认购等资金变动共用，避免并发下的余额丢失更新。</summary>
    public SemaphoreSlim LedgerGate { get; } = new(1, 1);

    /// <summary>进程本地时区偏移，流水时间统一使用该偏移构造。</summary>
    public TimeSpan LocalOffset { get; }

    /// <summary>创建账本并写入种子数据。</summary>
    /// <param name="logger">日志记录器。</param>
    public MockBankStore(ILogger<MockBankStore> logger)
    {
        _logger = logger;
        LocalOffset = DateTimeOffset.Now.Offset;
        var anchor = DateTimeOffset.Now;

        SeedCustomers(anchor);
        SeedAccounts(anchor);
        SeedCards(anchor);
        SeedProducts(anchor);
        SeedTransactions(anchor);

        _logger.LogInformation(
            "Mock 银行账本初始化完成：客户 {Customers} 个 / 账户 {Accounts} 个 / 卡 {Cards} 张 / 流水 {Transactions} 笔 / 理财产品 {Products} 只 / 数据基准日 {Anchor:yyyy-MM-dd}",
            _customers.Count, _accounts.Count, _cards.Count, _transactions.Count, _products.Count,
            anchor.ToString("yyyy-MM-dd"));
    }

    // ---------------------------------------------------------------- 查询

    /// <summary>获取全部客户。</summary>
    public IReadOnlyList<Customer> Customers =>
        _customers.Values.OrderBy(c => c.UserId, StringComparer.Ordinal).ToList();

    /// <summary>获取全部账户（按账号升序）。</summary>
    public IReadOnlyList<Account> AllAccounts =>
        _accounts.Values.OrderBy(a => a.AccountNo, StringComparer.Ordinal).ToList();

    /// <summary>获取全部卡片（按卡号升序）。</summary>
    public IReadOnlyList<BankCard> AllCards =>
        _cards.Values.OrderBy(c => c.CardNo, StringComparer.Ordinal).ToList();

    /// <summary>按客户号查询客户。</summary>
    /// <param name="userId">客户号。</param>
    /// <returns>客户实体；不存在时返回 null。</returns>
    public Customer? GetCustomer(string? userId) =>
        userId is not null && _customers.TryGetValue(userId, out var customer) ? customer : null;

    /// <summary>按账号查询账户。</summary>
    /// <param name="accountNo">账号。</param>
    /// <returns>账户实体；不存在时返回 null。</returns>
    public Account? GetAccount(string? accountNo) =>
        accountNo is not null && _accounts.TryGetValue(accountNo, out var account) ? account : null;

    /// <summary>查询某客户名下的全部账户。</summary>
    /// <param name="userId">客户号。</param>
    /// <returns>账户列表（按账号升序）。</returns>
    public IReadOnlyList<Account> GetAccountsByUser(string? userId) =>
        _accounts.Values
            .Where(a => userId is not null && string.Equals(a.UserId, userId, StringComparison.Ordinal))
            .OrderBy(a => a.AccountNo, StringComparer.Ordinal)
            .ToList();

    /// <summary>按卡号查询卡片。</summary>
    /// <param name="cardNo">卡号。</param>
    /// <returns>卡片实体；不存在时返回 null。</returns>
    public BankCard? GetCard(string? cardNo) =>
        cardNo is not null && _cards.TryGetValue(cardNo, out var card) ? card : null;

    /// <summary>查询某客户名下的全部卡片。</summary>
    /// <param name="userId">客户号。</param>
    /// <returns>卡片列表（按卡号升序）。</returns>
    public IReadOnlyList<BankCard> GetCardsByUser(string? userId) =>
        _cards.Values
            .Where(c => userId is not null && string.Equals(c.UserId, userId, StringComparison.Ordinal))
            .OrderBy(c => c.CardNo, StringComparer.Ordinal)
            .ToList();

    /// <summary>按账号查询其绑定的卡片。</summary>
    /// <param name="accountNo">账号。</param>
    /// <returns>卡片实体；不存在时返回 null。</returns>
    public BankCard? GetCardByAccount(string? accountNo) =>
        _cards.Values.FirstOrDefault(c => string.Equals(c.AccountNo, accountNo, StringComparison.Ordinal));

    /// <summary>按流水号查询单笔交易。</summary>
    /// <param name="txNo">交易流水号。</param>
    /// <returns>流水实体；不存在时返回 null。</returns>
    public BankTransaction? GetTransaction(string? txNo) =>
        txNo is not null && _transactions.TryGetValue(txNo, out var tx) ? tx : null;

    /// <summary>按条件筛选交易流水（时间倒序）。</summary>
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
            query = query.Where(t => string.Equals(t.UserId, userId, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(accountNo))
        {
            query = query.Where(t => string.Equals(t.AccountNo, accountNo, StringComparison.Ordinal));
        }

        if (from.HasValue)
        {
            query = query.Where(t => t.Timestamp >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(t => t.Timestamp <= to.Value);
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            var keyword = category.Trim();
            query = query.Where(t => string.Equals(t.Category, keyword, StringComparison.OrdinalIgnoreCase));
        }

        if (direction.HasValue)
        {
            query = query.Where(t => t.Direction == direction.Value);
        }

        return query.OrderByDescending(t => t.Timestamp).ThenByDescending(t => t.TxNo, StringComparer.Ordinal).ToList();
    }

    /// <summary>按代码查询理财产品。</summary>
    /// <param name="code">产品代码（忽略大小写）。</param>
    /// <returns>产品实体；不存在时返回 null。</returns>
    public WealthProduct? GetProduct(string? code) =>
        code is not null && _products.TryGetValue(code.Trim(), out var product) ? product : null;

    /// <summary>按风险等级与类型筛选理财产品。</summary>
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

    // ---------------------------------------------------------------- 写入

    /// <summary>覆盖写入账户（余额 / 状态变更）。调用方需自行持有 <see cref="LedgerGate"/>。</summary>
    /// <param name="account">账户实体。</param>
    public void UpdateAccount(Account account) => _accounts[account.AccountNo] = account;

    /// <summary>覆盖写入卡片（状态 / 限额变更）。</summary>
    /// <param name="card">卡片实体。</param>
    public void UpdateCard(BankCard card) => _cards[card.CardNo] = card;

    /// <summary>覆盖写入理财产品（募集金额变更）。调用方需自行持有 <see cref="LedgerGate"/>。</summary>
    /// <param name="product">产品实体。</param>
    public void UpdateProduct(WealthProduct product) => _products[product.Code] = product;

    /// <summary>新增一笔交易流水。</summary>
    /// <param name="transaction">流水实体。</param>
    /// <returns>写入后的流水实体。</returns>
    public BankTransaction AddTransaction(BankTransaction transaction)
    {
        _transactions[transaction.TxNo] = transaction;
        return transaction;
    }

    /// <summary>生成下一个全局唯一的交易流水号，形如 TX202609270000001。</summary>
    /// <param name="timestamp">交易时间（取其 yyyyMMdd 部分）。</param>
    /// <returns>交易流水号。</returns>
    public string NextTransactionNo(DateTimeOffset timestamp) =>
        $"TX{timestamp:yyyyMMdd}{Interlocked.Increment(ref _txSequence):D7}";

    /// <summary>统计账户总资产：储蓄存款余额 + 信用已用额度 + 理财在管规模。</summary>
    /// <returns>总资产金额。</returns>
    public decimal TotalAssets() =>
        _accounts.Values.Sum(a => a.AvailableBalance >= 0 ? a.AvailableBalance : 0m)
        + _products.Values.Sum(p => p.RaisedAmount);

    // ------------------------------------------------------------ 种子数据

    private void SeedCustomers(DateTimeOffset anchor)
    {
        var seeds = new (string UserId, string Name, string IdCard, string Phone, RiskLevel Risk, string Level, int MonthsAgo)[]
        {
            ("u_demo01", "张明", "110101199001011234", "13800138000", RiskLevel.R3, "金卡", 62),
            ("u_demo02", "李华", "310101199203054321", "13900139000", RiskLevel.R2, "普通", 38),
            ("u_demo03", "王芳", "440101199505068765", "13700137000", RiskLevel.R4, "白金", 26),
        };

        foreach (var s in seeds)
        {
            _customers[s.UserId] = new Customer
            {
                UserId = s.UserId,
                Name = s.Name,
                IdCardNo = s.IdCard,
                Phone = s.Phone,
                RiskLevel = s.Risk,
                CustomerLevel = s.Level,
                BranchName = DefaultBranch,
                CreatedAt = At(anchor.AddMonths(-s.MonthsAgo)),
            };
        }
    }

    private void SeedAccounts(DateTimeOffset anchor)
    {
        var seeds = new (string UserId, string AccountNo, AccountType Type, string Product, decimal Balance, decimal Limit, decimal Used, decimal Daily, int MonthsAgo)[]
        {
            ("u_demo01", "6222020200000001", AccountType.Savings, "薪金卡", 250_000.00m, 0m, 0m, 500_000.00m, 62),
            ("u_demo01", "6222020200000002", AccountType.Credit, "白金信用卡", -12_800.00m, 50_000.00m, 12_800.00m, 50_000.00m, 48),
            ("u_demo02", "6222020200000003", AccountType.Savings, "活期一本通", 88_000.50m, 0m, 0m, 500_000.00m, 38),
            ("u_demo02", "6222020200000004", AccountType.Credit, "金卡信用卡", -3_200.00m, 30_000.00m, 3_200.00m, 50_000.00m, 26),
            ("u_demo03", "6222020200000005", AccountType.Savings, "至尊储蓄卡", 152_000.00m, 0m, 0m, 500_000.00m, 26),
            ("u_demo03", "6222020200000006", AccountType.Credit, "钻石信用卡", -45_000.00m, 80_000.00m, 45_000.00m, 50_000.00m, 20),
        };

        foreach (var s in seeds)
        {
            _accounts[s.AccountNo] = new Account
            {
                AccountNo = s.AccountNo,
                UserId = s.UserId,
                AccountType = s.Type,
                ProductName = s.Product,
                Balance = s.Balance,
                CreditLimit = s.Limit,
                CreditUsed = s.Used,
                DailyLimit = s.Daily,
                Status = AccountStatus.Normal,
                Currency = DefaultCurrency,
                OpenDate = At(anchor.AddMonths(-s.MonthsAgo)),
                BranchName = DefaultBranch,
            };
        }
    }

    private void SeedCards(DateTimeOffset anchor)
    {
        foreach (var account in _accounts.Values.OrderBy(a => a.AccountNo, StringComparer.Ordinal))
        {
            var customer = _customers[account.UserId];
            _cards[account.AccountNo] = new BankCard
            {
                CardNo = account.AccountNo,
                AccountNo = account.AccountNo,
                UserId = account.UserId,
                CardType = account.AccountType == AccountType.Credit ? CardType.Credit : CardType.Debit,
                Status = CardStatus.Normal,
                BoundPhone = customer.Phone,
                DailyLimit = account.DailyLimit,
                IssueDate = At(account.OpenDate),
                ExpiryDate = At(anchor.AddYears(4)),
                CardOrg = BankName,
                StatusReason = "初始状态",
                StatusChangedAt = At(account.OpenDate),
            };
        }
    }

    private void SeedProducts(DateTimeOffset anchor)
    {
        var seeds = new (string Code, string Name, ProductType Type, RiskLevel Risk, decimal Rate, decimal Min, int Term, decimal Raised)[]
        {
            ("WP001", "稳健增利90天", ProductType.Steady, RiskLevel.R2, 3.20m, 1_000.00m, 90, 62_000_000m),
            ("WP002", "安心纯债180天", ProductType.Steady, RiskLevel.R2, 3.85m, 5_000.00m, 180, 48_500_000m),
            ("WP003", "平衡优选混合", ProductType.Balanced, RiskLevel.R3, 5.10m, 10_000.00m, 365, 91_200_000m),
            ("WP004", "成长精选30天", ProductType.Aggressive, RiskLevel.R3, 4.25m, 5_000.00m, 30, 18_700_000m),
            ("WP005", "进取科技主题", ProductType.Aggressive, RiskLevel.R4, 8.50m, 20_000.00m, 180, 35_400_000m),
            ("WP006", "短债现金管理", ProductType.Steady, RiskLevel.R1, 2.15m, 100.00m, 7, 73_600_000m),
        };

        foreach (var s in seeds)
        {
            _products[s.Code] = new WealthProduct
            {
                Code = s.Code,
                Name = s.Name,
                Type = s.Type,
                RiskLevel = s.Risk,
                AnnualRate = s.Rate,
                MinAmount = s.Min,
                MaxAmount = 500_000.00m,
                TermDays = s.Term,
                Status = ProductStatus.OnSale,
                NetAssetValue = 1.0000m,
                TotalRaiseLimit = 100_000_000.00m,
                RaisedAmount = s.Raised,
                SaleStartDate = At(anchor.AddMonths(-2)),
                SaleEndDate = At(anchor.AddMonths(3)),
            };
        }
    }

    private void SeedTransactions(DateTimeOffset anchor)
    {
        var random = new Random(20260927);
        var plans = new (string UserId, int Count, decimal Salary, string Employer)[]
        {
            ("u_demo01", 28, 18_500.00m, "星辰科技有限公司"),
            ("u_demo02", 25, 12_800.00m, "云图信息技术有限公司"),
            ("u_demo03", 30, 23_500.00m, "天健建筑设计研究院"),
        };

        var months = new[]
        {
            FirstOfMonth(anchor).AddMonths(-2),
            FirstOfMonth(anchor).AddMonths(-1),
            FirstOfMonth(anchor),
        };

        foreach (var plan in plans)
        {
            var savings = _accounts.Values.First(a => a.UserId == plan.UserId && a.AccountType == AccountType.Savings);
            var credit = _accounts.Values.First(a => a.UserId == plan.UserId && a.AccountType == AccountType.Credit);

            // 收入：每月工资 + 部分月份的理财收益
            foreach (var month in months)
            {
                AddSeed(plan.UserId, savings.AccountNo, month.AddDays(4).AddHours(9), plan.Salary,
                    "工资", plan.Employer, TransactionDirection.Income, TransactionChannel.Counter, "月度工资代发");

                if (random.Next(0, 100) < 70)
                {
                    var interest = Math.Round(
                        (decimal)random.Next(35, 460) + (decimal)random.NextDouble(),
                        2, MidpointRounding.AwayFromZero);
                    AddSeed(plan.UserId, savings.AccountNo, month.AddDays(19).AddHours(20).AddMinutes(random.Next(0, 50)),
                        interest, "理财收益", "模拟银行理财", TransactionDirection.Income,
                        TransactionChannel.MobileApp, "理财产品到期收益");
                }
            }

            var incomeCount = months.Length * (random.Next(0, 100) < 70 ? 2 : 1);
            var expenseTotal = Math.Max(6, plan.Count - incomeCount);
            var perMonth = expenseTotal / months.Length;
            var remainder = expenseTotal - perMonth * months.Length;

            for (var i = 0; i < months.Length; i++)
            {
                var count = perMonth + (i == months.Length - 1 ? remainder : 0);
                for (var n = 0; n < count; n++)
                {
                    var catalog = ExpenseCatalog[random.Next(ExpenseCatalog.Length)];
                    var raw = catalog.Min + (decimal)random.NextDouble() * (catalog.Max - catalog.Min);
                    var amount = Math.Round(raw, 2, MidpointRounding.AwayFromZero);
                    var merchant = catalog.Merchants[random.Next(catalog.Merchants.Length)];
                    var useCredit = random.Next(0, 100) < 38;
                    var when = RandomDateInMonth(random, months[i], anchor);
                    var channel = random.Next(0, 10) switch
                    {
                        0 or 1 => TransactionChannel.Counter,
                        2 => TransactionChannel.Atm,
                        3 or 4 => TransactionChannel.Pos,
                        5 => TransactionChannel.OnlineBank,
                        _ => TransactionChannel.MobileApp,
                    };

                    AddSeed(plan.UserId, useCredit ? credit.AccountNo : savings.AccountNo, when, amount,
                        catalog.Category, merchant, TransactionDirection.Expense, channel, null);
                }
            }
        }
    }

    private void AddSeed(
        string userId,
        string accountNo,
        DateTimeOffset when,
        decimal amount,
        string category,
        string counterparty,
        TransactionDirection direction,
        TransactionChannel channel,
        string? remark)
    {
        AddTransaction(new BankTransaction
        {
            TxNo = NextTransactionNo(when),
            UserId = userId,
            AccountNo = accountNo,
            Direction = direction,
            Amount = amount,
            Category = category,
            Merchant = $"{category}-{counterparty}",
            CounterpartyName = counterparty,
            Remark = remark,
            Channel = channel,
            Currency = DefaultCurrency,
            Timestamp = when,
        });
    }

    private static readonly (string Category, string[] Merchants, decimal Min, decimal Max)[] ExpenseCatalog =
    [
        ("餐饮", ["海底捞火锅", "星巴克咖啡", "麦当劳", "瑞幸咖啡", "呷哺呷哺", "沙县小吃", "必胜客", "味千拉面"], 15.00m, 300.00m),
        ("购物", ["京东商城", "淘宝天猫", "拼多多", "优衣库", "小米之家", "沃尔玛", "永辉超市"], 50.00m, 5_000.00m),
        ("交通", ["滴滴出行", "中国石化加油站", "12306铁路", "首汽约车", "城市停车缴费"], 5.00m, 200.00m),
        ("娱乐", ["万达影城", "好乐迪KTV", "腾讯视频会员", "Steam游戏", "大麦网演出"], 20.00m, 800.00m),
        ("医疗", ["同仁堂药店", "美团挂号", "市第一医院", "爱康体检中心"], 20.00m, 2_000.00m),
        ("教育", ["新东方教育", "当当网", "得到APP", "中国大学MOOC"], 50.00m, 3_000.00m),
        ("住房", ["链家房租", "万盛物业管理", "国家电网电费", "自来水公司水费"], 3_000.00m, 6_000.00m),
        ("通讯", ["中国移动", "中国联通", "中国电信"], 30.00m, 200.00m),
    ];

    // ---------------------------------------------------------------- 工具

    private DateTimeOffset At(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, LocalOffset);

    private static DateTimeOffset FirstOfMonth(DateTimeOffset value) =>
        new(value.Year, value.Month, 1, 0, 0, 0, value.Offset);

    private static DateTimeOffset RandomDateInMonth(Random random, DateTimeOffset monthStart, DateTimeOffset maxDate)
    {
        var daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
        var lastDay = monthStart.AddDays(daysInMonth - 1);
        var upper = maxDate < lastDay ? maxDate : lastDay;
        if (upper.Day < 1)
        {
            upper = monthStart;
        }

        var day = random.Next(1, upper.Day + 1);
        return new DateTimeOffset(
            monthStart.Year, monthStart.Month, day,
            random.Next(6, 23), random.Next(0, 60), random.Next(0, 60),
            monthStart.Offset);
    }
}
