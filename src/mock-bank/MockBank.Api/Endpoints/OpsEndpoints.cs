// ===== 运维端点：健康检查、OpenAPI 文档与管理端演示统计 =====

using System.Text.Json;
using MockBank.Api.Contracts;
using MockBank.Api.Data;
using MockBank.Api.Domain;
using MockBank.Api.Services;

namespace MockBank.Api.Endpoints;

/// <summary>健康检查、OpenAPI 文档与管理端统计 Minimal API 端点。</summary>
public static class OpsEndpoints
{
    /// <summary>服务版本号。</summary>
    public const string ServiceVersion = "1.0.0-mock";

    // JsonNode.ToJsonString 要求 JsonSerializerOptions 显式提供 TypeInfoResolver（.NET 8 起）。
    private static readonly JsonSerializerOptions OpenApiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    private static readonly DateTimeOffset ProcessStartTime = DateTimeOffset.Now;

    /// <summary>注册运维端点。</summary>
    /// <param name="app">端点路由构建器。</param>
    /// <returns>路由构建器。</returns>
    public static IEndpointRouteBuilder MapOpsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", GetHealth)
            .WithTags("运维")
            .WithMetadata(new ApiOperationMetadata("GetHealth", "健康检查探针，进程存活且账本已加载时返回 status=OK。", "健康检查探针，进程存活且账本已加载时返回 status=OK。", ["运维"]))
            .Produces<HealthResponse>(StatusCodes.Status200OK);

        app.MapGet("/api/corebank/v1/_admin/stats", GetAdminStats)
            .WithTags("运维")
            .WithMetadata(new ApiOperationMetadata("GetAdminStats", "管理端演示统计：客户数、账户数、卡片数、流水笔数与总资产（总托管资产）。", "管理端演示统计：客户数、账户数、卡片数、流水笔数与总资产（总托管资产）。", ["运维"]))
            .Produces<AdminStatsResponse>(StatusCodes.Status200OK);

        app.MapGet("/openapi/v1.json", GetOpenApiDocument)
            .WithTags("运维")
            .WithMetadata(new ApiOperationMetadata("GetOpenApiDocument", "返回本服务的 OpenAPI 3.0 文档（由 ApiExplorer 端点元数据与 CLR 反射生成）。", "返回本服务的 OpenAPI 3.0 文档（由 ApiExplorer 端点元数据与 CLR 反射生成）。", ["运维"]))
            .Produces<string>(StatusCodes.Status200OK, "application/json");

        return app;
    }

    /// <summary>GET /openapi/v1.json — 输出 OpenAPI 3.0 文档。</summary>
    /// <param name="builder">OpenAPI 文档生成器。</param>
    /// <returns>OpenAPI 文档 JSON 文本。</returns>
    private static IResult GetOpenApiDocument(OpenApiDocumentBuilder builder) =>
        Results.Text(builder.Build().ToJsonString(OpenApiJsonOptions), "application/json; charset=utf-8");

    /// <summary>GET /health — 健康检查。</summary>
    /// <param name="store">账本，用于确认种子数据已加载。</param>
    /// <returns>健康状态响应体。</returns>
    private static IResult GetHealth(MockBankStore store)
    {
        var ready = store.Customers.Count > 0 && store.AllAccounts.Count > 0;
        var response = new HealthResponse(
            ready ? "OK" : "DEGRADED",
            MockBankStore.BankName,
            ServiceVersion,
            DateTimeOffset.Now,
            Math.Round((DateTimeOffset.Now - ProcessStartTime).TotalSeconds, 1));

        return Results.Ok(response);
    }

    /// <summary>GET /api/corebank/v1/_admin/stats — 管理端统计。</summary>
    /// <param name="store">账本。</param>
    /// <returns>统计响应体。</returns>
    private static IResult GetAdminStats(MockBankStore store)
    {
        var accounts = store.AllAccounts;
        var cards = store.AllCards;
        var now = DateTimeOffset.Now;
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, store.LocalOffset);
        var monthCount = store.QueryTransactions(null, null, monthStart, null).Count;

        var depositTotal = accounts.Where(a => a.AccountType == AccountType.Savings).Sum(a => a.Balance);
        var creditUsed = accounts.Where(a => a.AccountType == AccountType.Credit).Sum(a => a.CreditUsed);
        var creditLimit = accounts.Where(a => a.AccountType == AccountType.Credit).Sum(a => a.CreditLimit);
        var aum = store.QueryProducts(null, null).Sum(p => p.RaisedAmount);

        var stats = new AdminStatsResponse(
            store.Customers.Count,
            accounts.Count,
            cards.Count,
            store.QueryTransactions(null).Count,
            depositTotal,
            creditUsed,
            creditLimit,
            aum,
            depositTotal + aum,
            now.ToString("yyyy-MM"),
            monthCount,
            now);

        return Results.Ok(stats);
    }
}
