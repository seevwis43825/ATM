// ===== 宿主程序入口 =====
// 职责：装配框架 → 加载插件 → 注入插件服务 → 暴露 API
// 宿主本身不含任何业务逻辑，所有能力来自插件。

using BankingAgent.Base;
using BankingAgent.Base.Agents;
using BankingAgent.Base.Ai;
using BankingAgent.Base.Data;
using BankingAgent.Base.Events;
using BankingAgent.Base.Plugins;
using BankingAgent.Base.Security.Audit;
using BankingAgent.Base.Security.Auth;
using BankingAgent.Base.Security.Compliance;
using BankingAgent.PluginSdk;
using BankingAgent.Base.Plugins.Security;
using BankingAgent.Base.Security.Hardening;
using BankingAgent.Base.Security.RateLimit;
using BankingAgent.Host.Middleware;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

// ===== 1. 装配核心框架 =====
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

// ===== 5. API 端点 =====

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
// 修复越权漏洞：原实现 userId 由客户端传入，任何人可传别人的 userId。
// 现在所有受保护端点必须携带合法 JWT，且 userId 一律从令牌提取。
app.Use(async (ctx, next) =>
{
    var tokenService = ctx.RequestServices.GetRequiredService<ITokenService>();

    var header = ctx.Request.Headers.Authorization.ToString();
    var principal = tokenService.Validate(header);

    if (principal.IsAuthenticated)
    {
        // 让 CurrentUserAccessor 与审计链拿到真实身份
        using var scope = CurrentUserAccessor.Enter(principal.UserId, role: ParseRole(principal.Role));
        ctx.Items["Principal"] = principal;
        await next();
        return;
    }

    // 公开端点白名单
    var path = ctx.Request.Path.Value ?? "";
    if (IsPublic(path))
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

// ===== 健康检查 =====
// 真实探活：不再无条件返回 "healthy"。
// 原实现无论数据库是否可用都返回 200 + healthy，K8s 就绪探针与运维告警
// 会被彻底欺骗 —— 一个"永远绿灯"的探针等于没有探针。
// 现在真正执行 SELECT 1 并检查迁移状态；不健康返回 503，让编排系统能摘流量。
app.MapGet("/health", async (DatabaseInitializer initializer, ILlmClient llm, CancellationToken ct) =>
{
    var db = await initializer.CheckHealthAsync(ct);

    var payload = new
    {
        status = db.IsHealthy ? "healthy" : "unhealthy",
        plugins = registry.LoadedPlugins.Count,
        // 意图识别当前由谁提供：配了密钥走模型，否则规则表兜底
        ai = new
        {
            intentRecognition = llm.IsAvailable ? "llm+rule" : "rule",
            model = llm.IsAvailable ? llm.Model : null
        },
        database = new
        {
            healthy = db.IsHealthy,
            provider = db.Provider,
            database = db.DatabaseName,
            latencyMs = db.LatencyMs,
            appliedMigrations = db.AppliedMigrationCount,
            pendingMigrations = db.PendingMigrations,
            error = db.Error
        },
        timestamp = DateTimeOffset.UtcNow
    };

    return db.IsHealthy
        ? Results.Ok(payload)
        : Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable);
});

// 就绪探针：只回答"能否开始接流量"，不暴露数据库细节
app.MapGet("/health/ready", async (DatabaseInitializer initializer, CancellationToken ct) =>
{
    var db = await initializer.CheckHealthAsync(ct);
    return db.IsHealthy
        ? Results.Ok(new { ready = true })
        : Results.Json(new { ready = false, reason = db.Error ?? "database unavailable" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
});

// 存活探针：进程活着即返回 200，不查数据库。
// 与就绪探针分开是有意的：数据库短暂不可用不应该导致容器被反复重启，
// 只应被摘掉流量。这是 K8s 里 liveness/readiness 的经典区别。
app.MapGet("/health/live", () => Results.Ok(new { alive = true, timestamp = DateTimeOffset.UtcNow }));

// ===== 认证端点（演示用；生产应接统一身份认证）=====
app.MapPost("/api/auth/token", (TokenRequest req, ITokenService tokenService) =>
{
    // 演示环境的账号校验。生产环境必须对接银行统一认证/OTP/MFA。
    if (string.IsNullOrWhiteSpace(req.UserId) || string.IsNullOrWhiteSpace(req.Password))
    {
        return Results.BadRequest(new { code = "INVALID_REQUEST", message = "用户名与密码不能为空" });
    }

    if (!DemoCredentials.TryValidate(req.UserId, req.Password, out var displayName))
    {
        // 统一返回 401，不区分"用户不存在"与"密码错误"，避免账号枚举
        return Results.Unauthorized();
    }

    // 角色一律以服务端账号表为准，绝不信任客户端传入的角色（否则可自助提权）
    var role = DemoCredentials.GetRole(req.UserId) ?? JwtRoles.User;
    var token = tokenService.IssueToken(req.UserId, role, displayName);

    return Results.Ok(new
    {
        token,
        tokenType = "Bearer",
        userId = req.UserId,
        role,
        displayName,
        expiresInSeconds = 1800
    });
});

// 返回当前令牌对应的身份，便于前端自检
app.MapGet("/api/auth/me", (HttpContext ctx) =>
{
    var principal = (CurrentPrincipal?)ctx.Items["Principal"];
    if (principal is null) return Results.Unauthorized();
    return Results.Ok(new
    {
        userId = principal.UserId,
        role = principal.Role,
        displayName = principal.DisplayName,
        canAudit = principal.CanAudit,
        canAdminister = principal.CanAdminister
    });
});

// ===== 插件管理 =====
app.MapGet("/api/plugins", () => Results.Ok(registry.LoadedPlugins.Select(p => new
{
    id = p.Manifest.Id.Value,
    name = p.Manifest.Name,
    version = p.Manifest.Version.ToString(),
    description = p.Manifest.Description,
    scenarios = p.Manifest.Scenarios,
    featureFlags = p.Manifest.FeatureFlags,
    maxClassification = p.Manifest.MaxDataClassification.ToString(),
    dependencies = p.Manifest.Dependencies.Select(d => new
    {
        id = d.PluginId.Value,
        minVersion = d.MinimumVersion.ToString(),
        optional = d.IsOptional
    }),
    agents = p.RegisteredAgents,
    isActive = p.IsActive,
    loadedAt = p.LoadedAt
})));

// AgentRouter 是单例，必须从根容器解析
app.MapGet("/api/plugins/agents", () =>
{
    var router = app.Services.GetRequiredService<AgentRouter>();
    return Results.Ok(router.Agents.Select(a => new
    {
        id = a.Id.Value,
        name = a.Name,
        role = a.Role.ToString(),
        intents = a.SupportedIntents,
        keywords = a.TriggerKeywords
    }));
});

// ===== 编排器（多 Agent 协同）=====
app.MapPost("/api/orchestrate", async (ChatRequest req, HttpContext ctx, CancellationToken ct) =>
{
    var orchestrator = app.Services.GetRequiredService<AgentOrchestrator>();
    var principal = (CurrentPrincipal?)ctx.Items["Principal"];
    if (principal is null) return Results.Unauthorized();

    var intent = (await app.Services.GetRequiredService<IIntentClassifier>()
        .ClassifyAsync(req.Message, ct)).Intent;
    var request = new AgentRequest
    {
        UserInput = req.Message,
        UserId = principal.UserId,
        SessionId = req.SessionId,
        CancellationToken = ct,
        Slots = req.Slots ?? new Dictionary<string, object?>(),
        SharedContext = new Dictionary<string, object?> { ["intent"] = intent },
        Upstream = AgentResult.Ok(intent: intent)
    };

    var result = await orchestrator.RunAsync(request, ct: ct);

    return Results.Ok(new
    {
        success = result.Success,
        content = result.FinalContent,
        requiresHumanApproval = result.RequiresHumanApproval,
        pendingStepId = result.PendingStepId,
        stepsExecuted = result.StepsExecuted,
        totalElapsedMs = result.TotalElapsedMs,
        steps = result.Steps.Select(s => new
        {
            stepId = s.StepId,
            success = s.Success,
            elapsedMs = s.ElapsedMs,
            failureReason = s.FailureReason,
            agents = s.AgentResults.Select(a => new
            {
                agentId = a.Key,
                success = a.Value.Success,
                intent = a.Value.Intent,
                content = a.Value.Content,
                requiresHumanInLoop = a.Value.RequiresHumanInLoop
            })
        })
    });
});

// ===== 轨迹查询（对齐 Harness 的 Trajectory 视图）=====
app.MapGet("/api/trajectory/{sessionId}", (string sessionId) =>
{
    var log = app.Services.GetRequiredService<ITrajectoryLog>();
    var events = log.GetSession(sessionId);
    if (events.Count == 0) return Results.NotFound(new { code = "SESSION_NOT_FOUND" });

    return Results.Ok(new
    {
        sessionId,
        eventCount = events.Count,
        events = events.Select(e => new
        {
            sequence = e.Sequence,
            type = e.Type.ToString(),
            timestamp = e.Timestamp,
            stepId = e.StepId,
            agentId = e.AgentId,
            intent = e.Intent,
            success = e.Success,
            detail = e.Detail,
            data = e.Data
        })
    });
});

app.MapGet("/api/trajectory", (int? limit) =>
{
    var log = app.Services.GetRequiredService<ITrajectoryLog>();
    var events = log.GetAll(limit ?? 200);
    return Results.Ok(new
    {
        total = events.Count,
        events = events.Select(e => new
        {
            sequence = e.Sequence,
            sessionId = e.SessionId,
            type = e.Type.ToString(),
            timestamp = e.Timestamp
        })
    });
});

// AgentRouter 是单例，从根容器解析
app.MapGet("/api/plugins/compliance", () =>
{
    var guard = app.Services.GetRequiredService<ComplianceGuard>();
    return Results.Ok(guard.Rules.Select(r => new
    {
        ruleId = r.RuleId,
        version = r.RuleVersion,
        scenarios = r.Scenarios
    }));
});

app.MapGet("/api/plugins/events", () =>
{
    var bus = app.Services.GetService<InMemoryEventBus>();
    return Results.Ok(new
    {
        published = bus?.PublishedCount ?? 0,
        deadLetters = bus?.DeadLetters.Count ?? 0,
        recent = bus?.History.TakeLast(20).Select(e => new
        {
            e.EventType, e.Source, e.OccurredAt, e.Payload
        })
    });
});

// 停用/启用插件（热插拔演示）—— 仅管理员
app.MapPost("/api/plugins/{pluginId}/stop", async (string pluginId, HttpContext ctx, CancellationToken ct) =>
{
    var principal = (CurrentPrincipal?)ctx.Items["Principal"];
    if (principal?.CanAdminister != true) return Forbidden("只有管理员可以停用插件");

    var ok = await registry.StopPluginAsync(new PluginId(pluginId), ct);
    return ok ? Results.Ok(new { pluginId, action = "stopped" }) : Results.NotFound();
});

app.MapPost("/api/plugins/{pluginId}/start", async (string pluginId, HttpContext ctx, CancellationToken ct) =>
{
    var principal = (CurrentPrincipal?)ctx.Items["Principal"];
    if (principal?.CanAdminister != true) return Forbidden("只有管理员可以启用插件");

    var ok = await registry.StartPluginAsync(new PluginId(pluginId), ct);
    return ok ? Results.Ok(new { pluginId, action = "started" }) : Results.NotFound();
});

// ===== 对话演示 =====
app.MapPost("/api/chat", async (ChatRequest req, HttpContext ctx, CancellationToken ct) =>
{
    var router = app.Services.GetRequiredService<AgentRouter>();
    var principal = (CurrentPrincipal?)ctx.Items["Principal"];

    if (principal is null)
    {
        return Results.Unauthorized();
    }

    // 意图识别：LLM 优先（需配置 Ai:ApiKey），规则表兜底。
    // 候选意图由已注册 Agent（插件）自报的意图前缀与触发关键词生成，
    // 因此新增插件零改动即可被识别 —— 这两级识别都内聚在 IIntentClassifier 里。
    var decision = await app.Services.GetRequiredService<IIntentClassifier>()
        .ClassifyAsync(req.Message, ct);
    var intent = decision.Intent;

    // 越权防护：userId 一律取自已验证的令牌，忽略请求体中的任何 userId
    var effectiveUserId = principal.UserId;

    // 客服角色可代客操作，但必须显式声明目标用户，且会被审计
    if (principal.CanImpersonateSupport &&
        !string.IsNullOrWhiteSpace(req.UserId) &&
        req.UserId != effectiveUserId)
    {
        if (req.ImpersonationReason is null)
        {
            return Results.BadRequest(new
            {
                code = "IMPERSONATION_REASON_REQUIRED",
                message = "代客操作必须提供 impersonationReason，将记入审计日志"
            });
        }
    }
    else if (!string.IsNullOrWhiteSpace(req.UserId) && req.UserId != effectiveUserId)
    {
        return Forbidden("无权访问其他用户的数据");
    }

    var targetUserId = principal.CanImpersonateSupport && !string.IsNullOrWhiteSpace(req.UserId)
        ? req.UserId
        : effectiveUserId;

    // 代客操作必须留痕
    if (targetUserId != effectiveUserId)
    {
        await app.Services.GetRequiredService<IAuditLogger>().WriteAsync(new AuditEvent
        {
            AuditId = Guid.NewGuid().ToString("N")[..12],
            Timestamp = DateTimeOffset.UtcNow,
            ActorType = "STAFF",
            ActorId = effectiveUserId,
            Operation = "user.impersonation",
            Scenario = "compliance",
            Decision = "APPROVED",
            DecisionReason = req.ImpersonationReason ?? ""
        }, ct);
    }

    var request = new AgentRequest
    {
        UserInput = req.Message,
        UserId = targetUserId,   // 来自令牌，绝不使用请求体中的 userId
        SessionId = req.SessionId,
        CancellationToken = ct,
        Slots = req.Slots ?? new Dictionary<string, object?>(),
        SharedContext = new Dictionary<string, object?> { ["intent"] = intent }
    };

    var result = await router.RouteAsync(request with
    {
        Upstream = AgentResult.Ok(intent: intent)
    }, ct);

    return Results.Ok(new ChatResponse(
        req.Message, intent, result.Success, result.Content, result.Intent,
        result.RequiresHumanInLoop, result.SideEffectCommitted,
        result.ErrorCode, result.ErrorMessage, result.Data,
        result.Confidence, Math.Round(result.Elapsed.TotalMilliseconds, 1),
        decision.Source));
});

// ===== 人工回环确认 =====
app.MapPost("/api/chat/confirm", async (ConfirmRequest req, HttpContext ctx, CancellationToken ct) =>
{
    var router = app.Services.GetRequiredService<AgentRouter>();
    var audit = app.Services.GetRequiredService<IAuditLogger>();
    var principal = (CurrentPrincipal?)ctx.Items["Principal"];

    if (principal is null) return Results.Unauthorized();

    // 越权防护：确认操作也必须以令牌身份为准
    if (!string.IsNullOrWhiteSpace(req.UserId)
        && req.UserId != principal.UserId
        && !principal.CanImpersonateSupport)
    {
        return Forbidden("无权为其他用户确认操作");
    }

    var operatorId = principal.UserId;

    await audit.WriteAsync(new AuditEvent
    {
        AuditId = Guid.NewGuid().ToString("N")[..12],
        Timestamp = DateTimeOffset.UtcNow,
        ActorType = "USER",
        ActorId = operatorId,
        Operation = "human.approval",
        Scenario = "transfer",
        Decision = "APPROVED",
        DecisionReason = "用户通过指纹或短信确认",
        Amount = req.Amount,
        RequestId = req.SessionId
    }, ct);

    var slots = new Dictionary<string, object?>(req.Slots) { ["confirmed"] = true };

    var result = await router.RouteAsync(new AgentRequest
    {
        UserInput = "用户已确认",
        UserId = operatorId,   // 来自令牌
        SessionId = req.SessionId,
        Slots = slots,
        SharedContext = new Dictionary<string, object?> { ["intent"] = "transfer.execute" },
        Upstream = AgentResult.Ok(intent: "transfer.execute"),
        CancellationToken = ct
    }, ct);

    return Results.Ok(new
    {
        success = result.Success,
        content = result.Content,
        committed = result.SideEffectCommitted,
        data = result.Data,
        error = result.ErrorMessage
    });
});

app.Logger.LogInformation("AI Banking Agent 宿主就绪，插件数 {Count}", registry.LoadedPlugins.Count);

// 明确告知意图识别当前走哪条路，避免"以为接了模型其实没接"
var llmClient = app.Services.GetRequiredService<ILlmClient>();
if (llmClient.IsAvailable)
{
    app.Logger.LogInformation("意图识别：大模型 {Model}（失败自动降级规则表）", llmClient.Model);
}
else
{
    app.Logger.LogInformation("意图识别：规则表（未配置 Ai:ApiKey；接入方式见 README「接入大模型」）");
}

await app.RunAsync();

// ===== 辅助函数 =====
// 注意：顶层语句文件里的本地函数不支持 XML 文档注释（会触发 CS1587），
// 因此这里统一用普通注释。

// 返回 403。自定义鉴权中间件下 Results.Forbid() 需要 ASP.NET Core
// 鉴权方案配合，否则会抛 InvalidOperationException，因此显式构造响应。
static IResult Forbidden(string message) => Results.Json(
    new { code = "FORBIDDEN", message },
    statusCode: StatusCodes.Status403Forbidden);

// 免认证端点白名单。
static bool IsPublic(string path) =>
    path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
    || path.StartsWith("/api/auth/token", StringComparison.OrdinalIgnoreCase);

// 角色字符串转枚举。
static ActorRole ParseRole(string role) => role switch
{
    JwtRoles.Admin => ActorRole.Admin,
    JwtRoles.Auditor => ActorRole.Auditor,
    JwtRoles.Staff => ActorRole.Staff,
    JwtRoles.Agent => ActorRole.Agent,
    _ => ActorRole.User
};

// 意图识别已内聚到 IIntentClassifier：
//   - 配置了 Ai:ApiKey 时由大模型判定（候选意图来自插件自报的意图与关键词）；
//   - 未配置或模型不可用时自动降级到 AgentRouter.InferIntentFromInput 的规则表。
// 宿主因此不再持有任何业务关键词 —— 新增场景只要在 Agent 上声明
// TriggerKeywords，即可被规则与模型同时认识。

// ===== 类型定义 =====

/// <summary>对话请求。</summary>
public sealed record ChatRequest(
    string Message, string UserId, string? SessionId, Dictionary<string, object?>? Slots,
    string? ImpersonationReason = null);

/// <summary>令牌签发请求。</summary>
public sealed record TokenRequest(string UserId, string Password, string? Role = null);

/// <summary>对话响应。</summary>
public sealed record ChatResponse(
    string Echo, string Intent, bool Success, string? Content, string? ResultIntent,
    bool RequiresHumanInLoop, bool SideEffectCommitted, string? ErrorCode, string? ErrorMessage,
    IReadOnlyDictionary<string, object?> Data, double Confidence, double ElapsedMs,
    /// <summary>意图来源：llm（大模型判定）或 rule（规则表兜底）。</summary>
    string IntentSource = "rule");

/// <summary>人工确认请求。</summary>
public sealed record ConfirmRequest(
    string UserId, string? SessionId, decimal Amount, Dictionary<string, object?> Slots);

/// <summary>
/// 演示环境的账号表。生产环境必须替换为银行统一身份认证，
/// 密码不得以明文或可逆方式存储，且需支持 OTP / MFA。
/// </summary>
internal static class DemoCredentials
{
    private static readonly Dictionary<string, (string Password, string Name, string Role)> Accounts = new()
    {
        ["u_demo01"] = ("demo1234", "张明", JwtRoles.User),
        ["u_demo02"] = ("demo1234", "李华", JwtRoles.User),
        ["u_demo03"] = ("demo1234", "王芳", JwtRoles.User),
        ["staff_01"] = ("staff1234", "客服小李", JwtRoles.Staff),
        ["audit_01"] = ("audit1234", "审计员小王", JwtRoles.Auditor),
        ["admin_01"] = ("admin1234", "管理员", JwtRoles.Admin)
    };

    /// <summary>校验演示账号。</summary>
    public static bool TryValidate(string userId, string password, out string displayName)
    {
        displayName = "";
        if (!Accounts.TryGetValue(userId, out var account)) return false;
        if (account.Password != password) return false;
        displayName = account.Name;
        return true;
    }

    /// <summary>查询账号角色，供签发令牌时使用。</summary>
    public static string? GetRole(string userId) =>
        Accounts.TryGetValue(userId, out var a) ? a.Role : null;
}

/// <summary>桥接插件生命周期钩子到容器中的 IPlugin 实现。</summary>
internal sealed class PluginLifecycleBridge(IServiceProvider sp) : IPluginStartupHook, IPluginShutdownHook
{
    public async Task StartAsync(PluginId id, CancellationToken ct)
    {
        foreach (var hook in sp.GetServices<IPlugin>())
        {
            await hook.OnStartingAsync(new SimplePluginContext(id, sp), ct);
        }
    }

    public async Task StopAsync(PluginId id, CancellationToken ct)
    {
        foreach (var hook in sp.GetServices<IPlugin>())
        {
            await hook.OnStoppingAsync(new SimplePluginContext(id, sp), ct);
        }
    }

    private sealed class SimplePluginContext(PluginId id, IServiceProvider services) : IPluginContext
    {
        public PluginManifest Manifest { get; } = new()
        {
            Id = id,
            Name = id.Value,
            Description = "",
            Version = PluginVersion.Parse("1.0.0")
        };

        public IServiceProvider Services { get; } = services;

        public string PartitionName => id.Value.ToLowerInvariant().Replace('.', '_');

        public ILogger Logger { get; } =
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }
}
