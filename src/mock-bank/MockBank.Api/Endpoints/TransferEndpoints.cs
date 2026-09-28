// ===== 转账端点：行内转账提交与流水分页查询、单笔详情 =====

using Microsoft.AspNetCore.Mvc;
using MockBank.Api.Contracts;
using MockBank.Api.Data;
using MockBank.Api.Domain;
using MockBank.Api.Services;

namespace MockBank.Api.Endpoints;

/// <summary>转账与交易流水相关 Minimal API 端点。</summary>
public static class TransferEndpoints
{
    /// <summary>转账 API 根路径。</summary>
    public const string RoutePrefix = "/api/corebank/v1";

    /// <summary>注册转账与流水端点。</summary>
    /// <param name="app">端点路由构建器。</param>
    /// <returns>路由构建器。</returns>
    public static IEndpointRouteBuilder MapTransferEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(RoutePrefix).WithTags("转账与流水");

        group.MapPost("/transfers", PostTransfer)
            .WithMetadata(new ApiOperationMetadata("PostTransfer", "行内转账：校验账户状态、单日限额与余额后记账，返回核心系统交易流水号。支持 X-Mock-Scenario 故障注入。", "行内转账：校验账户状态、单日限额与余额后记账，返回核心系统交易流水号。支持 X-Mock-Scenario 故障注入。", ["转账与流水"], typeof(TransferRequest)))
            .Accepts<TransferRequest>("application/json")
            .Produces<TransferResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound);

        group.MapGet("/beneficiaries", GetBeneficiaries)
            .WithMetadata(new ApiOperationMetadata("GetBeneficiaries", "按姓名或手机号检索行内收款人，返回可用于行内转账的储蓄账户。", "按姓名或手机号检索行内收款人，返回可用于行内转账的储蓄账户。", ["转账与流水"]))
            .Produces<IReadOnlyList<BeneficiaryResponse>>(StatusCodes.Status200OK);

        group.MapGet("/transactions", GetTransactions)
            .WithMetadata(new ApiOperationMetadata("GetTransactions", "分页查询交易流水，支持按时间区间、账号与交易分类筛选，按交易时间倒序返回。", "分页查询交易流水，支持按时间区间、账号与交易分类筛选，按交易时间倒序返回。", ["转账与流水"]))
            .Produces<PagedResult<TransactionResponse>>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest);

        group.MapGet("/transactions/{txNo}", GetTransaction)
            .WithMetadata(new ApiOperationMetadata("GetTransactionByNo", "按交易流水号查询单笔交易详情。", "按交易流水号查询单笔交易详情。", ["转账与流水"]))
            .Produces<TransactionResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>POST /api/corebank/v1/transfers — 提交行内转账。</summary>
    /// <param name="request">转账请求体。</param>
    /// <param name="service">转账服务。</param>
    /// <param name="store">账本，用于解析客户名。</param>
    /// <param name="businessLog">业务日志写入器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功返回 200 + 流水号；失败返回 400 / 404 + 结构化错误体。</returns>
    private static async Task<IResult> PostTransfer(
        TransferRequest? request,
        TransferService service,
        MockBankStore store,
        BusinessLogWriter businessLog,
        CancellationToken cancellationToken)
    {
        var outcome = await service.TransferAsync(request, cancellationToken).ConfigureAwait(false);

        var userId = request?.UserId;
        if (string.IsNullOrWhiteSpace(userId) && !string.IsNullOrWhiteSpace(request?.FromAccountNo))
        {
            userId = store.GetAccount(request.FromAccountNo)?.UserId;
        }

        if (outcome.IsSuccess)
        {
            businessLog.Write(userId, "TRANSFER", request?.Amount ?? 0m, $"SUCCESS:{outcome.Response!.TxNo}");
            return Results.Ok(outcome.Response);
        }

        businessLog.Write(userId, "TRANSFER", request?.Amount ?? 0m, $"FAILED:{outcome.Code}");
        return Results.Json(
            new ApiErrorResponse(outcome.Code, outcome.Message, outcome.Details, null),
            statusCode: outcome.StatusCode);
    }

    /// <summary>GET /api/corebank/v1/beneficiaries — 按姓名或手机号检索行内收款人。</summary>
    /// <param name="keyword">姓名或手机号片段。</param>
    /// <param name="store">账本。</param>
    /// <returns>匹配到的收款账户列表；关键词为空时返回空列表。</returns>
    private static IResult GetBeneficiaries([FromQuery] string? keyword, MockBankStore store)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return Results.Ok(Array.Empty<BeneficiaryResponse>());
        }

        var k = keyword.Trim();
        var matches = new List<BeneficiaryResponse>();

        foreach (var customer in store.Customers)
        {
            var hit = customer.Name.Contains(k, StringComparison.Ordinal)
                      || customer.Phone.Contains(k, StringComparison.Ordinal);
            if (!hit) continue;

            // 只返回储蓄账户：信用卡账户不能作为行内转账的收款方。
            matches.AddRange(store.GetAccountsByUser(customer.UserId)
                .Where(a => a.AccountType == AccountType.Savings)
                .Select(a => new BeneficiaryResponse(
                    customer.Name, a.AccountNo, a.AccountTypeText, a.BranchName)));
        }

        return Results.Ok(matches);
    }

    /// <summary>GET /api/corebank/v1/transactions — 分页查询交易流水。</summary>
    /// <param name="userId">客户号，可为空表示不限。</param>
    /// <param name="accountNo">账号，可为空表示不限。</param>
    /// <param name="from">起始日期时间（含），可为空。</param>
    /// <param name="to">结束日期时间（含），可为空。</param>
    /// <param name="category">交易分类，可为空表示不限。</param>
    /// <param name="direction">资金方向 income / expense，可为空表示不限。</param>
    /// <param name="page">页码，从 1 开始，默认 1。</param>
    /// <param name="pageSize">每页条数，默认 20，最大 200。</param>
    /// <param name="store">账本。</param>
    /// <returns>分页流水结果；分页参数非法时返回 400。</returns>
    private static IResult GetTransactions(
        [FromQuery] string? userId,
        [FromQuery] string? accountNo,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? category,
        [FromQuery] string? direction,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        MockBankStore store)
    {
        var pageNumber = page ?? 1;
        var size = pageSize ?? 20;

        if (pageNumber < 1)
        {
            return BadRequest("page 必须大于等于 1。", new Dictionary<string, object?> { ["page"] = pageNumber });
        }

        if (size is < 1 or > 200)
        {
            return BadRequest("pageSize 必须介于 1 与 200 之间。", new Dictionary<string, object?> { ["pageSize"] = size });
        }

        TransactionDirection? parsedDirection = null;
        if (!string.IsNullOrWhiteSpace(direction))
        {
            if (string.Equals(direction, "income", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(direction, "收入", StringComparison.Ordinal))
            {
                parsedDirection = TransactionDirection.Income;
            }
            else if (string.Equals(direction, "expense", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(direction, "支出", StringComparison.Ordinal))
            {
                parsedDirection = TransactionDirection.Expense;
            }
            else
            {
                return BadRequest("direction 仅支持 income / expense（收入 / 支出）。",
                    new Dictionary<string, object?> { ["direction"] = direction });
            }
        }

        if (from.HasValue && to.HasValue && from.Value > to.Value)
        {
            return BadRequest("from 不能晚于 to。",
                new Dictionary<string, object?> { ["from"] = from.Value, ["to"] = to.Value });
        }

        var transactions = store
            .QueryTransactions(userId, accountNo, from, to, category, parsedDirection)
            .Select(ToResponse)
            .ToList();

        return Results.Ok(PagedResult<TransactionResponse>.From(transactions, pageNumber, size));
    }

    /// <summary>GET /api/corebank/v1/transactions/{txNo} — 查询单笔交易详情。</summary>
    /// <param name="txNo">交易流水号。</param>
    /// <param name="store">账本。</param>
    /// <returns>交易详情；流水号不存在时返回 404。</returns>
    private static IResult GetTransaction(string txNo, MockBankStore store)
    {
        var transaction = store.GetTransaction(txNo);
        return transaction is null
            ? Results.Json(
                new ApiErrorResponse(
                    ErrorCodes.TRANSACTION_NOT_FOUND,
                    $"交易流水号 {txNo} 不存在。",
                    new Dictionary<string, object?> { ["txNo"] = txNo },
                    null),
                statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(ToResponse(transaction));
    }

    private static IResult BadRequest(string message, IReadOnlyDictionary<string, object?> details) =>
        Results.Json(
            new ApiErrorResponse(ErrorCodes.VALIDATION_ERROR, message, details, null),
            statusCode: StatusCodes.Status400BadRequest);

    private static TransactionResponse ToResponse(BankTransaction transaction) =>
        new(transaction.TxNo, transaction.UserId, transaction.AccountNo, transaction.DirectionText,
            transaction.Amount, transaction.SignedAmount, transaction.Category, transaction.Merchant,
            transaction.CounterpartyName, transaction.CounterpartyAccountNo, transaction.ChannelText,
            transaction.Remark, transaction.Currency, transaction.Timestamp);
}
