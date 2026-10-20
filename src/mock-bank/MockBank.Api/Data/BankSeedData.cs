// ===== 种子数据工厂：为每家被模拟的银行生成初始数据 =====
//
// 数据设计的三个刻意选择：
//
// 1. **同一个人在不同银行是不同的客户**。
//    真实世界里客户号是行内主键，跨行不通用；这里用同一个 UserId 贯穿三家行，
//    是为了让「张明在工行、建行、招行各有一张卡」能被一次跨库聚合查出来 ——
//    这正是「跨行卡片」要演示的东西。
//
// 2. **李华只在工行有一张储蓄卡**。
//    收款人检索按姓名返回全部同名储蓄账户；李华若在多家银行都有储蓄账户，
//    「给李华转 500」就会变成多义匹配。保留单一储蓄账户，
//    是为了让一句话转账的演示链路保持确定；
//    而王芳在三家行都有储蓄账户，用来演示跨行收款人的多义提示。
//
// 3. **公募基金是代销产品，各行清单不同**。
//    公募基金由基金管理公司发行，银行只是代销渠道；同一只基金可以被多家银行同时代销，
//    各行数据库各自存一份独立副本（多库架构的直接体现），跨行聚合时按基金代码归并。

using System.Globalization;
using MockBank.Api.Domain;

namespace MockBank.Api.Data;

/// <summary>单家银行的初始数据集合。</summary>
public sealed record BankSeedData
{
    /// <summary>本行客户主档。</summary>
    public required IReadOnlyList<Customer> Customers { get; init; }

    /// <summary>本行账户主档。</summary>
    public required IReadOnlyList<Account> Accounts { get; init; }

    /// <summary>本行银行卡主档。</summary>
    public required IReadOnlyList<BankCard> Cards { get; init; }

    /// <summary>本行交易流水。</summary>
    public required IReadOnlyList<BankTransaction> Transactions { get; init; }

    /// <summary>本行发行的理财产品。</summary>
    public required IReadOnlyList<WealthProduct> Products { get; init; }

    /// <summary>本行代销的公募基金主档。</summary>
    public required IReadOnlyList<PublicFund> PublicFunds { get; init; }

    /// <summary>本行代销基金的净值历史点。</summary>
    public required IReadOnlyList<FundNavPoint> FundNavPoints { get; init; }
}

/// <summary>单家银行的基金代销种子数据。</summary>
/// <param name="PublicFunds">本行代销的公募基金主档。</param>
/// <param name="FundNavPoints">本行代销基金的净值历史点。</param>
public sealed record BankFundSeed(
    IReadOnlyList<PublicFund> PublicFunds,
    IReadOnlyList<FundNavPoint> FundNavPoints);

/// <summary>按银行生成初始数据的工厂。</summary>
public static class BankSeedFactory
{
    private const string DefaultCurrency = "CNY";

    /// <summary>为指定银行生成初始数据。</summary>
    /// <param name="bank">目标银行描述符。</param>
    /// <param name="anchor">时间基准日，种子数据里的相对时间都以此为锚点。</param>
    /// <returns>该行的初始数据集合。</returns>
    public static BankSeedData Create(BankDescriptor bank, DateTimeOffset anchor)
    {
        var spec = ResolveSpec(bank);
        var offset = anchor.Offset;

        var customers = spec.Customers
            .Select(c => new Customer
            {
                UserId = c.UserId,
                BankCode = bank.BankCode,
                Name = c.Name,
                IdCardNo = c.IdCard,
                Phone = c.Phone,
                RiskLevel = c.Risk,
                CustomerLevel = c.Level,
                BranchName = bank.BranchName,
                CreatedAt = At(anchor.AddMonths(-c.MonthsAgo), offset),
            })
            .ToList();

        var accounts = spec.Accounts
            .Select(a => new Account
            {
                AccountNo = a.AccountNo,
                BankCode = bank.BankCode,
                UserId = a.UserId,
                AccountType = a.Type,
                ProductName = a.Product,
                Balance = a.Balance,
                CreditLimit = a.Limit,
                CreditUsed = a.Used,
                DailyLimit = a.Daily,
                Status = AccountStatus.Normal,
                Currency = DefaultCurrency,
                OpenDate = At(anchor.AddMonths(-a.MonthsAgo), offset),
                BranchName = bank.BranchName,
            })
            .ToList();

        // 卡与账户一一对应：卡号即账号，发卡行取本行。
        var cards = accounts
            .Select(a =>
            {
                var customer = customers.First(c => c.UserId == a.UserId);
                return new BankCard
                {
                    CardNo = a.AccountNo,
                    BankCode = bank.BankCode,
                    AccountNo = a.AccountNo,
                    UserId = a.UserId,
                    CardType = a.AccountType == AccountType.Credit ? CardType.Credit : CardType.Debit,
                    Status = CardStatus.Normal,
                    BoundPhone = customer.Phone,
                    DailyLimit = a.DailyLimit,
                    IssueDate = a.OpenDate,
                    ExpiryDate = At(anchor.AddYears(4), offset),
                    CardOrg = bank.BankName,
                    StatusReason = "初始状态",
                    StatusChangedAt = a.OpenDate,
                };
            })
            .ToList();

        var products = spec.Products
            .Select(p => new WealthProduct
            {
                Code = p.Code,
                BankCode = bank.BankCode,
                Name = p.Name,
                Type = p.Type,
                RiskLevel = p.Risk,
                AnnualRate = p.Rate,
                MinAmount = p.Min,
                MaxAmount = 500_000.00m,
                TermDays = p.Term,
                Status = ProductStatus.OnSale,
                NetAssetValue = 1.0000m,
                TotalRaiseLimit = 100_000_000.00m,
                RaisedAmount = p.Raised,
                SaleStartDate = At(anchor.AddMonths(-2), offset),
                SaleEndDate = At(anchor.AddMonths(3), offset),
            })
            .ToList();

        var transactions = BuildTransactions(bank, spec, accounts, anchor);

        var fundSeed = CreateFundCatalog(bank, anchor);

        return new BankSeedData
        {
            Customers = customers,
            Accounts = accounts,
            Cards = cards,
            Transactions = transactions,
            Products = products,
            PublicFunds = fundSeed.PublicFunds,
            FundNavPoints = fundSeed.FundNavPoints,
        };
    }

    /// <summary>
    /// 生成指定银行的基金代销清单与净值历史。
    /// 单独暴露这个方法，是为了让「老库升级」场景能只补基金数据：
    /// 基金表是后加的，历史数据库里结构有了但数据是空的，
    /// 不补的话基金相关接口会永远返回空列表（静默失效，且不报错）。
    /// </summary>
    /// <param name="bank">目标银行描述符。</param>
    /// <param name="anchor">时间基准日，净值序列以该日为最后一天向前回溯。</param>
    /// <returns>该行的基金代销种子数据。</returns>
    public static BankFundSeed CreateFundCatalog(BankDescriptor bank, DateTimeOffset anchor)
    {
        if (!DistributorCatalog.TryGetValue(bank.BankCode, out var codes))
        {
            throw new NotSupportedException($"没有为银行 {bank.BankCode} 配置基金代销清单。");
        }

        // 净值序列的锚点截断到「自然日」：同一个进程里各行必须拿到同一天的日期，
        // 否则跨行归并时同一天会出现两套净值，净值走势图会自相矛盾。
        var navAnchor = anchor.Date;
        var funds = new List<PublicFund>();
        var navPoints = new List<FundNavPoint>();

        foreach (var code in codes)
        {
            var profile = FundUniverse.First(f => f.FundCode == code);
            var series = FundNavSeriesBuilder.Build(profile, navAnchor);
            funds.Add(ToFundEntity(bank, profile, series));
            navPoints.AddRange(series.NavPoints.Select(p => new FundNavPoint
            {
                FundCode = profile.FundCode,
                NavDate = p.NavDate,
                UnitNav = p.UnitNav,
                AccumulatedNav = p.AccumulatedNav,
                DailyGrowthRate = p.DailyGrowthRate,
            }));
        }

        return new BankFundSeed(funds, navPoints);
    }

    // ---------------------------------------------------------------- 各行规格

    private sealed record CustomerSpec(
        string UserId, string Name, string IdCard, string Phone,
        RiskLevel Risk, string Level, int MonthsAgo);

    private sealed record AccountSpec(
        string UserId, string AccountNo, AccountType Type, string Product,
        decimal Balance, decimal Limit, decimal Used, decimal Daily, int MonthsAgo);

    private sealed record ProductSpec(
        string Code, string Name, ProductType Type, RiskLevel Risk,
        decimal Rate, decimal Min, int Term, decimal Raised);

    private sealed record BankSpec(
        IReadOnlyList<CustomerSpec> Customers,
        IReadOnlyList<AccountSpec> Accounts,
        IReadOnlyList<ProductSpec> Products,
        IReadOnlyList<(string UserId, int Count, decimal Salary, string Employer)> Payroll,
        int RandomSeed);

    private static BankSpec ResolveSpec(BankDescriptor bank) => bank.BankCode switch
    {
        "ICBC" => new BankSpec(
            Customers:
            [
                new("u_demo01", "张明", "110101199001011234", "13800138000", RiskLevel.R3, "金卡", 62),
                new("u_demo02", "李华", "310101199203054321", "13900139000", RiskLevel.R2, "普通", 38),
                new("u_demo03", "王芳", "440101199505068765", "13700137000", RiskLevel.R4, "白金", 26),
            ],
            Accounts:
            [
                new("u_demo01", "6222020200000001", AccountType.Savings, "薪金卡", 250_000.00m, 0m, 0m, 500_000.00m, 62),
                new("u_demo01", "6222020200000002", AccountType.Credit, "白金信用卡", -12_800.00m, 50_000.00m, 12_800.00m, 50_000.00m, 48),
                new("u_demo02", "6222020200000003", AccountType.Savings, "活期一本通", 88_000.50m, 0m, 0m, 500_000.00m, 38),
                new("u_demo02", "6222020200000004", AccountType.Credit, "金卡信用卡", -3_200.00m, 30_000.00m, 3_200.00m, 50_000.00m, 26),
                new("u_demo03", "6222020200000005", AccountType.Savings, "至尊储蓄卡", 152_000.00m, 0m, 0m, 500_000.00m, 26),
                new("u_demo03", "6222020200000006", AccountType.Credit, "钻石信用卡", -45_000.00m, 80_000.00m, 45_000.00m, 50_000.00m, 20),
            ],
            Products:
            [
                new("WP001", "稳健增利90天", ProductType.Steady, RiskLevel.R2, 3.20m, 1_000.00m, 90, 62_000_000m),
                new("WP002", "安心纯债180天", ProductType.Steady, RiskLevel.R2, 3.85m, 5_000.00m, 180, 48_500_000m),
            ],
            Payroll:
            [
                ("u_demo01", 28, 18_500.00m, "星辰科技有限公司"),
                ("u_demo02", 25, 12_800.00m, "云图信息技术有限公司"),
                ("u_demo03", 30, 23_500.00m, "天健建筑设计研究院"),
            ],
            RandomSeed: 20260927),

        "CCB" => new BankSpec(
            Customers:
            [
                new("u_demo01", "张明", "110101199001011234", "13800138000", RiskLevel.R2, "金卡", 44),
                new("u_demo03", "王芳", "440101199505068765", "13700137000", RiskLevel.R3, "白金", 24),
            ],
            Accounts:
            [
                new("u_demo01", "6217000100000001", AccountType.Savings, "乐当家理财卡", 85_000.00m, 0m, 0m, 300_000.00m, 44),
                new("u_demo01", "6217000100000002", AccountType.Credit, "建行白金信用卡", -6_800.00m, 40_000.00m, 6_800.00m, 50_000.00m, 36),
                new("u_demo03", "6217000100000003", AccountType.Savings, "建行储蓄卡", 120_000.00m, 0m, 0m, 300_000.00m, 24),
            ],
            Products:
            [
                new("WP101", "平衡优选混合", ProductType.Balanced, RiskLevel.R3, 5.10m, 10_000.00m, 365, 91_200_000m),
                new("WP102", "成长精选30天", ProductType.Aggressive, RiskLevel.R3, 4.25m, 5_000.00m, 30, 18_700_000m),
            ],
            Payroll:
            [
                ("u_demo01", 18, 18_500.00m, "星辰科技有限公司"),
                ("u_demo03", 20, 23_500.00m, "天健建筑设计研究院"),
            ],
            RandomSeed: 20260928),

        "CMB" => new BankSpec(
            Customers:
            [
                new("u_demo01", "张明", "110101199001011234", "13800138000", RiskLevel.R3, "金卡", 30),
                new("u_demo03", "王芳", "440101199505068765", "13700137000", RiskLevel.R3, "白金", 18),
            ],
            Accounts:
            [
                new("u_demo01", "6225000100000001", AccountType.Savings, "招行一卡通", 32_000.00m, 0m, 0m, 200_000.00m, 30),
                new("u_demo01", "6225000100000002", AccountType.Credit, "金葵花信用卡", -23_000.00m, 60_000.00m, 23_000.00m, 50_000.00m, 22),
                new("u_demo03", "6225000100000003", AccountType.Savings, "招行储蓄卡", 66_000.00m, 0m, 0m, 200_000.00m, 18),
            ],
            Products:
            [
                new("WP201", "进取科技主题", ProductType.Aggressive, RiskLevel.R4, 8.50m, 20_000.00m, 180, 35_400_000m),
                new("WP202", "短债现金管理", ProductType.Steady, RiskLevel.R1, 2.15m, 100.00m, 7, 73_600_000m),
            ],
            Payroll:
            [
                ("u_demo01", 12, 18_500.00m, "星辰科技有限公司"),
                ("u_demo03", 14, 23_500.00m, "天健建筑设计研究院"),
            ],
            RandomSeed: 20260929),

        _ => throw new NotSupportedException($"没有为银行 {bank.BankCode} 配置种子数据。"),
    };

    // ---------------------------------------------------------------- 流水生成

    private static IReadOnlyList<BankTransaction> BuildTransactions(
        BankDescriptor bank,
        BankSpec spec,
        IReadOnlyList<Account> accounts,
        DateTimeOffset anchor)
    {
        var random = new Random(spec.RandomSeed);
        var transactions = new List<BankTransaction>();
        var sequence = 0L;

        // 本行的流水号在本行内唯一，按本行序发生成；带行代码以保证跨行后仍全局唯一。
        string NextTxNo(DateTimeOffset when) =>
            $"TX{when:yyyyMMdd}{bank.BankCode}{Interlocked.Increment(ref sequence):D7}";

        var months = new[]
        {
            FirstOfMonth(anchor).AddMonths(-2),
            FirstOfMonth(anchor).AddMonths(-1),
            FirstOfMonth(anchor),
        };

        foreach (var plan in spec.Payroll)
        {
            var savings = accounts.First(a => a.UserId == plan.UserId && a.AccountType == AccountType.Savings);
            var credit = accounts.FirstOrDefault(a => a.UserId == plan.UserId && a.AccountType == AccountType.Credit);

            // 收入：每月工资 + 部分月份的理财收益
            foreach (var month in months)
            {
                transactions.Add(NewTransaction(
                    bank, NextTxNo, plan.UserId, savings.AccountNo, month.AddDays(4).AddHours(9),
                    plan.Salary, "工资", plan.Employer, TransactionDirection.Income,
                    TransactionChannel.Counter, "月度工资代发"));

                if (random.Next(0, 100) < 70)
                {
                    var interest = Math.Round(
                        (decimal)random.Next(35, 460) + (decimal)random.NextDouble(),
                        2, MidpointRounding.AwayFromZero);
                    transactions.Add(NewTransaction(
                        bank, NextTxNo, plan.UserId, savings.AccountNo, month.AddDays(19).AddHours(20).AddMinutes(random.Next(0, 50)),
                        interest, "理财收益", $"{bank.ShortName}理财", TransactionDirection.Income,
                        TransactionChannel.MobileApp, "理财产品到期收益"));
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
                    var useCredit = credit is not null && random.Next(0, 100) < 38;
                    var when = RandomDateInMonth(random, months[i], anchor);
                    var channel = random.Next(0, 10) switch
                    {
                        0 or 1 => TransactionChannel.Counter,
                        2 => TransactionChannel.Atm,
                        3 or 4 => TransactionChannel.Pos,
                        5 => TransactionChannel.OnlineBank,
                        _ => TransactionChannel.MobileApp,
                    };

                    transactions.Add(NewTransaction(
                        bank, NextTxNo, plan.UserId,
                        useCredit ? credit!.AccountNo : savings.AccountNo, when, amount,
                        catalog.Category, merchant, TransactionDirection.Expense, channel, null));
                }
            }
        }

        return transactions;
    }

    private static BankTransaction NewTransaction(
        BankDescriptor bank,
        Func<DateTimeOffset, string> nextTxNo,
        string userId,
        string accountNo,
        DateTimeOffset when,
        decimal amount,
        string category,
        string counterparty,
        TransactionDirection direction,
        TransactionChannel channel,
        string? remark) =>
        new()
        {
            TxNo = nextTxNo(when),
            BankCode = bank.BankCode,
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
        };

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

    // ---------------------------------------------------------------- 基金代销清单

    /// <summary>
    /// 全系统共用的公募基金目录。代码、简称、基金公司、基金经理、费率、规模都取真实公募产品的形态，
    /// 这样代销清单看起来才像银行 App 里的「基金超市」，而不是随机生成的占位数据。
    /// </summary>
    private static readonly IReadOnlyList<FundProfile> FundUniverse =
    [
        new("000001", "华夏成长混合", "华夏成长证券投资基金",
            FundCategory.Mixed, FundRiskLevel.R3, "华夏基金管理有限公司", "潘学军", "2001-04-17",
            43.15m, 1.20m, 0.20m, 1.50m, 0.50m, FundSubscriptionStatus.OpenSubscription, 10m,
            0.0231m, 0.0030m, 17.5m, "中证综合指数收益率×70%+银行活期存款利率(税后)×30%"),
        new("000032", "易方达信用债债券A", "易方达信用债债券型证券投资基金A类",
            FundCategory.Bond, FundRiskLevel.R2, "易方达基金管理有限公司", "胡剑", "2011-03-29",
            18.72m, 0.30m, 0.10m, 0.80m, 0.10m, FundSubscriptionStatus.OpenSubscription, 1m,
            0.0402m, 0.0060m, 2.6m, "中债综合全价指数收益率"),
        new("000198", "天弘余额宝货币", "天弘余额宝货币市场基金",
            FundCategory.MoneyMarket, FundRiskLevel.R1, "天弘基金管理有限公司", "王旭", "2013-06-14",
            7623.45m, 0.15m, 0.05m, 0.00m, 0.00m, FundSubscriptionStatus.OpenSubscription, 1m,
            0.0161m, 0.0161m, 0.15m, "七天通知存款利率(税后)"),
        new("000751", "嘉实新兴产业股票", "嘉实新兴产业股票型证券投资基金",
            FundCategory.Equity, FundRiskLevel.R4, "嘉实基金管理有限公司", "归凯", "2014-03-04",
            27.34m, 1.20m, 0.20m, 1.50m, 0.50m, FundSubscriptionStatus.Limited, 10m,
            0.0863m, 0.0030m, 21.0m, "沪深300指数收益率×95%+银行活期存款利率(税后)×5%"),
        new("000961", "天弘沪深300ETF联接A", "天弘沪深300交易型开放式指数证券投资基金联接基金A类",
            FundCategory.Index, FundRiskLevel.R4, "天弘基金管理有限公司", "熊耀军", "2013-03-14",
            108.62m, 0.50m, 0.10m, 1.20m, 0.50m, FundSubscriptionStatus.OpenSubscription, 10m,
            0.0543m, 0.0030m, 19.0m, "沪深300指数收益率×95%+银行活期存款利率(税后)×5%"),
        new("001102", "前海国家主题混合", "前海开源国家主题优势企业灵活配置混合型证券投资基金",
            FundCategory.Mixed, FundRiskLevel.R4, "前海开源基金管理有限公司", "王聪", "2015-03-30",
            5.82m, 1.50m, 0.10m, 1.50m, 0.50m, FundSubscriptionStatus.Suspended, 10m,
            0.0376m, 0.0030m, 22.5m, "中证800指数收益率×80%+银行活期存款利率(税后)×20%"),
        new("001410", "信澳新能源产业股票", "信澳新能源产业股票型证券投资基金",
            FundCategory.Equity, FundRiskLevel.R4, "信达澳亚基金管理有限公司", "冯明远", "2015-06-10",
            61.27m, 1.50m, 0.25m, 1.50m, 0.50m, FundSubscriptionStatus.OpenSubscription, 10m,
            0.0972m, 0.0030m, 24.0m, "沪深300指数收益率×95%+银行活期存款利率(税后)×5%"),
        new("001594", "天弘中证银行指数A", "天弘中证银行指数型证券投资基金A类",
            FundCategory.Index, FundRiskLevel.R4, "天弘基金管理有限公司", "熊耀军", "2015-12-31",
            21.36m, 0.50m, 0.10m, 1.20m, 0.50m, FundSubscriptionStatus.OpenSubscription, 10m,
            0.0684m, 0.0030m, 20.5m, "中证银行指数收益率×95%+银行活期存款利率(税后)×5%"),
        new("003096", "中欧医疗健康混合A", "中欧医疗健康混合型证券投资基金A类",
            FundCategory.Mixed, FundRiskLevel.R4, "中欧基金管理有限公司", "葛兰", "2016-02-18",
            268.47m, 1.50m, 0.25m, 1.50m, 0.50m, FundSubscriptionStatus.Limited, 10m,
            0.0786m, 0.0030m, 23.5m, "中证医药卫生指数收益率×70%+银行活期存款利率(税后)×30%"),
        new("005156", "嘉实领航资产配置混合FOF", "嘉实领航资产配置混合型基金中基金(FOF)",
            FundCategory.FundOfFunds, FundRiskLevel.R3, "嘉实基金管理有限公司", "王征", "2017-12-14",
            8.16m, 0.80m, 0.20m, 1.00m, 0.50m, FundSubscriptionStatus.OpenSubscription, 10m,
            0.0482m, 0.0050m, 12.0m, "中债综合全价指数收益率×30%+沪深300指数收益率×50%+银行活期存款利率(税后)×20%"),
        new("005827", "易方达蓝筹精选混合", "易方达蓝筹精选混合型证券投资基金",
            FundCategory.Mixed, FundRiskLevel.R4, "易方达基金管理有限公司", "张坤", "2015-06-26",
            214.85m, 1.20m, 0.20m, 1.50m, 0.50m, FundSubscriptionStatus.Limited, 10m,
            0.0716m, 0.0120m, 19.5m, "中证白酒指数收益率×80%+银行活期存款利率(税后)×20%"),
        new("050027", "博时信用债券A", "博时信用债券型证券投资基金A类",
            FundCategory.Bond, FundRiskLevel.R2, "博时基金管理有限公司", "张鹏", "2011-12-08",
            9.46m, 0.30m, 0.10m, 0.80m, 0.10m, FundSubscriptionStatus.OpenSubscription, 1m,
            0.0354m, 0.0060m, 2.2m, "中债综合全价指数收益率"),
        new("110011", "易方达中小盘混合", "易方达中小盘混合型证券投资基金",
            FundCategory.Mixed, FundRiskLevel.R4, "易方达基金管理有限公司", "张坤", "2007-05-25",
            183.24m, 1.20m, 0.20m, 1.50m, 0.50m, FundSubscriptionStatus.OpenSubscription, 10m,
            0.0925m, 0.0060m, 21.5m, "中证500指数收益率×75%+银行活期存款利率(税后)×25%"),
        new("110017", "易方达增强回报债券A", "易方达增强回报债券型证券投资基金A类",
            FundCategory.Bond, FundRiskLevel.R2, "易方达基金管理有限公司", "胡剑", "2007-03-20",
            25.83m, 0.80m, 0.20m, 1.00m, 0.50m, FundSubscriptionStatus.OpenSubscription, 1m,
            0.0461m, 0.0080m, 3.2m, "中债综合指数收益率×90%+银行活期存款利率(税后)×10%"),
        new("160119", "南方中证500ETF联接A", "南方中证500交易型开放式指数证券投资基金联接基金A类",
            FundCategory.Index, FundRiskLevel.R4, "南方基金管理有限公司", "滕国根", "2013-03-08",
            42.68m, 0.50m, 0.10m, 1.20m, 0.50m, FundSubscriptionStatus.OpenSubscription, 10m,
            0.0726m, 0.0030m, 20.0m, "中证500指数收益率×95%+银行活期存款利率(税后)×5%"),
        new("161003", "富国天惠成长混合LOF", "富国天惠成长混合型证券投资基金(LOF)",
            FundCategory.Mixed, FundRiskLevel.R3, "富国基金管理有限公司", "朱少醒", "2005-11-21",
            38.29m, 1.20m, 0.20m, 1.50m, 0.50m, FundSubscriptionStatus.OpenSubscription, 10m,
            0.1043m, 0.0060m, 18.5m, "中证800指数收益率×80%+银行活期存款利率(税后)×20%"),
        new("161725", "招商中证白酒指数", "招商中证白酒交易型开放式指数证券投资基金",
            FundCategory.Index, FundRiskLevel.R4, "招商基金管理有限公司", "侯昊", "2015-05-27",
            92.31m, 1.00m, 0.20m, 1.20m, 0.50m, FundSubscriptionStatus.Limited, 10m,
            0.0958m, 0.0030m, 26.0m, "中证白酒指数收益率×95%+银行活期存款利率(税后)×5%"),
        new("270042", "广发纳斯达克100指数A", "广发纳斯达克100交易型开放式指数证券投资基金联接基金A类",
            FundCategory.QualifiedForeign, FundRiskLevel.R4, "广发基金管理有限公司", "林英睿", "2017-08-31",
            36.52m, 0.80m, 0.25m, 1.20m, 0.50m, FundSubscriptionStatus.OpenSubscription, 10m,
            0.1286m, 0.0030m, 17.5m, "纳斯达克100指数收益率×95%+银行活期存款利率(税后)×5%"),
        new("320007", "诺安成长混合", "诺安成长混合型证券投资基金",
            FundCategory.Mixed, FundRiskLevel.R4, "诺安基金管理有限公司", "曹颖", "2009-06-30",
            15.78m, 1.50m, 0.25m, 1.50m, 0.50m, FundSubscriptionStatus.OpenSubscription, 10m,
            0.0427m, 0.0030m, 22.0m, "中证综合指数收益率×80%+银行活期存款利率(税后)×20%"),
        new("519674", "银河创新成长混合", "银河创新成长混合型证券投资基金",
            FundCategory.Mixed, FundRiskLevel.R4, "银河基金管理有限公司", "郑巍山", "2002-09-06",
            12.45m, 1.20m, 0.20m, 1.50m, 0.50m, FundSubscriptionStatus.OpenSubscription, 10m,
            0.0568m, 0.0030m, 20.5m, "中证综合指数收益率×80%+银行活期存款利率(税后)×20%"),
    ];

    /// <summary>
    /// 各行的基金代销清单。刻意让清单互不相同、且存在重叠：
    /// 重叠的基金体现「同一只基金被多家银行同时代销」，只出现在一行的基金体现「各行货架不一样」。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> DistributorCatalog =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ICBC"] =
            [
                "000001", "000032", "000198", "000961", "001102", "003096",
                "005827", "110017", "160119", "161725", "320007", "519674",
            ],
            ["CCB"] =
            [
                "000032", "000198", "000751", "001410", "001594", "005156",
                "110011", "161003", "161725", "050027",
            ],
            ["CMB"] =
            [
                "000001", "000198", "000961", "003096", "005827",
                "110011", "161003", "161725", "270042",
            ],
        };

    /// <summary>把基金档案与净值序列组装成公募基金实体。</summary>
    /// <param name="bank">代销该基金的银行。</param>
    /// <param name="profile">基金档案。</param>
    /// <param name="series">该基金的净值序列。</param>
    /// <returns>公募基金实体。</returns>
    private static PublicFund ToFundEntity(BankDescriptor bank, FundProfile profile, FundNavSeries series) =>
        new()
        {
            FundCode = profile.FundCode,
            BankCode = bank.BankCode,
            FundName = profile.FundName,
            FundFullName = profile.FundFullName,
            Category = profile.Category,
            RiskLevel = profile.RiskLevel,
            FundCompany = profile.FundCompany,
            FundManager = profile.FundManager,
            InceptionDate = InceptionDate(profile),
            NavDate = series.NavDate,
            UnitNav = series.UnitNav,
            AccumulatedNav = series.AccumulatedNav,
            DailyGrowthRate = series.DailyGrowthRate,
            MonthlyReturn = series.MonthlyReturn,
            YearlyReturn = series.YearlyReturn,
            SinceInceptionReturn = series.SinceInceptionReturn,
            FundScale = profile.FundScale,
            ManagementFeeRate = profile.ManagementFeeRate,
            CustodyFeeRate = profile.CustodyFeeRate,
            SubscriptionFeeRate = profile.SubscriptionFeeRate,
            RedemptionFeeRate = profile.RedemptionFeeRate,
            InvestmentScope = InvestmentScopeOf(profile.Category),
            PerformanceBenchmark = profile.Benchmark,
            SubscriptionStatus = profile.SubscriptionStatus,
            MinPurchaseAmount = profile.MinPurchaseAmount,
        };

    /// <summary>按基金类型给出投资范围的标准表述。</summary>
    /// <param name="category">基金类型。</param>
    /// <returns>投资范围描述。</returns>
    private static string InvestmentScopeOf(FundCategory category) =>
        category switch
        {
            FundCategory.MoneyMarket =>
                "本基金投资于现金、期限在一年以内的银行存款、债券回购、央行票据、债券型基金等现金管理工具，保持不低于5%的现金或者到期日在一年以内的政府债券。",
            FundCategory.Bond =>
                "本基金投资于国债、金融债、企业债、公司债、可转换公司债、短期融资券、中期票据等债券资产，债券资产占基金资产的比例不低于80%。",
            FundCategory.Mixed =>
                "本基金投资于股票、债券、货币市场工具及中国证监会允许基金投资的其他金融工具，其中股票资产占基金资产的比例为0%-95%，保持不低于5%的现金或者到期日在一年以内的政府债券。",
            FundCategory.Equity =>
                "本基金投资于股票资产，股票资产占基金资产的比例不低于80%，同时保持不低于5%的现金或者到期日在一年以内的政府债券。",
            FundCategory.Index =>
                "本基金主要投资于标的指数的成份券及其备选成份券，股票资产占基金资产的比例不低于90%，保持不低于5%的现金或者到期日在一年以内的政府债券。",
            FundCategory.QualifiedForeign =>
                "本基金主要投资于境外市场股票、债券及银行存款等，经国务院证券监督管理机构批准的中国证监会认可的投资品种，境外投资比例不低于基金资产的80%。",
            FundCategory.FundOfFunds =>
                "本基金投资于其他基金份额，其中权益类基金（股票型、混合型等）占基金资产的比例不低于70%，保持不低于5%的现金或者到期日在一年以内的政府债券。",
            _ => "本基金投资于中国证监会允许基金投资的其他金融工具。",
        };

    // ---------------------------------------------------------------- 净值序列生成

    /// <summary>
    /// 基金净值序列生成器。
    ///
    /// 数值模型（目标是让所有数字互相对得上，而不是各写各的）：
    ///   * 累计净值 A 按「日总收益率」复利增长，日总收益率 = 年化漂移 + 随机波动；
    ///   * 分红从累计净值中派发并计入累计每份分红 D，于是单位净值 U = A - D，A ≥ U 恒成立；
    ///   * 净值点上的日增长率就是 U 的日环比，因此与单位净值序列严格对应；
    ///   * 近一月、近一年、成立以来收益率全部由同一条序列算出，不会互相打脸。
    ///
    /// 随机性来源是「基金代码」派生的固定种子：同一只基金在任意银行、任意次构建里
    /// 都会得到完全相同的净值序列（各行副本因此可以安全归并）。
    /// </summary>
    private static class FundNavSeriesBuilder
    {
        /// <summary>内部生成的序列长度：一年，用于计算近一年收益率。</summary>
        private const int SeriesDays = 365;

        /// <summary>对外暴露的净值点数量：最近 90 个自然日。</summary>
        private const int NavPointDays = 90;

        /// <summary>净值与收益率保留的小数位。</summary>
        private const int Scale = 4;

        /// <summary>生成一只基金的净值序列与派生指标。</summary>
        /// <param name="profile">基金档案。</param>
        /// <param name="anchor">序列的最后一天（自然日）。</param>
        /// <returns>净值序列与派生指标。</returns>
        public static FundNavSeries Build(FundProfile profile, DateTime anchor)
        {
            var inception = InceptionDate(profile);
            var years = Math.Max(1.0, (anchor - inception).TotalDays / 365.25);

            // 成立至今按年化总收益率复利得到的累计净值（今天的值）。
            var accumulatedModel = Compound((double)profile.AnnualTotalReturn, years);
            var dailyReturns = GenerateDailyReturns(profile);
            var windowStart = anchor.AddDays(-(SeriesDays - 1));

            var accumulated = BuildAccumulatedSeries(accumulatedModel, dailyReturns);
            var dividends = BuildDividendSchedule(profile, windowStart, anchor, accumulated);
            var (unitNavs, dailyRates) = BuildUnitNavSeries(profile, accumulated, dividends, accumulatedModel);

            return Summarize(profile, anchor, accumulated, unitNavs, dailyRates, accumulatedModel);
        }

        /// <summary>生成窗口期内的日总收益率序列（百分数）。</summary>
        /// <param name="profile">基金档案。</param>
        /// <returns>日收益率序列。</returns>
        private static decimal[] GenerateDailyReturns(FundProfile profile)
        {
            var random = new Random(SeedFromFundCode(profile.FundCode));
            var dailyDrift = (decimal)(Math.Pow(1.0 + (double)profile.AnnualTotalReturn, 1.0 / SeriesDays) - 1.0) * 100m;
            var dailyVolatility = (double)profile.AnnualVolatility / Math.Sqrt(SeriesDays);

            var returns = new decimal[SeriesDays];
            for (var i = 0; i < SeriesDays; i++)
            {
                var noise = (decimal)(NextGaussian(random) * dailyVolatility);
                returns[i] = Math.Round(dailyDrift + noise, 6, MidpointRounding.AwayFromZero);
            }

            return returns;
        }

        /// <summary>由日收益率推出累计净值序列，终点精确落在模型值上。</summary>
        /// <param name="accumulatedModel">今天应有的累计净值。</param>
        /// <param name="dailyReturns">日收益率序列。</param>
        /// <returns>累计净值序列。</returns>
        private static decimal[] BuildAccumulatedSeries(decimal accumulatedModel, decimal[] dailyReturns)
        {
            var factor = 1m;
            foreach (var rate in dailyReturns)
            {
                factor *= 1m + rate / 100m;
            }

            var accumulated = new decimal[SeriesDays];
            accumulated[0] = Round(accumulatedModel / factor);
            for (var i = 1; i < SeriesDays; i++)
            {
                accumulated[i] = Round(accumulated[i - 1] * (1m + dailyReturns[i] / 100m));
            }

            return accumulated;
        }

        /// <summary>
        /// 安排窗口期内的分红：货币基金按日结转全部收益；其余基金在年度权益登记日派发一次。
        /// </summary>
        /// <param name="profile">基金档案。</param>
        /// <param name="windowStart">序列第一天。</param>
        /// <param name="anchor">序列最后一天。</param>
        /// <param name="accumulated">累计净值序列。</param>
        /// <returns>逐日分红金额（未派发为 0）。</returns>
        private static decimal[] BuildDividendSchedule(
            FundProfile profile, DateTime windowStart, DateTime anchor, decimal[] accumulated)
        {
            var dividends = new decimal[SeriesDays];

            if (profile.Category == FundCategory.MoneyMarket)
            {
                // 货币基金的收益按日全额结转：每日分红 = 当日累计净值的增量，单位净值恒为 1.0000。
                for (var i = 1; i < SeriesDays; i++)
                {
                    dividends[i] = accumulated[i] - accumulated[i - 1];
                }

                return dividends;
            }

            var inception = InceptionDate(profile);
            var exDividend = SafeAnniversary(inception, anchor);
            var index = (exDividend - windowStart).Days;
            if (index < 0 || index >= SeriesDays)
            {
                return dividends;
            }

            var baseValue = index == 0 ? accumulated[0] : accumulated[index - 1];
            dividends[index] = Round(baseValue * profile.AnnualDividendYield);
            return dividends;
        }

        /// <summary>由累计净值与分红推出单位净值序列及其日环比。</summary>
        /// <param name="profile">基金档案。</param>
        /// <param name="accumulated">累计净值序列。</param>
        /// <param name="dividends">逐日分红。</param>
        /// <param name="accumulatedModel">今天应有的累计净值。</param>
        /// <returns>单位净值序列与日增长率序列。</returns>
        private static (decimal[] UnitNavs, decimal[] DailyRates) BuildUnitNavSeries(
            FundProfile profile, decimal[] accumulated, decimal[] dividends, decimal accumulatedModel)
        {
            var dividendBeforeWindow = TotalDividend(profile, accumulatedModel) - dividends.Sum();
            var runningDividend = dividendBeforeWindow;
            var unitNavs = new decimal[SeriesDays];
            var dailyRates = new decimal[SeriesDays];

            for (var i = 0; i < SeriesDays; i++)
            {
                runningDividend += dividends[i];
                unitNavs[i] = profile.Category == FundCategory.MoneyMarket
                    ? 1.0000m
                    : Round(accumulated[i] - runningDividend);
                dailyRates[i] = i == 0 ? 0m : Round((unitNavs[i] - unitNavs[i - 1]) / unitNavs[i - 1] * 100m);
            }

            return (unitNavs, dailyRates);
        }

        /// <summary>汇总净值点与派生收益率指标。</summary>
        /// <param name="profile">基金档案。</param>
        /// <param name="anchor">序列最后一天。</param>
        /// <param name="accumulated">累计净值序列。</param>
        /// <param name="unitNavs">单位净值序列。</param>
        /// <param name="dailyRates">日增长率序列。</param>
        /// <param name="accumulatedModel">今天应有的累计净值（成立以来涨幅的基准）。</param>
        /// <returns>净值序列与派生指标。</returns>
        private static FundNavSeries Summarize(
            FundProfile profile,
            DateTime anchor,
            decimal[] accumulated,
            decimal[] unitNavs,
            decimal[] dailyRates,
            decimal accumulatedModel)
        {
            var last = SeriesDays - 1;
            var navPoints = new List<FundNavPoint>();
            for (var i = last - NavPointDays + 1; i <= last; i++)
            {
                navPoints.Add(new FundNavPoint
                {
                    FundCode = profile.FundCode,
                    NavDate = anchor.AddDays(i - last),
                    UnitNav = unitNavs[i],
                    AccumulatedNav = accumulated[i],
                    DailyGrowthRate = dailyRates[i],
                });
            }

            return new FundNavSeries(
                navPoints,
                anchor,
                unitNavs[last],
                accumulated[last],
                dailyRates[last],
                ReturnBetween(accumulated, last, 30),
                ReturnBetween(accumulated, last, SeriesDays - 1),
                Round((accumulatedModel - 1m) * 100m));
        }

        /// <summary>计算累计净值在某段区间内的收益率（百分数）。</summary>
        /// <param name="accumulated">累计净值序列。</param>
        /// <param name="last">区间终点下标。</param>
        /// <param name="span">区间长度（天）。</param>
        /// <returns>区间收益率（百分数）。</returns>
        private static decimal ReturnBetween(decimal[] accumulated, int last, int span)
        {
            var start = Math.Max(0, last - span);
            if (accumulated[start] == 0m) return 0m;
            return Round((accumulated[last] - accumulated[start]) / accumulated[start] * 100m);
        }

        /// <summary>成立以来累计分红：按「分红率 × 累计净值」的年度计提口径求和。</summary>
        /// <param name="profile">基金档案。</param>
        /// <param name="accumulatedModel">今天应有的累计净值。</param>
        /// <returns>成立以来每份累计分红。</returns>
        private static decimal TotalDividend(FundProfile profile, decimal accumulatedModel)
        {
            if (profile.AnnualTotalReturn <= 0m || profile.AnnualDividendYield <= 0m) return 0m;
            return profile.AnnualDividendYield * (accumulatedModel - 1m) / profile.AnnualTotalReturn;
        }
    }

    /// <summary>公募基金档案：一只基金的静态属性与净值生成参数。</summary>
    /// <param name="FundCode">基金代码。</param>
    /// <param name="FundName">基金简称。</param>
    /// <param name="FundFullName">基金全称。</param>
    /// <param name="Category">基金类型。</param>
    /// <param name="RiskLevel">基金风险等级。</param>
    /// <param name="FundCompany">基金公司。</param>
    /// <param name="FundManager">基金经理。</param>
    /// <param name="InceptionDate">成立日期。</param>
    /// <param name="FundScale">基金规模（亿元）。</param>
    /// <param name="ManagementFeeRate">管理费率（年化百分数）。</param>
    /// <param name="CustodyFeeRate">托管费率（年化百分数）。</param>
    /// <param name="SubscriptionFeeRate">申购费率（百分数）。</param>
    /// <param name="RedemptionFeeRate">赎回费率（百分数）。</param>
    /// <param name="SubscriptionStatus">申赎状态。</param>
    /// <param name="MinPurchaseAmount">起购金额（元）。</param>
    /// <param name="AnnualTotalReturn">年化总收益率（小数，用于生成净值序列）。</param>
    /// <param name="AnnualDividendYield">年化分红率（小数，用于生成净值序列）。</param>
    /// <param name="AnnualVolatility">年化波动率（百分数，用于生成净值序列）。</param>
    /// <param name="Benchmark">业绩比较基准。</param>
    private sealed record FundProfile(
        string FundCode,
        string FundName,
        string FundFullName,
        FundCategory Category,
        FundRiskLevel RiskLevel,
        string FundCompany,
        string FundManager,
        string InceptionDate,
        decimal FundScale,
        decimal ManagementFeeRate,
        decimal CustodyFeeRate,
        decimal SubscriptionFeeRate,
        decimal RedemptionFeeRate,
        FundSubscriptionStatus SubscriptionStatus,
        decimal MinPurchaseAmount,
        decimal AnnualTotalReturn,
        decimal AnnualDividendYield,
        decimal AnnualVolatility,
        string Benchmark);

    /// <summary>一只基金的净值序列与派生指标。</summary>
    /// <param name="NavPoints">净值历史点（最近 90 个自然日，按日期升序）。</param>
    /// <param name="NavDate">最新净值日期。</param>
    /// <param name="UnitNav">最新单位净值。</param>
    /// <param name="AccumulatedNav">最新累计净值。</param>
    /// <param name="DailyGrowthRate">最新一日单位净值增长率（百分数）。</param>
    /// <param name="MonthlyReturn">近一月收益率（百分数）。</param>
    /// <param name="YearlyReturn">近一年收益率（百分数）。</param>
    /// <param name="SinceInceptionReturn">成立以来涨幅（百分数）。</param>
    private sealed record FundNavSeries(
        IReadOnlyList<FundNavPoint> NavPoints,
        DateTime NavDate,
        decimal UnitNav,
        decimal AccumulatedNav,
        decimal DailyGrowthRate,
        decimal MonthlyReturn,
        decimal YearlyReturn,
        decimal SinceInceptionReturn);

    // ---------------------------------------------------------------- 工具

    private static DateTimeOffset At(DateTimeOffset value, TimeSpan offset) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, offset);

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

    /// <summary>取基金的成立日期。</summary>
    /// <param name="profile">基金档案。</param>
    /// <returns>成立日期（自然日）。</returns>
    private static DateTime InceptionDate(FundProfile profile) =>
        DateTime.Parse(profile.InceptionDate, CultureInfo.InvariantCulture);

    /// <summary>
    /// 由基金代码派生随机种子。
    /// 必须用自实现的哈希而不是 string.GetHashCode()：后者在 .NET 里是**按进程随机化**的，
    /// 同一只基金在两次启动会得到不同的净值序列，演示数据就不再可复现了。
    /// </summary>
    /// <param name="fundCode">基金代码。</param>
    /// <returns>稳定的随机种子。</returns>
    private static int SeedFromFundCode(string fundCode)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var character in fundCode)
        {
            hash ^= character;
            hash *= prime;
        }

        return (int)(hash & 0x7FFFFFFF);
    }

    /// <summary>生成标准正态分布随机数（Box-Muller 变换）。</summary>
    /// <param name="random">随机数发生器。</param>
    /// <returns>服从标准正态分布的随机数。</returns>
    private static double NextGaussian(Random random)
    {
        var first = 1.0 - random.NextDouble();
        var second = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(first)) * Math.Cos(2.0 * Math.PI * second);
    }

    /// <summary>按年化收益率复利若干年，得到累计净值。</summary>
    /// <param name="annualRate">年化收益率（小数）。</param>
    /// <param name="years">年数。</param>
    /// <returns>累计净值。</returns>
    private static decimal Compound(double annualRate, double years) =>
        (decimal)Math.Pow(1.0 + annualRate, years);

    /// <summary>把净值与收益率统一舍入到 4 位小数。</summary>
    /// <param name="value">待舍入的数值。</param>
    /// <returns>舍入后的数值。</returns>
    private static decimal Round(decimal value) =>
        Math.Round(value, 4, MidpointRounding.AwayFromZero);

    /// <summary>
    /// 取基金在指定年份的年度权益登记日（成立纪念日）。
    /// 2 月 29 日成立的产品在平年没有对应日期，这里退到当月最后一天，避免构造非法日期。
    /// </summary>
    /// <param name="inception">成立日期。</param>
    /// <param name="anchor">参考日期。</param>
    /// <returns>该年度的权益登记日。</returns>
    private static DateTime SafeAnniversary(DateTime inception, DateTime anchor)
    {
        var day = Math.Min(inception.Day, DateTime.DaysInMonth(anchor.Year, inception.Month));
        var anniversary = new DateTime(anchor.Year, inception.Month, day);
        return anniversary > anchor ? anniversary.AddYears(-1) : anniversary;
    }
}
