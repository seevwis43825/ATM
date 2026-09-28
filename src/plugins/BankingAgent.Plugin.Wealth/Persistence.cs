// ===== 理财 数据分区 =====
//
// 只有当插件需要自己落库时才用这个文件（模板参数 withPersistence=true）。
// 只读场景不要落库，直接走 ICoreBankClient 查核心系统即可。
//
// 注册方式（在 PluginEntryPoint.ConfigureServices 里）：
//   services.AddSingleton<IEntitySetContributor, 理财PersistenceContributor>();

using BankingAgent.Base.Data;
using BankingAgent.PluginSdk;
using Microsoft.EntityFrameworkCore;

namespace BankingAgent.Plugin.Wealth;

/// <summary>
/// 插件自己的持久化实体。
/// 继承 BaseEntity 即可获得审计字段（CreatedAt/By、UpdatedAt/By）、
/// 软删除（IsDeleted）与乐观并发（RowVersion）。
/// </summary>
public class 理财Record : BaseEntity
{
    /// <summary>所属用户。查询与索引的第一列，便于按用户隔离。</summary>
    public required string UserId { get; set; }

    /// <summary>业务键。需要幂等时用它做唯一索引。</summary>
    public string? BusinessKey { get; set; }

    /// <summary>状态。</summary>
    public string Status { get; set; } = "Pending";

    /// <summary>
    /// 敏感字段加密：标上 [Encrypted] 后，EF 会在读写时自动加解密。
    /// 前置条件是宿主注入了 ICryptoService（生产必须配置密钥）。
    /// </summary>
    [BankingAgent.Base.Data.Encryption.Encrypted]
    public string? SensitivePayload { get; set; }
}

/// <summary>
/// 数据分区贡献器。宿主据此把本插件的实体注册进 DbContext 并映射到独立 Schema。
/// </summary>
public sealed class 理财PersistenceContributor : IEntitySetContributor
{
    // 分区名即 PostgreSQL 下的 Schema 名。只用小写字母与下划线。
    /// <inheritdoc />
    public string PartitionName => "plugin_wealth";

    /// <inheritdoc />
    public DataClassification MaxClassification => DataClassification.L3;

    /// <inheritdoc />
    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<理财Record>(entity =>
        {
            entity.ToTable("wealth_records");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.RowVersion).IsConcurrencyToken();

            // 幂等键唯一索引。注意：可空唯一索引在 SQLite/PG 下允许多个 NULL 共存，
            // 因此真正需要幂等的字段建议加 IsRequired()。
            entity.HasIndex(e => e.BusinessKey).IsUnique();

            // 按用户 + 时间查询是审计与对账的主要路径
            entity.HasIndex(e => new { e.UserId, e.CreatedAt });
        });
    }
}