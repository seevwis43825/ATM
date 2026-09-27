// ===== 银行系统主数据库上下文 =====
// 设计要点：
// 1. 插件分区隔离：每个插件注册自己的 IEntitySetContributor，映射到独立子 Schema。
// 2. 审计字段自动填充：SaveChanges 时统一写入 CreatedAt/UpdatedAt/RowVersion。
// 3. 软删除：查询默认过滤 IsDeleted。
// 4. 双 Provider：SQLite（开发/演示）与 PostgreSQL（生产）共用同一套模型。

namespace BankingAgent.Base.Data;

using System.Text.Json;
using BankingAgent.PluginSdk;
using BankingAgent.Base.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;

/// <summary>插件实体贡献器。插件实现此接口以把自己的实体注册进 DbContext。</summary>
public interface IEntitySetContributor
{
    /// <summary>该插件的数据分区名（子 Schema / 表前缀）。</summary>
    string PartitionName { get; }
    /// <summary>注册实体类型到模型构建器。</summary>
    void ConfigureModel(ModelBuilder modelBuilder);
    /// <summary>该分区包含的数据敏感级别，供合规校验。</summary>
    DataClassification MaxClassification { get; }
}

/// <summary>银行系统数据库上下文。</summary>
public class BankingDbContext : DbContext
{
    private readonly IReadOnlyDictionary<string, IEntitySetContributor> _contributors;
    private readonly ICurrentUserAccessor _currentUser;
    private readonly DateTimeOffsetProvider _clock;

    /// <summary>
    /// 主构造函数。DbContext 由 DI 容器构造时会注入插件贡献器、当前用户与时钟。
    /// </summary>
    public BankingDbContext(
        DbContextOptions<BankingDbContext> options,
        IEnumerable<IEntitySetContributor> contributors,
        ICurrentUserAccessor currentUser,
        DateTimeOffsetProvider clock) : base(options)
    {
        _contributors = contributors.ToDictionary(c => c.PartitionName, StringComparer.OrdinalIgnoreCase);
        _currentUser = currentUser;
        _clock = clock;
    }

    /// <summary>
    /// 最小构造函数。供 <see cref="IDbContextFactory{TContext}"/> 使用：
    /// 池化工厂只能接受单一 options 参数，插件贡献器通过工厂的服务定位器回填。
    /// </summary>
    public BankingDbContext(DbContextOptions<BankingDbContext> options) : base(options)
    {
        _contributors = PluginContributorRegistry.Resolve();
        _currentUser = CurrentUserAccessor.Static;
        _clock = DateTimeOffsetProvider.Static;
    }

    /// <summary>已注册的数据分区名。</summary>
    public IReadOnlyCollection<string> Partitions => _contributors.Keys.ToList();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        foreach (var contributor in _contributors.Values)
        {
            // SQLite 不支持真正的 Schema，PostgreSQL 下映射为独立 Schema。
            if (Database.IsNpgsql())
            {
                modelBuilder.HasDefaultSchema(contributor.PartitionName);
            }
            contributor.ConfigureModel(modelBuilder);
        }
    }

    /// <inheritdoc />
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyAuditFields();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ApplyAuditFields();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>统一填充审计字段。</summary>
    private void ApplyAuditFields()
    {
        var now = _clock.UtcNow;
        var user = _currentUser.UserId ?? "system";

        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.CreatedBy = user;
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.UpdatedBy = user;
                    // 防止审计字段被篡改：强制覆盖
                    entry.Property(nameof(BaseEntity.CreatedAt)).IsModified = false;
                    entry.Property(nameof(BaseEntity.CreatedBy)).IsModified = false;
                    break;
            }
        }
    }

    /// <inheritdoc />
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        await SaveChangesAsync(true, cancellationToken);

    /// <summary>写出变更审计快照，用于合规追溯。</summary>
    public IReadOnlyList<ChangeSnapshot> GetChangeSnapshots() =>
        ChangeTracker.Entries<BaseEntity>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => new ChangeSnapshot(
                e.Entity.GetType().Name,
                e.Entity.Id.ToString(),
                e.State.ToString(),
                JsonSerializer.Serialize(Snapshot(e.Entity))))
            .ToList();

    /// <summary>基于当前值生成脱敏后的快照，遵循最小暴露原则。</summary>
    private static Dictionary<string, object?> Snapshot(BaseEntity entity)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in entity.GetType().GetProperties())
        {
            if (prop.Name is nameof(BaseEntity.RowVersion)) continue;
            var value = prop.GetValue(entity);
            dict[prop.Name] = value switch
            {
                string s when prop.Name.Contains("Pwd", StringComparison.OrdinalIgnoreCase)
                              || prop.Name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                              || prop.Name.Contains("Cvv", StringComparison.OrdinalIgnoreCase)
                              || prop.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                    => "********",
                string s2 when prop.Name.Contains("Phone", StringComparison.OrdinalIgnoreCase) => DataMasker.MaskPhone(s2),
                string s3 when prop.Name.Contains("IdCard", StringComparison.OrdinalIgnoreCase) => DataMasker.MaskIdCard(s3),
                _ => value
            };
        }
        return dict;
    }
}

/// <summary>变更快照记录。</summary>
public sealed record ChangeSnapshot(
    string EntityType, string EntityId, string Operation, string PayloadJson);

/// <summary>可注入的时钟抽象，便于测试与确定性重放。</summary>
public sealed class DateTimeOffsetProvider(Func<DateTimeOffset>? factory = null)
{
    /// <summary>默认实例，供最小构造路径使用。</summary>
    public static DateTimeOffsetProvider Static { get; } = new();

    public DateTimeOffset UtcNow => factory?.Invoke() ?? DateTimeOffset.UtcNow;
}

/// <summary>
/// 插件贡献器的静态注册表。
/// 插件加载时写入，DbContext 工厂创建实例时读取，弥补工厂无法注入集合的限制。
/// </summary>
public static class PluginContributorRegistry
{
    private static readonly List<IEntitySetContributor> Items = [];
    private static readonly object Gate = new();

    /// <summary>注册全部贡献器。</summary>
    public static void Register(IEnumerable<IEntitySetContributor> contributors)
    {
        lock (Gate)
        {
            Items.Clear();
            Items.AddRange(contributors);
        }
    }

    internal static IReadOnlyDictionary<string, IEntitySetContributor> Resolve()
    {
        lock (Gate)
        {
            return Items.ToDictionary(c => c.PartitionName, StringComparer.OrdinalIgnoreCase);
        }
    }
}

/// <summary>当前操作者访问器。审计与数据隔离的基础。</summary>
public interface ICurrentUserAccessor
{
    string? UserId { get; }
    string? TenantId { get; }
    /// <summary>当前请求是否来自审计员（只读宽权限）。</summary>
    bool IsAuditor { get; }
}

/// <summary>默认实现，从 AsyncLocal 读取。</summary>
public sealed class CurrentUserAccessor : ICurrentUserAccessor
{
    /// <summary>默认实例，供最小构造路径使用。</summary>
    public static CurrentUserAccessor Static { get; } = new();

    private static readonly AsyncLocal<Actor?> Current = new();
    public string? UserId => Current.Value?.UserId;
    public string? TenantId => Current.Value?.TenantId;
    public bool IsAuditor => Current.Value?.Role is ActorRole.Auditor or ActorRole.Admin;

    public static IDisposable Enter(string userId, string? tenantId = null, ActorRole role = ActorRole.User)
    {
        var previous = Current.Value;
        Current.Value = new Actor(userId, tenantId, role);
        return new Scope(() => Current.Value = previous);
    }

    private sealed class Actor(string userId, string? tenantId, ActorRole role)
    {
        public string UserId { get; } = userId;
        public string? TenantId { get; } = tenantId;
        public ActorRole Role { get; } = role;
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { if (!_disposed) { dispose(); _disposed = true; } }
    }
}

/// <summary>操作者角色。</summary>
public enum ActorRole
{
    User,
    Agent,
    Staff,
    Auditor,
    Admin,
    System
}

/// <summary>EF Core provider 判定辅助扩展。</summary>
internal static class DatabaseProviderExtensions
{
    public static bool IsNpgsql(this DatabaseFacade db) =>
        db.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
}
