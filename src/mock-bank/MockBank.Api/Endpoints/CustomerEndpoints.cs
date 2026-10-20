// ===== 客户档案端点 =====
//
// 为什么需要这个端点：客户风险等级是**行内**评级，理财场景必须用它做风险匹配
// （银行理财 R2 客户不能买 R4 产品，这是合规硬要求）。
// 插件拿不到核心系统的客户主档，风险匹配就无从谈起。
//
// 注意：响应**不含**身份证号与手机号 —— 插件做风险匹配只需要评级，不需要这些。

using MockBank.Api.Contracts;
using MockBank.Api.Data;
using MockBank.Api.Domain;

namespace MockBank.Api.Endpoints;

/// <summary>客户档案相关 Minimal API 端点。</summary>
public static class CustomerEndpoints
{
    /// <summary>客户档案 API 根路径。</summary>
    public const string RoutePrefix = "/api/corebank/v1";

    /// <summary>注册客户档案端点。</summary>
    /// <param name="app">端点路由构建器。</param>
    /// <returns>路由构建器。</returns>
    public static IEndpointRouteBuilder MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(RoutePrefix).WithTags("客户档案");

        group.MapGet("/customers/{userId}", GetCustomer)
            .WithMetadata(new ApiOperationMetadata("GetCustomerById", "按客户号查询该客户在各开户行的客户档案，含行内风险等级（R1-R4）与客户等级。用于理财风险匹配。响应不含身份证号与手机号。", "按客户号查询该客户在各开户行的客户档案，含行内风险等级（R1-R4）与客户等级。用于理财风险匹配。响应不含身份证号与手机号。", ["客户档案"]))
            .Produces<IReadOnlyList<CustomerResponse>>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>GET /api/corebank/v1/customers/{userId} — 客户档案。</summary>
    /// <param name="userId">客户号。</param>
    /// <param name="store">账本门面。</param>
    /// <returns>该客户在各行的档案列表；客户不存在时返回 404。</returns>
    private static IResult GetCustomer(string userId, MockBankStore store)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Results.Json(
                new ApiErrorResponse(
                    ErrorCodes.VALIDATION_ERROR,
                    "客户号不能为空。",
                    new Dictionary<string, object?> { ["userId"] = userId ?? "" },
                    null),
                statusCode: StatusCodes.Status400BadRequest);
        }

        // 同一个客户号在多家银行各有一份主档（客户评级是行内评级），
        // 因此这里返回全部，主调用方按行选取。
        var customers = store.GetCustomersByUser(userId);
        if (customers.Count == 0)
        {
            return Results.Json(
                new ApiErrorResponse(
                    ErrorCodes.CUSTOMER_NOT_FOUND,
                    $"客户号 {userId} 不存在。",
                    new Dictionary<string, object?> { ["userId"] = userId },
                    null),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(customers.Select(c => c.ToResponse()).ToList());
    }
}
