// ===== 公募基金端点：代销清单、基金详情、净值历史与排行 =====
//
// 路由设计说明：
//   /funds/rankings 是字面量路径，必须排在 /funds/{code} 之前注册，
//   否则 "rankings" 会被当成基金代码（ASP.NET Core 虽然优先匹配字面量，
//   但显式注册顺序能让人一眼看懂路由的优先级）。
//
// 参数约定：
//   * 非法参数一律返回 400 + 结构化错误体（ApiErrorResponse），不返回空列表 —
//     空列表会让调用方误以为「没有这类基金」，而真实原因是「参数写错了」。

using Microsoft.AspNetCore.Mvc;
using MockBank.Api.Contracts;
using MockBank.Api.Services;

namespace MockBank.Api.Endpoints;

/// <summary>公募基金相关 Minimal API 端点。</summary>
public static class FundEndpoints
{
    /// <summary>公募基金 API 根路径。</summary>
    public const string RoutePrefix = "/api/corebank/v1";

    /// <summary>注册公募基金端点。</summary>
    /// <param name="app">端点路由构建器。</param>
    /// <returns>路由构建器。</returns>
    public static IEndpointRouteBuilder MapFundEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(RoutePrefix).WithTags("公募基金");

        group.MapGet("/funds", GetFunds)
            .WithMetadata(new ApiOperationMetadata("GetFunds", "查询公募基金代销清单，可按基金类型、风险等级（R1-R5）、基金公司、代码或名称模糊筛选。", "查询公募基金代销清单，可按基金类型、风险等级（R1-R5）、基金公司、代码或名称模糊筛选。", ["公募基金"]))
            .Produces<IReadOnlyList<FundResponse>>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest);

        group.MapGet("/funds/rankings", GetFundRankings)
            .WithMetadata(new ApiOperationMetadata("GetFundRankings", "查询公募基金排行，可按日增长率（daily）、近一月（monthly）、近一年（yearly）排序。", "查询公募基金排行，可按日增长率（daily）、近一月（monthly）、近一年（yearly）排序。", ["公募基金"]))
            .Produces<IReadOnlyList<FundResponse>>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest);

        group.MapGet("/funds/{code}", GetFund)
            .WithMetadata(new ApiOperationMetadata("GetFundByCode", "按基金代码查询公募基金详情，含基金公司、基金经理、费率、规模、申赎状态与风险提示。", "按基金代码查询公募基金详情，含基金公司、基金经理、费率、规模、申赎状态与风险提示。", ["公募基金"]))
            .Produces<FundResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound);

        group.MapGet("/funds/{code}/nav", GetFundNav)
            .WithMetadata(new ApiOperationMetadata("GetFundNavByCode", "按基金代码查询净值历史，默认最近 60 天，最多 365 天。", "按基金代码查询净值历史，默认最近 60 天，最多 365 天。", ["公募基金"]))
            .Produces<FundNavResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>GET /api/corebank/v1/funds — 公募基金代销清单（可筛选）。</summary>
    /// <param name="category">基金类型，可为空表示不限。</param>
    /// <param name="riskLevel">基金风险等级 R1-R5，可为空表示不限。</param>
    /// <param name="company">基金公司，可为空表示不限。</param>
    /// <param name="keyword">基金代码或简称，可为空表示不限。</param>
    /// <param name="bankCode">限定代销行代码，可为空表示不限。</param>
    /// <param name="limit">最多返回多少只，默认 20，最大 100。</param>
    /// <param name="service">公募基金服务。</param>
    /// <returns>基金列表；筛选参数非法时返回 400。</returns>
    private static IResult GetFunds(
        [FromQuery] string? category,
        [FromQuery] string? riskLevel,
        [FromQuery] string? company,
        [FromQuery] string? keyword,
        [FromQuery] string? bankCode,
        [FromQuery] int? limit,
        FundService service)
    {
        var funds = service.QueryFunds(
            category, riskLevel, company, keyword, bankCode, limit ?? 0, out var error, out var message);

        return funds is null
            ? BadRequest(error, message, new Dictionary<string, object?>
            {
                ["category"] = category ?? "",
                ["riskLevel"] = riskLevel ?? "",
                ["company"] = company ?? "",
                ["keyword"] = keyword ?? "",
                ["bankCode"] = bankCode ?? "",
                ["limit"] = limit ?? 0,
            })
            : Results.Ok(funds);
    }

    /// <summary>GET /api/corebank/v1/funds/rankings — 公募基金排行。</summary>
    /// <param name="metric">排行维度：daily / monthly / yearly，默认 daily。</param>
    /// <param name="limit">最多返回多少只，默认 10，最大 50。</param>
    /// <param name="service">公募基金服务。</param>
    /// <returns>排行后的基金列表；参数非法时返回 400。</returns>
    private static IResult GetFundRankings(
        [FromQuery] string? metric,
        [FromQuery] int? limit,
        FundService service)
    {
        var rankings = service.GetRankings(metric, limit ?? 0, out var error, out var message);

        return rankings is null
            ? BadRequest(error, message, new Dictionary<string, object?>
            {
                ["metric"] = metric ?? "",
                ["limit"] = limit ?? 0,
            })
            : Results.Ok(rankings);
    }

    /// <summary>GET /api/corebank/v1/funds/{code} — 公募基金详情。</summary>
    /// <param name="code">基金代码。</param>
    /// <param name="service">公募基金服务。</param>
    /// <returns>基金详情；没有银行代销该基金时返回 404。</returns>
    private static IResult GetFund(string code, FundService service)
    {
        var fund = service.GetFund(code);
        return fund is null
            ? NotFound($"基金代码 {code} 不在任何银行的代销清单中。", code)
            : Results.Ok(fund);
    }

    /// <summary>GET /api/corebank/v1/funds/{code}/nav — 公募基金净值历史。</summary>
    /// <param name="code">基金代码。</param>
    /// <param name="days">回溯天数，默认 60，最大 365。</param>
    /// <param name="service">公募基金服务。</param>
    /// <returns>基金详情与净值序列；参数非法返回 400，基金不存在返回 404。</returns>
    private static IResult GetFundNav(string code, [FromQuery] int? days, FundService service)
    {
        var result = service.GetFundNav(code, days ?? 0, out var error, out var message);
        if (result is null)
        {
            // 参数非法与基金不存在要分开：前者是 400，后者是 404。
            var isValidationError = error == ErrorCodes.VALIDATION_ERROR;
            return isValidationError
                ? BadRequest(error, message, new Dictionary<string, object?>
                {
                    ["fundCode"] = code,
                    ["days"] = days ?? 0,
                })
                : NotFound(message, code);
        }

        return Results.Ok(result);
    }

    private static IResult BadRequest(string code, string message, IReadOnlyDictionary<string, object?> details) =>
        Results.Json(new ApiErrorResponse(code, message, details, null), statusCode: StatusCodes.Status400BadRequest);

    private static IResult NotFound(string message, string fundCode) =>
        Results.Json(
            new ApiErrorResponse(
                "FUND_NOT_FOUND",
                message,
                new Dictionary<string, object?> { ["fundCode"] = fundCode },
                null),
            statusCode: StatusCodes.Status404NotFound);
}
