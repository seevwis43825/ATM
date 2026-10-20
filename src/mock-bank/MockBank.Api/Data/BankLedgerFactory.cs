// ===== 银行账本工厂：按配置创建各行核心系统实例并完成建库与种子写入 =====

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MockBank.Api.Domain;

namespace MockBank.Api.Data;

/// <summary>
/// 银行账本工厂。负责三件事：
///   1. 为每个启用的银行创建独立的数据库文件与账本实例；
///   2. 首次运行时把该行的种子数据写入它自己的库；
///   3. 缓存账本实例，保证同一进程内始终是同一批对象（写锁才能真正互斥）。
/// </summary>
public sealed class BankLedgerFactory
{
    private readonly MockBankOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _dataDirectory;
    private readonly Dictionary<string, BankLedger> _ledgers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    /// <summary>构造工厂。</summary>
    /// <param name="options">模拟银行配置。</param>
    /// <param name="loggerFactory">日志工厂。</param>
    /// <param name="dataDirectory">各行数据库文件的绝对目录。</param>
    public BankLedgerFactory(
        IOptions<MockBankOptions> options,
        ILoggerFactory loggerFactory,
        string dataDirectory)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _dataDirectory = dataDirectory;
    }

    /// <summary>取指定银行的账本实例，不存在时按需创建。</summary>
    /// <param name="bank">银行描述符。</param>
    /// <returns>该行的账本实例。</returns>
    public BankLedger Get(BankDescriptor bank)
    {
        lock (_sync)
        {
            if (_ledgers.TryGetValue(bank.BankCode, out var existing))
            {
                return existing;
            }

            var ledger = Create(bank);
            _ledgers[bank.BankCode] = ledger;
            return ledger;
        }
    }

    /// <summary>按配置创建全部启用银行的账本实例。</summary>
    /// <returns>账本实例列表，顺序与配置一致。</returns>
    public IReadOnlyList<BankLedger> CreateAll()
    {
        var enabled = EnabledBanks();
        return enabled.Select(Get).ToList();
    }

    /// <summary>按配置重建全部银行数据库（清空后重新写入种子数据）。</summary>
    public void ResetAll()
    {
        lock (_sync)
        {
            foreach (var bank in EnabledBanks())
            {
                var dbPath = DatabasePath(bank);
                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                }

                // 重新建库并写种子
                var ledger = new BankLedger(bank, ConnectionString(bank), _loggerFactory.CreateLogger<BankLedger>());
                ledger.EnsureCreated();
                ConfigureConcurrencyMode(ledger);
                var anchor = DateTimeOffset.Now;
                ledger.Seed(BankSeedFactory.Create(bank, anchor), anchor);
                ledger.InitializeTransactionSequence();
                _ledgers[bank.BankCode] = ledger;
            }
        }
    }

    /// <summary>取某个银行的数据库文件绝对路径。</summary>
    /// <param name="bank">银行描述符。</param>
    /// <returns>数据库文件路径。</returns>
    public string DatabasePath(BankDescriptor bank) =>
        Path.Combine(_dataDirectory, bank.DatabaseFileName);

    /// <summary>
    /// 把该行数据库切换到 WAL 日志模式。
    ///
    /// 为什么必须设：SQLite 默认用 rollback journal，一次写事务会**锁住整个库**，
    /// 另一个进程（或另一条连接）此时写入会立刻收到 "database is locked"。
    /// 演示环境里「宿主调 Mock Bank」与「E2E 脚本直连 Mock Bank」并发访问同一个库文件
    /// 是常态，切到 WAL 后读写不再互斥，偶发并发写退化为排队而不是 500。
    /// </summary>
    /// <param name="ledger">该行账本。</param>
    private void ConfigureConcurrencyMode(BankLedger ledger)
    {
        try
        {
            using var db = ledger.CreateContext();
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        }
        catch (Exception ex)
        {
            // 切换失败不影响功能：只是并发写更容易撞锁，退化成等待与报错而已。
            _loggerFactory.CreateLogger<BankLedgerFactory>()
                .LogWarning(ex, "银行 {BankCode} 切换 WAL 模式失败", ledger.Bank.BankCode);
        }
    }

    /// <summary>当前启用的银行列表。</summary>
    /// <returns>银行描述符列表。</returns>
    private IReadOnlyList<BankDescriptor> EnabledBanks()
    {
        var wanted = _options.Banks
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return BankCatalog.All
            .Where(b => wanted.Count == 0 || wanted.Contains(b.BankCode))
            .ToList();
    }

    /// <summary>创建单个银行的账本，并完成建库与首次种子写入。</summary>
    /// <param name="bank">银行描述符。</param>
    /// <returns>账本实例。</returns>
    private BankLedger Create(BankDescriptor bank)
    {
        Directory.CreateDirectory(_dataDirectory);

        var dbPath = DatabasePath(bank);
        if (_options.ResetOnStartup && File.Exists(dbPath))
        {
            DeleteDatabaseFiles(dbPath);
        }

        var ledger = new BankLedger(
            bank, ConnectionString(bank), _loggerFactory.CreateLogger<BankLedger>());

        // ===== 结构漂移自愈 =====
        // EnsureCreated 对已存在的库不做任何演进，模型加表后旧库会一直缺表，
        // 直到某次查询新表才抛 "no such table"（错误指向业务查询，排查方向被带偏）。
        // 模拟核心系统的数据全部来自种子、可确定性重建，因此这里选择：
        // 检测到缺表 → 重建该行库 → 重新写种子，并显式告警。
        // 不重建的后果是整个服务起不来（启动期未处理异常直接崩）。
        if (File.Exists(dbPath) && !ledger.IsSchemaCompatible(out var missingTables))
        {
            _loggerFactory.CreateLogger<BankLedgerFactory>().LogWarning(
                "银行 {BankCode} 数据库结构与当前模型不一致（缺少表：{Missing}），正在重建该行数据库。",
                bank.BankCode, string.Join(", ", missingTables));

            DeleteDatabaseFiles(dbPath);
            ledger = new BankLedger(
                bank, ConnectionString(bank), _loggerFactory.CreateLogger<BankLedger>());
        }

        ledger.EnsureCreated();
        ConfigureConcurrencyMode(ledger);

        // 首次运行：该行库是空的，写入本行种子数据。
        var counts = ledger.CountAll();
        if (counts.Customers == 0 && counts.Accounts == 0)
        {
            var anchor = DateTimeOffset.Now;
            ledger.Seed(BankSeedFactory.Create(bank, anchor), anchor);
        }
        else
        {
            _loggerFactory.CreateLogger<BankLedger>().LogInformation(
                "银行 {BankCode} 已存在历史数据，保留不重建：客户 {Customers} / 账户 {Accounts} / 卡 {Cards} / 流水 {Transactions} / 理财 {Products}",
                bank.BankCode, counts.Customers, counts.Accounts, counts.Cards, counts.Transactions, counts.Products);
        }

        // 流水号计数器必须续接到库中已有流水之后，
        // 否则重启后新生成的流水号会与种子数据里的重号，表现为转账接口 500。
        ledger.InitializeTransactionSequence();

        // 载入内存快照：读路径走内存（跨行聚合每次要问 3 家银行，
        // 走数据库会让读慢约 5 倍），SQLite 仍是持久化的真相来源。
        ledger.LoadSnapshot();

        return ledger;
    }

    /// <summary>
    /// 删除数据库文件及其 WAL/SHM 附属文件。
    /// 只删 -wal/-shm 不够：残留的 WAL 会在新库打开时被回放，
    /// 把旧结构的数据"复活"回来，制造出比缺表更难查的错乱。
    /// </summary>
    private static void DeleteDatabaseFiles(string dbPath)
    {
        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// 生成本行的连接串。
    ///
    /// <c>Default Timeout=30</c> 让 SQLite 在遇到写锁时最多等 30 秒再报错，
    /// 避免并发访问同一个库文件时立刻失败。
    /// </summary>
    /// <param name="bank">银行描述符。</param>
    /// <returns>连接字符串。</returns>
    private string ConnectionString(BankDescriptor bank) =>
        $"Data Source={DatabasePath(bank)};Default Timeout=30";
}
