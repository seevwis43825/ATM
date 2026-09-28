// ===== 工作单元 =====
// 封装事务边界与幂等键。DbContext 通过工厂获取，
// 避免单例服务（Agent）直接持有 scoped 的 DbContext。

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Base.Data;

/// <summary>工作单元。封装事务边界与幂等控制。</summary>
public class UnitOfWork(IDbContextFactory<BankingDbContext> factory, ILogger<UnitOfWork> logger)
{
    private readonly ConcurrentDictionary<string, byte> _idempotencyKeys = new();

    /// <summary>创建新的数据库上下文。Agent 等单例服务通过它获取作用域内实例。</summary>
    public BankingDbContext CreateContext() => factory.CreateDbContext();

    /// <summary>在独立事务中执行操作。适用于单例服务调用场景。</summary>
    public async Task<T> ExecuteAsync<T>(
        Func<BankingDbContext, CancellationToken, Task<T>> operation,
        CancellationToken ct = default)
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
    public async Task ExecuteAsync(
        Func<BankingDbContext, CancellationToken, Task> operation,
        CancellationToken ct = default)
    {
        await ExecuteAsync<object?>(async (db, c) =>
        {
            await operation(db, c);
            return null;
        }, ct);
    }

    /// <summary>幂等键登记。返回 false 表示该键已执行过，调用方应直接返回原结果。</summary>
    public bool TryRegisterIdempotencyKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return true;
        return _idempotencyKeys.TryAdd(key, 0);
    }
}
