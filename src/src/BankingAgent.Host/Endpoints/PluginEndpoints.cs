// ===== 插件管理端点 =====
//
// 插件清单、路由表、合规规则、事件总线状态，以及插件的停用/启用（热插拔演示）。
// 停用与启用仅限管理员。

using BankingAgent.Base.Agents;
using BankingAgent.Base.Events;
using BankingAgent.Base.Plugins;
using BankingAgent.Base.Security.Auth;
using BankingAgent.Base.Security.Compliance;
using BankingAgent.PluginSdk;

namespace BankingAgent.Host.Endpoints;

/// <summary>插件管理相关端点。</summary>
public static class PluginEndpoints
{
    /// <summary>注册插件管理端点。</summary>
    /// <param name="app">端点路由构建器。</param>
    /// <param name="registry">插件注册表。</param>
    /// <returns>路由构建器。</returns>
    public static IEndpointRouteBuilder MapPluginEndpoints(
        this IEndpointRouteBuilder app, PluginRegistry registry)
    {
        app.MapGet("/api/plugins", () => DescribePlugins(registry))
            .WithTags("插件")
            .Produces(StatusCodes.Status200OK);

        // AgentRouter 是单例，必须从根容器解析
        app.MapGet("/api/plugins/agents", (AgentRouter router) => DescribeAgents(router))
            .WithTags("插件")
            .Produces(StatusCodes.Status200OK);

        app.MapGet("/api/plugins/compliance", (ComplianceGuard guard) => DescribeCompliance(guard))
            .WithTags("插件")
            .Produces(StatusCodes.Status200OK);

        app.MapGet("/api/plugins/events", (InMemoryEventBus? bus) => DescribeEvents(bus))
            .WithTags("插件")
            .Produces(StatusCodes.Status200OK);

        app.MapPost("/api/plugins/{pluginId}/stop", async (string pluginId, HttpContext ctx, CancellationToken ct) =>
                await ChangePluginStateAsync(registry, pluginId, ctx, stop: true, ct))
            .WithTags("插件")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/plugins/{pluginId}/start", async (string pluginId, HttpContext ctx, CancellationToken ct) =>
                await ChangePluginStateAsync(registry, pluginId, ctx, stop: false, ct))
            .WithTags("插件")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>GET /api/plugins — 已加载插件清单。</summary>
    /// <param name="registry">插件注册表。</param>
    /// <returns>插件清单。</returns>
    private static IResult DescribePlugins(PluginRegistry registry) =>
        Results.Ok(registry.LoadedPlugins.Select(p => new
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
        }));

    /// <summary>GET /api/plugins/agents — 路由表。</summary>
    /// <param name="router">Agent 路由器。</param>
    /// <returns>Agent 及其声明的意图与关键词。</returns>
    private static IResult DescribeAgents(AgentRouter router) =>
        Results.Ok(router.Agents.Select(a => new
        {
            id = a.Id.Value,
            name = a.Name,
            role = a.Role.ToString(),
            intents = a.SupportedIntents,
            keywords = a.TriggerKeywords,
            patterns = a.TriggerPatterns
        }));

    /// <summary>GET /api/plugins/compliance — 已加载合规规则。</summary>
    /// <param name="guard">合规守卫。</param>
    /// <returns>规则清单。</returns>
    private static IResult DescribeCompliance(ComplianceGuard guard) =>
        Results.Ok(guard.Rules.Select(r => new
        {
            ruleId = r.RuleId,
            version = r.RuleVersion,
            scenarios = r.Scenarios
        }));

    /// <summary>GET /api/plugins/events — 事件总线状态。</summary>
    /// <param name="bus">事件总线；未注册时为 null。</param>
    /// <returns>发布计数、死信与最近事件。</returns>
    private static IResult DescribeEvents(InMemoryEventBus? bus) =>
        Results.Ok(new
        {
            published = bus?.PublishedCount ?? 0,
            deadLetters = bus?.DeadLetters.Count ?? 0,
            recent = bus?.History.TakeLast(20).Select(e => new
            {
                e.EventType, e.Source, e.OccurredAt, e.Payload
            })
        });

    /// <summary>停用或启用插件（热插拔演示）。</summary>
    /// <param name="registry">插件注册表。</param>
    /// <param name="pluginId">插件 ID。</param>
    /// <param name="ctx">当前请求上下文。</param>
    /// <param name="stop">true 表示停用，false 表示启用。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>操作结果。</returns>
    private static async Task<IResult> ChangePluginStateAsync(
        PluginRegistry registry,
        string pluginId,
        HttpContext ctx,
        bool stop,
        CancellationToken ct)
    {
        var principal = (CurrentPrincipal?)ctx.Items["Principal"];
        if (principal?.CanAdminister != true)
        {
            return HostSecurity.Forbidden(stop ? "只有管理员可以停用插件" : "只有管理员可以启用插件");
        }

        var id = new PluginId(pluginId);
        var ok = stop
            ? await registry.StopPluginAsync(id, ct)
            : await registry.StartPluginAsync(id, ct);

        return ok
            ? Results.Ok(new { pluginId, action = stop ? "stopped" : "started" })
            : Results.NotFound();
    }
}
