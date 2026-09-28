// ===== 理财 插件入口 =====
//
// 本文件由 `dotnet new banking-plugin` 生成。
// 职责边界（不要越界）：
//   * 这里只做「声明 + 依赖注入注册」，不写业务逻辑
//   * 业务逻辑放在 Agent 里
//   * 需要落库时实现 IEntitySetContributor（见 Persistence.cs）

using BankingAgent.Base.Agents;
using BankingAgent.Base.Security.Audit;
using BankingAgent.PluginSdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Plugin.Wealth;

/// <summary>理财 插件入口。宿主扫描程序集时首先发现本类。</summary>
public sealed class 理财PluginEntryPoint : IPluginEntryPoint
{
    /// <inheritdoc />
    public PluginManifest GetManifest() => new()
    {
        // 全局唯一，反向域名风格。改名会影响审计与路由，发布后不要随意变更。
        Id = new PluginId("banking.wealth"),
        Name = "理财",
        Description = "请补充一句话描述该插件提供的业务能力",
        Version = PluginVersion.Parse("1.0.0"),
        Author = "请填写负责人",
        Scenarios = ["wealth"],

        // 功能开关名。注意：当前版本仅作声明，运行时强制尚未实现，
        // 因此不要依赖它做安全控制（详见 docs/plugin/03-implementation-status.md）。
        FeatureFlags = ["wealth.enabled"],

        // 该插件处理的数据最高敏感级别，会参与合规校验。
        // L1 公开 / L2 内部 / L3 机密（账户、余额）/ L4 绝密（密码、CVV）
        MaxDataClassification = DataClassification.L2,

        // 需要依赖其他插件时声明（宿主会做拓扑排序与版本校验）。
        // 注意：只能通过事件通信，禁止直接引用其他插件程序集。
        Dependencies = []
    };

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        // Agent 无状态，注册为单例即可安全共享
        services.AddSingleton<IBankingAgent, 理财Agent>();

        // 需要独立数据分区时，把 Persistence.cs 里的贡献器注册进来：
        // services.AddSingleton<IEntitySetContributor, 理财PersistenceContributor>();

        context.Logger.LogInformation(
            "理财 插件服务注册完成，数据分区: {Partition}", context.PartitionName);
    }
}