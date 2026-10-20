// ===== 银行账本工厂：按配置创建各行核心系统实例并完成建库与种子写入 =====

using Microsoft.Data.Sqlite;
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

    /// <summary>重置时等待各行写锁的上限；超时即放弃，避免管理端点无限期挂起。</summary>
    private static readonly TimeSpan ResetLockTimeout = TimeSpan.FromSeconds(30);

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

    /// <summary>
    /// 按配置重建全部银行数据库（清空后重新写入种子数据）。
    ///
    /// ===== 为什么必须「原地重置」而不是换一个新账本实例 =====
    /// 账本实例注册为**容器单例**，而 <c>MockBankStore</c> 在构造时就把账本列表
    /// 固化下来了（<c>_ledgers = ledgers.ToList()</c>）。若这里换新实例，
    /// 存储层仍持有旧实例的**内存快照**，读接口会继续返回重置前的数据 ——
    /// 表现为「接口返回 200，但数据根本没重置」，比直接报错更难查。
    /// </summary>
    public void ResetAll()
    {
        lock (_sync)
        {
            // 加锁顺序必须与 MockBankStore.AcquireLedgerLocksAsync 一致（行代码字典序），
            // 否则会与跨行转账形成「A 持 A 锁等 B 锁、B 持 B 锁等 A 锁」的死锁。
            var ledgers = EnabledBanks()
                .OrderBy(b => b.BankCode, StringComparer.Ordinal)
                .Select(Get)
                .ToList();

            var acquired = new List<BankLedger>();
            try
            {
                foreach (var ledger in ledgers)
                {
                    if (!ledger.Gate.Wait(ResetLockTimeout))
                    {
                        throw new TimeoutException(
                            $"重置超时：银行 {ledger.Bank.BankCode} 的写锁在 {ResetLockTimeout.TotalSeconds:F0} 秒内未被释放。");
                    }

                    acquired.Add(ledger);
                }

                foreach (var ledger in acquired)
                {
                    ResetLedger(ledger);
                }
            }
            finally
            {
                // 取锁失败或重置抛异常时必须把已拿到的锁还回去，否则写路径会被永久阻塞。
                for (var i = acquired.Count - 1; i >= 0; i--)
                {
                    acquired[i].Gate.Release();
                }
            }
        }
    }

    /// <summary>
    /// 把单个银行的库重建为初始种子状态（原地重置，实例不变、只换数据）。
    /// </summary>
    /// <param name="ledger">该行账本。</param>
    private void ResetLedger(BankLedger ledger)
    {
        var bank = ledger.Bank;
        var dbPath = DatabasePath(bank);
        var connectionString = ConnectionString(bank);

        // ===== 必须先释放连接池，否则文件删不掉 =====
        // Microsoft.Data.Sqlite 6.0 起默认开启连接池：`using` 释放 DbContext
        // 只是把连接**还回池里**，文件句柄仍然开着。此时 File.Delete 会抛
        // IOException（"being used by another process" —— 其实是同一个进程），
        // 表现为 POST /_admin/reset 直接 500。
        ReleaseDatabaseHandles(connectionString, bank.BankCode);

        // 连 -wal/-shm 一起删：只删 .db 会让残留 WAL 在新库打开时被回放，
        // 把重置前的数据"复活"回来（DeleteDatabaseFiles 的注释已警告过这一点）。
        DeleteDatabaseFiles(dbPath);

        // 先清空内存快照：LoadSnapshot 只做覆盖、不做删除，
        // 不先清空的话重置前的账户/卡片会残留在快照里继续被读出去。
        ledger.ClearSnapshot();

        // 重新建库并写种子
        ledger.EnsureCreated();
        ConfigureConcurrencyMode(ledger);
        var anchor = DateTimeOffset.Now;
        ledger.Seed(BankSeedFactory.Create(bank, anchor), anchor);
        ledger.InitializeTransactionSequence();
        ledger.LoadSnapshot();
    }

    /// <summary>
    /// 释放指定连接串上的 SQLite 连接池，让数据库文件真正可以被删除。
    ///
    /// 为什么需要：Microsoft.Data.Sqlite 6.0 起**默认开启连接池**，
    /// <c>using</c> 释放 <c>DbContext</c> 只是把连接还回池里，操作系统层面的
    /// 文件句柄仍然开着。此时 <c>File.Delete</c> 在 Windows 上会抛
    /// <c>IOException: ... being used by another process</c>（其实是同一个进程）。
    /// <c>ClearPool</c> 是官方释放句柄的入口：它关闭池中的空闲连接，
    /// 并让之后归还的连接直接关闭而不是回到池里。
    /// </summary>
    /// <param name="connectionString">数据库连接串。</param>
    /// <param name="bankCode">行代码，仅用于日志。</param>
    private void ReleaseDatabaseHandles(string connectionString, string bankCode)
    {
        try
        {
            using var probe = new SqliteConnection(connectionString);
            SqliteConnection.ClearPool(probe);
        }
        catch (Exception ex)
        {
            // 释放失败不阻断流程：随后的删除会抛出真实异常，信息更准确。
            _loggerFactory.CreateLogger<BankLedgerFactory>()
                .LogWarning(ex, "银行 {BankCode} 释放数据库连接池失败", bankCode);
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
        var connectionString = ConnectionString(bank);
        if (_options.ResetOnStartup && File.Exists(dbPath))
        {
            ReleaseDatabaseHandles(connectionString, bank.BankCode);
            DeleteDatabaseFiles(dbPath);
        }

        var ledger = new BankLedger(
            bank, connectionString, _loggerFactory.CreateLogger<BankLedger>());

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

            // IsSchemaCompatible 刚刚打开过该库的连接（连接池默认开启，连接已还回池里，
            // 文件句柄仍开着）。不先释放池就直接删文件会抛 IOException，
            // 而这段自愈逻辑的立意正是「别让服务起不来」—— 删失败反而成了启动即崩。
            ReleaseDatabaseHandles(connectionString, bank.BankCode);

            DeleteDatabaseFiles(dbPath);
            ledger = new BankLedger(
                bank, connectionString, _loggerFactory.CreateLogger<BankLedger>());
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
