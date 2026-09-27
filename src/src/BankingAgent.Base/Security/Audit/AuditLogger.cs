// ===== 审计日志（不可篡改）=====
// 对应 docs/05-security-compliance/04-audit-logging.md：
// 1. HMAC 链式签名：每条记录签名包含前一条的签名，任何篡改都会导致链断裂。
// 2. 只允许 INSERT：应用层不提供更新/删除路径。
// 3. 独立通道：审计写入失败必须告警，不允许静默丢弃。

namespace BankingAgent.Base.Security.Audit;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    ILogger<AuditLogger> logger) : IAuditLogger
{
    private readonly AuditOptions _options = options.Value;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private string? _lastSignature;

    /// <summary>最近一次签名，供下一条链式使用。</summary>
    public string? LastSignature
    {
        get { lock (_gate) return _lastSignature; }
    }

    /// <inheritdoc />
    public async Task WriteAsync(AuditEvent evt, CancellationToken ct = default)
    {
        var signed = SignAndAdvance(evt);
        logger.LogInformation(
            "AUDIT | {Timestamp:yyyy-MM-dd HH:mm:ss.fff} | {ActorType}:{ActorId} | {Operation} | {Decision} | sig={Sig}",
            signed.Timestamp, signed.ActorType, signed.ActorId, signed.Operation,
            signed.Decision, signed.Signature[..Math.Min(12, signed.Signature.Length)]);

        await AppendToFileAsync(signed, ct);
    }

    /// <summary>
    /// 计算签名并原子推进链尾。
    /// 读链尾、算签名、写链尾必须在同一临界区内完成，
    /// 否则并发写入时多条记录会引用同一个前序签名，导致审计链断裂。
    /// </summary>
    private AuditEvent SignAndAdvance(AuditEvent evt)
    {
        if (!_options.EnableChainSignature) return evt;

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
    private static string Canonicalize(AuditEvent evt, string previousSignature)
    {
        var sb = new StringBuilder();
        sb.Append(evt.AuditId).Append('|')
          .Append(evt.Timestamp.ToUnixTimeMilliseconds()).Append('|')
          .Append(evt.ActorType).Append('|')
          .Append(evt.ActorId).Append('|')
          .Append(evt.Operation).Append('|')
          .Append(evt.Scenario).Append('|')
          .Append(evt.Decision).Append('|')
          .Append(evt.RuleId).Append('|')
          .Append(evt.Amount?.ToString("F2") ?? "").Append('|')
          .Append(previousSignature);
        return sb.ToString();
    }

    /// <summary>HMAC-SHA256 签名。</summary>
    private string ComputeHmac(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.SigningKey));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    /// <summary>追加到审计文件，形成独立的离线证据链。</summary>
    private async Task AppendToFileAsync(AuditEvent evt, CancellationToken ct)
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

            // 文件追加本身不是线程安全的，并发写会撕裂行，审计证据不可接受
            await _fileGate.WaitAsync(ct);
            try
            {
                await File.AppendAllTextAsync(_options.FilePath, line, ct);
            }
            finally
            {
                _fileGate.Release();
            }
        }
        catch (Exception ex)
        {
            // 审计文件写入失败必须显式告警，不允许静默
            logger.LogCritical(ex, "审计文件写入失败，审计链存在断裂风险: {Path}", _options.FilePath);
        }
    }

    /// <summary>操作者 ID 哈希，避免审计文件泄露用户标识。</summary>
    private static string HashActor(string actorId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(actorId));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }
}
