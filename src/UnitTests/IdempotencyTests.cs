// ===== 幂等性与插件版本单元测试 =====
// 幂等是资金安全的底线：重复请求不得重复扣款。

using BankingAgent.Base.Agents;
using BankingAgent.Base.Data;
using BankingAgent.PluginSdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace UnitTests;

public class IdempotencyTests
{
    [Fact]
    public void FirstRegistration_Succeeds()
    {
        var uow = CreateUow();
        Assert.True(uow.TryRegisterIdempotencyKey("session-001"));
    }

    [Fact]
    public void DuplicateRegistration_Fails()
    {
        var uow = CreateUow();
        Assert.True(uow.TryRegisterIdempotencyKey("session-001"));
        Assert.False(uow.TryRegisterIdempotencyKey("session-001"));
        Assert.False(uow.TryRegisterIdempotencyKey("session-001"));
    }

    [Fact]
    public void DifferentKeys_BothSucceed()
    {
        var uow = CreateUow();
        Assert.True(uow.TryRegisterIdempotencyKey("session-001"));
        Assert.True(uow.TryRegisterIdempotencyKey("session-002"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankKey_AlwaysSucceeds(string? key)
    {
        // 空幂等键应放行（由调用方保证生成），而不是让所有请求互相阻塞
        var uow = CreateUow();
        Assert.True(uow.TryRegisterIdempotencyKey(key!));
        Assert.True(uow.TryRegisterIdempotencyKey(key!));
    }

    [Fact]
    public void ConcurrentRegistration_ExactlyOneSucceeds()
    {
        var uow = CreateUow();
        var successes = 0;

        Parallel.For(0, 100, _ =>
        {
            if (uow.TryRegisterIdempotencyKey("race-key")) Interlocked.Increment(ref successes);
        });

        Assert.Equal(1, successes);
    }

    private static UnitOfWork CreateUow()
    {
        // 注意：BankingDbContext 有两个构造函数（多参与最小参），
        // EF 内置工厂无法选择，因此这里直接复用生产使用的 SimpleDbContextFactory。
        var services = new ServiceCollection();
        services.AddSingleton(new DateTimeOffsetProvider());
        services.AddSingleton<ICurrentUserAccessor>(new CurrentUserAccessor());
        services.AddSingleton(sp =>
        {
            var ob = new DbContextOptionsBuilder<BankingDbContext>();
            ob.UseSqlite("Data Source=:memory:");
            return ob.Options;
        });
        services.AddSingleton<IDbContextFactory<BankingDbContext>>(sp =>
            new SimpleDbContextFactory(
                sp,
                sp.GetRequiredService<DbContextOptions<BankingDbContext>>()));

        var sp2 = services.BuildServiceProvider();
        return new UnitOfWork(
            sp2.GetRequiredService<IDbContextFactory<BankingDbContext>>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UnitOfWork>.Instance);
    }
}

public class PluginVersionTests
{
    [Theory]
    [InlineData("1.0.0", 1, 0, 0)]
    [InlineData("2.5.3", 2, 5, 3)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("3", 3, 0, 0)]
    public void Parse_CorrectComponents(string text, int major, int minor, int patch)
    {
        var v = PluginVersion.Parse(text);
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Patch);
    }

    [Fact]
    public void Parse_Prerelease()
    {
        var v = PluginVersion.Parse("1.0.0-beta.1");
        Assert.Equal("beta.1", v.Prerelease);
    }

    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.1.0", "1.0.0", true)]
    [InlineData("2.0.0", "1.0.0", false)]  // 主版本不同 → 不兼容
    [InlineData("1.0.0", "1.0.0", true)]   // 相同版本 → 兼容
    [InlineData("0.9.0", "1.0.0", false)]  // 低于最低要求 → 不兼容
    public void IsCompatible_RespectsSemVer(string actual, string minimum, bool expected)
    {
        var a = PluginVersion.Parse(actual);
        var m = PluginVersion.Parse(minimum);
        Assert.Equal(expected, a.IsCompatible(m));
    }

    [Fact]
    public void ToString_RoundTrips()
    {
        Assert.Equal("1.2.3", PluginVersion.Parse("1.2.3").ToString());
    }
}

public class AgentRouterTests
{
    private sealed class FakeAgent(string id, params string[] intents) : IBankingAgent
    {
        public AgentId Id => new(id);
        public string Name => id;
        public AgentRole Role => AgentRole.DomainExpert;
        public IReadOnlyList<string> SupportedIntents => intents;
        public Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct = default)
            => Task.FromResult(AgentResult.Ok());
    }

    private static AgentRouter CreateRouter() => new(
        new IBankingAgent[]
        {
            new FakeAgent("transfer", "transfer"),
            new FakeAgent("transfer.exec", "transfer.execute"),
            new FakeAgent("bill", "bill")
        },
        Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentRouter>.Instance);

    [Fact]
    public void Resolve_ExactMatch()
    {
        var router = CreateRouter();
        Assert.Equal("bill", router.Resolve("bill")?.Id.Value);
    }

    [Fact]
    public void Resolve_LongestPrefixWins()
    {
        // 关键：transfer.execute 必须路由到更具体的 Agent，而不是笼统的 transfer
        var router = CreateRouter();
        Assert.Equal("transfer.exec", router.Resolve("transfer.execute")?.Id.Value);
    }

    [Fact]
    public void Resolve_UnknownIntent_ReturnsNull()
    {
        var router = CreateRouter();
        Assert.Null(router.Resolve("wealth.recommend"));
    }

    [Fact]
    public void Resolve_NullOrEmpty_ReturnsNull()
    {
        var router = CreateRouter();
        Assert.Null(router.Resolve(null));
        Assert.Null(router.Resolve(""));
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        var router = CreateRouter();
        Assert.NotNull(router.Resolve("BILL"));
    }

    [Fact]
    public async Task RouteAsync_NoAgent_ReturnsFailWithHandoff()
    {
        var router = CreateRouter();
        var result = await router.RouteAsync(new AgentRequest
        {
            UserInput = "无法识别的请求",
            UserId = "u_1",
            Upstream = AgentResult.Ok(intent: "wealth.recommend")
        });

        Assert.False(result.Success);
        Assert.Equal("NO_AGENT", result.ErrorCode);
    }
}
