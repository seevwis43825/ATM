// ===== 契约：行内转账请求 / 响应契约 =====

namespace MockBank.Api.Contracts;

/// <summary>行内转账请求体。</summary>
/// <param name="FromAccountNo">付款账号。</param>
/// <param name="ToAccountNo">收款账号。</param>
/// <param name="Amount">转账金额（元），必须大于 0。</param>
/// <param name="Currency">币种，默认 CNY。</param>
/// <param name="Remark">转账附言。</param>
/// <param name="UserId">发起客户号，用于风控与审计。</param>
public sealed record TransferRequest(
    string? FromAccountNo,
    string? ToAccountNo,
    decimal Amount,
    string Currency = "CNY",
    string? Remark = null,
    string? UserId = null);

/// <summary>行内转账成功响应体。</summary>
/// <param name="TxNo">核心系统生成的交易流水号。</param>
/// <param name="FromAccountNo">付款账号。</param>
/// <param name="ToAccountNo">收款账号。</param>
/// <param name="Amount">实际转账金额。</param>
/// <param name="Currency">币种。</param>
/// <param name="FromBalanceAfter">扣款后付款账户余额。</param>
/// <param name="ToBalanceAfter">入账后收款账户余额。</param>
/// <param name="CompletedAt">交易完成时间。</param>
/// <param name="Message">结果描述。</param>
public sealed record TransferResponse(
    string TxNo,
    string FromAccountNo,
    string ToAccountNo,
    decimal Amount,
    string Currency,
    decimal FromBalanceAfter,
    decimal ToBalanceAfter,
    DateTimeOffset CompletedAt,
    string Message = "转账成功");

/// <summary>转账服务统一返回值，由端点层翻译为 HTTP 响应。</summary>
/// <param name="IsSuccess">是否成功。</param>
/// <param name="StatusCode">对应的 HTTP 状态码。</param>
/// <param name="Code">成功时为 OK，失败时为 <see cref="ErrorCodes"/> 中的错误码。</param>
/// <param name="Message">结果描述。</param>
/// <param name="Details">结构化错误明细。</param>
/// <param name="Response">成功时的转账结果。</param>
public sealed record TransferOutcome(
    bool IsSuccess,
    int StatusCode,
    string Code,
    string Message,
    IReadOnlyDictionary<string, object?>? Details,
    TransferResponse? Response)
{
    /// <summary>构造一个失败结果。</summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <param name="code">错误码。</param>
    /// <param name="message">错误描述。</param>
    /// <param name="details">结构化错误明细。</param>
    /// <returns>失败的 <see cref="TransferOutcome"/>。</returns>
    public static TransferOutcome Failure(int statusCode, string code, string message,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(false, statusCode, code, message, details, null);

    /// <summary>构造一个成功结果。</summary>
    /// <param name="response">转账结果。</param>
    /// <returns>成功的 <see cref="TransferOutcome"/>。</returns>
    public static TransferOutcome Success(TransferResponse response) =>
        new(true, StatusCodes.Status200OK, "OK", "转账成功", null, response);
}
