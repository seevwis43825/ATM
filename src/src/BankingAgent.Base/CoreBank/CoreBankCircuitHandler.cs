// ===== 核心银行调用的熔断 HTTP 处理器 =====
//
// 放在 HTTP 管线层而不是改 CoreBankClient 的每个方法：
//   客户端有 20+ 个方法逐个调 HTTP，逐个改既啰嗦又容易漏；
//   在管线层统一拦截，改动只有"注册时加一个 handler"这一处，
//   且对所有下游调用（包括以后新增的端点）自动生效。

namespace BankingAgent.Base.CoreBank;

using System.Net.Http;
using Microsoft.Extensions.Logging;

/// <summary>熔断处理器：核心系统不可用时快速失败，避免请求线程被超时拖死。</summary>
public sealed class CoreBankCircuitHandler(
    CoreBankCircuitBreaker breaker,
    ILogger<CoreBankCircuitHandler> logger) : DelegatingHandler
{
    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!breaker.TryEnter())
        {
            logger.LogWarning(
                "核心银行熔断已打开，快速失败（不再发起调用）：{Method} {Uri}",
                request.Method, request.RequestUri);
            throw new CoreBankUnavailableException(
                $"核心系统当前不可用（熔断已打开），已快速失败：{request.Method} {request.RequestUri}");
        }

        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            breaker.ReportSuccess();
            return response;
        }
        catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
        {
            breaker.ReportFailure();
            logger.LogWarning(ex,
                "核心银行调用失败（连续失败 {Failures} 次）：{Method} {Uri}",
                breaker.ConsecutiveFailures, request.Method, request.RequestUri);
            throw;
        }
    }

    /// <summary>
    /// 判断异常是否属于「传输层失败」（下游不可达），而非业务错误或调用方取消。
    /// </summary>
    /// <param name="ex">异常。</param>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    /// <returns>true = 传输层失败，应计入熔断。</returns>
    private static bool IsTransportFailure(Exception ex, CancellationToken cancellationToken) =>
        ex switch
        {
            HttpRequestException => true,
            // 超时也会抛 TaskCanceledException；调用方主动取消不算下游故障
            TaskCanceledException => !cancellationToken.IsCancellationRequested,
            IOException => true,
            _ => false
        };
}
