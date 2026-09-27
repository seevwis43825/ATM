// ===== 契约：理财产品查询与认购契约 =====

using MockBank.Api.Domain;

namespace MockBank.Api.Contracts;

/// <summary>理财产品查询响应体。</summary>
/// <param name="Code">产品代码。</param>
/// <param name="Name">产品名称。</param>
/// <param name="Type">产品类型中文名（稳健 / 平衡 / 进取）。</param>
/// <param name="RiskLevel">风险等级文本（R1-R4）。</param>
/// <param name="AnnualRate">年化收益率（百分数，3.20 表示 3.20%）。</param>
/// <param name="MinAmount">起投金额。</param>
/// <param name="MaxAmount">单笔最高认购金额。</param>
/// <param name="TermDays">产品期限（天）。</param>
/// <param name="Status">销售状态中文名。</param>
/// <param name="NetAssetValue">单位净值。</param>
/// <param name="TotalRaiseLimit">募集规模上限。</param>
/// <param name="RaisedAmount">已募集金额。</param>
/// <param name="RemainingAmount">剩余可募集金额。</param>
/// <param name="SaleStartDate">发售开始日期。</param>
/// <param name="SaleEndDate">发售结束日期。</param>
/// <param name="RiskNote">风险提示。</param>
public sealed record ProductResponse(
    string Code,
    string Name,
    string Type,
    string RiskLevel,
    decimal AnnualRate,
    decimal MinAmount,
    decimal MaxAmount,
    int TermDays,
    string Status,
    decimal NetAssetValue,
    decimal TotalRaiseLimit,
    decimal RaisedAmount,
    decimal RemainingAmount,
    DateTimeOffset SaleStartDate,
    DateTimeOffset SaleEndDate,
    string RiskNote);

/// <summary>理财产品认购请求体。</summary>
/// <param name="UserId">认购客户号。</param>
/// <param name="ProductCode">理财产品代码。</param>
/// <param name="Amount">认购金额（元）。</param>
public sealed record SubscribeRequest(string? UserId, string? ProductCode, decimal Amount);

/// <summary>理财产品认购成功响应体。</summary>
/// <param name="SubscribeNo">认购流水号。</param>
/// <param name="UserId">客户号。</param>
/// <param name="ProductCode">产品代码。</param>
/// <param name="ProductName">产品名称。</param>
/// <param name="Amount">认购金额。</param>
/// <param name="DebitAccountNo">扣款账号（客户储蓄账户）。</param>
/// <param name="DebitBalanceAfter">扣款后账户余额。</param>
/// <param name="RiskLevel">产品风险等级文本。</param>
/// <param name="CustomerRiskLevel">客户风险等级文本。</param>
/// <param name="ExpectedAnnualRate">预期年化收益率（百分数）。</param>
/// <param name="TermDays">产品期限（天）。</param>
/// <param name="SubscribedAt">认购时间。</param>
/// <param name="Message">结果描述。</param>
public sealed record SubscribeResponse(
    string SubscribeNo,
    string UserId,
    string ProductCode,
    string ProductName,
    decimal Amount,
    string DebitAccountNo,
    decimal DebitBalanceAfter,
    string RiskLevel,
    string CustomerRiskLevel,
    decimal ExpectedAnnualRate,
    int TermDays,
    DateTimeOffset SubscribedAt,
    string Message = "认购成功");

/// <summary>理财认购服务统一返回值，由端点层翻译为 HTTP 响应。</summary>
/// <param name="IsSuccess">是否认购成功。</param>
/// <param name="StatusCode">对应的 HTTP 状态码。</param>
/// <param name="Code">成功时为 OK，失败时为 <see cref="ErrorCodes"/> 中的错误码。</param>
/// <param name="Message">结果描述。</param>
/// <param name="Response">认购成功时的结果。</param>
public sealed record SubscribeOutcome(
    bool IsSuccess,
    int StatusCode,
    string Code,
    string Message,
    SubscribeResponse? Response)
{
    /// <summary>构造一个认购失败结果。</summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <param name="code">错误码。</param>
    /// <param name="message">错误描述。</param>
    /// <returns>失败的 <see cref="SubscribeOutcome"/>。</returns>
    public static SubscribeOutcome Failure(int statusCode, string code, string message) =>
        new(false, statusCode, code, message, null);

    /// <summary>构造一个认购成功结果。</summary>
    /// <param name="response">认购结果。</param>
    /// <returns>成功的 <see cref="SubscribeOutcome"/>。</returns>
    public static SubscribeOutcome Success(SubscribeResponse response) =>
        new(true, StatusCodes.Status200OK, "OK", "认购成功", response);
}

/// <summary>把理财产品实体映射为对外响应体。</summary>
public static class ProductMapping
{
    /// <summary>实体转 DTO。</summary>
    /// <param name="product">理财产品实体。</param>
    /// <returns>理财产品查询响应体。</returns>
    public static ProductResponse ToResponse(this WealthProduct product) =>
        new(product.Code, product.Name, product.TypeText, product.RiskLevel.ToText(), product.AnnualRate,
            product.MinAmount, product.MaxAmount, product.TermDays, product.StatusText, product.NetAssetValue,
            product.TotalRaiseLimit, product.RaisedAmount, product.RemainingAmount,
            product.SaleStartDate, product.SaleEndDate, product.RiskNote);
}
