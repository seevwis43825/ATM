// ===== 轻量 DbContext 工厂 =====
// 为什么不直接用 EF Core 的 DbContextFactory：
// 1. EF 内置工厂要求 DbContext 只有一个接受 DbContextOptions 的构造函数；
// 2. 本项目的 DbContext 需要注入插件贡献器集合、当前用户与时钟。
// 因此这里手写一个工厂，把这些依赖通过静态注册表与容器回填，
// 既保留 EF 的标准用法（IDbContextFactory），又不牺牲架构完整性。

namespace BankingAgent.Base.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

/// <summary>DbContext 工厂。生产环境可替换为池化实现以提升性能。</summary>
public sealed class SimpleDbContextFactory(
    IServiceProvider rootProvider,
    DbContextOptions<BankingDbContext> options) : IDbContextFactory<BankingDbContext>
{
    /// <inheritdoc />
    public BankingDbContext CreateDbContext()
    {
        var contributors = rootProvider.GetServices<IEntitySetContributor>();
        PluginContributorRegistry.Register(contributors);

        return new BankingDbContext(options, contributors,
            rootProvider.GetRequiredService<ICurrentUserAccessor>(),
            rootProvider.GetRequiredService<DateTimeOffsetProvider>());
    }

    /// <inheritdoc />
    public Task<BankingDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(CreateDbContext());
    }
}
