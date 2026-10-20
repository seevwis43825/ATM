// ===== 审计日志独立存储 =====
// 合规要求（《商业银行法》《个保法》第 47 条）：
// 审计日志必须独立存储、不可与业务数据同生共死、不可篡改。
//
// 设计要点：
// 1. 独立 Schema（PostgreSQL）/ 独立表
// 2. 应用层不提供 UPDATE / DELETE 路径
// 3. 按月分区，便于归档与合规留存（≥5 年）
// 4. HMAC 链式签名，篡改可检出

using System.Linq.Expressions;
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

    /// <summary>
    /// 批量追加审计记录（一个事务一次提交）。
    ///
    /// 为什么需要批量：逐条提交意味着每条审计事件都要付一次 SQLite 事务提交成本
    /// （含 fsync），而审计写入在请求关键路径上，会把整个系统吞吐压到每秒几十次。
    /// 批量提交把这份成本摊薄到一批记录上。
    /// </summary>
    /// <param name="entities">待追加的记录。</param>
    /// <param name="ct">取消令牌。</param>
    Task AppendBatchAsync(IReadOnlyList<AuditEventEntity> entities, CancellationToken ct = default);

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
    public async Task AppendBatchAsync(IReadOnlyList<AuditEventEntity> entities, CancellationToken ct = default)
    {
        if (entities.Count == 0) return;

        await using var db = await _factory.CreateDbContextAsync(ct);
        db.AuditEvents.AddRange(entities);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 查询审计事件（可按操作者哈希、场景、时间区间过滤，按时间倒序）。
    ///
    /// **排序为什么分 Provider**：
    /// EF 的 SQLite provider **拒绝翻译 DateTimeOffset 的 ORDER BY**，直接抛
    /// <c>NotSupportedException: SQLite does not support expressions of type
    /// 'DateTimeOffset' in ORDER BY clauses</c> —— 查询直接失败，不是降级。
    /// （MockBank 的 <c>BankDbContext</c> 当初踩的是同一个坑，见
    /// <c>docs/13-database/02-multi-bank-databases.md</c> §6.2。）
    ///
    /// 两种 Provider 分别处理：
    /// - **SQLite（开发环境）**：范围过滤留在 SQL 里（能翻译），**排序在内存里做**。
    ///   这样**不改存储格式**，存量审计库照常可读 —— 改格式会让旧行的时间偏移量
    ///   解析出错，而审计记录不允许丢。
    ///   代价：会取回全部匹配行。开发环境数据量可控；生产走 PostgreSQL，不受此限。
    /// - **PostgreSQL（生产环境）**：`timestamptz` 原生支持该排序，保持库内排序。
    ///
    /// 时间区间的**过滤**（<c>&gt;=</c> / <c>&lt;=</c>）两种 Provider 都能翻译，
    /// 且审计时间戳一律为 UTC（+00:00），文本比较与时间比较等价。
    /// </summary>
    /// <param name="actorHash">操作者哈希；为空则不过滤。</param>
    /// <param name="scenario">场景；为空则不过滤。</param>
    /// <param name="from">起始时间（含）；为空则不限。</param>
    /// <param name="to">结束时间（含）；为空则不限。</param>
    /// <param name="limit">返回条数上限，最多 1000。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>按时间倒序的审计事件列表。</returns>
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

        var take = Math.Min(limit, 1000);

        if (db.Database.IsSqlite())
        {
            var all = await query.ToListAsync(ct);
            return all.OrderByDescending(r => r.Timestamp).Take(take).ToList();
        }

        return await query
            .OrderByDescending(e => e.Timestamp)
            .Take(take)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<ChainVerification> VerifyChainAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var filtered = db.AuditEvents
            .AsNoTracking()
            .Where(e => e.Timestamp >= from && e.Timestamp <= to);

        List<AuditEventEntity> events;
        if (db.Database.IsSqlite())
        {
            // 同 QueryAsync：SQLite 不支持 DateTimeOffset 的 ORDER BY，排序放内存。
            var all = await filtered.ToListAsync(ct);
            events = all.OrderBy(e => e.Timestamp).ThenBy(e => e.Id).ToList();
        }
        else
        {
            events = await filtered
                .OrderBy(e => e.Timestamp)
                .ThenBy(e => e.Id)
                .ToListAsync(ct);
        }

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
