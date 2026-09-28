// ===== 智能转账插件 =====
// 这是「如何接入插件」的完整参考实现，演示四件事：
// 1. 声明插件身份（TransferPluginEntryPoint）
// 2. 实现领域 Agent（TransferAgent）
// 3. 强制合规守卫 + 人工回环（资金操作不可绕过）
// 4. 发布领域事件解耦其他插件

using BankingAgent.Base.Agents;
using BankingAgent.Base.Data;
using BankingAgent.Base.Data.Encryption;
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

/// <summary>
/// 转账领域实体。演示插件如何拥有自己的数据分区。
///
/// 字段级加密原则（见 docs/09-uml/04-data-model.md §7.4）：
/// L3 敏感字段落盘必须加密。是否可用 <c>Searchable = true</c> 取决于
/// 该列是否需要等值查询或唯一索引 —— 确定性加密会泄露"两值是否相等"，
/// 因此只对低价值字段开启。
/// </summary>
public class TransferRecord : BaseEntity
{
    /// <summary>
    /// 发起客户号（L3）。
    /// Searchable：列参与 (UserId, CreatedAt) 复合索引与按用户查询，
    /// 必须是确定性密文，否则索引失效。
    /// </summary>
    [Encrypted(Searchable = true)]
    public required string UserId { get; set; }

    /// <summary>付款账号（L3）。无等值查询，使用随机加密（最强）。</summary>
    [Encrypted]
    public required string FromAccountNo { get; set; }

    /// <summary>收款账号（L3）。无等值查询，使用随机加密。</summary>
    [Encrypted]
    public required string ToAccountNo { get; set; }

    public decimal Amount { get; set; }
    public string Currency { get; set; } = "CNY";
    public string Status { get; set; } = "Pending";
    public string? TransactionNo { get; set; }

    /// <summary>
    /// 幂等键（L3）。
    /// Searchable 是**必须**的：落库前要用它做等值查询判断是否重复提交
    /// （见 TransferAgent 的 FirstOrDefaultAsync），且该列有唯一索引。
    /// 若用随机加密，重复提交将查不到已有记录 —— 幂等保护直接失效。
    /// </summary>
    [Encrypted(Searchable = true)]
    public string? IdempotencyKey { get; set; }

    /// <summary>用户输入的自然语言备注（含个人信息的风险，L3）。随机加密。</summary>
    [Encrypted]
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

    /// <summary>从自然语言中抽取收款人姓名，用于按姓名解析收款账户。</summary>
    string? ExtractTargetName(string input);
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

    /// <summary>「给/转给/付给…」之后的 2-4 个汉字视为收款人姓名。</summary>
    private static readonly System.Text.RegularExpressions.Regex NamePattern =
        new(@"(?:转给|付给|汇给|打给|给)\s*(?<name>[\u4e00-\u9fa5]{2,4})",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 姓名可能被动词粘连（「给张三转500」会匹配到「张三转」），
    /// 这些字出现在末尾时属于动词而非姓名的一部分。
    /// </summary>
    private static readonly char[] NameTrailingVerbs =
        ['转', '汇', '付', '打', '账', '钱', '的', '块', '元'];

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

    /// <inheritdoc />
    public string? ExtractTargetName(string input)
    {
        var match = NamePattern.Match(input);
        if (!match.Success) return null;

        var name = match.Groups["name"].Value.TrimEnd(NameTrailingVerbs);
        return name.Length >= 2 ? name : null;
    }
}
