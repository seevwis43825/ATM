// ===== 卡片端点：客户卡片列表、挂失/冻结等状态变更与限额调整 =====

using Microsoft.AspNetCore.Mvc;
using MockBank.Api.Contracts;
using MockBank.Api.Services;

namespace MockBank.Api.Endpoints;

/// <summary>银行卡管理相关 Minimal API 端点。</summary>
public static class CardEndpoints
{
    /// <summary>卡片 API 根路径。</summary>
    public const string RoutePrefix = "/api/corebank/v1/cards";

    /// <summary>注册卡片端点。</summary>
    /// <param name="app">端点路由构建器。</param>
    /// <returns>路由构建器。</returns>
    public static IEndpointRouteBuilder MapCardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(RoutePrefix).WithTags("银行卡");

        group.MapGet("", GetCards)
            .WithMetadata(new ApiOperationMetadata("GetCardsByUser", "查询指定客户名下的全部银行卡，含卡类型、卡状态、绑定手机号与单日限额。", "查询指定客户名下的全部银行卡，含卡类型、卡状态、绑定手机号与单日限额。", ["银行卡"]))
            .Produces<IReadOnlyList<CardResponse>>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest);

        group.MapGet("/{cardNo}", GetCard)
            .WithMetadata(new ApiOperationMetadata("GetCardByNo", "按卡号查询单张银行卡的详细信息。", "按卡号查询单张银行卡的详细信息。", ["银行卡"]))
            .Produces<CardResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/{cardNo}/status", PostCardStatus)
            .WithMetadata(new ApiOperationMetadata("PostCardStatus", "变更卡片状态：挂失 / 解挂 / 冻结 / 解冻 / 恢复正常，并记录变更原因。", "变更卡片状态：挂失 / 解挂 / 冻结 / 解冻 / 恢复正常，并记录变更原因。", ["银行卡"], typeof(CardStatusChangeRequest)))
            .Accepts<CardStatusChangeRequest>("application/json")
            .Produces<CardStatusChangeResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/{cardNo}/limit", PostCardLimit)
            .WithMetadata(new ApiOperationMetadata("PostCardLimit", "调整卡片单日交易限额，变更同时联动所属账户的限额配置。", "调整卡片单日交易限额，变更同时联动所属账户的限额配置。", ["银行卡"], typeof(CardLimitAdjustRequest)))
            .Accepts<CardLimitAdjustRequest>("application/json")
            .Produces<CardLimitAdjustResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>GET /api/corebank/v1/cards?userId= — 查询客户全部卡片。</summary>
    /// <param name="userId">客户号，必填。</param>
    /// <param name="service">卡片服务。</param>
    /// <returns>卡片列表；userId 缺失时返回 400。</returns>
    private static IResult GetCards([FromQuery] string? userId, CardService service)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Results.Json(
                new ApiErrorResponse(
                    ErrorCodes.VALIDATION_ERROR,
                    "查询参数 userId 不能为空。",
                    new Dictionary<string, object?> { ["userId"] = userId ?? "" },
                    null),
                statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.Ok(service.GetCardsByUser(userId));
    }

    /// <summary>GET /api/corebank/v1/cards/{cardNo} — 查询单张卡片。</summary>
    /// <param name="cardNo">卡号。</param>
    /// <param name="service">卡片服务。</param>
    /// <returns>卡片详情；卡号不存在时返回 404。</returns>
    private static IResult GetCard(string cardNo, CardService service)
    {
        var card = service.GetCard(cardNo);
        return card is null ? NotFoundCard(cardNo) : Results.Ok(card);
    }

    /// <summary>POST /api/corebank/v1/cards/{cardNo}/status — 变更卡状态。</summary>
    /// <param name="cardNo">卡号。</param>
    /// <param name="request">状态变更请求体。</param>
    /// <param name="service">卡片服务。</param>
    /// <returns>变更结果；参数非法返回 400，卡号不存在返回 404。</returns>
    private static IResult PostCardStatus(
        string cardNo,
        [FromBody] CardStatusChangeRequest? request,
        CardService service)
    {
        var result = service.ChangeStatus(cardNo, request, out var error, out var message);
        if (result is null)
        {
            return Results.Json(
                new ApiErrorResponse(
                    error,
                    message,
                    new Dictionary<string, object?> { ["cardNo"] = cardNo },
                    null),
                statusCode: error == ErrorCodes.CARD_NOT_FOUND
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status400BadRequest);
        }

        return Results.Ok(result);
    }

    /// <summary>POST /api/corebank/v1/cards/{cardNo}/limit — 调整卡限额。</summary>
    /// <param name="cardNo">卡号。</param>
    /// <param name="request">限额调整请求体。</param>
    /// <param name="service">卡片服务。</param>
    /// <returns>调整结果；参数非法返回 400，卡号不存在返回 404。</returns>
    private static IResult PostCardLimit(
        string cardNo,
        [FromBody] CardLimitAdjustRequest? request,
        CardService service)
    {
        var result = service.AdjustLimit(cardNo, request, out var error, out var message);
        if (result is null)
        {
            return Results.Json(
                new ApiErrorResponse(
                    error,
                    message,
                    new Dictionary<string, object?> { ["cardNo"] = cardNo },
                    null),
                statusCode: error == ErrorCodes.CARD_NOT_FOUND
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status400BadRequest);
        }

        return Results.Ok(result);
    }

    private static IResult NotFoundCard(string cardNo) =>
        Results.Json(
            new ApiErrorResponse(
                ErrorCodes.CARD_NOT_FOUND,
                $"卡号 {cardNo} 不存在。",
                new Dictionary<string, object?> { ["cardNo"] = cardNo },
                null),
            statusCode: StatusCodes.Status404NotFound);
}
