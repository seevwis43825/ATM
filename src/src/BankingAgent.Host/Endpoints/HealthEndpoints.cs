// ===== 健康检查端点 =====
//
// 三种探针的区别是有意为之的：
//   - /health        完整状态，含数据库真实探活与 AI 连通性；数据库不可用返回 503
//   - /health/ready  就绪探针：只回答「能否开始接流量」
//   - /health/live   存活探针：进程活着就返回 200，不查数据库
//
// 为什么不用一个端点全包：K8s 会因为就绪探针失败而**摘掉流量**，
// 但不会因为存活探针失败就立刻重启容器（除非配了重启策略）。
// 数据库短暂不可用时，正确反应是摘流量而不是重启进程。

using BankingAgent.Base.Ai;
using BankingAgent.Base.Data;
using BankingAgent.Base.Plugins;

namespace BankingAgent.Host.Endpoints;

/// <summary>健康检查相关端点。</summary>
public static class HealthEndpoints
{
    /// <summary>注册健康检查端点。</summary>
    /// <param name="app">端点路由构建器。</param>
    /// <param name="registry">插件注册表，用于报告已加载插件数。</param>
    /// <returns>路由构建器。</returns>
    public static IEndpointRouteBuilder MapHealthEndpoints(
        this IEndpointRouteBuilder app, PluginRegistry registry)
    {
        app.MapGet("/health", (DatabaseInitializer initializer, ILlmClient llm,
                AiConnectivityProbe probe, CancellationToken ct) =>
            GetHealth(initializer, llm, probe, registry, ct))
            .WithTags("运维")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/health/ready", GetReady)
            .WithTags("运维")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/health/live", GetLive)
            .WithTags("运维")
            .Produces(StatusCodes.Status200OK);

        return app;
    }

    /// <summary>GET /health — 完整健康状态。</summary>
    /// <param name="initializer">数据库初始化器，用于真实探活。</param>
    /// <param name="llm">模型客户端，用于说明意图识别当前由谁提供。</param>
    /// <param name="probe">AI 连通性探针，用于暴露最近一次自检结果。</param>
    /// <param name="registry">插件注册表，用于报告已加载插件数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>健康状态；数据库不可用时返回 503。</returns>
    private static async Task<IResult> GetHealth(
        DatabaseInitializer initializer,
        ILlmClient llm,
        AiConnectivityProbe probe,
        PluginRegistry registry,
        CancellationToken ct)
    {
        var db = await initializer.CheckHealthAsync(ct);
        var lastProbe = probe.LastResult;

        var payload = new
        {
            status = db.IsHealthy ? "healthy" : "unhealthy",
            plugins = registry.LoadedPlugins.Count,
            // 意图识别当前由谁提供：配了密钥走模型，否则规则表兜底。
            // 同时暴露最近一次连通性探测结果 —— 「AI 到底通没通」必须是可观测的事实，
            // 而不是靠日志推测。
            ai = new
            {
                intentRecognition = llm.IsAvailable ? "llm+rule" : "rule",
                model = llm.IsAvailable ? llm.Model : null,
                configured = llm.IsAvailable,
                lastProbe = lastProbe is null
                    ? null
                    : new
                    {
                        checkedAt = lastProbe.CheckedAt,
                        callSucceeded = lastProbe.CallSucceeded,
                        mappedToKnownIntent = lastProbe.MappedToKnownIntent,
                        resolvedIntent = lastProbe.ResolvedIntent,
                        elapsedMs = lastProbe.ElapsedMs,
                        summary = lastProbe.Summary
                    }
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
    }

    /// <summary>GET /health/ready — 就绪探针。</summary>
    /// <param name="initializer">数据库初始化器。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>就绪状态。</returns>
    private static async Task<IResult> GetReady(DatabaseInitializer initializer, CancellationToken ct)
    {
        var db = await initializer.CheckHealthAsync(ct);
        return db.IsHealthy
            ? Results.Ok(new { ready = true })
            : Results.Json(new { ready = false, reason = db.Error ?? "database unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>GET /health/live — 存活探针。</summary>
    /// <returns>存活状态。</returns>
    private static IResult GetLive() =>
        Results.Ok(new { alive = true, timestamp = DateTimeOffset.UtcNow });
}
