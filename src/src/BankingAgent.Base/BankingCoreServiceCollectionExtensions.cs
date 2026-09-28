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
using BankingAgent.Base.Security.Auth;
using BankingAgent.Base.Security.Compliance;
using BankingAgent.Base.Cryptography;
using BankingAgent.Base.Plugins.Security;
using BankingAgent.Base.Security.Hardening;
using BankingAgent.Base.Security.RateLimit;
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

    /// <summary>注册数据库基础设施（委托给 DatabaseServiceCollectionExtensions）。</summary>
    public static IServiceCollection AddBankingDatabase(
        this IServiceCollection services, IConfiguration configuration)
    {
        return services.AddBankingDatabaseCore(configuration);
    }

    /// <summary>注册安全合规基础设施。</summary>
    public static IServiceCollection AddBankingSecurity(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AuditOptions>(configuration.GetSection("Audit"));
        services.Configure<ComplianceOptions>(configuration.GetSection("Compliance"));
        services.Configure<JwtOptions>(configuration.GetSection("Jwt"));
        services.Configure<RateLimitOptions>(configuration.GetSection("RateLimit"));
        services.Configure<CryptoOptions>(configuration.GetSection("Crypto"));

        services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuditOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ComplianceOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<JwtOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RateLimitOptions>>().Value);

        // 密码敏捷服务：缺少密钥时构造即抛异常，生产环境启动必失败
        services.AddSingleton<ICryptoService>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CryptoOptions>>().Value;
            return new CryptoService(
                Microsoft.Extensions.Options.Options.Create(opts),
                sp.GetRequiredService<ILogger<CryptoService>>());
        });

        services.AddSingleton<IAuditLogger, AuditLogger>();
        services.AddSingleton<ITokenService, TokenService>();
        services.AddSingleton<RateLimiter>();

        // 安全门：生产环境配置不合规时阻止启动
        services.AddSingleton<SecurityGate>();

        // 插件签名验证：防供应链投毒
        services.Configure<PluginSignatureOptions>(configuration.GetSection("PluginSignature"));
        services.AddSingleton<PluginSignatureVerifier>();

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

        // 编排体系：轨迹日志 + 策略 + 编排器
        services.AddSingleton<ITrajectoryLog, InMemoryTrajectoryLog>();
        services.AddSingleton<IOrchestrationStrategy, AdaptiveStrategy>();
        services.AddSingleton<AgentOrchestrator>();
        return services;
    }
}

/// <summary>数据库配置快照。</summary>
public sealed record DatabaseSettings(string Provider, string ConnectionString, bool AutoMigrate)
{
    public bool IsNpgsql => Provider.Equals("Npgsql", StringComparison.OrdinalIgnoreCase)
                             || Provider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase);
}
