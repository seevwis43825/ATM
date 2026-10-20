// ===== 银行目录端点：列出被模拟的银行、各行数据规模，以及重置演示数据 =====

using MockBank.Api.Contracts;
using MockBank.Api.Data;
using MockBank.Api.Domain;

namespace MockBank.Api.Endpoints;

/// <summary>银行目录相关 Minimal API 端点。</summary>
public static class BankEndpoints
{
    /// <summary>银行目录 API 根路径。</summary>
    public const string RoutePrefix = "/api/corebank/v1";

    /// <summary>注册银行目录端点。</summary>
    /// <param name="app">端点路由构建器。</param>
    /// <returns>路由构建器。</returns>
    public static IEndpointRouteBuilder MapBankEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(RoutePrefix).WithTags("银行目录");

        group.MapGet("/banks", GetBanks)
            .WithMetadata(new ApiOperationMetadata("GetBanks", "列出被模拟的全部银行及其独立数据库文件与各行数据规模（客户 / 账户 / 卡 / 流水 / 理财）。", "列出被模拟的全部银行及其独立数据库文件与各行数据规模（客户 / 账户 / 卡 / 流水 / 理财）。", ["银行目录"]))
            .Produces<IReadOnlyList<BankOverviewResponse>>(StatusCodes.Status200OK);

        group.MapGet("/banks/{bankCode}", GetBank)
            .WithMetadata(new ApiOperationMetadata("GetBankByCode", "按行代码查询单家银行的信息与数据规模。", "按行代码查询单家银行的信息与数据规模。", ["银行目录"]))
            .Produces<BankOverviewResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/_admin/reset", PostReset)
            .WithMetadata(new ApiOperationMetadata("PostAdminReset", "重置全部银行数据库：清空每家银行的独立数据库并重新写入该行种子数据，用于恢复干净的演示状态。", "重置全部银行数据库：清空每家银行的独立数据库并重新写入该行种子数据，用于恢复干净的演示状态。", ["银行目录"]))
            .Produces<ResetResponse>(StatusCodes.Status200OK);

        return app;
    }

    /// <summary>GET /api/corebank/v1/banks — 银行目录。</summary>
    /// <param name="store">账本门面。</param>
    /// <param name="factory">账本工厂，用于取各行数据规模。</param>
    /// <returns>银行目录与各行数据规模。</returns>
    private static IResult GetBanks(MockBankStore store, BankLedgerFactory factory)
    {
        var overviews = store.Banks
            .Select(bank => BuildOverview(bank, factory))
            .ToList();
        return Results.Ok(overviews);
    }

    /// <summary>GET /api/corebank/v1/banks/{bankCode} — 单家银行详情。</summary>
    /// <param name="bankCode">行代码。</param>
    /// <param name="store">账本门面。</param>
    /// <param name="factory">账本工厂。</param>
    /// <returns>银行详情；行代码不存在时返回 404。</returns>
    private static IResult GetBank(string bankCode, MockBankStore store, BankLedgerFactory factory)
    {
        var bank = BankCatalog.Find(bankCode);
        if (bank is null)
        {
            return Results.Json(
                new ApiErrorResponse(
                    "BANK_NOT_FOUND",
                    $"行代码 {bankCode} 不存在。可用行代码：{string.Join(" / ", BankCatalog.All.Select(b => b.BankCode))}。",
                    new Dictionary<string, object?> { ["bankCode"] = bankCode },
                    null),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(BuildOverview(bank, factory));
    }

    /// <summary>POST /api/corebank/v1/_admin/reset — 重置全部银行数据库。</summary>
    /// <param name="factory">账本工厂。</param>
    /// <returns>重置结果。</returns>
    private static IResult PostReset(BankLedgerFactory factory)
    {
        factory.ResetAll();
        return Results.Ok(new ResetResponse(
            DateTimeOffset.Now,
            "已重置全部银行数据库并重新写入种子数据。"));
    }

    /// <summary>组装单家银行的概览信息。</summary>
    /// <param name="bank">银行描述符。</param>
    /// <param name="factory">账本工厂。</param>
    /// <returns>银行概览。</returns>
    private static BankOverviewResponse BuildOverview(BankDescriptor bank, BankLedgerFactory factory)
    {
        var counts = factory.Get(bank).CountAll();
        return new BankOverviewResponse(
            bank.ToResponse(),
            counts.Customers,
            counts.Accounts,
            counts.Cards,
            counts.Transactions,
            counts.Products);
    }
}

/// <summary>重置演示数据的响应体。</summary>
/// <param name="ResetAt">重置时间。</param>
/// <param name="Message">结果描述。</param>
public sealed record ResetResponse(DateTimeOffset ResetAt, string Message);
