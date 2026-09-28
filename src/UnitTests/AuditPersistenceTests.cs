// ===== 审计落库集成测试 =====
//
// 目的：确认「审计写入 -> 数据库可检索」这条链路真的通。
//
// 为什么必须用集成测试而不是单元测试：
//   审计涉及 审计库 DbContext -> EnsureCreated 建表 -> 仓储写入 三段，
//   任何一段没接上，表现都是"审计文件有内容、数据库没有"——
//   而写入失败会被 catch 成一条日志，系统表面完全正常。
//   这类静默失效只能靠"真建库、真写、真查"来发现。

using BankingAgent.Base.Data;
using BankingAgent.Base.Security.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace UnitTests;

public class AuditPersistenceTests
{
    private static (ServiceProvider Provider, string DbPath) BuildSqliteProvider()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"audit-it-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        // 用与生产相同的配置器，确保连接串处理一致
        services.AddDbContextFactory<AuditDbContext>((sp, options) =>
            DatabaseConfigurator.Configure(options, new DatabaseOptions
            {
                Provider = DatabaseProvider.Sqlite,
                ConnectionString = $"Data Source={dbPath}"
            }));

        return (services.BuildServiceProvider(), dbPath);
    }

    [Fact]
    public async Task AuditDbContext_EnsureCreated_CreatesAuditEventsTable()
    {
        var (provider, dbPath) = BuildSqliteProvider();
        try
        {
            var factory = provider.GetRequiredService<IDbContextFactory<AuditDbContext>>();
            await using var db = await factory.CreateDbContextAsync();

            // 这一步就是 DatabaseInitializer.EnsureAuditStoreAsync 做的事
            var created = await db.Database.EnsureCreatedAsync();
            Assert.True(created, "首次应创建审计库");

            // 表必须真的存在，且能写入
            db.AuditEvents.Add(new AuditEventEntity
            {
                Id = Guid.NewGuid(),
                AuditId = "AUD0000000001",
                Timestamp = DateTimeOffset.UtcNow,
                ActorType = "TEST",
                ActorHash = "hash",
                Operation = "test.op",
                Signature = "sig"
            });
            var saved = await db.SaveChangesAsync();
            Assert.Equal(1, saved);

            var count = await db.AuditEvents.CountAsync();
            Assert.Equal(1, count);
        }
        finally
        {
            await provider.DisposeAsync();
            Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task AuditLogger_WritesToRepository_WhenConfigured()
    {
        var (provider, dbPath) = BuildSqliteProvider();
        try
        {
            var factory = provider.GetRequiredService<IDbContextFactory<AuditDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
            }

            var repository = new EfAuditRepository(
                factory, provider.GetRequiredService<ILogger<EfAuditRepository>>());

            // 文件路径留空：本测试只验证落库那条路径
            var auditLogger = new AuditLogger(
                Options.Create(new AuditOptions
                {
                    SigningKey = new string('k', 48),
                    EnableChainSignature = true,
                    FilePath = ""
                }),
                provider.GetRequiredService<ILogger<AuditLogger>>(),
                repository);

            await auditLogger.WriteAsync(new AuditEvent
            {
                AuditId = "AUDINT0000001",
                Timestamp = DateTimeOffset.UtcNow,
                ActorType = "AGENT",
                ActorId = "u_demo01",
                Operation = "transfer.execute",
                Scenario = "transfer",
                Decision = "SUCCESS"
            });

            await using var verify = await factory.CreateDbContextAsync();
            var rows = await verify.AuditEvents.ToListAsync();

            var row = Assert.Single(rows);
            Assert.Equal("AUDINT0000001", row.AuditId);
            // 用户标识必须以哈希形式落库，不得出现明文
            Assert.DoesNotContain("u_demo01", row.ActorHash, StringComparison.Ordinal);
            Assert.False(string.IsNullOrEmpty(row.Signature), "签名不应为空");

            // 链式签名：首条的 PreviousSignature 应为 null（GENESIS）
            Assert.Null(row.PreviousSignature);
        }
        finally
        {
            await provider.DisposeAsync();
            Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task AuditLogger_DoesNotThrow_WhenRepositoryIsNull()
    {
        // 没注册仓储时（例如纯文件模式）不能崩，审计仍要写文件
        var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var auditLogger = new AuditLogger(
            Options.Create(new AuditOptions
            {
                SigningKey = new string('k', 48),
                EnableChainSignature = true,
                FilePath = ""
            }),
            provider.GetRequiredService<ILogger<AuditLogger>>(),
            repository: null);

        await auditLogger.WriteAsync(new AuditEvent
        {
            AuditId = "AUDNULL000001",
            Timestamp = DateTimeOffset.UtcNow,
            ActorType = "TEST",
            ActorId = "x",
            Operation = "op"
        });

        await provider.DisposeAsync();
    }

    private static void Cleanup(string dbPath)
    {
        foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* 清理失败不影响断言 */ }
        }
    }
}