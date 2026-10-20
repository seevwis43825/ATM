// ===== 银行核心系统数据库上下文 =====
//
// 关键设计：**一家银行 = 一个 DbContext = 一个独立的 SQLite 文件**。
//   - 数据库文件落在 MockBank.Api/App_Data/ 目录下，每家银行一个；
//   - 各行的客户、账户、卡、流水、理财互不可见，物理上就是互相独立的库；
//   - 跨行查询由上层的 MockBankStore 聚合若干个 DbContext 完成。
//
// 为什么用 EF Core 直接映射领域 record：
//   领域模型是 `sealed record` + `required init` + 只读计算属性，
//   EF Core 8 能通过主构造函数完成物化，且只读计算属性会被自动忽略，
//   因此不需要维护「实体类 + 映射层」两套模型 —— 一套模型即可，显著降低可读性成本。
//
// 关于 DateTimeOffset：
//   SQLite 没有原生日期类型，EF 默认按带时区偏移的文本存储，
//   跨偏移量的字符串比较会得到错误的时间区间结果（账单按月统计会算错）。
//   因此统一经转换器转成 UTC 的 DateTime 存储，保证排序与区间过滤正确。

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MockBank.Api.Domain;

namespace MockBank.Api.Data;

/// <summary>单家银行的数据库上下文。每个实例只连接一家银行的一个数据库文件。</summary>
public sealed class BankDbContext : DbContext
{
    private readonly string _connectionString;

    /// <summary>构造上下文。</summary>
    /// <param name="bank">本上下文所属的银行描述符。</param>
    /// <param name="connectionString">该银行数据库文件的连接字符串。</param>
    public BankDbContext(BankDescriptor bank, string connectionString)
    {
        Bank = bank;
        _connectionString = connectionString;
    }

    /// <summary>本上下文所属的银行。</summary>
    public BankDescriptor Bank { get; }

    /// <summary>本行客户主档。</summary>
    public DbSet<Customer> Customers => Set<Customer>();

    /// <summary>本行账户主档。</summary>
    public DbSet<Account> Accounts => Set<Account>();

    /// <summary>本行银行卡主档。</summary>
    public DbSet<BankCard> Cards => Set<BankCard>();

    /// <summary>本行交易流水。</summary>
    public DbSet<BankTransaction> Transactions => Set<BankTransaction>();

    /// <summary>本行发行的理财产品。</summary>
    public DbSet<WealthProduct> Products => Set<WealthProduct>();

    /// <summary>本行代销的公募基金主档。</summary>
    public DbSet<PublicFund> PublicFunds => Set<PublicFund>();

    /// <summary>本行代销基金的净值历史点。</summary>
    public DbSet<FundNavPoint> FundNavPoints => Set<FundNavPoint>();

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        options.UseSqlite(_connectionString);

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var customer = modelBuilder.Entity<Customer>();
        customer.ToTable("customers");
        customer.HasKey(c => c.UserId);
        customer.Property(c => c.UserId).HasMaxLength(32);
        customer.Property(c => c.BankCode).HasMaxLength(8);
        customer.Property(c => c.Name).HasMaxLength(64);
        customer.Property(c => c.IdCardNo).HasMaxLength(32);
        customer.Property(c => c.Phone).HasMaxLength(32);
        customer.Property(c => c.CustomerLevel).HasMaxLength(32);
        customer.Property(c => c.BranchName).HasMaxLength(128);
        customer.Property(c => c.RiskLevel).HasConversion<string>();

        var account = modelBuilder.Entity<Account>();
        account.ToTable("accounts");
        account.HasKey(a => a.AccountNo);
        account.Property(a => a.AccountNo).HasMaxLength(32);
        account.Property(a => a.BankCode).HasMaxLength(8);
        account.Property(a => a.UserId).HasMaxLength(32);
        account.Property(a => a.ProductName).HasMaxLength(64);
        account.Property(a => a.Currency).HasMaxLength(8);
        account.Property(a => a.BranchName).HasMaxLength(128);
        account.Property(a => a.AccountType).HasConversion<string>();
        account.Property(a => a.Status).HasConversion<string>();
        account.HasIndex(a => a.UserId);

        var card = modelBuilder.Entity<BankCard>();
        card.ToTable("cards");
        card.HasKey(c => c.CardNo);
        card.Property(c => c.CardNo).HasMaxLength(32);
        card.Property(c => c.BankCode).HasMaxLength(8);
        card.Property(c => c.AccountNo).HasMaxLength(32);
        card.Property(c => c.UserId).HasMaxLength(32);
        card.Property(c => c.BoundPhone).HasMaxLength(32);
        card.Property(c => c.CardOrg).HasMaxLength(64);
        card.Property(c => c.StatusReason).HasMaxLength(256);
        card.Property(c => c.CardType).HasConversion<string>();
        card.Property(c => c.Status).HasConversion<string>();
        card.HasIndex(c => c.UserId);

        var transaction = modelBuilder.Entity<BankTransaction>();
        transaction.ToTable("transactions");
        transaction.HasKey(t => t.TxNo);
        transaction.Property(t => t.TxNo).HasMaxLength(32);
        transaction.Property(t => t.BankCode).HasMaxLength(8);
        transaction.Property(t => t.UserId).HasMaxLength(32);
        transaction.Property(t => t.AccountNo).HasMaxLength(32);
        transaction.Property(t => t.Category).HasMaxLength(64);
        transaction.Property(t => t.Merchant).HasMaxLength(128);
        transaction.Property(t => t.CounterpartyName).HasMaxLength(128);
        transaction.Property(t => t.CounterpartyAccountNo).HasMaxLength(32);
        transaction.Property(t => t.Remark).HasMaxLength(256);
        transaction.Property(t => t.Currency).HasMaxLength(8);
        transaction.Property(t => t.Direction).HasConversion<string>();
        transaction.Property(t => t.Channel).HasConversion<string>();
        transaction.HasIndex(t => t.AccountNo);
        transaction.HasIndex(t => t.Timestamp);

        var product = modelBuilder.Entity<WealthProduct>();
        product.ToTable("products");
        product.HasKey(p => p.Code);
        product.Property(p => p.Code).HasMaxLength(32);
        product.Property(p => p.BankCode).HasMaxLength(8);
        product.Property(p => p.Name).HasMaxLength(128);
        product.Property(p => p.Type).HasConversion<string>();
        product.Property(p => p.RiskLevel).HasConversion<string>();
        product.Property(p => p.Status).HasConversion<string>();
        product.Property(p => p.RiskNote).HasMaxLength(512);

        // 公募基金是代销产品：主档按基金代码建主键（同一行内一只基金一条），
        // 净值点用「基金代码 + 净值日期」复合主键，保证同一天的净值不会被重复写入。
        var fund = modelBuilder.Entity<PublicFund>();
        fund.ToTable("public_funds");
        fund.HasKey(f => f.FundCode);
        fund.Property(f => f.FundCode).HasMaxLength(16);
        fund.Property(f => f.BankCode).HasMaxLength(8);
        fund.Property(f => f.FundName).HasMaxLength(64);
        fund.Property(f => f.FundFullName).HasMaxLength(128);
        fund.Property(f => f.FundCompany).HasMaxLength(64);
        fund.Property(f => f.FundManager).HasMaxLength(64);
        fund.Property(f => f.InvestmentScope).HasMaxLength(512);
        fund.Property(f => f.PerformanceBenchmark).HasMaxLength(256);
        fund.Property(f => f.RiskNote).HasMaxLength(256);
        fund.Property(f => f.Category).HasConversion<string>();
        fund.Property(f => f.RiskLevel).HasConversion<string>();
        fund.Property(f => f.SubscriptionStatus).HasConversion<string>();
        fund.HasIndex(f => f.Category);
        fund.HasIndex(f => f.RiskLevel);
        fund.HasIndex(f => f.FundCompany);

        var navPoint = modelBuilder.Entity<FundNavPoint>();
        navPoint.ToTable("fund_nav_points");
        navPoint.HasKey(n => new { n.FundCode, n.NavDate });
        navPoint.Property(n => n.FundCode).HasMaxLength(16);
        navPoint.HasIndex(n => n.FundCode);

        // 全局约定：所有 DateTimeOffset 一律转 UTC DateTime 存储，
        // 保证 SQLite 中的时间区间比较（账单按月、流水按日）结果正确。
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(new DateTimeOffsetToUtcConverter());
                }
                else if (property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(new NullableDateTimeOffsetToUtcConverter());
                }
            }
        }
    }

    /// <summary>把 <see cref="DateTimeOffset"/> 转成 UTC 的 <see cref="DateTime"/> 存储。</summary>
    private sealed class DateTimeOffsetToUtcConverter() : ValueConverter<DateTimeOffset, DateTime>(
        v => v.UtcDateTime,
        v => new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)));

    /// <summary>可空版本的时间转换器。</summary>
    private sealed class NullableDateTimeOffsetToUtcConverter() : ValueConverter<DateTimeOffset?, DateTime?>(
        v => v.HasValue ? v.Value.UtcDateTime : null,
        v => v.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(v.Value, DateTimeKind.Utc)) : null);
}
