// ===== 工作单元与数据库初始化 =====
// 负责事务边界、幂等控制、自动建表与种子数据。

namespace BankingAgent.Base.Data;

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>工作单元。封装事务边界与幂等控制。</summary>
public class UnitOfWork(IDbContextFactory<BankingDbContext> factory, ILogger<UnitOfWork> logger)
{
    private readonly ConcurrentDictionary<string, byte> _idempotencyKeys = new();

    /// <summary>创建新的数据库上下文。Agent 等单例服务通过它获取作用域内实例。</summary>
    public BankingDbContext CreateContext() => factory.CreateDbContext();

    /// <summary>在独立事务中执行操作。适用于单例服务调用场景。</summary>
    public async Task<T> ExecuteAsync<T>(Func<BankingDbContext, CancellationToken, Task<T>> operation, CancellationToken ct = default)
    {
        await using var db = factory.CreateDbContext();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var result = await operation(db, ct);
            await tx.CommitAsync(ct);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "事务回滚: {Operation}", operation.Method.Name);
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>无返回值的重载。</summary>
    public async Task ExecuteAsync(Func<BankingDbContext, CancellationToken, Task> operation, CancellationToken ct = default)
    {
        await ExecuteAsync<object?>(async (db, c) => { await operation(db, c); return null; }, ct);
    }

    /// <summary>幂等键登记。返回 false 表示该键已执行过，调用方应直接返回原结果。</summary>
    public bool TryRegisterIdempotencyKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return true;
        return _idempotencyKeys.TryAdd(key, 0);
    }
}

/// <summary>数据库初始化器。启动时自动建表（开发/演示环境）与执行迁移（生产）。</summary>
public class DatabaseInitializer(
    BankingDbContext db, ILogger<DatabaseInitializer> logger, bool autoMigrate)
{
    /// <summary>执行初始化。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (!autoMigrate)
        {
            logger.LogInformation("跳过自动建表（AutoMigrate=false），请手动执行迁移");
            return;
        }

        try
        {
            if (db.Database.IsNpgsql())
            {
                foreach (var partition in db.Partitions)
                {
                    await db.Database.ExecuteSqlRawAsync(
                        $"CREATE SCHEMA IF NOT EXISTS \"{partition}\";", ct);
                }
            }

            await db.Database.EnsureCreatedAsync(ct);
            logger.LogInformation(
                "数据库初始化完成。Provider={Provider}, 分区数={Count}, 分区=[{Partitions}]",
                db.Database.ProviderName, db.Partitions.Count, string.Join(", ", db.Partitions));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "数据库初始化失败");
            throw;
        }
    }
}
