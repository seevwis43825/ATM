// ===== 插件契约定义 =====
// 这是插件开发者唯一需要引用的程序集。定义插件的身份、生命周期、依赖声明与能力暴露。

namespace BankingAgent.PluginSdk;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>插件唯一标识。采用反向域名风格，全局唯一且不可变。</summary>
public sealed record PluginId(string Value)
{
    public override string ToString() => Value;

    public static implicit operator string(PluginId id) => id.Value;
}

/// <summary>插件语义化版本号。</summary>
public sealed record PluginVersion(int Major, int Minor, int Patch, string? Prerelease = null)
{
    public static PluginVersion Parse(string text)
    {
        var core = text.Split('-')[0];
        var parts = core.Split('.');
        int Get(int i) => i < parts.Length && int.TryParse(parts[i], out var v) ? v : 0;
        return new PluginVersion(Get(0), Get(1), Get(2), text.Contains('-') ? text.Split('-', 2)[1] : null);
    }

    /// <summary>判断当前版本是否满足最低版本要求（主版本必须相同）。</summary>
    public bool IsCompatible(PluginVersion minimum) =>
        Major == minimum.Major &&
        (Minor > minimum.Minor || (Minor == minimum.Minor && (Patch > minimum.Patch ||
            (Patch == minimum.Patch && Prerelease == minimum.Prerelease))));

    public override string ToString() =>
        Prerelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";
}

/// <summary>插件依赖声明。用于加载顺序拓扑排序与缺失检测。</summary>
public sealed record PluginDependency(PluginId PluginId, PluginVersion MinimumVersion, bool IsOptional = false);

/// <summary>插件声明的静态元数据。仅能表达，不可变。</summary>
public sealed record PluginManifest
{
    public required PluginId Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required PluginVersion Version { get; init; }
    public string Author { get; init; } = "unknown";
    public IReadOnlyList<PluginDependency> Dependencies { get; init; } = [];
    /// <summary>受该插件影响的业务场景标识，如 transfer / bill / card。</summary>
    public IReadOnlyList<string> Scenarios { get; init; } = [];
    /// <summary>插件自己提供的功能开关名。新功能默认关闭，由运维显式开启。</summary>
    public IReadOnlyList<string> FeatureFlags { get; init; } = [];
    /// <summary>该插件处理的数据最高敏感级别，供合规校验使用。</summary>
    public DataClassification MaxDataClassification { get; init; } = DataClassification.L2;
}

/// <summary>数据敏感级别。与 docs/05-security-compliance/03-data-classification.md 对齐。</summary>
public enum DataClassification
{
    /// <summary>公开：可对外披露。</summary>
    L1 = 1,
    /// <summary>内部：仅限团队内部。</summary>
    L2 = 2,
    /// <summary>机密：需严格访问控制，如身份证、账户余额。</summary>
    L3 = 3,
    /// <summary>绝密：需加密存储 + 审批访问，如密码、CVV。</summary>
    L4 = 4
}

/// <summary>插件运行期上下文，由宿主在加载时注入。插件只读使用。</summary>
public interface IPluginContext
{
    PluginManifest Manifest { get; }
    IServiceProvider Services { get; }
    /// <summary>插件的独立数据分区名（对应数据库子 Schema），插件只能写自己的分区。</summary>
    string PartitionName { get; }
    ILogger Logger { get; }
}

/// <summary>插件生命周期钩子。实现类由插件通过 DI 注册，宿主在加载时调用。</summary>
public interface IPlugin
{
    /// <summary>插件启动。所有依赖已就绪。用于注册订阅、启动后台任务。</summary>
    Task OnStartingAsync(IPluginContext context, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>插件停止。用于释放资源、取消订阅。</summary>
    Task OnStoppingAsync(IPluginContext cancellation, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>插件入口标记接口。每个插件程序集必须且只能实现一个。</summary>
public interface IPluginEntryPoint
{
    /// <summary>返回插件静态元数据。宿主据此建立索引与依赖图。</summary>
    PluginManifest GetManifest();
    /// <summary>在宿主 DI 容器中注册插件自己的服务。仅在加载期调用一次。</summary>
    void ConfigureServices(IServiceCollection services, IPluginContext context);
}
