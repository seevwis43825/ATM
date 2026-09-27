// ===== 理财端点：理财产品列表筛选、产品详情与认购 =====

using Microsoft.AspNetCore.Mvc;
using MockBank.Api.Contracts;
using MockBank.Api.Middleware;
using MockBank.Api.Services;

namespace MockBank.Api.Endpoints;

/// <summary>理财产品相关 Minimal API 端点。</summary>
public static class WealthEndpoints
{
    /// <summary>理财 API 根路径。</summary>
    public const string RoutePrefix = "/api/corebank/v1";

    /// <summary>注册理财端点。</summary>
    /// <param name="app">端点路由构建器。</param>
    /// <returns>路由构建器。</returns>
    public static IEndpointRouteBuilder MapWealthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(RoutePrefix).WithTags("理财");

        group.MapGet("/products", GetProducts)
            .WithMetadata(new ApiOperationMetadata("GetProducts", "查询理财产品列表，可按风险等级（R1-R4）与产品类型（稳健 / 平衡 / 进取）筛选。", "查询理财产品列表，可按风险等级（R1-R4）与产品类型（稳健 / 平衡 / 进取）筛选。", ["理财"]))
            .Produces<IReadOnlyList<ProductResponse>>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest);

        group.MapGet("/products/{code}", GetProduct)
            .WithMetadata(new ApiOperationMetadata("GetProductByCode", "按产品代码查询理财产品详情，含起投金额、期限、募集规模与风险提示。", "按产品代码查询理财产品详情，含起投金额、期限、募集规模与风险提示。", ["理财"]))
            .Produces<ProductResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/wealth/subscribe", PostSubscribe)
            .WithMetadata(new ApiOperationMetadata("PostWealthSubscribe", "认购理财产品：校验客户风险等级、起投金额、募集规模与储蓄账户余额后扣款记账。支持 X-Mock-Scenario 故障注入。", "认购理财产品：校验客户风险等级、起投金额、募集规模与储蓄账户余额后扣款记账。支持 X-Mock-Scenario 故障注入。", ["理财"], typeof(SubscribeRequest)))
            .Accepts<SubscribeRequest>("application/json")
            .Produces<SubscribeResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    /// <summary>GET /api/corebank/v1/products — 理财产品列表（可筛选）。</summary>
    /// <param name="riskLevel">风险等级 R1-R4，可为空。</param>
    /// <param name="type">产品类型 稳健 / 平衡 / 进取，可为空。</param>
    /// <param name="service">理财服务。</param>
    /// <returns>产品列表；筛选参数非法时返回 400。</returns>
    private static IResult GetProducts(
        [FromQuery] string? riskLevel,
        [FromQuery] string? type,
        WealthService service)
    {
        var products = service.QueryProducts(riskLevel, type, out var error, out var message);
        return products is null
            ? BadRequest(error, message, new Dictionary<string, object?>
            {
                ["riskLevel"] = riskLevel ?? "",
                ["type"] = type ?? "",
            })
            : Results.Ok(products);
    }

    /// <summary>GET /api/corebank/v1/products/{code} — 理财产品详情。</summary>
    /// <param name="code">产品代码。</param>
    /// <param name="service">理财服务。</param>
    /// <returns>产品详情；产品代码不存在时返回 404。</returns>
    private static IResult GetProduct(string code, WealthService service)
    {
        var product = service.GetProduct(code);
        return product is null
            ? BadRequestAsNotFound(ErrorCodes.PRODUCT_NOT_FOUND, $"产品代码 {code} 不存在。", code)
            : Results.Ok(product);
    }

    /// <summary>POST /api/corebank/v1/wealth/subscribe — 认购理财产品。</summary>
    /// <param name="request">认购请求体。</param>
    /// <param name="service">理财服务。</param>
    /// <param name="businessLog">业务日志写入器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>认购结果；校验失败返回 400 / 404 + 结构化错误体。</returns>
    private static async Task<IResult> PostSubscribe(
        [FromBody] SubscribeRequest? request,
        WealthService service,
        BusinessLogWriter businessLog,
        CancellationToken cancellationToken)
    {
        var outcome = await service.SubscribeAsync(request, cancellationToken).ConfigureAwait(false);

        var userId = string.IsNullOrWhiteSpace(request?.UserId) ? "-" : request!.UserId!;

        if (outcome.IsSuccess)
        {
            businessLog.Write(userId, "WEALTH_SUBSCRIBE", request?.Amount ?? 0m, $"SUCCESS:{outcome.Response!.ProductCode}");
            return Results.Ok(outcome.Response);
        }

        businessLog.Write(userId, "WEALTH_SUBSCRIBE", request?.Amount ?? 0m, $"FAILED:{outcome.Code}");
        return Results.Json(
            new ApiErrorResponse(
                outcome.Code,
                outcome.Message,
                new Dictionary<string, object?>
                {
                    ["userId"] = userId,
                    ["productCode"] = request?.ProductCode ?? "",
                },
                null),
            statusCode: outcome.StatusCode);
    }

    private static IResult BadRequest(string code, string message, IReadOnlyDictionary<string, object?> details) =>
        Results.Json(new ApiErrorResponse(code, message, details, null), statusCode: StatusCodes.Status400BadRequest);

    private static IResult BadRequestAsNotFound(string code, string message, string productCode) =>
        Results.Json(
            new ApiErrorResponse(
                code,
                message,
                new Dictionary<string, object?> { ["productCode"] = productCode },
                null),
            statusCode: StatusCodes.Status404NotFound);
}
