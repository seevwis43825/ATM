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

using System.Text.Json;
using System.Threading.Channels;
using BankingAgent.Base.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Base.Security.Audit;

/// <summary>审计库批量写入器：把审计事件攒批后一次性提交到数据库。</summary>
public sealed class AuditDbWriter(
    IAuditRepository inner,
    ILogger<AuditDbWriter> logger,
    string? deadLetterPath = null) : BackgroundService
{
    /// <summary>单批提交的最大条数。</summary>
    public const int DefaultBatchSize = 200;

    /// <summary>队列容量上限。满时生产者被背压阻塞，不会丢记录。</summary>
    public const int DefaultQueueCapacity = 10000;

    /// <summary>落库失败时的最大尝试次数（含首次）。</summary>
    public const int MaxCommitAttempts = 3;

    /// <summary>
    /// 重试退避基数（毫秒）。第 n 次失败后等待 <c>基数 × 4^(n-1)</c>。
    ///
    /// 注意**可达档位**：<see cref="MaxCommitAttempts"/> = 3 表示「含首次共 3 次尝试」，
    /// 因此最多只会发生 2 次延迟 —— 实际是 <b>200ms → 800ms</b>，
    /// 第三档 3200ms 永远不会执行（第三次失败即写死信，不再延迟）。
    /// 文档与注释都应按「200ms → 800ms」写，不要写成三档。
    /// </summary>
    public const int CommitRetryBaseDelayMs = 200;

    /// <summary>死信告警的节流窗口。</summary>
    public static readonly TimeSpan DeadLetterLogThrottle = TimeSpan.FromSeconds(30);

    private readonly Channel<AuditEventEntity> _queue =
        Channel.CreateBounded<AuditEventEntity>(new BoundedChannelOptions(DefaultQueueCapacity)
        {
            // 队列满时让写入方等待，而不是丢弃 —— 审计记录不允许丢
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    private readonly SemaphoreSlim _commitGate = new(1, 1);

    /// <summary>死信文件路径：重试耗尽后把失败批次完整留在这里，可事后补录。</summary>
    public string? DeadLetterPath { get; } = deadLetterPath;

    /// <summary>累计未能落库的记录条数。</summary>
    private long _deadLetterCount;

    /// <summary>上一次死信告警时间，用于节流。</summary>
    private DateTimeOffset? _lastDeadLetterLog;

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
    /// 真正执行落库。
    ///
    /// **失败不能只记一条日志就丢弃**：那一批是"可检索副本"里唯一一份，
    /// 丢了就再也检索不到这笔操作（权威的 JSONL 链还在，但按操作者/时间/场景
    /// 的查询能力直接缺失，且没有留下任何可供补录的输入）。
    /// 因此这里做三件事：
    /// 1. **有界重试**（含首次共 3 次尝试，即最多 2 次重试，退避 200ms→800ms，
    ///    最坏等待约 1.0s）—— 瞬时故障（库被占用、
    ///    磁盘抖动）自愈，不需要人工介入；
    /// 2. 重试仍失败 → **写入死信文件**（<c>Audit:DeadLetterPath</c>），
    ///    记录以 JSON 形式完整保留，可事后补录进库；
    /// 3. 累计失败条数并节流告警（逐条打堆栈会把宿主 CPU 烧光，见 AuditLogger 同款教训）。
    /// </summary>
    /// <param name="batch">待提交的记录。</param>
    /// <param name="ct">取消令牌。</param>
    private async Task CommitCoreAsync(List<AuditEventEntity> batch, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await inner.AppendBatchAsync(batch, ct).ConfigureAwait(false);
                if (attempt > 1)
                {
                    logger.LogInformation(
                        "审计批量落库在第 {Attempt} 次尝试后成功（{Count} 条）", attempt, batch.Count);
                }
                return;
            }
            catch (Exception ex) when (attempt < MaxCommitAttempts)
            {
                logger.LogWarning(ex,
                    "审计批量落库失败（第 {Attempt}/{Max} 次尝试），{Delay}ms 后重试：{Count} 条",
                    attempt, MaxCommitAttempts, CommitRetryDelayMs(attempt), batch.Count);

                try
                {
                    await Task.Delay(CommitRetryDelayMs(attempt), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                await HandleCommitFailureAsync(batch, ex, ct).ConfigureAwait(false);
                return;
            }
        }

        // 走到这里说明重试期间被取消 —— 仍然要保留这批记录。
        await HandleCommitFailureAsync(batch,
            new OperationCanceledException("审计批量落库在重试期间被取消"), CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 重试耗尽后的兜底：写死信文件 + 节流告警。
    /// </summary>
    /// <param name="batch">失败的记录。</param>
    /// <param name="ex">最后一次异常。</param>
    /// <param name="ct">取消令牌。</param>
    private async Task HandleCommitFailureAsync(
        List<AuditEventEntity> batch, Exception ex, CancellationToken ct)
    {
        var dropped = Interlocked.Add(ref _deadLetterCount, batch.Count);

        try
        {
            await WriteDeadLetterAsync(batch, ct).ConfigureAwait(false);
        }
        catch (Exception dlEx)
        {
            logger.LogCritical(dlEx,
                "审计死信文件写入失败，这批记录已无处可存：{Count} 条", batch.Count);
        }

        var now = DateTimeOffset.UtcNow;
        var last = _lastDeadLetterLog;
        if (last is not null && now - last.Value < DeadLetterLogThrottle)
        {
            return;
        }

        _lastDeadLetterLog = now;
        logger.LogCritical(ex,
            "审计批量落库重试 {Max} 次仍失败（{Count} 条已写入死信文件，累计缺失 {Total} 条）：{Path}",
            MaxCommitAttempts, batch.Count, dropped, DeadLetterPath);
    }

    /// <summary>把失败批次写入死信文件（每行一条 JSON）。</summary>
    /// <param name="batch">失败的记录。</param>
    /// <param name="ct">取消令牌。</param>
    private async Task WriteDeadLetterAsync(List<AuditEventEntity> batch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(DeadLetterPath)) return;

        var dir = Path.GetDirectoryName(DeadLetterPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var lines = string.Join(Environment.NewLine, batch.Select(e => JsonSerializer.Serialize(e)));
        await File.AppendAllTextAsync(DeadLetterPath, lines + Environment.NewLine, ct)
            .ConfigureAwait(false);
    }

    /// <summary>第 n 次失败后的退避时长。</summary>
    /// <param name="attempt">已失败的次数（从 1 起）。</param>
    /// <returns>等待毫秒数。</returns>
    private static int CommitRetryDelayMs(int attempt) => (int)(CommitRetryBaseDelayMs * Math.Pow(4, attempt - 1));
}
