// ===== 审计批量异步落库路径的集成测试 =====
//
// 目的：覆盖「IAuditRepository(装饰器) -> AuditDbWriter -> EfAuditRepository -> audit_events 表」
// 这条**异步**链路，确认它真的能把记录写进库并查得回来。
//
// 为什么必须专门测这条路径：
//   审计的**权威记录**是同步落盘的 JSONL + 链签名，数据库只是**可检索副本**。
//   这条链路是后台异步提交的，任何一段没接上，表现都是
//   「审计文件有内容、数据库查不到」——
//   而写入失败会被 catch 成一条 Critical 日志，系统表面完全正常。
//   这类静默失效只能靠"真跑写入器、真查库"来发现。
//
// 特别注意：旧的 AuditPersistenceTests 只直接测了 DbContext 与仓储，
// **没有覆盖装饰器 + 写入器**这一段。改异步落库时最容易漏掉的就是它。

using BankingAgent.Base.Data;
using BankingAgent.Base.Security.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace UnitTests;

public class AuditBatchedWriterTests
{
    /// <summary>装配与生产一致的三件套：EfAuditRepository + AuditDbWriter + 装饰器。</summary>
    private static (ServiceProvider Provider, string DbPath) BuildProvider()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"audit-batch-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        services.AddDbContextFactory<AuditDbContext>((sp, options) =>
            DatabaseConfigurator.Configure(options, new DatabaseOptions
            {
                Provider = DatabaseProvider.Sqlite,
                ConnectionString = $"Data Source={dbPath}"
            }));

        // 与 DatabaseServiceCollectionExtensions 相同的装配顺序：
        // 写入器依赖具体实现（避免与装饰器形成循环依赖），装饰器是 IAuditRepository 的唯一实现。
        services.AddSingleton<EfAuditRepository>();
        services.AddSingleton(sp => new AuditDbWriter(
            sp.GetRequiredService<EfAuditRepository>(),
            sp.GetRequiredService<ILogger<AuditDbWriter>>()));
        services.AddSingleton<IAuditRepository>(sp => new BatchedAuditRepository(
            sp.GetRequiredService<EfAuditRepository>(),
            sp.GetRequiredService<AuditDbWriter>()));

        return (services.BuildServiceProvider(), dbPath);
    }

    private static AuditEventEntity NewEvent(string auditId) => new()
    {
        Id = Guid.NewGuid(),
        AuditId = auditId,
        Timestamp = DateTimeOffset.UtcNow,
        ActorType = "TEST",
        ActorHash = "hash",
        Operation = "test.op",
        Scenario = "transfer",
        Decision = "SUCCESS",
        Signature = new string('a', 64)
    };

    [Fact]
    public async Task Append_ThroughBatchedWriter_LandsInDatabase()
    {
        var (provider, dbPath) = BuildProvider();
        try
        {
            var factory = provider.GetRequiredService<IDbContextFactory<AuditDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
            }

            var writer = provider.GetRequiredService<AuditDbWriter>();
            var repo = provider.GetRequiredService<IAuditRepository>();

            // 走装饰器 → 写入器 → 仓储这条异步链路
            await repo.AppendAsync(NewEvent("BATCH00000001"));
            await repo.AppendAsync(NewEvent("BATCH00000002"));

            // 读路径会先 flush，因此这里查得到就说明确实落库了
            var rows = await repo.QueryAsync(limit: 10);

            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, r => r.AuditId == "BATCH00000001");
            Assert.Contains(rows, r => r.AuditId == "BATCH00000002");
            Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r.Signature)));

            // 直接查库再确认一次：装饰器的查询不能自己骗自己
            await using (var db = await factory.CreateDbContextAsync())
            {
                var count = await db.AuditEvents.CountAsync();
                Assert.Equal(2, count);
            }

            await writer.StopAsync(CancellationToken.None);
        }
        finally
        {
            await provider.DisposeAsync();
            Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task AppendBatch_ThroughBatchedWriter_LandsAllRows()
    {
        var (provider, dbPath) = BuildProvider();
        try
        {
            var factory = provider.GetRequiredService<IDbContextFactory<AuditDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
            }

            var writer = provider.GetRequiredService<AuditDbWriter>();
            var repo = provider.GetRequiredService<IAuditRepository>();

            var batch = Enumerable.Range(0, 50)
                .Select(i => NewEvent($"BULK{i:D6}"))
                .ToList();
            await repo.AppendBatchAsync(batch);

            var rows = await repo.QueryAsync(limit: 100);
            Assert.Equal(50, rows.Count);

            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.Equal(50, await db.AuditEvents.CountAsync());
            }

            await writer.StopAsync(CancellationToken.None);
        }
        finally
        {
            await provider.DisposeAsync();
            Cleanup(dbPath);
        }
    }

    /// <summary>读前 flush：写完立刻查必须查得到（读己所写一致性）。</summary>
    [Fact]
    public async Task Query_AfterWrite_ReturnsFreshData()
    {
        var (provider, dbPath) = BuildProvider();
        try
        {
            var factory = provider.GetRequiredService<IDbContextFactory<AuditDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
            }

            var writer = provider.GetRequiredService<AuditDbWriter>();
            var repo = provider.GetRequiredService<IAuditRepository>();

            for (var i = 0; i < 10; i++)
            {
                await repo.AppendAsync(NewEvent($"FRESH{i:D4}"));

                // limit 要给足，否则条数会被 limit 截断，断言测的就不是"读己所写"了
                var rows = await repo.QueryAsync(limit: 100);
                Assert.Equal(i + 1, rows.Count);

                // 刚写的那条必须在结果里（时间倒序，第一条就是它）
                Assert.Equal($"FRESH{i:D4}", rows[0].AuditId);
            }

            await writer.StopAsync(CancellationToken.None);
        }
        finally
        {
            await provider.DisposeAsync();
            Cleanup(dbPath);
        }
    }

    /// <summary>停机时把队列里剩下的记录提交掉，不留审计尾巴。</summary>
    [Fact]
    public async Task Stop_DrainsPendingRecords()
    {
        var (provider, dbPath) = BuildProvider();
        try
        {
            var factory = provider.GetRequiredService<IDbContextFactory<AuditDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
            }

            var writer = provider.GetRequiredService<AuditDbWriter>();
            var repo = provider.GetRequiredService<IAuditRepository>();

            // 写入器还没开始消费就写入，然后立刻停机
            await repo.AppendAsync(NewEvent("DRAIN0000001"));
            await writer.StopAsync(CancellationToken.None);

            await using (var db = await factory.CreateDbContextAsync())
            {
                var count = await db.AuditEvents.CountAsync();
                Assert.Equal(1, count);
            }
        }
        finally
        {
            await provider.DisposeAsync();
            Cleanup(dbPath);
        }
    }

    private static void Cleanup(string dbPath)
    {
        foreach (var p in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* 清理失败不影响断言 */ }
        }
    }
}
