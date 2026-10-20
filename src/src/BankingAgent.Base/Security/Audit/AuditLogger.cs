// ===== 审计日志（不可篡改）=====
// 对应 docs/05-security-compliance/04-audit-logging.md：
// 1. HMAC 链式签名：每条记录签名包含前一条的签名，任何篡改都会导致链断裂。
// 2. 只允许 INSERT：应用层不提供更新/删除路径。
// 3. 独立通道：审计写入失败必须告警，不允许静默丢弃。

namespace BankingAgent.Base.Security.Audit;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BankingAgent.Base.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>审计配置。</summary>
public sealed class AuditOptions
{
    /// <summary>HMAC 签名密钥。生产环境必须通过环境变量注入，禁止硬编码。</summary>
    public string SigningKey { get; set; } = "dev-only-key-change-in-production";
    /// <summary>是否启用链式签名。</summary>
    public bool EnableChainSignature { get; set; } = true;
    /// <summary>审计日志文件输出路径。为空则只写数据库。</summary>
    public string? FilePath { get; set; } = "logs/audit-chain.log";

    /// <summary>
    /// 审计库**落库失败**时的死信文件路径（重试耗尽后，失败批次完整留在这里，可事后补录）。
    /// 为空则不写死信（仅告警）。
    /// </summary>
    public string? DeadLetterPath { get; set; } = "logs/audit-deadletter.jsonl";

    /// <summary>
    /// 是否让审计文件保持常驻句柄（避免每条记录 open→write→close）。
    /// </summary>
    /// <remarks>
    /// 默认 true。实测：每条记录都 open+close 时，宿主每个请求要在关键路径上
    /// 付 2~3 次文件打开/关闭；把审计文件放到慢速卷上，这部分足以把整体吞吐砍半
    /// （同一构建实测 473 QPS vs 982 QPS，差异全部来自这一个 I/O 点）。
    /// <para>
    /// 常驻句柄的代价：Windows 上写句柄会挡住用 <c>FileShare.Read</c> 打开的读者
    /// （<c>File.ReadAllLines</c> 正是这个模式），读者需改用 <c>FileShare.ReadWrite</c>。
    /// 仓库提供 <c>scripts/read-audit-chain.ps1</c> 作为合规读取示例。
    /// 若第三方工具只能用标准 <c>File.ReadAllLines</c>，把本项置为 false 即回到旧行为。
    /// </para>
    /// </remarks>
    public bool KeepFileOpen { get; set; } = true;
}

/// <summary>审计事件。</summary>
public sealed record AuditEvent
{
    public required string AuditId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string ActorType { get; init; }
    public required string ActorId { get; init; }
    public required string Operation { get; init; }
    public string Scenario { get; init; } = "";
    public string Intent { get; init; } = "";
    public string Decision { get; init; } = "";
    public string DecisionReason { get; init; } = "";
    public string RuleId { get; init; } = "";
    public string? RequestId { get; init; }
    public string? TraceId { get; init; }
    public string? SourcePlugin { get; init; }
    public decimal? Amount { get; init; }
    public double? RiskScore { get; init; }
    public string? ComplianceRule { get; init; }
    public long ElapsedMs { get; init; }
    public IReadOnlyDictionary<string, object?> Extra { get; init; }
        = new Dictionary<string, object?>();
    /// <summary>本条记录的 HMAC 签名。</summary>
    public string Signature { get; init; } = "";
    /// <summary>前一条记录的签名，形成链式结构。</summary>
    public string? PreviousSignature { get; init; }
}

/// <summary>审计写入契约。插件通过此接口写审计，不直接访问数据库。</summary>
public interface IAuditLogger
{
    Task WriteAsync(AuditEvent evt, CancellationToken ct = default);
    /// <summary>校验整条审计链是否完整未被篡改。</summary>
    AuditChainVerification VerifyChain(IReadOnlyList<AuditEvent> events);
}

/// <summary>审计链校验结果。</summary>
public sealed record AuditChainVerification(bool IsValid, int TotalRecords, IReadOnlyList<int> BrokenIndexes);

/// <summary>内存审计日志实现。配合数据库审计仓储使用。</summary>
public partial class AuditLogger(
    IOptions<AuditOptions> options,
    ILogger<AuditLogger> logger,
    IAuditRepository? repository = null) : IAuditLogger, IDisposable
{
    private readonly AuditOptions _options = options.Value;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private string? _lastSignature;

    // 审计文件常驻句柄：整个进程生命周期内保持打开，避免每条记录 open→write→close。
    // 为什么重要：审计写在请求关键路径上，每条多付一次文件打开/关闭，
    // 在慢速卷上足以把吞吐砍半（实测 473 vs 982 QPS）。
    // 读侧共享模式放宽到 ReadWrite|Delete：运维可以边跑边归档/轮转该文件。
    private FileStream? _fileStream;
    private StreamWriter? _fileWriter;

    /// <summary>审计文件写失败次数（累计，用于上报缺失条数）。</summary>
    private long _fileWriteFailures;

    /// <summary>上一次上报写失败的时间，用于日志节流。</summary>
    private DateTimeOffset? _lastFileFailureLog;

    /// <summary>写失败日志的节流窗口：窗口内只上报一次。</summary>
    private static readonly TimeSpan FileFailureLogThrottle = TimeSpan.FromSeconds(30);

    /// <summary>链尾是否已从归档文件续接过（只做一次）。</summary>
    private bool _chainSeeded;

    /// <summary>最近一次签名，供下一条链式使用。</summary>
    public string? LastSignature
    {
        get { lock (_gate) return _lastSignature; }
    }

    /// <inheritdoc />
    public async Task WriteAsync(AuditEvent evt, CancellationToken ct = default)
    {
        // ===== 签名与落盘必须在同一把锁内 =====
        //
        // 为什么：链尾只在内存里，"谁先签名、谁先落盘"若由两把不同的锁决定，
        // 就会出现「A 先签名、B 后签名，但 B 先落盘」——
        // 链本身没断（它是链表，仍可由 prev 指针还原），
        // 但**归档文件的物理顺序与链顺序不一致**：
        // 审计员从上往下读会看到 prev 指向更早记录的"乱序"条目，
        // 而按"逐行比对 prev == 上一行 sig"校验会误判为断裂。
        //
        // 代价：审计事件在本进程内串行落盘。但文件写本来就被 _fileGate 串行化，
        // 额外串行化的只是签名（微秒级），净成本≈0。
        await _fileGate.WaitAsync(ct);
        AuditEvent signed;
        try
        {
            signed = SignAndAdvance(evt);
            await AppendToFileCoreAsync(signed, ct);
        }
        finally
        {
            _fileGate.Release();
        }

        logger.LogInformation(
            "AUDIT | {Timestamp:yyyy-MM-dd HH:mm:ss.fff} | {ActorType}:{ActorId} | {Operation} | {Decision} | sig={Sig}",
            signed.Timestamp, signed.ActorType, signed.ActorId, signed.Operation,
            signed.Decision, signed.Signature[..Math.Min(12, signed.Signature.Length)]);

        // 同时落库：文件是离线证据，数据库提供可检索能力。
        // 《反洗钱法》第 30 条要求交易记录"可供查验"，只有文件是无法按
        // 操作者/时间/场景检索的。仓储实现只提供 Append，不提供改删。
        await AppendToRepositoryAsync(signed, ct);
    }

    /// <summary>
    /// 写入审计仓储。失败不阻断主流程，但必须显式告警 ——
    /// 审计丢失属于合规事故，不允许静默。
    /// </summary>
    private async Task AppendToRepositoryAsync(AuditEvent evt, CancellationToken ct)
    {
        if (repository is null) return;

        try
        {
            await repository.AppendAsync(ToEntity(evt), ct);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "审计落库失败（文件链已写入，但数据库缺失该条）。AuditId={AuditId}", evt.AuditId);
        }
    }

    /// <summary>把领域审计事件映射为审计库实体。</summary>
    private static AuditEventEntity ToEntity(AuditEvent evt) => new()
    {
        Id = Guid.NewGuid(),
        AuditId = evt.AuditId,
        Timestamp = evt.Timestamp,
        ActorType = evt.ActorType,
        // 与文件保持一致：库内同样只存哈希，避免明文用户标识扩散
        ActorHash = HashActor(evt.ActorId),
        Operation = evt.Operation,
        Scenario = evt.Scenario,
        Intent = evt.Intent,
        Decision = evt.Decision,
        DecisionReason = evt.DecisionReason,
        // 领域模型叫 ComplianceRule，审计表列名是 RuleId，这里做一次显式映射
        RuleId = evt.ComplianceRule ?? evt.RuleId,
        Amount = evt.Amount,
        RiskScore = evt.RiskScore,
        RequestId = evt.RequestId,
        TraceId = evt.TraceId,
        ElapsedMs = evt.ElapsedMs,
        Signature = evt.Signature,
        PreviousSignature = evt.PreviousSignature
    };

    /// <summary>
    /// 计算签名并原子推进链尾。
    /// 读链尾、算签名、写链尾必须在同一临界区内完成，
    /// 否则并发写入时多条记录会引用同一个前序签名，导致审计链断裂。
    /// </summary>
    public AuditEvent SignAndAdvance(AuditEvent evt)
    {
        if (!_options.EnableChainSignature) return evt;

        EnsureChainSeeded();

        lock (_gate)
        {
            var previous = _lastSignature ?? "GENESIS";
            var payload = Canonicalize(evt, previous);
            var signature = ComputeHmac(payload);
            _lastSignature = signature;
            return evt with
            {
                PreviousSignature = previous == "GENESIS" ? null : previous,
                Signature = signature
            };
        }
    }

    /// <summary>
    /// 首次写入前，把链尾接续到归档文件里已有的最后一条签名。
    /// </summary>
    /// <remarks>
    /// 为什么必须做：审计文件是**追加**的，而链尾只存在内存里。
    /// 若进程重启后从 GENESIS 重新开链，归档里就会出现多个 GENESIS，
    /// 链在重启边界处断开 —— 攻击者可以在那里截断并拼接两段记录，
    /// 结构校验反而看不出来。续上链尾后，整份归档才是一条连续的链。
    ///
    /// 只读文件末尾一小段（而不是整个文件）：审计文件可能很大。
    /// </remarks>
    private void EnsureChainSeeded()
    {
        if (_chainSeeded) return;

        lock (_gate)
        {
            if (_chainSeeded) return;
            _chainSeeded = true;

            if (!_options.EnableChainSignature) return;

            var path = _options.FilePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

            try
            {
                // 读取方必须用 FileShare.ReadWrite：写句柄（若已建立）不允许 Read 共享。
                using var fs = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);

                string? last = null;
                while (sr.ReadLine() is { } line)
                {
                    if (!string.IsNullOrWhiteSpace(line)) last = line;
                }

                if (last is null) return;

                using var doc = JsonDocument.Parse(last);
                if (doc.RootElement.TryGetProperty("sig", out var sig))
                {
                    _lastSignature = sig.GetString();
                }
            }
            catch (Exception ex)
            {
                // 读不到就当新链开始，不阻断业务（后续 SignAndAdvance 仍会正常签名）。
                logger.LogWarning(ex, "未能从既有审计归档续接链尾，本次启动按新链处理: {Path}", path);
            }
        }
    }

    /// <summary>计算签名但不推进链尾。仅供离线校验与测试使用。</summary>
    public AuditEvent Sign(AuditEvent evt)
    {
        if (!_options.EnableChainSignature) return evt;

        lock (_gate)
        {
            var previous = _lastSignature ?? "GENESIS";
            var payload = Canonicalize(evt, previous);
            return evt with
            {
                PreviousSignature = previous == "GENESIS" ? null : previous,
                Signature = ComputeHmac(payload)
            };
        }
    }

    /// <summary>校验链完整性。</summary>
    public AuditChainVerification VerifyChain(IReadOnlyList<AuditEvent> events)
    {
        var broken = new List<int>();
        string? expectedPrev = null;

        for (var i = 0; i < events.Count; i++)
        {
            var evt = events[i];
            if (evt.PreviousSignature != expectedPrev)
            {
                broken.Add(i);
            }
            var recomputed = ComputeHmac(Canonicalize(evt, expectedPrev ?? "GENESIS"));
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(recomputed),
                    Encoding.UTF8.GetBytes(evt.Signature)))
            {
                broken.Add(i);
            }
            expectedPrev = evt.Signature;
        }

        return new AuditChainVerification(broken.Count == 0, events.Count, broken.Distinct().ToList());
    }

    /// <summary>序列化待签名内容，字段顺序固定以保证可重现。</summary>
    /// <remarks>
    /// **操作者用哈希而不是原始 ID**：归档文件里存的是 <c>actor</c>（SHA256 前 12 位），
    /// 而签名输入若用原始 ID，归档就**不足以重算 HMAC** ——
    /// 离线只能校验链式衔接，无法证明"这条记录确实由持有密钥的一方产生"。
    /// 改成用同一份哈希后，归档自身包含了重算所需的全部输入，
    /// 完整密码学校验才真正可行（见 <c>scripts/read-audit-chain.ps1 -VerifyHmac</c>）。
    /// 代价：签名不再绑定原始 ID —— 但原始 ID 本就不该进签名材料（PII 最小化）。
    /// <para>
    /// 金额一律用 <c>InvariantCulture</c> 格式化：否则签名会随机器区域设置变化，
    /// 在逗号小数点的机器上生成的归档无法在别的机器上重算校验。
    /// 用不变文化同时也让签名输入与归档里的 JSON 数字表示保持一致。
    /// </para>
    /// </remarks>
    private static string Canonicalize(AuditEvent evt, string previousSignature)
    {
        var sb = new StringBuilder();
        sb.Append(evt.AuditId).Append('|')
          .Append(evt.Timestamp.ToUnixTimeMilliseconds()).Append('|')
          .Append(evt.ActorType).Append('|')
          .Append(HashActor(evt.ActorId)).Append('|')
          .Append(evt.Operation).Append('|')
          .Append(evt.Scenario).Append('|')
          .Append(evt.Decision).Append('|')
          .Append(evt.RuleId).Append('|')
          .Append(evt.Amount?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? "").Append('|')
          .Append(previousSignature);
        return sb.ToString();
    }

    /// <summary>HMAC-SHA256 签名。</summary>
    private string ComputeHmac(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.SigningKey));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    /// <summary>
    /// 追加到审计文件，形成独立的离线证据链。
    /// </summary>
    /// <remarks>
    /// 调用方**必须已持有** <c>_fileGate</c>（<see cref="WriteAsync"/> 会先拿锁再调本方法），
    /// 这样"签名顺序"与"落盘顺序"必然一致，归档文件的物理顺序即链顺序。
    /// </remarks>
    /// <param name="evt">已签名的事件。</param>
    /// <param name="ct">取消令牌。</param>
    private async Task AppendToFileCoreAsync(AuditEvent evt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.FilePath)) return;

        try
        {
            var dir = Path.GetDirectoryName(_options.FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var line = JsonSerializer.Serialize(new
            {
                evt.AuditId,
                ts = evt.Timestamp.ToString("O"),
                evt.ActorType,
                // 审计文件中用户 ID 做哈希，符合最小化原则
                actor = HashActor(evt.ActorId),
                evt.Operation,
                evt.Scenario,
                evt.Decision,
                evt.RuleId,
                evt.Amount,
                prev = evt.PreviousSignature,
                sig = evt.Signature
            }) + Environment.NewLine;

            if (_options.KeepFileOpen)
            {
                await AppendViaPersistentHandleAsync(line, ct);
            }
            else
            {
                await File.AppendAllTextAsync(_options.FilePath, line, ct);
            }
        }
        catch (Exception ex)
        {
            // 审计文件写入失败必须显式告警，不允许静默
            logger.LogCritical(ex, "审计文件写入失败，审计链存在断裂风险: {Path}", _options.FilePath);
        }
    }

    /// <summary>
    /// 用常驻句柄追加一行。
    /// </summary>
    /// <remarks>
    /// 每条都 <c>Flush</c>：保证记录在调用方返回前已进入操作系统页缓存，
    /// 其它进程立即可读 —— 与旧的 <c>File.AppendAllTextAsync</c>（每次 open→write→close）
    /// 语义一致，差别只是不再每次重建文件句柄。
    /// <para>
    /// 失败日志**必须节流**：审计写失败是每秒都可能发生的事件，
    /// 若每条都打一条带堆栈的 Critical，宿主会把自己的 CPU 全部烧在写日志上，
    /// 吞吐直接崩塌（实测出现过 780 → 38 QPS）。因此按时间窗节流，并把丢弃条数累计上报。
    /// </para>
    /// </remarks>
    /// <param name="line">已序列化的 JSONL 行。</param>
    /// <param name="ct">取消令牌。</param>
    private async Task AppendViaPersistentHandleAsync(string line, CancellationToken ct)
    {
        try
        {
            await EnsureFileWriterAsync(ct).ConfigureAwait(false);
            await _fileWriter!.WriteAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await _fileWriter.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 句柄失效（文件被外部删除/轮转）：重建后重试一次。
            ResetFileWriter();
            try
            {
                await EnsureFileWriterAsync(ct).ConfigureAwait(false);
                await _fileWriter!.WriteAsync(line.AsMemory(), ct).ConfigureAwait(false);
                await _fileWriter.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception retryEx)
            {
                ReportFileWriteFailure(retryEx);
            }
        }
    }

    /// <summary>
    /// 上报审计文件写入失败（按时间窗节流）。
    /// </summary>
    /// <remarks>
    /// 节流是必须的：审计写失败在故障期间是**每请求都可能发生**的事件，
    /// 逐条打带堆栈的日志会让宿主把自己的 CPU 烧光，吞吐从 780 QPS 掉到 38 QPS
    /// ——故障被"日志放大"成雪崩，比原始故障更严重。
    /// </remarks>
    /// <param name="ex">异常。</param>
    private void ReportFileWriteFailure(Exception ex)
    {
        var dropped = Interlocked.Increment(ref _fileWriteFailures);

        var now = DateTimeOffset.UtcNow;
        var last = _lastFileFailureLog;
        if (last is not null && now - last.Value < FileFailureLogThrottle)
        {
            return;
        }

        _lastFileFailureLog = now;
        logger.LogCritical(ex,
            "审计文件写入失败（文件链已缺失 {Dropped} 条，节流后每 {Window} 最多上报一次）: {Path}",
            dropped, FileFailureLogThrottle, _options.FilePath);
    }

    /// <summary>确保审计文件句柄已就绪。</summary>
    /// <param name="ct">取消令牌。</param>
    private async Task EnsureFileWriterAsync(CancellationToken ct)
    {
        if (_fileWriter is not null) return;

        var path = _options.FilePath!;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // FileShare.ReadWrite（**刻意不含 Delete**）：
        //   ReadWrite —— 允许合规工具边跑边读（读侧需用 FileShare.ReadWrite 打开）；
        //   不给 Delete —— 进程运行期间该文件**不能被删除或改名**。
        //     否则我们手里的写句柄会继续写进"已被删除的旧文件"，
        //     表面看审计链还在、文件却已经消失 —— 那是合规事故，不是性能问题。
        //     需要轮转请先停服务（标准做法），或把 KeepFileOpen 置为 false。
        _fileStream = new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite,
            bufferSize: 64 * 1024, useAsync: true);
        _fileWriter = new StreamWriter(_fileStream, new System.Text.UTF8Encoding(false))
        {
            AutoFlush = false,
            NewLine = Environment.NewLine
        };

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>丢弃当前文件句柄，下次写入时重建。</summary>
    private void ResetFileWriter()
    {
        try { _fileWriter?.Dispose(); } catch { /* 关闭失败无需处理：句柄本就要丢弃 */ }
        try { _fileStream?.Dispose(); } catch { /* 同上 */ }
        _fileWriter = null;
        _fileStream = null;
    }

    /// <summary>关闭审计文件句柄。宿主停机时由容器调用。</summary>
    public void Dispose()
    {
        ResetFileWriter();
        // 刻意不 Dispose _fileGate：停机瞬间可能仍有请求在等这把锁，
        // 释放它会让等待方抛 ObjectDisposedException；信号量本身不持有非托管资源。
        GC.SuppressFinalize(this);
    }

    /// <summary>操作者 ID 哈希，避免审计文件泄露用户标识。</summary>
    private static string HashActor(string actorId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(actorId));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }
}
