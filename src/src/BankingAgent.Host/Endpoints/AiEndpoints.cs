// ===== AI 连通性自检端点 =====
//
// 真实调用一次模型，把整条链路摊开给调用方看：
// 模型名 → 原始输出 → 归一化输出 → 是否命中已注册意图 → 耗时。
//
// 有了它，「AI 功能有没有生效」是**可验证的**，而不是靠感觉或翻日志。

using BankingAgent.Base.Ai;
using BankingAgent.Base.Security.Auth;
using BankingAgent.Host.Contracts;

namespace BankingAgent.Host.Endpoints;

/// <summary>AI 连通性自检端点。</summary>
public static class AiEndpoints
{
    /// <summary>注册 AI 自检端点。</summary>
    /// <param name="app">端点路由构建器。</param>
    /// <returns>路由构建器。</returns>
    public static IEndpointRouteBuilder MapAiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ai/probe", (HttpContext ctx, AiConnectivityProbe probe, CancellationToken ct) =>
                RunProbeAsync(ctx, probe, ct))
            .WithTags("AI")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }

    /// <summary>POST /api/ai/probe — 执行一次连通性探测。</summary>
    /// <param name="ctx">当前请求上下文。</param>
    /// <param name="probe">连通性探针。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>探测结果。</returns>
    private static async Task<IResult> RunProbeAsync(
        HttpContext ctx, AiConnectivityProbe probe, CancellationToken ct)
    {
        var principal = (CurrentPrincipal?)ctx.Items["Principal"];
        if (principal is null) return Results.Unauthorized();

        // 允许调用方自定义测试句；为空则用内置的易混场景测试句。
        var request = ctx.Request.HasJsonContentType()
            ? await ctx.Request.ReadFromJsonAsync<AiProbeRequest>(ct)
            : null;

        var result = await probe.RunAsync(request?.Text, ct);

        return Results.Ok(new
        {
            result.ModelConfigured,
            result.CallSucceeded,
            result.MappedToKnownIntent,
            result.Model,
            result.RawOutput,
            result.NormalizedOutput,
            result.ResolvedIntent,
            result.ProbeText,
            result.ElapsedMs,
            result.Error,
            result.CheckedAt,
            result.Summary
        });
    }
}
