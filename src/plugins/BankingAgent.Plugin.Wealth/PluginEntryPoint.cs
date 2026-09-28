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
        Description = "理财产品查询与推荐、账户余额查询（只读场景，认购属后续迭代）",
        Version = PluginVersion.Parse("1.0.0"),
        Author = "业务开发组",
        Scenarios = ["wealth"],

        // 功能开关名。注意：当前版本仅作声明，运行时强制尚未实现，
        // 因此不要依赖它做安全控制（详见 docs/plugin/03-implementation-status.md）。
        FeatureFlags = ["wealth.enabled"],

        // 该插件处理的数据最高敏感级别，会参与合规校验。
        // L1 公开 / L2 内部 / L3 机密（账户、余额）/ L4 绝密（密码、CVV）
        MaxDataClassification = DataClassification.L3,

        // 需要依赖其他插件时声明（宿主会做拓扑排序与版本校验）。
        // 注意：只能通过事件通信，禁止直接引用其他插件程序集。
        Dependencies = []
    };

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        // Agent 无状态，注册为单例即可安全共享
        services.AddSingleton<IBankingAgent, 理财Agent>();

        // 本插件是**只读场景**（产品查询 / 推荐 / 余额查询），不落自己的库，
        // 因此不注册 IEntitySetContributor：模型里不会出现用不到的空表，
        // 迁移门禁（dotnet ef migrations has-pending-model-changes）也能保持一致。
        //
        // 将来若确实需要落库（例如记录推荐历史），照 src/templates/banking-plugin/
        // 的 Persistence.cs 实现一个 IEntitySetContributor，**在这里注册**，
        // 并用 `dotnet ef migrations add <名称> --configuration Release` 补迁移。
        // 注意：实现了贡献器却忘记注册，属于"静默失效"——表不会被创建，也不报错
        // （见 docs/plugin/03-implementation-status.md 的「静默失效」清单）。

        context.Logger.LogInformation("理财插件服务注册完成（只读场景，无独立数据分区）");
    }
}