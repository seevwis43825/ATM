// ===== 审计链签名密钥的兜底与拒绝策略测试 =====
//
// 为什么需要这个测试：
//   基础配置 appsettings.json 里 Audit:SigningKey 是**空串**，而配置绑定会用空串
//   **覆盖**掉 AuditOptions 的默认值 —— 于是 HMACSHA256 拿到的是**零长度密钥**。
//
//   空密钥不是「没有密钥」，它是一个**公开可推导的密钥**：
//   审计链照样能生成、照样逐条自洽，但任何人都能伪造审计记录并重算链签名，
//   「不可篡改」这条合规承诺直接失效 —— 而且**全程不报任何错**。
//
//   这个缺陷在开发环境里完全看不出来（链照样生成、照样能验），
//   只有在生产配置下才会变成「合规承诺失效」，所以必须用测试锁死：
//     1. 非 Development 环境 + 空密钥 → 启动即失败（不给空密钥照跑留口子）；
//     2. Development 环境 + 空密钥 → 派生一个**确定性**密钥
//        （确定性很重要：重启后密钥不变，之前写入的链才能继续验）；
//     3. 显式配置了密钥 → 原样使用，不被派生逻辑覆盖。

using BankingAgent.Base;
using BankingAgent.Base.Security.Audit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;

namespace UnitTests;

public class AuditSigningKeyTests
{
    /// <summary>
    /// 按宿主的装配方式构建容器。
    /// </summary>
    /// <param name="environmentName">环境名（决定走派生还是拒绝）。</param>
    /// <param name="auditSigningKey">Audit:SigningKey 的配置值。</param>
    private static ServiceProvider BuildProvider(string environmentName, string auditSigningKey)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:ConnectionString"] =
                    $"Data Source={Path.Combine(Path.GetTempPath(), $"auditkey-{Guid.NewGuid():N}.db")}",
                ["Database:InitMode"] = "EnsureCreated",
                ["Crypto:DataEncryptionKey"] =
                    Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                ["Crypto:KeyId"] = "test-k1",
                ["Jwt:SigningKey"] = new string('k', 48),
                ["Audit:SigningKey"] = auditSigningKey,
                ["Audit:FilePath"] = "",
                ["CoreBank:BaseUrl"] = "http://localhost:5200",
                ["RateLimit:Enabled"] = "false",
                // 审计密钥的环境判定走配置（绑定阶段拿不到服务提供者），
                // 所以这里必须把环境名放进配置源。
                ["ASPNETCORE_ENVIRONMENT"] = environmentName
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        // 审计密钥的环境判定现在以**宿主环境**为准（与宿主行为同源），
        // 所以必须把同一个环境实例同时注册进容器并传给 AddBankingCore ——
        // 否则测的就不是生产实际走的那条路径（容器解析到的会是配置里的环境变量）。
        var environment = new StubHostEnvironment(environmentName);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddBankingCore(config, environment);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }

    [Fact]
    public void EmptySigningKey_OutsideDevelopment_FailsFast()
    {
        // 空密钥 = 公开可推导的密钥 = 审计链可被伪造。
        // 这必须在解析 IAuditLogger 时就炸，而不是等到有人去伪造审计记录。
        using var provider = BuildProvider("Production", string.Empty);

        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IAuditLogger>());

        Assert.Contains("Audit:SigningKey", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptySigningKey_Development_UsesDocumentedDefaultKey()
    {
        using var provider = BuildProvider("Development", string.Empty);

        var options = provider.GetRequiredService<AuditOptions>();

        // 关键：沿用的是**文档化的**开发默认密钥，而不是另派生一个。
        // 因为 scripts/read-audit-chain.ps1 的默认用法与既有归档都用这个密钥 ——
        // 换掉它就等于让所有历史归档离线重算不出来。
        Assert.Equal(AuditSigningKeyPolicy.DevelopmentDefaultKey, options.SigningKey);
        Assert.False(string.IsNullOrWhiteSpace(options.SigningKey),
            "开发环境也绝不能留成空串");
    }

    [Fact]
    public void EmptySigningKey_Development_ProducesUsableLogger()
    {
        using var provider = BuildProvider("Development", string.Empty);

        // 能真正解析出 IAuditLogger —— 说明 PostConfigure 兜底路径本身没把装配弄坏。
        var logger = provider.GetRequiredService<IAuditLogger>();
        Assert.NotNull(logger);
    }

    [Fact]
    public void ConfiguredSigningKey_IsNotOverwritten()
    {
        // 运维显式注入的密钥必须原样生效，不能被派生逻辑覆盖。
        const string configured = "an-operator-supplied-audit-signing-key-value";

        using var provider = BuildProvider("Development", configured);

        var options = provider.GetRequiredService<AuditOptions>();
        Assert.Equal(configured, options.SigningKey);
    }

    [Fact]
    public void HostEnvironment_WinsOverConfigEnvironmentVariable()
    {
        // 第四轮审查点出的「判定分裂」：ASP.NET Core 宿主是 DOTNET_ENVIRONMENT 优先
        // （已实测：ASPNETCORE=Development + DOTNET=Production ⇒ 宿主取 Production）。
        // 如果审计密钥策略自己再按配置解析一遍，两处就会给出相反的答案。
        // 现在策略直接用宿主环境 ⇒ 结构上不可能分裂。
        //
        // 这里构造「配置说开发、宿主说生产」：必须按**宿主**（生产）处理 ⇒ 拒绝启动。
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // 配置里写的是「开发」，但宿主环境是「生产」 —— 宿主说了算
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["DOTNET_ENVIRONMENT"] = "Production",
                ["Audit:SigningKey"] = "",
                ["RateLimit:Enabled"] = "false"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        var environment = new StubHostEnvironment("Production");
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddBankingCore(config, environment);

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IAuditLogger>());
        Assert.Contains("Audit:SigningKey", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>最小化的 IHostEnvironment 实现，只提供环境名与内容根。</summary>
    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "UnitTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new StubFileProvider();
    }

    /// <summary>
    /// 空内容根文件提供器。审计链路不会去读静态文件，
    /// 这里只是为了满足 <see cref="IHostEnvironment"/> 的契约。
    /// </summary>
    private sealed class StubFileProvider : IFileProvider
    {
        public IDirectoryContents GetDirectoryContents(string subpath) =>
            NotFoundDirectoryContents.Singleton;

        public IFileInfo GetFileInfo(string subpath) => new NotFoundFileInfo(subpath);

        public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
    }
}
