// ===== 审计库批量写入器 =====
//
// 为什么需要它：
//   每条审计事件都同步提交一次 SQLite 事务（建连接 → INSERT → 提交 → fsync），
//   单次十几到几十毫秒，而且被审计链的全局锁串行化 ——
//   实测把整个宿主的吞吐拖到**每秒二十来次**（对照：/health 探针能跑 1100+ req/s）。
//
// 这里采用**写穿（write-behind）**模型：
//   - **权威记录仍然同步**：HMAC 链式签名 + JSONL 归档，在请求返回前就已落盘；
//   - **数据库只是可检索副本**：《反洗钱法》第 30 条要求交易记录"可供查验"，
//     载体是 JSONL 归档 + 链式签名（可离线校验完整性），数据库提供按操作者/时间/场景检索的能力；
//   - **队列有界**：满了就背压等待，**绝不丢审计记录**；
//   - **读取前先 flush**：保证"写完立刻查"能查到，不出现读己所写不一致。
//
// 崩溃时最多丢「尚未批量提交」的数据库副本尾部，JSONL 与签名链完好无损 ——
// 审计的**可篡改性**由链式签名保证，不依赖数据库副本。

using System.Threading.Channels;
using BankingAgent.Base.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Base.Security.Audit;

/// <summary>审计库批量写入器：把审计事件攒批后一次性提交到数据库。</summary>
public sealed class AuditDbWriter(
    IAuditRepository inner,
    ILogger<AuditDbWriter> logger) : BackgroundService
{
    /// <summary>单批提交的最大条数。</summary>
    public const int DefaultBatchSize = 200;

    /// <summary>队列容量上限。满时生产者被背压阻塞，不会丢记录。</summary>
    public const int DefaultQueueCapacity = 10000;

    private readonly Channel<AuditEventEntity> _queue =
        Channel.CreateBounded<AuditEventEntity>(new BoundedChannelOptions(DefaultQueueCapacity)
        {
            // 队列满时让写入方等待，而不是丢弃 —— 审计记录不允许丢
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    private readonly SemaphoreSlim _commitGate = new(1, 1);

    /// <summary>把一条审计事件放入批量队列。</summary>
    /// <param name="entity">审计事件实体。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task EnqueueAsync(AuditEventEntity entity, CancellationToken ct = default) =>
        await _queue.Writer.WriteAsync(entity, ct).ConfigureAwait(false);

    /// <summary>把一批审计事件放入批量队列。</summary>
    /// <param name="entities">审计事件实体集合。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task EnqueueBatchAsync(
        IReadOnlyList<AuditEventEntity> entities, CancellationToken ct = default)
    {
        foreach (var entity in entities)
        {
            await _queue.Writer.WriteAsync(entity, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 立即把队列里已入队的记录提交掉。
    /// 读取路径在返回结果前调用它，保证读到的是最新数据。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        await _commitGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await DrainAllAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _commitGate.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<AuditEventEntity>(DefaultBatchSize);

        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                // ===== 关键顺序：先拿提交锁，再从队列取数据 =====
                // 这样「已从队列取走但尚未落库」的记录一定在锁的保护范围内，
                // 读路径的 Flush 因此不会漏掉正在途中的那一批。
                // 反过来（先取数据再拿锁）会留下一个窗口：
                // 取走的那批卡在锁上等待时，Flush 看不到它们却已经返回，
                // 「写完立刻查」就会查不到。
                await _commitGate.WaitAsync(stoppingToken).ConfigureAwait(false);
                try
                {
                    batch.Clear();

                    while (batch.Count < DefaultBatchSize && _queue.Reader.TryRead(out var item))
                    {
                        batch.Add(item);
                    }

                    if (batch.Count > 0)
                    {
                        await CommitCoreAsync(batch, stoppingToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _commitGate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机：把队列里剩下的记录提交掉，不留审计尾巴。
        }
        finally
        {
            _queue.Writer.TryComplete();
            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // 先停止消费循环（它会在 finally 里把剩余记录刷完），再收尾。
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// 把队列里剩余的记录**全部**取出并提交。
    ///
    /// 必须循环到空，而不是只取一批：只取一批的话，
    /// 队列里积压超过一批时 Flush 会提前返回，读路径仍可能查不到最新记录。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    private async Task DrainAllAsync(CancellationToken ct)
    {
        var batch = new List<AuditEventEntity>(DefaultBatchSize);

        while (_queue.Reader.TryRead(out var item))
        {
            batch.Add(item);

            if (batch.Count >= DefaultBatchSize)
            {
                await CommitCoreAsync(batch, ct).ConfigureAwait(false);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await CommitCoreAsync(batch, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 真正执行落库。失败要显式告警 —— 审计丢失属于合规事故。
    /// 调用方必须已持有提交锁。
    /// </summary>
    /// <param name="batch">待提交的记录。</param>
    /// <param name="ct">取消令牌。</param>
    private async Task CommitCoreAsync(List<AuditEventEntity> batch, CancellationToken ct)
    {
        try
        {
            await inner.AppendBatchAsync(batch, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "审计批量落库失败（文件链已写入，但数据库缺失 {Count} 条）", batch.Count);
        }
    }
}
