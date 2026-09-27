// ===== 账户端点：账户列表、余额查询与限额配置 =====

using Microsoft.AspNetCore.Mvc;
using MockBank.Api.Contracts;
using MockBank.Api.Services;

namespace MockBank.Api.Endpoints;

/// <summary>账户相关 Minimal API 端点。</summary>
public static class AccountEndpoints
{
    /// <summary>模拟银行核心系统 API 根路径。</summary>
    public const string RoutePrefix = "/api/corebank/v1/accounts";

    /// <summary>注册账户查询端点。</summary>
    /// <param name="app">端点路由构建器。</param>
    /// <returns>路由构建器。</returns>
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(RoutePrefix).WithTags("账户");

        group.MapGet("", GetAccounts)
            .WithMetadata(new ApiOperationMetadata("GetAccountsByUser", "查询指定客户名下的全部银行账户（每个客户默认含一张储蓄卡与一张信用卡）。", "查询指定客户名下的全部银行账户（每个客户默认含一张储蓄卡与一张信用卡）。", ["账户"]))
            .Produces<IReadOnlyList<AccountResponse>>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest);

        group.MapGet("/{accountNo}/balance", GetBalance)
            .WithMetadata(new ApiOperationMetadata("GetAccountBalance", "查询指定账号的实时余额、信用额度与账户状态。", "查询指定账号的实时余额、信用额度与账户状态。", ["账户"]))
            .Produces<AccountBalanceResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound);

        group.MapGet("/{accountNo}/limit", GetLimit)
            .WithMetadata(new ApiOperationMetadata("GetAccountLimit", "查询指定账号的单日限额、单笔限额与剩余可用信用额度。", "查询指定账号的单日限额、单笔限额与剩余可用信用额度。", ["账户"]))
            .Produces<AccountLimitResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>GET /api/corebank/v1/accounts?userId= — 查询客户全部账户。</summary>
    /// <param name="userId">客户号，必填。</param>
    /// <param name="service">账户服务。</param>
    /// <returns>账户列表；userId 缺失时返回 400。</returns>
    private static IResult GetAccounts([FromQuery] string? userId, AccountService service)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Results.BadRequest(new ApiErrorResponse(
                ErrorCodes.VALIDATION_ERROR,
                "查询参数 userId 不能为空。",
                new Dictionary<string, object?> { ["userId"] = userId ?? "" },
                null));
        }

        var accounts = service.GetAccountsByUser(userId);
        return Results.Ok(accounts);
    }

    /// <summary>GET /api/corebank/v1/accounts/{accountNo}/balance — 查询账户余额。</summary>
    /// <param name="accountNo">账号。</param>
    /// <param name="service">账户服务。</param>
    /// <returns>余额信息；账号不存在时返回 404。</returns>
    private static IResult GetBalance(string accountNo, AccountService service)
    {
        var balance = service.GetBalance(accountNo);
        return balance is null
            ? NotFoundAccount(accountNo)
            : Results.Ok(balance);
    }

    /// <summary>GET /api/corebank/v1/accounts/{accountNo}/limit — 查询账户限额配置。</summary>
    /// <param name="accountNo">账号。</param>
    /// <param name="service">账户服务。</param>
    /// <returns>限额配置；账号不存在时返回 404。</returns>
    private static IResult GetLimit(string accountNo, AccountService service)
    {
        var limit = service.GetLimit(accountNo);
        return limit is null
            ? NotFoundAccount(accountNo)
            : Results.Ok(limit);
    }

    private static IResult NotFoundAccount(string accountNo) =>
        Results.Json(
            new ApiErrorResponse(
                ErrorCodes.ACCOUNT_NOT_FOUND,
                $"账号 {accountNo} 不存在。",
                new Dictionary<string, object?> { ["accountNo"] = accountNo },
                null),
            statusCode: StatusCodes.Status404NotFound);
}
