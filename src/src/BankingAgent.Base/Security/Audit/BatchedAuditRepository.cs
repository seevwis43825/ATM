// ===== 审计仓储的批量写穿装饰器 =====
//
// 它把「逐条同步提交」换成「入队 + 后台批量提交」，读路径在返回前先 flush，
// 保证「写完立刻查」能查到。详细取舍见同目录的 AuditDbWriter。

using BankingAgent.Base.Data;

namespace BankingAgent.Base.Security.Audit;

/// <summary>把审计写入改为批量异步提交的仓储装饰器。</summary>
public sealed class BatchedAuditRepository(
    IAuditRepository inner,
    AuditDbWriter writer) : IAuditRepository
{
    /// <inheritdoc />
    public Task AppendAsync(AuditEventEntity entity, CancellationToken ct = default) =>
        writer.EnqueueAsync(entity, ct);

    /// <inheritdoc />
    public Task AppendBatchAsync(IReadOnlyList<AuditEventEntity> entities, CancellationToken ct = default) =>
        writer.EnqueueBatchAsync(entities, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditEventEntity>> QueryAsync(
        string? actorHash = null, string? scenario = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null,
        int limit = 100, CancellationToken ct = default)
    {
        await writer.FlushAsync(ct);
        return await inner.QueryAsync(actorHash, scenario, from, to, limit, ct);
    }

    /// <inheritdoc />
    public async Task<ChainVerification> VerifyChainAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await writer.FlushAsync(ct);
        return await inner.VerifyChainAsync(from, to, ct);
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await writer.FlushAsync(ct);
        return await inner.CountAsync(from, to, ct);
    }
}
