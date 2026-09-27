// ===== 宿主程序入口 =====
// 职责：装配框架 → 加载插件 → 注入插件服务 → 暴露 API
// 宿主本身不含任何业务逻辑，所有能力来自插件。

using BankingAgent.Base;
using BankingAgent.Base.Agents;
using BankingAgent.Base.Data;
using BankingAgent.Base.Events;
using BankingAgent.Base.Plugins;
using BankingAgent.Base.Security.Audit;
using BankingAgent.Base.Security.Compliance;
using BankingAgent.PluginSdk;
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
var bootstrap = builder.Services.BuildServiceProvider();
var loggerFactory = bootstrap.GetRequiredService<ILoggerFactory>();
var bootLogger = loggerFactory.CreateLogger("Bootstrap");

var registry = new PluginRegistry(bootstrap, loggerFactory.CreateLogger<PluginRegistry>(), loggerFactory);
var configuredPluginDir = builder.Configuration["Plugins:Directory"];
var pluginDir = string.IsNullOrWhiteSpace(configuredPluginDir)
    ? Path.Combine(AppContext.BaseDirectory, "plugins")
    : configuredPluginDir;

bootLogger.LogInformation("扫描插件目录: {Dir}", pluginDir);
var loaded = await registry.LoadFromDirectoryAsync(pluginDir);
bootLogger.LogInformation("已加载 {Count} 个插件: {Ids}",
    loaded.Count, string.Join(", ", loaded.Select(p => p.Manifest.Id.Value)));

// 把插件注册的服务并入主容器
foreach (var svc in registry.Services)
{
    builder.Services.Add(svc);
}

// Agent 路由器需要在所有插件服务就位后才能解析
builder.Services.AddSingleton<AgentRouter>();

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

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    plugins = registry.LoadedPlugins.Count,
    timestamp = DateTimeOffset.UtcNow
}));

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
        intents = a.SupportedIntents
    }));
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

// 停用/启用插件（热插拔演示）
app.MapPost("/api/plugins/{pluginId}/stop", async (string pluginId, CancellationToken ct) =>
{
    var ok = await registry.StopPluginAsync(new PluginId(pluginId), ct);
    return ok ? Results.Ok(new { pluginId, action = "stopped" }) : Results.NotFound();
});

app.MapPost("/api/plugins/{pluginId}/start", async (string pluginId, CancellationToken ct) =>
{
    var ok = await registry.StartPluginAsync(new PluginId(pluginId), ct);
    return ok ? Results.Ok(new { pluginId, action = "started" }) : Results.NotFound();
});

// ===== 对话演示 =====
app.MapPost("/api/chat", async (ChatRequest req, CancellationToken ct) =>
{
    var router = app.Services.GetRequiredService<AgentRouter>();

    var intent = DetectIntent(req.Message);

    var request = new AgentRequest
    {
        UserInput = req.Message,
        UserId = req.UserId,
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
        result.Confidence, Math.Round(result.Elapsed.TotalMilliseconds, 1)));
});

// ===== 人工回环确认 =====
app.MapPost("/api/chat/confirm", async (ConfirmRequest req, CancellationToken ct) =>
{
    var router = app.Services.GetRequiredService<AgentRouter>();
    var audit = app.Services.GetRequiredService<IAuditLogger>();

    await audit.WriteAsync(new AuditEvent
    {
        AuditId = Guid.NewGuid().ToString("N")[..12],
        Timestamp = DateTimeOffset.UtcNow,
        ActorType = "USER",
        ActorId = req.UserId,
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
        UserId = req.UserId,
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
await app.RunAsync();

// ===== 辅助函数 =====

/// <summary>极简意图识别。生产环境应替换为 LLM 分类器。</summary>
static string DetectIntent(string message)
{
    if (message.Contains("转账") || message.Contains("汇款")
        || message.Contains("打钱") || message.Contains("转给"))
        return "transfer.execute";
    if (message.Contains("账单") || message.Contains("花了多少") || message.Contains("消费"))
        return "bill.summary";
    if (message.Contains("卡") || message.Contains("挂失") || message.Contains("冻结"))
        return "card.list";
    if (message.Contains("余额"))
        return "account.balance";
    return "unknown";
}

// ===== 类型定义 =====

/// <summary>对话请求。</summary>
public sealed record ChatRequest(
    string Message, string UserId, string? SessionId, Dictionary<string, object?>? Slots);

/// <summary>对话响应。</summary>
public sealed record ChatResponse(
    string Echo, string Intent, bool Success, string? Content, string? ResultIntent,
    bool RequiresHumanInLoop, bool SideEffectCommitted, string? ErrorCode, string? ErrorMessage,
    IReadOnlyDictionary<string, object?> Data, double Confidence, double ElapsedMs);

/// <summary>人工确认请求。</summary>
public sealed record ConfirmRequest(
    string UserId, string? SessionId, decimal Amount, Dictionary<string, object?> Slots);

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
