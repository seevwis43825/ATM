// ===== 账单端点：按月汇总收入、支出、分类占比与日趋势 =====

using Microsoft.AspNetCore.Mvc;
using MockBank.Api.Contracts;
using MockBank.Api.Services;

namespace MockBank.Api.Endpoints;

/// <summary>月度账单相关 Minimal API 端点。</summary>
public static class StatementEndpoints
{
    /// <summary>账单 API 根路径。</summary>
    public const string RoutePrefix = "/api/corebank/v1/statements";

    /// <summary>注册账单端点。</summary>
    /// <param name="app">端点路由构建器。</param>
    /// <returns>路由构建器。</returns>
    public static IEndpointRouteBuilder MapStatementEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(RoutePrefix).WithTags("账单");

        group.MapGet("/{accountNo}", GetMonthlyStatement)
            .WithMetadata(new ApiOperationMetadata("GetMonthlyStatement", "查询指定账号的月度账单汇总：总收入、总支出、净流入、交易笔数、分类聚合与逐日趋势。month 参数格式为 yyyy-MM，缺省为当前自然月。", "查询指定账号的月度账单汇总：总收入、总支出、净流入、交易笔数、分类聚合与逐日趋势。month 参数格式为 yyyy-MM，缺省为当前自然月。", ["账单"]))
            .Produces<MonthlyStatementResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>GET /api/corebank/v1/statements/{accountNo}?month=2026-09 — 月度账单汇总。</summary>
    /// <param name="accountNo">账号。</param>
    /// <param name="month">月份（yyyy-MM），可为空表示当前月。</param>
    /// <param name="service">账单服务。</param>
    /// <returns>账单汇总；月份格式非法返回 400，账号不存在返回 404。</returns>
    private static IResult GetMonthlyStatement(
        string accountNo,
        [FromQuery] string? month,
        StatementService service)
    {
        if (!StatementService.TryParseMonth(month, out var year, out var monthOfYear))
        {
            return Results.Json(
                new ApiErrorResponse(
                    ErrorCodes.VALIDATION_ERROR,
                    $"月份参数格式非法：{month}，应为 yyyy-MM 格式。",
                    new Dictionary<string, object?> { ["month"] = month ?? "" },
                    null),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var statement = service.BuildMonthlyStatement(accountNo, year, monthOfYear);
        return statement is null
            ? Results.Json(
                new ApiErrorResponse(
                    ErrorCodes.ACCOUNT_NOT_FOUND,
                    $"账号 {accountNo} 不存在。",
                    new Dictionary<string, object?> { ["accountNo"] = accountNo },
                    null),
                statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(statement);
    }
}
