// ===== 智能转账插件 =====
// 这是「如何接入插件」的完整参考实现，演示四件事：
// 1. 声明插件身份（TransferPluginEntryPoint）
// 2. 实现领域 Agent（TransferAgent）
// 3. 强制合规守卫 + 人工回环（资金操作不可绕过）
// 4. 发布领域事件解耦其他插件

using BankingAgent.Base.Agents;
using BankingAgent.Base.Data;
using BankingAgent.Base.Security.Audit;
using BankingAgent.Base.Security.Compliance;
using BankingAgent.PluginSdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Plugin.Transfer;

/// <summary>转账插件入口。宿主扫描程序集时首先发现本类。</summary>
public sealed class TransferPluginEntryPoint : IPluginEntryPoint
{
    /// <inheritdoc />
    public PluginManifest GetManifest() => new()
    {
        Id = new PluginId("banking.transfer"),
        Name = "智能转账",
        Description = "处理行内转账、跨行转账，含限额校验、反洗钱筛查与人工回环",
        Version = PluginVersion.Parse("1.0.0"),
        Author = "业务开发组",
        Scenarios = ["transfer"],
        FeatureFlags = ["transfer.enabled", "transfer.auto_confirm"],
        // 转账涉及余额与流水，敏感级别为 L3
        MaxDataClassification = DataClassification.L3
    };

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        // Agent 注册为单例：Agent 无状态，可安全共享
        services.AddSingleton<IBankingAgent, TransferAgent>();

        // 插件自己的数据分区贡献器
        services.AddSingleton<IEntitySetContributor, TransferPersistenceContributor>();

        // 转账意图识别器
        services.AddSingleton<ITransferIntentParser, KeywordTransferIntentParser>();

        context.Logger.LogInformation("转账插件服务注册完成，数据分区: {Partition}", context.PartitionName);
    }
}

/// <summary>转账领域实体。演示插件如何拥有自己的数据分区。</summary>
public class TransferRecord : BaseEntity
{
    public required string UserId { get; set; }
    public required string FromAccountNo { get; set; }
    public required string ToAccountNo { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "CNY";
    public string Status { get; set; } = "Pending";
    public string? TransactionNo { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? Remark { get; set; }
    public bool HumanApproved { get; set; }
}

/// <summary>转账插件的数据分区配置。</summary>
public sealed class TransferPersistenceContributor : IEntitySetContributor
{
    public string PartitionName => "plugin_transfer";

    public DataClassification MaxClassification => DataClassification.L3;

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TransferRecord>(entity =>
        {
            entity.ToTable("transfer_records");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.RowVersion).IsConcurrencyToken();
            entity.HasIndex(e => e.IdempotencyKey).IsUnique();
            entity.HasIndex(e => new { e.UserId, e.CreatedAt });
        });
    }
}

/// <summary>转账意图解析契约。</summary>
public interface ITransferIntentParser
{
    /// <summary>判断输入是否为转账意图。</summary>
    bool IsTransferIntent(string input);

    /// <summary>从自然语言中抽取金额。</summary>
    decimal? ExtractAmount(string input);

    /// <summary>从自然语言中抽取收款账户。</summary>
    string? ExtractTargetAccount(string input);
}

/// <summary>基于关键词与正则的意图解析器。生产环境可替换为 LLM 实现。</summary>
public sealed class KeywordTransferIntentParser : ITransferIntentParser
{
    private static readonly string[] Keywords =
        ["转账", "转帐", "汇款", "打钱", "付给", "转给", "汇给"];

    private static readonly System.Text.RegularExpressions.Regex AmountPattern =
        new(@"(?<amount>\d+(?:\.\d+)?)\s*(?:元|块|万元|万)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex AccountPattern =
        new(@"(?<account>\d{12,19})", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <inheritdoc />
    public bool IsTransferIntent(string input) =>
        Keywords.Any(k => input.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public decimal? ExtractAmount(string input)
    {
        var match = AmountPattern.Match(input);
        if (!match.Success) return null;

        var value = decimal.Parse(match.Groups["amount"].Value);
        // 处理「5 万元」这类表达
        if (input.Contains("万元") || input.Contains("万块"))
        {
            value *= 10000m;
        }
        return value;
    }

    /// <inheritdoc />
    public string? ExtractTargetAccount(string input)
    {
        var match = AccountPattern.Match(input);
        return match.Success ? match.Groups["account"].Value : null;
    }
}
