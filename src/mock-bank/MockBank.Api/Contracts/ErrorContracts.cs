// ===== 契约：统一错误响应体（code / message / details / traceId）=====

namespace MockBank.Api.Contracts;

/// <summary>统一错误响应体。所有非 2xx 响应均返回该结构。</summary>
/// <param name="Code">机器可读的错误码，如 INSUFFICIENT_FUNDS。</param>
/// <param name="Message">面向用户的中文错误描述。</param>
/// <param name="Details">结构化错误明细，可为空。</param>
/// <param name="TraceId">请求追踪号，便于与日志对照。</param>
public sealed record ApiErrorResponse(
    string Code,
    string Message,
    IReadOnlyDictionary<string, object?>? Details = null,
    string? TraceId = null);

/// <summary>错误码常量表，供端点与 AI Agent 调用方约定。</summary>
public static class ErrorCodes
{
    /// <summary>参数校验失败（通用）。</summary>
    public const string VALIDATION_ERROR = "VALIDATION_ERROR";

    /// <summary>请求体缺失或格式错误。</summary>
    public const string BAD_REQUEST = "BAD_REQUEST";

    /// <summary>账户不存在。</summary>
    public const string ACCOUNT_NOT_FOUND = "ACCOUNT_NOT_FOUND";

    /// <summary>客户不存在。</summary>
    public const string CUSTOMER_NOT_FOUND = "CUSTOMER_NOT_FOUND";

    /// <summary>卡片不存在。</summary>
    public const string CARD_NOT_FOUND = "CARD_NOT_FOUND";

    /// <summary>理财产品不存在。</summary>
    public const string PRODUCT_NOT_FOUND = "PRODUCT_NOT_FOUND";

    /// <summary>交易流水不存在。</summary>
    public const string TRANSACTION_NOT_FOUND = "TRANSACTION_NOT_FOUND";

    /// <summary>账户余额不足。</summary>
    public const string INSUFFICIENT_FUNDS = "INSUFFICIENT_FUNDS";

    /// <summary>超出账户单日限额。</summary>
    public const string DAILY_LIMIT_EXCEEDED = "DAILY_LIMIT_EXCEEDED";

    /// <summary>账户已被冻结，无法办理业务。</summary>
    public const string ACCOUNT_FROZEN = "ACCOUNT_FROZEN";

    /// <summary>账户状态异常（销户、挂失等）。</summary>
    public const string ACCOUNT_STATUS_ABNORMAL = "ACCOUNT_STATUS_ABNORMAL";

    /// <summary>收付款账号相同。</summary>
    public const string SAME_ACCOUNT = "SAME_ACCOUNT";

    /// <summary>币种不一致。</summary>
    public const string CURRENCY_MISMATCH = "CURRENCY_MISMATCH";

    /// <summary>风险等级不匹配，客户不能购买该风险等级产品。</summary>
    public const string RISK_LEVEL_MISMATCH = "RISK_LEVEL_MISMATCH";

    /// <summary>认购金额低于起投金额。</summary>
    public const string AMOUNT_BELOW_MINIMUM = "AMOUNT_BELOW_MINIMUM";

    /// <summary>认购金额超过单笔上限。</summary>
    public const string AMOUNT_ABOVE_MAXIMUM = "AMOUNT_ABOVE_MAXIMUM";

    /// <summary>产品募集规模已满。</summary>
    public const string PRODUCT_SOLD_OUT = "PRODUCT_SOLD_OUT";

    /// <summary>产品不在售。</summary>
    public const string PRODUCT_NOT_ON_SALE = "PRODUCT_NOT_ON_SALE";

    /// <summary>下游（核心 / 清算）系统不可用。</summary>
    public const string SERVICE_UNAVAILABLE = "SERVICE_UNAVAILABLE";

    /// <summary>服务器内部异常。</summary>
    public const string INTERNAL_ERROR = "INTERNAL_ERROR";
}
