// ===== 插件可持久化实体基类 =====
// 插件在自己的数据分区内建表，继承此基类即可获得统一的审计字段与并发控制。

namespace BankingAgent.Base.Data;

using BankingAgent.PluginSdk;

/// <summary>所有可持久化实体的基类。提供审计字段与乐观并发令牌。</summary>
public abstract class BaseEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    /// <summary>软删除标记。金融审计场景禁止物理删除。</summary>
    public bool IsDeleted { get; set; }
    public string? DeletedAt { get; set; }
    /// <summary>乐观并发令牌，由 EF Core 自动维护。</summary>
    public uint RowVersion { get; set; }
}

/// <summary>带业务敏感级别标记的实体。Base 会据此自动施加脱敏与访问约束。</summary>
public interface ISensitiveEntity
{
    /// <summary>该实体包含数据的最高敏感级别。</summary>
    DataClassification MaxClassification { get; }
    /// <summary>需要脱敏展示的字段名。</summary>
    IReadOnlyCollection<string> MaskedFields { get; }
}
