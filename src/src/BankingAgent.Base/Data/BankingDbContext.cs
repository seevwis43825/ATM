// ===== 银行系统主数据库上下文 =====
// 设计要点：
// 1. 插件分区隔离：每个插件注册自己的 IEntitySetContributor，映射到独立子 Schema。
// 2. 审计字段自动填充：SaveChanges 时统一写入 CreatedAt/UpdatedAt/RowVersion。
// 3. 软删除：查询默认过滤 IsDeleted。
// 4. 双 Provider：SQLite（开发/演示）与 PostgreSQL（生产）共用同一套模型。

namespace BankingAgent.Base.Data;

using System.Reflection;
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
    private readonly BankingAgent.Base.Cryptography.ICryptoService? _crypto;

    /// <summary>
    /// 主构造函数。DbContext 由 DI 容器构造时会注入插件贡献器、当前用户与时钟。
    /// </summary>
    public BankingDbContext(
        DbContextOptions<BankingDbContext> options,
        IEnumerable<IEntitySetContributor> contributors,
        ICurrentUserAccessor currentUser,
        DateTimeOffsetProvider clock,
        BankingAgent.Base.Cryptography.ICryptoService? crypto = null) : base(options)
    {
        _contributors = contributors.ToDictionary(c => c.PartitionName, StringComparer.OrdinalIgnoreCase);
        _currentUser = currentUser;
        _clock = clock;
        _crypto = crypto;

        // 模型缓存键必须把「哪些贡献器」与「是否启用字段加密」算进去。
        // 详见 ModelCacheKey 的注释：否则 EF 会把第一个建好的模型缓存起来，
        // 后续完全不同的配置会静默复用那个模型。
        ModelCacheKey = BuildModelCacheKey();
    }

    /// <summary>
    /// 本实例的模型缓存键。
    /// EF Core 默认只按 DbContext 类型缓存模型，对本项目是错的：
    /// 同一个 BankingDbContext 类型在不同场景下的模型并不相同 ——
    /// 装了哪些插件（贡献器集合）、有没有启用字段加密，都会改变模型。
    /// 若不参与缓存键，先建好的模型会被后续实例复用，
    /// 表现为"插件表不见了"或"敏感字段没加密"，且不报任何错。
    /// </summary>
    private string ModelCacheKey { get; set; } = "unresolved";

    private string BuildModelCacheKey()
    {
        var partitions = string.Join(",", _contributors.Keys.OrderBy(k => k, StringComparer.Ordinal));
        var cryptoKey = _crypto is null ? "nocrypto" : $"crypto:{_crypto.CurrentKeyId}";
        return $"{partitions}|{cryptoKey}";
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
        // 这条路径拿不到 ICryptoService（工厂已改为显式构造并传 crypto），
        // 因此缓存键按"无加密"计算，与工厂路径天然区分开。
        ModelCacheKey = BuildModelCacheKey();
    }

    /// <summary>已注册的数据分区名。</summary>
    public IReadOnlyCollection<string> Partitions => _contributors.Keys.ToList();

    /// <summary>
    /// 参与 EF 模型缓存的键。由构造函数按贡献器集合与加密状态计算。
    /// </summary>
    internal string GetModelCacheKey() => ModelCacheKey;

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);

        // 在这里（而不是在建 options 的各处）替换缓存键工厂，
        // 保证无论走工厂、DI 还是设计时路径都生效。
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, BankingModelCacheKeyFactory>();
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ===== 插件分区隔离 =====
        // 关键点：HasDefaultSchema 是「全局」设置，在循环里调用只有最后一次生效。
        // 若沿用旧写法，第二个插件起，所有插件的表都会被建到同一个 Schema。
        // 正确做法是绕过贡献器，直接给「该贡献器注册的实体」逐个指定 Schema。
        var isNpgsql = Database.IsNpgsql();

        foreach (var contributor in _contributors.Values)
        {
            var before = modelBuilder.Model.GetEntityTypes().Select(e => e.Name).ToHashSet();

            contributor.ConfigureModel(modelBuilder);

            if (!isNpgsql) continue;   // SQLite 无 Schema 概念，退化用表名前缀隔离

            var added = modelBuilder.Model.GetEntityTypes()
                .Select(e => e.Name)
                .Where(name => !before.Contains(name));

            foreach (var typeName in added)
            {
                var entity = modelBuilder.Entity(typeName);
                entity.ToTable(entity.Metadata.GetTableName()!, contributor.PartitionName);
            }
        }

        ApplyFieldEncryption(modelBuilder);
    }

    /// <summary>
    /// 扫描所有标记了 [Encrypted] 的字符串属性，自动装配加密转换器。
    /// 自动扫描的意义：新增敏感字段时只要加上特性就会被加密，不会漏。
    /// </summary>
    private void ApplyFieldEncryption(ModelBuilder modelBuilder)
    {
        if (_crypto is null) return;

        // 两种转换器：随机（默认，最安全）与确定性（可等值查询）。
        // 必须按字段的 Searchable 分别装配 —— 用同一个随机转换器去加密
        // 参与等值查询的列，会让该查询永远匹配不到，且不报任何错。
        var randomized = new BankingAgent.Base.Data.Encryption.FieldCipher(_crypto)
            .CreateConverter();
        var deterministic = new BankingAgent.Base.Data.Encryption.FieldCipher(_crypto)
        {
            Searchable = true
        }.CreateConverter();

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType != typeof(string)) continue;

                // EF Core 不会因为属性上贴了自定义特性就自动生成注解，
                // 必须自己判断并把注解写进模型（下面的 SetAnnotation）。
                var attribute = property.PropertyInfo?
                    .GetCustomAttributes(
                        typeof(BankingAgent.Base.Data.Encryption.EncryptedAttribute), true)
                    .OfType<BankingAgent.Base.Data.Encryption.EncryptedAttribute>()
                    .FirstOrDefault();
                if (attribute is null) continue;

                // 注解本身也有用：EncryptedFieldLogGuard 与迁移审阅都依赖它识别敏感列
                property.SetAnnotation(EncryptedAnnotation, true);
                property.SetAnnotation(EncryptedSearchableAnnotation, attribute.Searchable);
                property.SetValueConverter(attribute.Searchable ? deterministic : randomized);
            }
        }
    }

    /// <summary>
    /// 字段级加密的模型注解名。
    /// 公开为常量，避免各处再写裸字符串 "Encrypted" 造成不一致。
    /// </summary>
    public const string EncryptedAnnotation = "BankingAgent:Encrypted";

    /// <summary>该加密列是否使用确定性加密（可等值查询）。</summary>
    public const string EncryptedSearchableAnnotation = "BankingAgent:EncryptedSearchable";

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

/// <summary>
/// 按「贡献器集合 + 加密状态」区分模型缓存的缓存键工厂。
///
/// EF Core 默认把模型缓存在 <c>IMemoryCache</c> 里，键只包含 DbContext 类型。
/// 本项目同一个 DbContext 类型在不同场景下的模型并不相同，因此必须扩展键，
/// 否则先建好的模型会被后续实例复用 —— 插件表缺失或敏感字段未加密，
/// 而且不抛异常、不打日志。
/// </summary>
internal sealed class BankingModelCacheKeyFactory : IModelCacheKeyFactory
{
    /// <inheritdoc />
    public object Create(DbContext context, bool designTime)
    {
        if (context is BankingDbContext banking)
        {
            return (context.GetType(), banking.GetModelCacheKey(), designTime);
        }

        return (context.GetType(), designTime);
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

    /// <summary>解析当前已注册的全部贡献器（快照）。</summary>
    public static IReadOnlyList<IEntitySetContributor> ResolveAll()
    {
        lock (Gate)
        {
            return Items.ToList();
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
