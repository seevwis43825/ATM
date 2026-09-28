// ===== 容器装配健全性测试 =====
//
// 为什么需要这个测试：
//   大多数 DI 问题（生命周期错配、缺少注册、构造函数解析不到）不会导致编译失败，
//   只在**宿主启动**时暴露。而"启动即崩"是最贵的故障 ——
//   本地可能因为某个分支没跑到而没发现，CI 的集成作业才发现。
//
//   这里直接按宿主的装配方式构建一个容器并强制校验，
//   把这类问题前移到单元测试阶段。
//
// 实证：把 IAuditRepository 从 Scoped 改成 Singleton 之前，
//   IAuditLogger（单例）依赖它（Scoped）会让宿主启动直接抛
//   "Cannot consume scoped service from singleton"。
//   本测试正是为了锁死这一类回归。

using BankingAgent.Base;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace UnitTests;

public class ContainerWiringTests
{
    /// <summary>
    /// 按宿主的装配顺序构建容器，开启 ValidateScopes + ValidateOnBuild。
    /// 这两项是"启动即校验"，能一次性抓出生命周期错配与无法解析的构造函数。
    /// </summary>
    private static ServiceProvider BuildValidatedProvider()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                // 用临时文件库，避免污染宿主的 bankingagent.db
                ["Database:ConnectionString"] =
                    $"Data Source={Path.Combine(Path.GetTempPath(), $"wiring-{Guid.NewGuid():N}.db")}",
                ["Database:InitMode"] = "EnsureCreated",
                ["Crypto:DataEncryptionKey"] =
                    Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                ["Crypto:KeyId"] = "test-k1",
                ["Jwt:SigningKey"] = new string('k', 48),
                ["Audit:SigningKey"] = new string('a', 48),
                ["Audit:FilePath"] = "",
                ["CoreBank:BaseUrl"] = "http://localhost:5200",
                ["RateLimit:Enabled"] = "false"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        // SecurityGate 直接依赖 IConfiguration；WebApplicationBuilder 会自动注册它，
        // 手工装配的容器必须显式加上，否则 ValidateOnBuild 会报无法解析。
        services.AddSingleton<IConfiguration>(config);
        services.AddBankingCore(config);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }

    [Fact]
    public void Container_BuildsWithValidation()
    {
        // 构造过程本身即断言：任何生命周期错配或缺失注册都会在这里抛出
        using var provider = BuildValidatedProvider();
        Assert.NotNull(provider);
    }

    [Fact]
    public void Container_ResolvesAllSingletonServices()
    {
        using var provider = BuildValidatedProvider();

        // 这些是宿主启动后会立即用到的单例，任何一个解析失败都等于启动失败
        Assert.NotNull(provider.GetRequiredService<BankingAgent.Base.Security.Audit.IAuditLogger>());
        Assert.NotNull(provider.GetRequiredService<BankingAgent.Base.Security.Auth.ITokenService>());
        Assert.NotNull(provider.GetRequiredService<BankingAgent.Base.Security.RateLimit.RateLimiter>());
        Assert.NotNull(provider.GetRequiredService<BankingAgent.Base.Cryptography.ICryptoService>());
        Assert.NotNull(provider.GetRequiredService<BankingAgent.Base.Data.DatabaseInitializer>());
        Assert.NotNull(provider.GetRequiredService<
            Microsoft.EntityFrameworkCore.IDbContextFactory<BankingAgent.Base.Data.BankingDbContext>>());
    }

    [Fact]
    public void Container_AuditRepositoryIsSingletonToMatchAuditLogger()
    {
        using var provider = BuildValidatedProvider();

        var a = provider.GetRequiredService<BankingAgent.Base.Data.IAuditRepository>();
        var b = provider.GetRequiredService<BankingAgent.Base.Data.IAuditRepository>();

        // IAuditLogger 是单例且依赖 IAuditRepository，
        // 因此仓储必须也是单例，否则容器启动校验直接失败
        Assert.Same(a, b);
    }

    [Fact]
    public void Container_EncryptionIsActive()
    {
        // 加密服务必须真的被装配进来：字段级加密链路依赖它，
        // 一旦退化为 null，敏感字段会静默明文落盘
        using var provider = BuildValidatedProvider();
        var crypto = provider.GetRequiredService<BankingAgent.Base.Cryptography.ICryptoService>();

        var payload = crypto.Encrypt("sensitive-value"u8.ToArray());
        var back = System.Text.Encoding.UTF8.GetString(crypto.Decrypt(payload));

        Assert.Equal("sensitive-value", back);
    }
}