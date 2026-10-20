// ===== 轨迹端点 =====
//
// 轨迹记录了每一步的意图、Agent、耗时与结果，用于：
//   - 排障：看清一条请求到底经过了哪些 Agent
//   - 回放：复现某次会话的执行过程
//   - 答辩演示：展示多 Agent 协作的可见过程

using BankingAgent.Base.Agents;

namespace BankingAgent.Host.Endpoints;

/// <summary>轨迹查询端点。</summary>
public static class TrajectoryEndpoints
{
    /// <summary>注册轨迹端点。</summary>
    /// <param name="app">端点路由构建器。</param>
    /// <returns>路由构建器。</returns>
    public static IEndpointRouteBuilder MapTrajectoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/trajectory/{sessionId}", (string sessionId, ITrajectoryLog log) =>
                GetSessionTrajectory(sessionId, log))
            .WithTags("轨迹")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/trajectory", (int? limit, ITrajectoryLog log) =>
                GetRecentTrajectory(limit, log))
            .WithTags("轨迹")
            .Produces(StatusCodes.Status200OK);

        return app;
    }

    /// <summary>GET /api/trajectory/{sessionId} — 某会话的完整轨迹。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="log">轨迹日志。</param>
    /// <returns>轨迹事件列表；会话不存在时返回 404。</returns>
    private static IResult GetSessionTrajectory(string sessionId, ITrajectoryLog log)
    {
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
    }

    /// <summary>GET /api/trajectory — 最近的轨迹事件。</summary>
    /// <param name="limit">返回条数，默认 200。</param>
    /// <param name="log">轨迹日志。</param>
    /// <returns>最近的事件列表。</returns>
    private static IResult GetRecentTrajectory(int? limit, ITrajectoryLog log)
    {
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
    }
}
