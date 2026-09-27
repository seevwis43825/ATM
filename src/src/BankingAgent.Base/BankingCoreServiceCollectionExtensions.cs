// ===== 一行装配整个框架 =====
// 宿主只需调用 AddBankingCore(...)，Base 的数据库、审计、合规、事件总线、
// 核心银行客户端全部就绪。插件无需关心这些基础设施。

namespace BankingAgent.Base;

using BankingAgent.Base.Agents;
using BankingAgent.Base.CoreBank;
using BankingAgent.Base.Data;
using BankingAgent.Base.Events;
using BankingAgent.Base.Plugins;
using BankingAgent.Base.Security.Audit;
using BankingAgent.Base.Security.Compliance;
using BankingAgent.PluginSdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

/// <summary>框架装配扩展。</summary>
public static class BankingCoreServiceCollectionExtensions
{
    /// <summary>注册 Base 全部基础能力。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">配置根，用于读取连接字符串与阈值。</param>
    public static IServiceCollection AddBankingCore(
        this IServiceCollection services, IConfiguration configuration)
    {
        // ===== 1. 时钟与当前用户 =====
        services.TryAddSingleton(new DateTimeOffsetProvider());
        services.TryAddSingleton<ICurrentUserAccessor, CurrentUserAccessor>();

        // ===== 2. 数据库 =====
        services.AddBankingDatabase(configuration);

        // ===== 3. 安全合规 =====
        services.AddBankingSecurity(configuration);

        // ===== 4. 事件总线 =====
        services.AddSingleton<IEventPublisher>(sp =>
        {
            var handlers = sp.GetServices<IDomainEventHandler>();
            return new InMemoryEventBus(handlers, sp.GetRequiredService<ILogger<InMemoryEventBus>>());
        });
        services.TryAddSingleton(sp => sp.GetRequiredService<IEventPublisher>() as InMemoryEventBus
            ?? throw new InvalidOperationException("事件总线未注册为 InMemoryEventBus"));

        // ===== 5. 核心银行客户端 =====
        services.Configure<CoreBankOptions>(configuration.GetSection("CoreBank"));
        services.AddHttpClient<ICoreBankClient, CoreBankClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CoreBankOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        // ===== 6. 插件系统 =====
        services.AddSingleton<PluginRegistry>();

        return services;
    }

    /// <summary>注册数据库基础设施。支持 SQLite 与 PostgreSQL 双 Provider。</summary>
    public static IServiceCollection AddBankingDatabase(
        this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? "Sqlite";
        var connectionString = configuration["Database:ConnectionString"]
            ?? "Data Source=bankingagent.db";

        services.AddSingleton(new DatabaseSettings(provider, connectionString, configuration.GetValue("Database:AutoMigrate", true)));

        services.AddDbContext<BankingDbContext>((sp, options) =>
        {
            var settings = sp.GetRequiredService<DatabaseSettings>();
            if (settings.IsNpgsql)
            {
                // 生产环境启用 PostgreSQL 时需安装 Npgsql.EntityFrameworkCore.PostgreSQL
                // 未安装时给出明确指引，而不是抛出难以理解的类型错误
                throw new InvalidOperationException(
                    "检测到 Database:Provider=Npgsql，但未安装 Npgsql provider。" +
                    "请执行: dotnet add BankingAgent.Base package Npgsql.EntityFrameworkCore.PostgreSQL");
            }

            options.UseSqlite(settings.ConnectionString);
        });

        var settingsSnapshot = new DatabaseSettings(provider, connectionString,
            configuration.GetValue("Database:AutoMigrate", true));
        services.AddSingleton(settingsSnapshot);

        // 手工构建 DbContextOptions 并注册为单例。
        // 若使用 AddDbContext/AddDbContextFactory 扩展，EF 会额外注册一份 scoped 的
        // DbContextOptions，导致单例工厂消费 scoped 服务而抛异常，故此处绕开扩展方法。
        services.AddSingleton(sp =>
        {
            var optionsBuilder = new DbContextOptionsBuilder<BankingDbContext>();
            if (settingsSnapshot.IsNpgsql)
            {
                throw new InvalidOperationException(
                    "检测到 Database:Provider=Npgsql，但未安装 Npgsql provider。" +
                    "请执行: dotnet add BankingAgent.Base package Npgsql.EntityFrameworkCore.PostgreSQL");
            }
            optionsBuilder.UseSqlite(settingsSnapshot.ConnectionString);
            return optionsBuilder.Options;
        });

        services.AddScoped<BankingDbContext>();
        services.AddSingleton<IDbContextFactory<BankingDbContext>>(sp =>
            new SimpleDbContextFactory(sp, sp.GetRequiredService<DbContextOptions<BankingDbContext>>()));

        services.AddScoped<UnitOfWork>();
        services.AddScoped(sp => new DatabaseInitializer(
            sp.GetRequiredService<BankingDbContext>(),
            sp.GetRequiredService<ILogger<DatabaseInitializer>>(),
            sp.GetRequiredService<DatabaseSettings>().AutoMigrate));
        return services;
    }

    /// <summary>注册安全合规基础设施。</summary>
    public static IServiceCollection AddBankingSecurity(
        this IServiceCollection services, IConfiguration configuration)
    {
        // 配置对象必须注册为具体类型，因为规则类直接注入它们
        services.Configure<AuditOptions>(configuration.GetSection("Audit"));
        services.Configure<ComplianceOptions>(configuration.GetSection("Compliance"));
        services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuditOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ComplianceOptions>>().Value);

        services.AddSingleton<IAuditLogger, AuditLogger>();

        // 内置合规规则。插件可追加自定义规则。
        services.AddSingleton<IComplianceRule, TransferAmountRule>();
        services.AddSingleton<IComplianceRule, SelfTransferRule>();
        services.AddSingleton<IComplianceRule, AmlThresholdRule>();
        services.AddSingleton<IComplianceRule, ScenarioPermissionRule>();
        services.AddSingleton<ComplianceGuard>();

        return services;
    }

    /// <summary>注册插件提供的 Agent（宿主在构建容器后调用）。</summary>
    public static IServiceCollection AddPluginAgents(this IServiceCollection services)
    {
        services.AddSingleton<AgentRouter>();
        return services;
    }
}

/// <summary>数据库配置快照。</summary>
public sealed record DatabaseSettings(string Provider, string ConnectionString, bool AutoMigrate)
{
    public bool IsNpgsql => Provider.Equals("Npgsql", StringComparison.OrdinalIgnoreCase)
                             || Provider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase);
}
