// ===== 宿主环境名解析 =====
//
// 为什么需要它：
//   「当前是什么环境」这个判断，散落在三处 ——
//   ① 宿主的 builder.Environment（决定开发/生产行为）；
//   ② DI 里的审计签名密钥策略（决定缺密钥时放行还是拒绝启动）；
//   ③ SecurityGate 的生产判定（决定安全门能不能被关闭）。
//
//   三处各解析一遍，就可能出现「一处说生产、一处说开发」的**分裂** ——
//   而分裂的后果是安全门被短路、审计密钥被放行，两条都指向同一件事：
//   空密钥签名的审计链可以在生产环境里照跑。
//
//   所以这里给出**唯一**的解析规则，三处都用它。
//
// ⚠️ 优先级不能改：ASP.NET Core 宿主是 **DOTNET_ENVIRONMENT 优先**
//   （已实测：ASPNETCORE=Development + DOTNET=Production ⇒ 宿主取 Production）。
//   这里若写成 ASPNETCORE 优先，两个变量都设且不同时就会与宿主判定相反。

using Microsoft.Extensions.Configuration;

namespace BankingAgent.Base.Security.Hardening;

/// <summary>宿主环境名的统一解析。</summary>
public static class HostEnvironmentResolver
{
    /// <summary>未指定环境时的默认值。ASP.NET Core 自身的默认环境就是 Production（fail-closed）。</summary>
    public const string DefaultEnvironmentName = "Production";

    /// <summary>开发环境名。</summary>
    public const string DevelopmentEnvironmentName = "Development";

    /// <summary>
    /// 解析当前生效的环境名。
    ///
    /// 规则与 ASP.NET Core 宿主**完全一致**：
    /// <c>DOTNET_ENVIRONMENT</c> 优先于 <c>ASPNETCORE_ENVIRONMENT</c>，
    /// 两者都没设时为 <see cref="DefaultEnvironmentName"/>（Production）。
    ///
    /// 为什么**不**回退到配置里的 <c>SecurityGate:Environment</c>：
    /// 随仓库入库的 <c>appsettings.json</c> 里它是 <c>Development</c>（本地开发便利），
    /// 而宿主在没设环境变量时的真实环境是 <c>Production</c>。
    /// 若拿配置值兜底，就会出现「宿主认为是生产、安全门却以为是开发」——
    /// 正是本类要消灭的那种分裂，而且这次分裂的代价是安全门可被关闭。
    /// </summary>
    /// <param name="configuration">宿主配置（默认含环境变量源）。</param>
    /// <returns>生效的环境名。</returns>
    public static string Resolve(IConfiguration configuration) =>
        configuration["DOTNET_ENVIRONMENT"]
        ?? configuration["ASPNETCORE_ENVIRONMENT"]
        ?? DefaultEnvironmentName;

    /// <summary>当前是否是开发环境。</summary>
    /// <param name="configuration">宿主配置（默认含环境变量源）。</param>
    /// <returns>是否是 Development 环境。</returns>
    public static bool IsDevelopment(IConfiguration configuration) =>
        Resolve(configuration).Equals(DevelopmentEnvironmentName, StringComparison.OrdinalIgnoreCase);
}
