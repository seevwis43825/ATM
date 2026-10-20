// ===== 宿主程序入口 =====
//
// 职责：装配框架 → 加载插件 → 注入插件服务 → 暴露 API。
// 宿主本身不含任何业务逻辑，所有能力来自插件。
//
// 这个文件刻意保持短小：它只回答「这个进程由哪些部分组成、装配顺序是什么」，
// 具体的端点实现在 Endpoints/ 目录按职责分文件，契约在 Contracts/ 目录。
// 装配顺序有依赖关系，不能随意调换 —— 每一步都标了注释说明为什么必须在这个位置。

using BankingAgent.Base;
using BankingAgent.Base.Agents;
using BankingAgent.Base.Ai;
using BankingAgent.Base.Data;
using BankingAgent.Base.Events;
using BankingAgent.Base.Plugins;
using BankingAgent.Base.Plugins.Security;
using BankingAgent.Base.Security.Audit;
using BankingAgent.Base.Security.Auth;
using BankingAgent.Base.Security.Hardening;
using BankingAgent.Host;
using BankingAgent.Host.Endpoints;
using BankingAgent.Host.Middleware;
using BankingAgent.PluginSdk;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

// ===== 1. 装配核心框架 =====
// 数据库、审计、合规、限流、加密、核心银行客户端、事件总线都在这里就位。
builder.Services.AddBankingCore(builder.Configuration);
builder.Services.AddSingleton<IPluginStartupHook, PluginLifecycleBridge>();
builder.Services.AddSingleton<IPluginShutdownHook, PluginLifecycleBridge>();

// ===== 2. 加载插件（在 Build 之前，把插件服务并入主容器）=====
// 两阶段容器是插件化的硬性要求：插件必须在主容器 Build 之前把自己的服务
// 注册进 builder.Services，否则插件的 Agent/工具无法进入主容器。
// bootstrap 容器只用于取 ILoggerFactory 与 PluginSignatureVerifier，用完即弃。
// ASP0000 针对的是"从应用代码长期持有第二份单例"，此处不适用，故显式抑制。
#pragma warning disable ASP0000
var bootstrap = builder.Services.BuildServiceProvider();
#pragma warning restore ASP0000
var loggerFactory = bootstrap.GetRequiredService<ILoggerFactory>();
var bootLogger = loggerFactory.CreateLogger("Bootstrap");

// ===== 安全门：生产配置不合规则阻止启动 =====
var securityGate = new SecurityGate(
    builder.Configuration,
    loggerFactory.CreateLogger<SecurityGate>());
securityGate.Enforce();

// ===== 审计链签名密钥：启动期硬校验 =====
//
// 为什么必须放在启动期、而不是"第一次写审计时"才失败：
//   空密钥不是"没有密钥"，它是**公开可推导的密钥** —— 审计链照样自洽，
//   但任何人都能伪造记录并重算签名，「不可篡改」这条合规承诺静默失效。
//   若只在写审计时才抛，表现是「每个请求 500」，而不是「服务起不来」——
//   后者一眼能定位，前者会被误判成业务故障。
//
// 判定规则见 AuditSigningKeyPolicy：空串按「未配置」处理；
// 非开发环境缺密钥 → 拒绝启动；开发环境沿用公开的开发默认密钥并告警。
var auditSigningKey = builder.Configuration["Audit:SigningKey"];
if (string.IsNullOrWhiteSpace(auditSigningKey) && builder.Environment.IsDevelopment())
{
    bootLogger.LogWarning(
        "未配置 Audit:SigningKey，已启用开发环境默认密钥（公开可推导，仅限本地开发）。" +
        "生产环境必须通过环境变量 Audit__SigningKey 注入。");
}

// 非开发环境且缺密钥 → 抛异常，阻止启动。
AuditSigningKeyPolicy.Resolve(auditSigningKey, builder.Environment.IsDevelopment());

var registry = new PluginRegistry(
    bootstrap,
    loggerFactory.CreateLogger<PluginRegistry>(),
    loggerFactory,
    bootstrap.GetRequiredService<PluginSignatureVerifier>());
var pluginDir = string.IsNullOrWhiteSpace(builder.Configuration["Plugins:Directory"])
    ? Path.Combine(AppContext.BaseDirectory, "plugins")
    : builder.Configuration["Plugins:Directory"]!;

bootLogger.LogInformation("扫描插件目录: {Dir}", pluginDir);
var loaded = await registry.LoadFromDirectoryAsync(pluginDir);
bootLogger.LogInformation("已加载 {Count} 个插件: {Ids}",
    loaded.Count, string.Join(", ", loaded.Select(p => p.Manifest.Id.Value)));

// 把插件注册的服务并入主容器
foreach (var svc in registry.Services)
{
    builder.Services.Add(svc);
}

// 编排器与路由器并列暴露：路由器做单跳，编排器做多跳规划
builder.Services.AddSingleton<AgentRouter>();
builder.Services.AddSingleton<ITrajectoryLog, InMemoryTrajectoryLog>();
builder.Services.AddSingleton<IOrchestrationStrategy, AdaptiveStrategy>();
builder.Services.AddSingleton<AgentOrchestrator>();

var app = builder.Build();
registry.AttachProvider(app.Services);
await bootstrap.DisposeAsync();

// 把插件贡献器写入静态注册表，供 DbContext 工厂路径使用
PluginContributorRegistry.Register(app.Services.GetServices<IEntitySetContributor>());

// ===== 3. 数据库初始化 =====
// 必须在插件启动之前：插件的启动钩子可能已经要读写自己的数据分区。
using (var scope = app.Services.CreateScope())
{
    var init = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await init.InitializeAsync();
}

// ===== 4. 启动全部插件 =====
foreach (var plugin in registry.LoadedPlugins)
{
    await registry.StartPluginAsync(plugin.Manifest.Id);
}

// ===== 5. 中间件管道 =====
// 顺序不能随意调换：
//   安全响应头必须在最前（否则 4xx/5xx 响应会漏掉这些头）
//   静态资源必须在认证之前（页面本身是公开资源，登录由页面内调用 /api/auth/token 完成）
//   限流在认证之前（限流要按 IP 与路径判定，不能等身份解析完）

// ===== 安全响应头（必须在最前）=====
app.UseMiddleware<SecurityHeadersMiddleware>();

// ===== 演示用对话页面（wwwroot，单文件，无需构建）=====
// 必须放在认证中间件之前：页面本身是公开资源，登录与鉴权由页面内的
// /api/auth/token 调用完成，不能因为缺令牌而把首页也挡成 401。
app.UseDefaultFiles();
app.UseStaticFiles();

// ===== HTTPS 重定向 + HSTS =====
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// ===== 速率限制 =====
app.UseMiddleware<RateLimitingMiddleware>();

// ===== 认证中间件 =====
app.Use(async (ctx, next) =>
{
    var tokenService = ctx.RequestServices.GetRequiredService<ITokenService>();

    var header = ctx.Request.Headers.Authorization.ToString();
    var principal = tokenService.Validate(header);

    if (principal.IsAuthenticated)
    {
        // 让 CurrentUserAccessor 与审计链拿到真实身份
        using var scope = CurrentUserAccessor.Enter(principal.UserId, role: HostSecurity.ParseRole(principal.Role));
        ctx.Items["Principal"] = principal;
        await next();
        return;
    }

    // 公开端点白名单
    var path = ctx.Request.Path.Value ?? "";
    if (HostSecurity.IsPublic(path))
    {
        await next();
        return;
    }

    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
    await ctx.Response.WriteAsJsonAsync(new
    {
        code = "UNAUTHORIZED",
        message = "缺少或无效的身份凭证，请在 Authorization 头中提供 Bearer 令牌"
    });
});

// ===== 6. 端点映射 =====
// 按职责分文件，见 Endpoints/ 目录：
//   HealthEndpoints     健康检查（/health、/health/ready、/health/live）
//   AuthEndpoints       令牌签发与身份查询
//   PluginEndpoints     插件清单、路由表、合规规则、事件总线、热插拔
//   ChatEndpoints       对话、人工确认、多 Agent 编排
//   TrajectoryEndpoints 轨迹回放
//   AiEndpoints         AI 连通性自检
app.MapHealthEndpoints(registry);
app.MapAuthEndpoints();
app.MapPluginEndpoints(registry);
app.MapChatEndpoints();
app.MapTrajectoryEndpoints();
app.MapAiEndpoints();

app.Logger.LogInformation("AI Banking Agent 宿主就绪，插件数 {Count}", registry.LoadedPlugins.Count);

// 明确告知意图识别当前走哪条路，避免"以为接了模型其实没接"
var llmClient = app.Services.GetRequiredService<ILlmClient>();
if (llmClient.IsAvailable)
{
    app.Logger.LogInformation("意图识别：大模型 {Model}（失败自动降级规则表）", llmClient.Model);
    app.Logger.LogInformation("连通性自检：POST /api/ai/probe 可验证模型是否真的可用");
}
else
{
    app.Logger.LogInformation("意图识别：规则表（未配置 Ai:ApiKey；接入方式见 README「接入大模型」）");
    app.Logger.LogInformation("连通性自检：POST /api/ai/probe 会提示未配置模型密钥");
}

await app.RunAsync();
