// ===== 数据库基础设施：多 Provider + 连接治理 =====
// 解决三个生产问题：
// 1. 接入方式 —— 统一支持 SQLite(开发) / PostgreSQL(生产) / SQL Server(信创)
// 2. 稳定性 —— 连接池、健康检查、执行策略(超时+重试)、慢查询监控
// 3. 安全     —— 生产不回传服务端错误细节、证书吊销校验、审计独立存储
//
// 对应 docs/13-database/01-migration-and-ci.md

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Base.Data;

/// <summary>数据库类型。</summary>
public enum DatabaseProvider
{
    /// <summary>SQLite，仅用于本地开发与测试。</summary>
    Sqlite = 0,
    /// <summary>PostgreSQL，生产推荐。</summary>
    PostgreSql = 1,
    /// <summary>SQL Server，金融行业信创场景。</summary>
    SqlServer = 2
}

/// <summary>数据库初始化模式。</summary>
public enum DatabaseInitMode
{
    /// <summary>不自动初始化，由外部（DBA 或部署脚本）负责。</summary>
    None,
    /// <summary>直接建表，仅限本地开发与单元测试。无迁移历史。</summary>
    EnsureCreated,
    /// <summary>应用 EF Core 迁移。生产唯一正确方式。</summary>
    Migrate
}

/// <summary>数据库配置。</summary>
public sealed class DatabaseOptions
{
    /// <summary>数据库类型。</summary>
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;

    /// <summary>连接字符串。生产环境必须从环境变量注入，禁止入库。</summary>
    public string ConnectionString { get; set; } = "Data Source=bankingagent.db";

    /// <summary>初始化模式。</summary>
    public DatabaseInitMode InitMode { get; set; } = DatabaseInitMode.EnsureCreated;

    /// <summary>命令超时（秒）。金融核心交易建议 30-60 秒。</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>是否启用连接池。SQLite 单文件场景建议开启。</summary>
    public bool EnablePooling { get; set; } = true;

    /// <summary>最大连接池大小。需与数据库 max_connections 匹配，否则适得其反。</summary>
    public int MaxPoolSize { get; set; } = 100;

    /// <summary>最小连接池大小（预热）。</summary>
    public int MinPoolSize { get; set; } = 5;

    /// <summary>连接空闲回收时间（秒）。</summary>
    public int ConnectionIdleLifetimeSeconds { get; set; } = 300;

    /// <summary>连接最长生存时间（秒）。需小于数据库侧的空闲超时。</summary>
    public int ConnectionLifetimeSeconds { get; set; } = 1800;

    /// <summary>失败重试次数（仅对瞬时故障有效）。</summary>
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>重试基础延迟（毫秒）。</summary>
    public int RetryBaseDelayMs { get; set; } = 200;

    /// <summary>慢查询阈值（毫秒），超过则记警告日志。</summary>
    public int SlowQueryThresholdMs { get; set; } = 1000;

    /// <summary>是否启用敏感数据日志。生产必须为 false（会打印 PII 与参数值）。</summary>
    public bool EnableSensitiveDataLogging { get; set; }
}

/// <summary>数据库健康状态。</summary>
public sealed record DatabaseHealth
{
    public required bool IsHealthy { get; init; }
    public string Provider { get; set; } = "";
    public long LatencyMs { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CheckedAt { get; init; }
    public IReadOnlyList<string> PendingMigrations { get; init; } = [];
    public int AppliedMigrationCount { get; set; }
    public string? DatabaseName { get; set; }

    /// <summary>数据库服务端版本或探活应答。探活失败时为 null。</summary>
    public string? ServerVersion { get; set; }
}

/// <summary>
/// 数据库配置器。按 Provider 应用连接池、超时、重试与安全策略。
/// 泛型约束让业务库与审计库共用同一套配置。
/// </summary>
public static class DatabaseConfigurator
{
    /// <summary>配置 DbContext（泛型版）。</summary>
    public static void Configure<TContext>(
        DbContextOptionsBuilder<TContext> options,
        DatabaseOptions config)
        where TContext : DbContext
    {
        switch (config.Provider)
        {
            case DatabaseProvider.PostgreSql:
                ApplyPostgres(options, config);
                break;
            case DatabaseProvider.SqlServer:
                ApplySqlServer(options, config);
                break;
            default:
                ApplySqlite(options, config);
                break;
        }

        if (config.EnableSensitiveDataLogging)
        {
            options.EnableSensitiveDataLogging();
        }
    }

    /// <summary>配置 DbContext（非泛型，供迁移工具使用）。</summary>
    public static void Configure(DbContextOptionsBuilder options, DatabaseOptions config)
    {
        switch (config.Provider)
        {
            case DatabaseProvider.PostgreSql:
                ApplyPostgresCore(options, config);
                break;
            case DatabaseProvider.SqlServer:
                ApplySqlServerCore(options, config);
                break;
            default:
                ApplySqliteCore(options, config);
                break;
        }
    }

    private static void ApplyPostgres<TContext>(
        DbContextOptionsBuilder<TContext> options, DatabaseOptions config)
        where TContext : DbContext
    {
        var csb = BuildPostgresConnectionString(config);
        options.UseNpgsql(csb, npgsql =>
        {
            npgsql.CommandTimeout(config.CommandTimeoutSeconds);
            npgsql.MigrationsAssembly(typeof(DatabaseConfigurator).Assembly.GetName().Name);

            if (config.MaxRetryCount > 0)
            {
                npgsql.EnableRetryOnFailure(
                    maxRetryCount: config.MaxRetryCount,
                    maxRetryDelay: TimeSpan.FromMilliseconds(
                        config.RetryBaseDelayMs * config.MaxRetryCount),
                    errorCodesToAdd: null);
            }
        });
    }

    private static void ApplyPostgresCore(
        DbContextOptionsBuilder options, DatabaseOptions config)
    {
        var csb = BuildPostgresConnectionString(config);
        options.UseNpgsql(csb, npgsql =>
        {
            npgsql.CommandTimeout(config.CommandTimeoutSeconds);
            npgsql.MigrationsAssembly(typeof(DatabaseConfigurator).Assembly.GetName().Name);
        });
    }

    private static string BuildPostgresConnectionString(DatabaseOptions config)
    {
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(config.ConnectionString)
        {
            // ===== 连接池治理 =====
            Pooling = config.EnablePooling,
            MaxPoolSize = config.MaxPoolSize,
            MinPoolSize = config.MinPoolSize,
            // 回收闲置连接，避免长期占用服务端资源
            ConnectionIdleLifetime = config.ConnectionIdleLifetimeSeconds,
            // 连接最长生存时间，需小于数据库侧 idle_in_transaction_session_timeout
            ConnectionLifetime = config.ConnectionLifetimeSeconds,
            Timeout = config.CommandTimeoutSeconds,
            CommandTimeout = config.CommandTimeoutSeconds,
            // 网络闪断自动重连
            TcpKeepAlive = true,
            // ===== 安全 =====
            // 生产不回传服务端错误细节（防信息泄漏）
            IncludeErrorDetail = false,
            // 校验证书吊销（金融合规要求）
            CheckCertificateRevocation = true
        };
        return csb.ConnectionString;
    }

    private static void ApplySqlServer<TContext>(
        DbContextOptionsBuilder<TContext> options, DatabaseOptions config)
        where TContext : DbContext
    {
        var csb = BuildSqlServerConnectionString(config);
        options.UseSqlServer(csb, sql =>
        {
            sql.CommandTimeout(config.CommandTimeoutSeconds);
            sql.MigrationsAssembly(typeof(DatabaseConfigurator).Assembly.GetName().Name);
            sql.EnableRetryOnFailure(
                maxRetryCount: Math.Max(1, config.MaxRetryCount),
                maxRetryDelay: TimeSpan.FromSeconds(5),
                errorNumbersToAdd: null);
        });
    }

    private static void ApplySqlServerCore(
        DbContextOptionsBuilder options, DatabaseOptions config)
    {
        options.UseSqlServer(BuildSqlServerConnectionString(config), sql =>
        {
            sql.CommandTimeout(config.CommandTimeoutSeconds);
            sql.MigrationsAssembly(typeof(DatabaseConfigurator).Assembly.GetName().Name);
        });
    }

    private static string BuildSqlServerConnectionString(DatabaseOptions config)
    {
        var csb = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(config.ConnectionString)
        {
            MaxPoolSize = config.MaxPoolSize,
            MinPoolSize = config.MinPoolSize,
            ConnectTimeout = config.CommandTimeoutSeconds,
            ConnectRetryCount = Math.Max(1, config.MaxRetryCount),
            ConnectRetryInterval = Math.Max(1, config.RetryBaseDelayMs / 1000),
            MultipleActiveResultSets = true,
            // 生产必须校验服务端证书
            TrustServerCertificate = false
        };
        return csb.ConnectionString;
    }

    private static void ApplySqlite<TContext>(
        DbContextOptionsBuilder<TContext> options, DatabaseOptions config)
        where TContext : DbContext
    {
        options.UseSqlite(BuildSqliteConnectionString(config), sqlite =>
        {
            sqlite.CommandTimeout(config.CommandTimeoutSeconds);
            sqlite.MigrationsAssembly(typeof(DatabaseConfigurator).Assembly.GetName().Name);
        });
    }

    private static void ApplySqliteCore(
        DbContextOptionsBuilder options, DatabaseOptions config)
    {
        options.UseSqlite(BuildSqliteConnectionString(config), sqlite =>
        {
            sqlite.CommandTimeout(config.CommandTimeoutSeconds);
            sqlite.MigrationsAssembly(typeof(DatabaseConfigurator).Assembly.GetName().Name);
        });
    }

    private static string BuildSqliteConnectionString(DatabaseOptions config)
    {
        // SQLite 连接串仅支持 DataSource / Pooling / DefaultTimeout
        var csb = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(config.ConnectionString)
        {
            Pooling = config.EnablePooling,
            DefaultTimeout = config.CommandTimeoutSeconds
        };
        return csb.ConnectionString;
    }
}

/// <summary>慢查询监控拦截器。只记录命令类型与耗时，绝不记录参数值（含 PII）。</summary>
public sealed class DatabaseDiagnosticsInterceptor(
    DatabaseOptions config,
    ILogger<DatabaseDiagnosticsInterceptor> logger) : DbCommandInterceptor
{
    private long _totalCommands;
    private long _slowCommands;
    private long _totalMs;

    /// <summary>命令总数。</summary>
    public long TotalCommands => Interlocked.Read(ref _totalCommands);

    /// <summary>慢查询数（超过阈值）。</summary>
    public long SlowCommands => Interlocked.Read(ref _slowCommands);

    /// <summary>平均耗时（毫秒）。</summary>
    public long AverageMs
    {
        get
        {
            var total = Interlocked.Read(ref _totalCommands);
            return total == 0 ? 0 : Interlocked.Read(ref _totalMs) / total;
        }
    }

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Mark(command);
        return result;
    }

    /// <inheritdoc />
    public override DbDataReader ReaderExecuted(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Finish(command, "Query");
        return result;
    }

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Mark(command);
        return result;
    }

    /// <inheritdoc />
    public override int NonQueryExecuted(
        DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Finish(command, "Command");
        return result;
    }

    private static void Mark(DbCommand command) =>
        command.CommandText = "/*bk_t=" + DateTimeOffset.UtcNow.Ticks + "*/" + command.CommandText;

    private void Finish(DbCommand command, string kind)
    {
        Interlocked.Increment(ref _totalCommands);

        if (!command.CommandText.StartsWith("/*bk_t=", StringComparison.Ordinal)) return;

        var end = command.CommandText.IndexOf("*/", StringComparison.Ordinal);
        if (end < 7 || !long.TryParse(command.CommandText[6..end], out var startTicks)) return;

        var elapsedMs = (DateTimeOffset.UtcNow.Ticks - startTicks) / TimeSpan.TicksPerMillisecond;
        Interlocked.Add(ref _totalMs, elapsedMs);

        if (elapsedMs > config.SlowQueryThresholdMs)
        {
            Interlocked.Increment(ref _slowCommands);
            logger.LogWarning(
                "慢查询 {Kind} 耗时 {Elapsed}ms（阈值 {Threshold}ms）: {Sql}",
                kind, elapsedMs, config.SlowQueryThresholdMs,
                Truncate(command.CommandText, 200));
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";
}
