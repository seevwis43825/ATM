// ===== 字段级加密（EF Core Value Converter）=====
// 对应《个保法》第 51 条「采取相应的加密、去标识化等安全技术措施」。
//
// 设计要点：
// 1. 实体属性用 [Encrypted] 标注需加密的字段
// 2. 透明加密：业务代码无感知，EF 读写时自动加解密
// 3. 密文自描述：包含算法与密钥标识，密钥轮换后仍能解密历史数据
// 4. 幂等：已加密的值不会重复加密

using System.Text;
using System.Text.Json;
using BankingAgent.Base.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Base.Data.Encryption;

/// <summary>标记需要字段级加密的属性。</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EncryptedAttribute : Attribute
{
    /// <summary>
    /// 是否可用于等值查询（生成确定性密文）。
    /// 仅适用于低价值字段；高价值字段必须关闭，否则会形成彩虹表。
    /// </summary>
    public bool Searchable { get; init; }
}

/// <summary>字段加解密工具。供转换器与业务代码共用。</summary>
public sealed class FieldCipher(ICryptoService crypto)
{
    /// <summary>密文前缀，便于识别与调试。</summary>
    public const string Prefix = "enc:v1:";

    /// <summary>加密明文。已是密文则原样返回（幂等）。</summary>
    public string Encrypt(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value.StartsWith(Prefix, StringComparison.Ordinal)) return value;

        var payload = crypto.Encrypt(Encoding.UTF8.GetBytes(value));
        return Prefix + JsonSerializer.Serialize(payload);
    }

    /// <summary>解密。已是明文则原样返回（兼容迁移前数据）。</summary>
    public string Decrypt(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return value;

        var json = value[Prefix.Length..];
        var payload = JsonSerializer.Deserialize<EncryptedPayload>(json)
            ?? throw new InvalidOperationException(
                "加密字段格式损坏，可能是密钥不匹配或数据被篡改");

        return Encoding.UTF8.GetString(crypto.Decrypt(payload));
    }

    /// <summary>构造 EF Core 值转换器。</summary>
    public ValueConverter<string, string> CreateConverter() =>
        new(
            v => Encrypt(v),
            v => Decrypt(v));
}

/// <summary>需要字段加密的实体基类。</summary>
public abstract class EncryptedEntity : BaseEntity
{
    /// <summary>最后加密时间，便于审计与密钥轮换影响分析。</summary>
    public DateTimeOffset? LastEncryptedAt { get; set; }
}

/// <summary>EF Core 拦截器：审计加密字段变更（只记字段名，不记明文）。</summary>
public sealed class EncryptedFieldLogGuard(
    Microsoft.Extensions.Logging.ILogger<EncryptedFieldLogGuard> logger) : SaveChangesInterceptor
{
    /// <summary>保存前记录敏感字段变更。</summary>
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is null) return base.SavingChanges(eventData, result);

        foreach (var entry in eventData.Context.ChangeTracker.Entries<EncryptedEntity>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;

            var fields = entry.Properties
                .Where(p => p.Metadata.FindAnnotation(BankingDbContext.EncryptedAnnotation) is not null)
                .Select(p => p.Metadata.Name)
                .ToList();

            if (fields.Count == 0) continue;

            entry.Entity.LastEncryptedAt = DateTimeOffset.UtcNow;

            logger.LogInformation(
                "加密实体 {Entity} [{EntityId}] 敏感字段变更: {Fields}",
                entry.Metadata.ClrType.Name, entry.Entity.Id, string.Join(", ", fields));
        }

        return base.SavingChanges(eventData, result);
    }
}
