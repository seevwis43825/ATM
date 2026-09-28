// ===== 故障注入中间件：通过 X-Mock-Scenario 请求头模拟余额不足 / 超限 / 冻结 / 超时 / 下游故障 =====

using System.Text.Json;
using MockBank.Api.Contracts;
using MockBank.Api.Services;

namespace MockBank.Api.Middleware;

/// <summary>
/// 模拟故障注入中间件。仅在请求头 <c>X-Mock-Scenario</c> 命中已登记场景、
/// 且请求为对转账或理财认购端点的 POST 时生效。
///
/// 安全边界：本中间件**默认只在 Development 环境启用**。
/// 故障注入等于"凭一个请求头就能让转账全部失败或强制延迟 3 秒"，
/// 在被他人访问到的环境里就是一个可被滥用的可用性攻击面。
/// 非开发环境必须显式设置 <c>MockBank:EnableFaultInjection=true</c> 才开启。
/// </summary>
/// <param name="next">下一个中间件。</param>
/// <param name="logger">日志记录器。</param>
/// <param name="businessLog">业务日志写入器。</param>
/// <param name="environment">宿主环境，用于判断是否允许注入。</param>
/// <param name="configuration">配置，用于显式放行。</param>
public sealed class MockScenarioMiddleware(
    RequestDelegate next,
    ILogger<MockScenarioMiddleware> logger,
    BusinessLogWriter businessLog,
    IHostEnvironment environment,
    IConfiguration configuration)
{
    /// <summary>故障注入请求头名称。</summary>
    public const string HeaderName = "X-Mock-Scenario";

    /// <summary>所有支持的场景值，供响应头回显给调用方。</summary>
    public const string SupportedScenarios =
        "success,insufficient_funds,daily_limit_exceeded,account_frozen,timeout,downstream_error";

    /// <summary>timeout 场景模拟的下游响应时延（毫秒）。</summary>
    public const int TimeoutDelayMilliseconds = 3000;

    /// <summary>
    /// 是否允许故障注入。
    /// 默认仅 Development 开启；其他环境需要显式配置放行，
    /// 避免"演示用的开关"被无意带到公网环境。
    /// </summary>
    private bool FaultInjectionAllowed =>
        environment.IsDevelopment()
        || configuration.GetValue("MockBank:EnableFaultInjection", false);

    private const string TransferPath = "/api/corebank/v1/transfers";
    private const string SubscribePath = "/api/corebank/v1/wealth/subscribe";

    /// <summary>注入错误体的序列化设置，与全局 JSON 配置保持 camelCase 一致。</summary>
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>执行管道；命中场景时短路返回预设结果。</summary>
    /// <param name="context">当前 HTTP 上下文。</param>
    /// <returns>异步任务。</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        var scenario = context.Request.Headers[HeaderName].ToString().Trim();

        if (string.IsNullOrEmpty(scenario)
            || string.Equals(scenario, "success", StringComparison.OrdinalIgnoreCase)
            || !IsControlledRequest(context.Request))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // 非开发环境且未显式放行：忽略注入请求头，按正常流程处理。
        // 选择"忽略"而不是"报错"，是为了让调用方无需感知环境差异。
        if (!FaultInjectionAllowed)
        {
            logger.LogWarning(
                "拒绝故障注入请求（当前环境 {Environment} 未启用）：scenario={Scenario} path={Path}",
                environment.EnvironmentName, scenario, context.Request.Path);
            await next(context).ConfigureAwait(false);
            return;
        }

        var operation = IsTransfer(context.Request.Path) ? "TRANSFER" : "WEALTH_SUBSCRIBE";

        switch (scenario.ToLowerInvariant())
        {
            case "insufficient_funds":
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, ErrorCodes.INSUFFICIENT_FUNDS,
                    "付款账户余额不足（由 X-Mock-Scenario=insufficient_funds 强制模拟）。", operation, 0m)
                    .ConfigureAwait(false);
                return;

            case "daily_limit_exceeded":
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, ErrorCodes.DAILY_LIMIT_EXCEEDED,
                    "交易金额超过账户单日限额（由 X-Mock-Scenario=daily_limit_exceeded 强制模拟）。", operation, 0m)
                    .ConfigureAwait(false);
                return;

            case "account_frozen":
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, ErrorCodes.ACCOUNT_FROZEN,
                    "账户已被冻结，无法办理业务（由 X-Mock-Scenario=account_frozen 强制模拟）。", operation, 0m)
                    .ConfigureAwait(false);
                return;

            case "downstream_error":
                await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, ErrorCodes.SERVICE_UNAVAILABLE,
                    "下游核心 / 清算系统暂时不可用（由 X-Mock-Scenario=downstream_error 强制模拟）。", operation, 0m)
                    .ConfigureAwait(false);
                return;

            case "timeout":
                logger.LogWarning("注入慢响应场景：{Operation} 延迟 {Delay}ms", operation, TimeoutDelayMilliseconds);
                await Task.Delay(TimeoutDelayMilliseconds, context.RequestAborted).ConfigureAwait(false);
                await next(context).ConfigureAwait(false);
                return;

            default:
                logger.LogWarning("未知的 {Header} 场景值 {Scenario}，按正常流程处理。", HeaderName, scenario);
                await next(context).ConfigureAwait(false);
                return;
        }
    }

    private static bool IsControlledRequest(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) && (IsTransfer(request.Path) || IsSubscribe(request.Path));

    private static bool IsTransfer(PathString path) =>
        path.Equals(TransferPath, StringComparison.OrdinalIgnoreCase);

    private static bool IsSubscribe(PathString path) =>
        path.Equals(SubscribePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>写出注入的错误响应并记录业务日志。</summary>
    private async Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        string operation,
        decimal amount)
    {
        var (userId, parsedAmount) = await TryReadRequestHintAsync(context.Request, context.RequestAborted)
            .ConfigureAwait(false);
        var effectiveAmount = amount == 0m ? parsedAmount : amount;

        logger.LogWarning("注入故障场景 {Code}：{Method} {Path} -> {StatusCode}",
            code, context.Request.Method, context.Request.Path, statusCode);
        businessLog.Write(userId, $"{operation}(mock:{code})", effectiveAmount, $"FAILED:{code}");

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";

        var error = new ApiErrorResponse(
            code,
            message,
            new Dictionary<string, object?>
            {
                ["mockScenario"] = code,
                ["requestPath"] = context.Request.Path.Value,
                ["injectedAt"] = DateTimeOffset.Now,
            },
            context.TraceIdentifier);

        await JsonSerializer.SerializeAsync(context.Response.Body, error, WebJsonOptions, context.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>尝试从请求体中读取 userId / amount，仅用于让注入的业务日志更完整。</summary>
    /// <param name="request">当前请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>请求体中的客户号与金额；解析失败时返回 (null, 0)。</returns>
    private static async Task<(string? UserId, decimal Amount)> TryReadRequestHintAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is null or <= 0 || request.ContentLength > 64 * 1024)
        {
            return (null, 0m);
        }

        try
        {
            request.EnableBuffering();
            request.Body.Position = 0;
            using var document = await JsonDocument.ParseAsync(
                    request.Body, default(JsonDocumentOptions), cancellationToken)
                .ConfigureAwait(false);
            request.Body.Position = 0;

            var root = document.RootElement;
            var userId = root.TryGetProperty("userId", out var userIdElement)
                ? userIdElement.GetString()
                : null;
            var amount = root.TryGetProperty("amount", out var amountElement)
                && amountElement.ValueKind == JsonValueKind.Number
                && amountElement.TryGetDecimal(out var parsed)
                    ? parsed
                    : 0m;

            return (userId, amount);
        }
        catch (JsonException)
        {
            return (null, 0m);
        }
        finally
        {
            if (request.Body.CanSeek)
            {
                request.Body.Position = 0;
            }
        }
    }
}
