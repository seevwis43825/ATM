// ===== 数据库基础设施装配 =====
// 集中管理：多 Provider、连接池、健康检查、迁移、审计独立存储。

namespace BankingAgent.Base.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>数据库基础设施装配扩展。</summary>
public static class DatabaseServiceCollectionExtensions
{
    /// <summary>注册完整的数据库基础设施。</summary>
    public static IServiceCollection AddBankingDatabaseCore(
        this IServiceCollection services, IConfiguration configuration)
    {
        // ===== 1. 配置对象 =====
        var section = configuration.GetSection("Database");
        services.Configure<DatabaseOptions>(section);
        // 同时注册裸类型：DatabaseConfigurator / BankingDbContextFactory / DatabaseInitializer
        // 都在构造函数里直接依赖 DatabaseOptions，只注册 IOptions<> 会解析失败。
        services.AddSingleton(sp =>
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DatabaseOptions>>().Value);

        // ===== 2. 业务库上下文 =====
        // 显式构造工厂，而不是 AddDbContextFactory<TContext, TFactory>()：
        // 后者要求工厂只有一个接受 DbContextOptions 的构造函数，
        // 而本项目需要把插件贡献器、当前用户、时钟、加密服务、诊断拦截器一并注入。
        services.AddSingleton<DatabaseDiagnosticsInterceptor>();
        services.AddSingleton<IDbContextFactory<BankingDbContext>>(sp =>
            new BankingDbContextFactory(
                sp,
                sp.GetRequiredService<DatabaseOptions>(),
                sp.GetRequiredService<DatabaseDiagnosticsInterceptor>()));

        // 兼容旧代码的 scoped 解析
        services.AddScoped<BankingDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<BankingDbContext>>().CreateDbContext());

        // ===== 3. 审计库上下文（物理隔离）=====
        services.AddDbContextFactory<AuditDbContext>((sp, options) =>
            DatabaseConfigurator.Configure(options, sp.GetRequiredService<DatabaseOptions>()));

        services.AddScoped<IAuditRepository, EfAuditRepository>();

        // ===== 4. 工作单元与初始化器 =====
        services.AddScoped<UnitOfWork>();
        services.AddSingleton<DatabaseInitializer>();

        // ===== 5. 迁移上下文（供 dotnet ef 使用）=====
        services.AddSingleton<IDesignTimeDbContextFactory<BankingDbContext>, DesignTimeFactory>();

        return services;
    }
}
