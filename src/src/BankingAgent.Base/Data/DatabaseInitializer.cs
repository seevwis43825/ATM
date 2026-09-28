// ===== 数据库初始化与健康检查 =====
// 生产环境唯一的正确路径是 DatabaseInitMode.Migrate。
// EnsureCreated 仅限本地开发（无迁移历史，表结构演进会失控）。
//
// 对应 docs/13-database/01-migration-and-ci.md

using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Base.Data;

/// <summary>数据库初始化器。负责建表/迁移与健康探测。</summary>
public sealed class DatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    DatabaseOptions options,
    ILogger<DatabaseInitializer> logger)
{
    private int _initialized;

    /// <summary>执行初始化。幂等，可重复调用。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            logger.LogDebug("数据库已初始化，跳过");
            return;
        }

        if (options.InitMode == DatabaseInitMode.None)
        {
            logger.LogInformation("数据库初始化模式为 None，由外部流程负责");
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BankingDbContext>();
        var sw = Stopwatch.StartNew();

        if (options.InitMode == DatabaseInitMode.EnsureCreated)
        {
            var created = await db.Database.EnsureCreatedAsync(ct);
            logger.LogWarning(
                "数据库已用 EnsureCreated {Action}（无迁移历史，仅限开发环境）。生产请改用 Migrate 模式。",
                created ? "创建" : "校验");

            // 审计库是**独立的 DbContext**（物理隔离），EnsureCreated 只作用于
            // 被调用的那个上下文。若不一并初始化，audit_events 表根本不存在，
            // 审计落库会持续失败 —— 而且失败被 catch 吞掉，只留一条 Critical 日志，
            // 表面上看系统一切正常。这类"静默失效"必须在这里堵死。
            await EnsureAuditStoreAsync(scope.ServiceProvider, ct);
            return;
        }

        // ===== Migrate 模式（生产）=====
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0)
        {
            logger.LogInformation(
                "检测到 {Count} 个待应用迁移: {Migrations}",
                pending.Count, string.Join(", ", pending));
        }

        // 迁移文件里的 CreateTable 直接指定了 schema（如 plugin_transfer），
        // 但 EF Core 只负责建表，不会创建 Schema 本身。
        // 因此必须先建好全部插件分区 Schema，否则迁移在 PostgreSQL 上会直接失败。
        await EnsureSchemasAsync(db, ct);

        await db.Database.MigrateAsync(ct);
        sw.Stop();

        // 审计库同样需要初始化（独立上下文，迁移不会自动覆盖它）
        await EnsureAuditStoreAsync(scope.ServiceProvider, ct);

        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
        logger.LogInformation(
            "数据库迁移完成，耗时 {Elapsed}ms。已应用 {Count} 个迁移，当前 Provider={Provider}",
            sw.ElapsedMilliseconds, applied.Count, options.Provider);
    }

    /// <summary>
    /// 确保审计库（独立 DbContext）已建表。
    ///
    /// 为什么单独处理：审计库与业务库是两个 DbContext，
    /// EnsureCreated / Migrate 都只作用于被调用的那一个。
    /// 漏掉审计库的后果是 audit_events 表不存在，
    /// 而审计写入失败会被 catch 吞成一条日志 —— 系统表面完全正常，
    /// 实际合规证据缺失。必须在启动阶段就显式建好。
    /// </summary>
    private async Task EnsureAuditStoreAsync(IServiceProvider scopedProvider, CancellationToken ct)
    {
        try
        {
            var auditFactory = scopedProvider.GetService<IDbContextFactory<AuditDbContext>>();
            if (auditFactory is null)
            {
                logger.LogWarning("未注册 AuditDbContext 工厂，跳过审计库初始化");
                return;
            }

            await using var auditDb = await auditFactory.CreateDbContextAsync(ct);

            // PostgreSQL 下审计表在独立 schema 中，必须先把 schema 建出来
            if (auditDb.Database.IsNpgsql())
            {
                await auditDb.Database.ExecuteSqlRawAsync(
                    "CREATE SCHEMA IF NOT EXISTS \"audit\";", ct);
            }

            var created = await auditDb.Database.EnsureCreatedAsync(ct);
            logger.LogInformation("审计库初始化完成（{Action}）", created ? "已建表" : "已存在");
        }
        catch (Exception ex)
        {
            // 审计库建不出来属于合规风险，必须显式告警，不能静默
            logger.LogError(ex, "审计库初始化失败，审计记录将无法落库");
        }
    }

    /// <summary>
    /// 创建全部插件数据分区 Schema（仅 PostgreSQL/SQL Server 需要）。
    /// SQLite 不支持 Schema，直接跳过。
    /// </summary>
    private async Task EnsureSchemasAsync(BankingDbContext db, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return;

        // 审计库使用独立 schema，与业务库物理隔离
        var schemas = db.Partitions.Append("audit").Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var schema in schemas)
        {
            if (!IsValidIdentifier(schema))
            {
                // 分区名来自插件清单，理论上可信，但拼接进 DDL 前仍必须校验，
                // 否则一个恶意插件就能注入任意 SQL。
                throw new InvalidOperationException(
                    $"非法的 Schema 名「{schema}」：只允许字母、数字与下划线，且必须以字母或下划线开头。");
            }

            // 标识符（Schema 名）无法参数化，只能拼接字符串，
            // 因此上面的 IsValidIdentifier 白名单校验是唯一防线。
            // EF1002 无法识别这种"先校验后拼接"的模式，这里显式抑制并说明理由。
#pragma warning disable EF1002 // 已通过 IsValidIdentifier 白名单校验，不存在注入面
            await db.Database.ExecuteSqlRawAsync(
                $"CREATE SCHEMA IF NOT EXISTS \"{schema}\";", ct);
#pragma warning restore EF1002
        }

        logger.LogInformation("已确保 Schema 存在: {Schemas}", string.Join(", ", schemas));
    }

    /// <summary>校验 SQL 标识符合法性（防注入）。</summary>
    private static bool IsValidIdentifier(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.Length <= 63 &&
        (char.IsLetter(name[0]) || name[0] == '_') &&
        name.All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>健康检查。供 /health 与 K8s 探针使用。</summary>
    public async Task<DatabaseHealth> CheckHealthAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BankingDbContext>();

            // 真实探活：执行 SELECT 1，不只看连接池状态
            var canConnect = await db.Database.CanConnectAsync(ct);

            if (!canConnect)
            {
                return new DatabaseHealth
                {
                    IsHealthy = false,
                    Provider = options.Provider.ToString(),
                    Error = "无法建立数据库连接",
                    CheckedAt = DateTimeOffset.UtcNow
                };
            }

            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            var appliedCount = (await db.Database.GetAppliedMigrationsAsync(ct)).Count();

            // EnsureCreated 模式下 schema 由模型直接建出，不写 __EFMigrationsHistory，
            // 因此 GetPendingMigrations 会把基线迁移一直报成"待应用"。
            // 那是假信号：环境其实是就绪的，只是走的不是迁移路径。
            // 只在 Migrate 模式下把待应用迁移视为真实运维信号。
            var usesMigrations = options.InitMode == DatabaseInitMode.Migrate;

            // 真实探活：SELECT 1 只能证明连接可用，取服务端版本用于运维定位
            var serverVersion = db.Database.ProviderName ?? options.Provider.ToString();
            await db.Database.ExecuteSqlRawAsync("SELECT 1", ct);

            sw.Stop();

            return new DatabaseHealth
            {
                IsHealthy = true,
                Provider = options.Provider.ToString(),
                LatencyMs = sw.ElapsedMilliseconds,
                PendingMigrations = usesMigrations ? pending : [],
                AppliedMigrationCount = usesMigrations ? appliedCount : 0,
                DatabaseName = db.Database.GetDbConnection().Database,
                ServerVersion = serverVersion,
                CheckedAt = DateTimeOffset.UtcNow
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex, "数据库健康检查失败");
            return new DatabaseHealth
            {
                IsHealthy = false,
                Provider = options.Provider.ToString(),
                LatencyMs = sw.ElapsedMilliseconds,
                Error = ex.Message,
                CheckedAt = DateTimeOffset.UtcNow
            };
        }
    }

    /// <summary>获取待应用迁移列表（部署前检查用）。</summary>
    public async Task<IReadOnlyList<string>> GetPendingMigrationsAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BankingDbContext>();
        return (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
    }
}
