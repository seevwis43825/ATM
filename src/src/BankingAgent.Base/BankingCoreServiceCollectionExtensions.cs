// ===== 一行装配整个框架 =====
// 宿主只需调用 AddBankingCore(...)，Base 的数据库、审计、合规、事件总线、
// 核心银行客户端全部就绪。插件无需关心这些基础设施。

namespace BankingAgent.Base;

using BankingAgent.Base.Agents;
using BankingAgent.Base.Ai;
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
using System.Text;

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

        // ===== 7. 大模型接入（可选增强，未配密钥即自动降级）=====
        services.AddBankingAi(configuration);

        return services;
    }

    /// <summary>
    /// 注册大模型接入与意图识别。
    ///
    /// 未配置 <c>Ai:ApiKey</c> 时 <see cref="ILlmClient.IsAvailable"/> 为 false，
    /// 意图识别自动走规则表 —— 系统不依赖模型也能完整运行（这是刻意的设计，
    /// 银行入口不能因为上游模型抖动或欠费而整体不可用）。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">配置根，读取 Ai 节。</param>
    public static IServiceCollection AddBankingAi(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LlmOptions>(configuration.GetSection("Ai"));

        services.AddHttpClient<ILlmClient, OpenAiCompatibleLlmClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LlmOptions>>().Value;

            // 配置来自外部，缺省值在这里兜底，避免空串让 Uri 构造直接抛异常
            var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl)
                ? "https://api.deepseek.com/v1"
                : options.BaseUrl;
            // 必须以 "/" 结尾，才能与相对路径 "chat/completions" 正确拼接
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds));
        })
        .ConfigurePrimaryHttpMessageHandler(sp =>
        {
            // 连接阶段单独设更短的上限：端点不可达时不该让用户等满整个生成超时
            // 才拿到降级结果。模型生成慢仍由 client.Timeout 控制。
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LlmOptions>>().Value;
            var connectSeconds = Math.Clamp(options.TimeoutSeconds / 2, 1, 3);
            return new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(connectSeconds),
                // 长时间持有的客户端也能感知 DNS / 端点变更
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            };
        });

        // 分类器是无状态的，但持有 HttpClient 的 ILlmClient 由工厂按请求创建，
        // 因此这里用 Transient 让处理器轮换（DNS 变更感知）继续生效。
        services.TryAddSingleton<AgentRouter>();
        services.AddTransient<IIntentClassifier, LlmIntentClassifier>();

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

        // 密码敏捷服务。
        //
        // 密钥来源优先级：配置/环境变量 > 开发环境派生密钥 > 启动失败。
        // 为什么要有"开发环境派生密钥"这一层：
        //   原先开发密钥写在 appsettings.Development.json 里并被 git 跟踪，
        //   等于把 AES 主密钥提交进仓库。现在该文件不再入库，
        //   若不给开发环境兜底，任何人 clone 下来都会因缺少密钥而无法启动。
        //   因此仅在 Development 下用固定材料派生一个**确定性**开发密钥
        //   （确定性是为了重启后仍能解密之前写入的数据），并打印显著警告。
        // 生产环境绝不走这条路：SecurityGate 会先一步拦截缺少密钥的启动。
        services.AddSingleton<ICryptoService>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CryptoOptions>>().Value;
            var log = sp.GetRequiredService<ILogger<CryptoService>>();

            if (string.IsNullOrWhiteSpace(opts.DataEncryptionKey) && IsDevelopment(sp))
            {
                opts.DataEncryptionKey = DeriveDevelopmentKey("crypto:" + opts.KeyId);
                log.LogWarning(
                    "未配置 Crypto:DataEncryptionKey，已启用开发环境派生密钥（KeyId={KeyId}）。" +
                    "该密钥仅用于本地开发，禁止用于任何真实数据；" +
                    "生产环境请通过环境变量 Crypto__DataEncryptionKey 注入。",
                    opts.KeyId);
            }

            return new CryptoService(
                Microsoft.Extensions.Options.Options.Create(opts), log);
        });

        services.AddSingleton<IAuditLogger, AuditLogger>();

        // 令牌服务同 CryptoService：缺少密钥时构造即抛异常。
        // 而认证中间件对**每个请求**都要解析 ITokenService，一旦抛异常，
        // 连 /health 都会变成 500（并且开发环境下回显完整堆栈）。
        // 因此这里同样只在 Development 下兜底，生产保持"缺密钥即启动失败"。
        services.AddSingleton<ITokenService>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<JwtOptions>>().Value;
            var log = sp.GetRequiredService<ILogger<TokenService>>();

            if (Encoding.UTF8.GetByteCount(opts.SigningKey ?? "") < 32 && IsDevelopment(sp))
            {
                opts.SigningKey = DeriveDevelopmentKey("jwt-hs256");
                log.LogWarning(
                    "未配置有效的 Jwt:SigningKey，已启用开发环境派生密钥。" +
                    "生产环境必须通过环境变量 Jwt__SigningKey 注入至少 32 字节的密钥。");
            }

            return new TokenService(Microsoft.Extensions.Options.Options.Create(opts), log);
        });

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

    /// <summary>
    /// 派生开发环境专用密钥。
    /// 用固定材料经 SHA-256 得到 32 字节，保证：
    ///   1) 不同开发者机器上一致（团队复现问题不需要交换密钥）
    ///   2) 重启后不变（此前写入的密文仍可解密）
    /// 代价是它公开可推导 —— 因此只能用于本地开发，生产由 SecurityGate 拦截。
    /// </summary>
    /// <param name="purpose">用途标识，不同用途派生不同密钥，避免一钥多用。</param>
    private static string DeriveDevelopmentKey(string purpose)
    {
        var material = Encoding.UTF8.GetBytes(
            $"AI-Banking-Agent/development-only-key/{purpose}");
        return Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(material));
    }

    /// <summary>判断当前是否运行在 Development 环境。</summary>
    private static bool IsDevelopment(IServiceProvider sp)
    {
        // 直接比较环境名，避免依赖 Hosting 的 IsDevelopment 扩展方法
        var env = sp.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>();
        return env is not null &&
               env.EnvironmentName.Equals("Development", StringComparison.OrdinalIgnoreCase);
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
