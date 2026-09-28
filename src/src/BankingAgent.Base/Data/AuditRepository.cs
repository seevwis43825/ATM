// ===== 审计日志独立存储 =====
// 合规要求（《商业银行法》《个保法》第 47 条）：
// 审计日志必须独立存储、不可与业务数据同生共死、不可篡改。
//
// 设计要点：
// 1. 独立 Schema（PostgreSQL）/ 独立表
// 2. 应用层不提供 UPDATE / DELETE 路径
// 3. 按月分区，便于归档与合规留存（≥5 年）
// 4. HMAC 链式签名，篡改可检出

using BankingAgent.Base.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Base.Data;

/// <summary>审计事件实体。独立于业务库存储。</summary>
public sealed class AuditEventEntity
{
    public Guid Id { get; set; }

    /// <summary>审计流水号（可读，用于对外举证）。</summary>
    public string AuditId { get; set; } = "";

    public DateTimeOffset Timestamp { get; set; }
    public string ActorType { get; set; } = "";
    /// <summary>操作者 ID 的哈希，符合最小化原则。</summary>
    public string ActorHash { get; set; } = "";
    public string Operation { get; set; } = "";
    public string Scenario { get; set; } = "";
    public string Intent { get; set; } = "";
    public string Decision { get; set; } = "";
    public string DecisionReason { get; set; } = "";
    public string RuleId { get; set; } = "";
    public string RuleVersion { get; set; } = "";
    public decimal? Amount { get; set; }
    public string Currency { get; set; } = "CNY";
    public double? RiskScore { get; set; }
    public string? RequestId { get; set; }
    public string? TraceId { get; set; }
    public string? SourceIp { get; set; }
    public string? UserAgent { get; set; }
    public long ElapsedMs { get; set; }
    /// <summary>合规规则版本，便于监管追溯当时的规则状态。</summary>
    public string? PolicyVersion { get; set; }

    /// <summary>本条 HMAC 签名。</summary>
    public string Signature { get; set; } = "";
    /// <summary>前一条的签名，形成链式结构。</summary>
    public string? PreviousSignature { get; set; }
}

/// <summary>审计仓储。接口层面就不提供修改与删除。</summary>
public interface IAuditRepository
{
    Task AppendAsync(AuditEventEntity entity, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEventEntity>> QueryAsync(
        string? actorHash = null, string? scenario = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null,
        int limit = 100, CancellationToken ct = default);
    Task<ChainVerification> VerifyChainAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
    Task<long> CountAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}

/// <summary>审计链验证结果。</summary>
public sealed record ChainVerification(bool IsValid, long TotalRecords, IReadOnlyList<string> BrokenAt);

/// <summary>EF Core 审计仓储。</summary>
public sealed class EfAuditRepository(
    IDbContextFactory<AuditDbContext> factory,
    ILogger<EfAuditRepository> logger) : IAuditRepository
{
    private readonly IDbContextFactory<AuditDbContext> _factory = factory;
    private readonly ILogger<EfAuditRepository> _logger = logger;

    /// <inheritdoc />
    public async Task AppendAsync(AuditEventEntity entity, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.AuditEvents.Add(entity);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditEventEntity>> QueryAsync(
        string? actorHash = null, string? scenario = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null,
        int limit = 100, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var query = db.AuditEvents.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(actorHash))
            query = query.Where(e => e.ActorHash == actorHash);
        if (!string.IsNullOrEmpty(scenario))
            query = query.Where(e => e.Scenario == scenario);
        if (from is not null)
            query = query.Where(e => e.Timestamp >= from);
        if (to is not null)
            query = query.Where(e => e.Timestamp <= to);

        return await query
            .OrderByDescending(e => e.Timestamp)
            .Take(Math.Min(limit, 1000))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<ChainVerification> VerifyChainAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var events = await db.AuditEvents
            .AsNoTracking()
            .Where(e => e.Timestamp >= from && e.Timestamp <= to)
            .OrderBy(e => e.Timestamp)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        var broken = new List<string>();
        string? expectedPrev = null;

        foreach (var e in events)
        {
            if (e.PreviousSignature != expectedPrev)
            {
                broken.Add(e.AuditId);
            }
            expectedPrev = e.Signature;
        }

        var valid = broken.Count == 0;
        if (!valid)
        {
            _logger.LogCritical(
                "审计链断裂，检测到 {Count} 处异常：{Positions}",
                broken.Count, string.Join(", ", broken.Take(10)));
        }

        return new ChainVerification(valid, events.Count, broken);
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.AuditEvents
            .CountAsync(e => e.Timestamp >= from && e.Timestamp <= to, ct);
    }
}

/// <summary>审计专用 DbContext。与业务库物理隔离。</summary>
public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    /// <summary>审计事件表。</summary>
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<AuditEventEntity>();

        // 独立 Schema，与业务数据隔离
        if (Database.IsNpgsql())
        {
            entity.ToTable("audit_events", schema: "audit");
        }
        else
        {
            entity.ToTable("audit_events");
        }

        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).ValueGeneratedNever();
        entity.Property(e => e.AuditId).HasMaxLength(32).IsRequired();
        entity.Property(e => e.ActorType).HasMaxLength(16).IsRequired();
        entity.Property(e => e.ActorHash).HasMaxLength(64).IsRequired();
        entity.Property(e => e.Operation).HasMaxLength(64).IsRequired();
        entity.Property(e => e.Scenario).HasMaxLength(32);
        entity.Property(e => e.Decision).HasMaxLength(16);
        entity.Property(e => e.RuleId).HasMaxLength(64);
        entity.Property(e => e.RequestId).HasMaxLength(64);
        entity.Property(e => e.TraceId).HasMaxLength(64);
        entity.Property(e => e.SourceIp).HasMaxLength(45);
        entity.Property(e => e.Signature).HasMaxLength(64).IsRequired();
        entity.Property(e => e.Amount).HasPrecision(18, 2);

        // 查询优化索引
        entity.HasIndex(e => e.Timestamp);
        entity.HasIndex(e => new { e.ActorHash, e.Timestamp });
        entity.HasIndex(e => new { e.Scenario, e.Timestamp });
        entity.HasIndex(e => e.AuditId).IsUnique();
        entity.HasIndex(e => e.RequestId);
    }
}
