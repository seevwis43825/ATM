// ===== 全局异常处理中间件：捕获未处理异常并返回统一的 INTERNAL_ERROR 错误体 =====

using System.Text.Json;
using MockBank.Api.Contracts;

namespace MockBank.Api.Middleware;

/// <summary>全局异常处理中间件，保证任何未捕获异常都返回结构化错误体而不是连接中断。</summary>
/// <param name="next">下一个中间件。</param>
/// <param name="logger">日志记录器。</param>
public sealed class GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
{
    /// <summary>错误体序列化设置，与全局 JSON 配置保持 camelCase 一致。</summary>
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);
    /// <summary>执行管道；捕获异常并转换为 500 响应。</summary>
    /// <param name="context">当前 HTTP 上下文。</param>
    /// <returns>异步任务。</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation("请求被客户端取消：{Method} {Path}", context.Request.Method, context.Request.Path);
        }
        catch (Exception ex)
        {
            var traceId = context.TraceIdentifier;
            logger.LogError(ex, "未处理异常：{Method} {Path} TraceId={TraceId}",
                context.Request.Method, context.Request.Path, traceId);

            if (context.Response.HasStarted)
            {
                logger.LogWarning("响应已开始写出，无法返回错误体，仅记录日志。TraceId={TraceId}", traceId);
                return;
            }

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json; charset=utf-8";

            var error = new ApiErrorResponse(
                ErrorCodes.INTERNAL_ERROR,
                "模拟银行核心系统内部错误，请稍后重试或联系客服。",
                new Dictionary<string, object?>
                {
                    ["exception"] = ex.GetType().Name,
                    ["path"] = context.Request.Path.Value,
                },
                traceId);

            await JsonSerializer.SerializeAsync(context.Response.Body, error, WebJsonOptions, context.RequestAborted)
                .ConfigureAwait(false);
        }
    }
}
