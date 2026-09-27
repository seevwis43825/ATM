// ===== 理财服务：理财产品查询、风险等级匹配与认购扣款 =====

using MockBank.Api.Contracts;
using MockBank.Api.Data;
using MockBank.Api.Domain;

namespace MockBank.Api.Services;

/// <summary>理财产品服务。</summary>
/// <param name="store">模拟银行账本。</param>
/// <param name="accounts">账户服务，用于认购扣款。</param>
/// <param name="logger">日志记录器。</param>
public sealed class WealthService(MockBankStore store, AccountService accounts, ILogger<WealthService> logger)
{
    /// <summary>按风险等级与产品类型查询理财产品列表。</summary>
    /// <param name="riskLevel">风险等级文本，可为空表示不限。</param>
    /// <param name="type">产品类型文本，可为空表示不限。</param>
    /// <param name="error">筛选参数非法时输出错误码。</param>
    /// <param name="message">筛选参数非法时输出中文错误描述。</param>
    /// <returns>产品响应列表；参数非法时返回 null。</returns>
    public IReadOnlyList<ProductResponse>? QueryProducts(
        string? riskLevel,
        string? type,
        out string error,
        out string message)
    {
        error = string.Empty;
        message = string.Empty;

        RiskLevel? parsedRisk = null;
        if (!string.IsNullOrWhiteSpace(riskLevel))
        {
            if (RiskLevelExtensions.TryParseRiskLevel(riskLevel, out var level))
            {
                parsedRisk = level;
            }
            else
            {
                error = ErrorCodes.VALIDATION_ERROR;
                message = $"无法识别的风险等级：{riskLevel}。支持值：R1 / R2 / R3 / R4。";
                return null;
            }
        }

        ProductType? parsedType = null;
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (ProductTypeExtensions.TryParseProductType(type, out var productType))
            {
                parsedType = productType;
            }
            else
            {
                error = ErrorCodes.VALIDATION_ERROR;
                message = $"无法识别的产品类型：{type}。支持值：稳健 / 平衡 / 进取。";
                return null;
            }
        }

        return store.QueryProducts(parsedRisk, parsedType).Select(p => p.ToResponse()).ToList();
    }

    /// <summary>查询单只理财产品详情。</summary>
    /// <param name="code">产品代码。</param>
    /// <returns>产品响应；不存在时返回 null。</returns>
    public ProductResponse? GetProduct(string? code) => store.GetProduct(code)?.ToResponse();

    /// <summary>认购理财产品：校验风险等级、起投金额、募集规模与账户余额后扣款。</summary>
    /// <param name="request">认购请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>认购结果，成功时携带认购流水号与扣款后余额，失败时携带错误码与描述。</returns>
    public async Task<SubscribeOutcome> SubscribeAsync(
        SubscribeRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return SubscribeOutcome.Failure(
                StatusCodes.Status400BadRequest, ErrorCodes.BAD_REQUEST, "认购请求体不能为空。");
        }

        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ProductCode))
        {
            return SubscribeOutcome.Failure(
                StatusCodes.Status400BadRequest, ErrorCodes.VALIDATION_ERROR, "客户号与产品代码均不能为空。");
        }

        var customer = store.GetCustomer(request.UserId);
        if (customer is null)
        {
            return SubscribeOutcome.Failure(
                StatusCodes.Status404NotFound, ErrorCodes.CUSTOMER_NOT_FOUND, $"客户号 {request.UserId} 不存在。");
        }

        var product = store.GetProduct(request.ProductCode);
        if (product is null)
        {
            return SubscribeOutcome.Failure(
                StatusCodes.Status404NotFound, ErrorCodes.PRODUCT_NOT_FOUND, $"产品代码 {request.ProductCode} 不存在。");
        }

        if (request.Amount <= 0m)
        {
            return SubscribeOutcome.Failure(
                StatusCodes.Status400BadRequest, ErrorCodes.VALIDATION_ERROR, "认购金额必须大于 0。");
        }

        if (product.Status != ProductStatus.OnSale)
        {
            return SubscribeOutcome.Failure(
                StatusCodes.Status400BadRequest, ErrorCodes.PRODUCT_NOT_ON_SALE,
                $"产品 {product.Code} 当前状态为 {product.StatusText}，不可认购。");
        }

        if (customer.RiskLevel < product.RiskLevel)
        {
            return SubscribeOutcome.Failure(
                StatusCodes.Status400BadRequest, ErrorCodes.RISK_LEVEL_MISMATCH,
                $"客户风险等级为 {customer.RiskLevel.ToText()}，低于产品 {product.Code} 要求的 {product.RiskLevel.ToText()}，不予受理。");
        }

        if (request.Amount < product.MinAmount)
        {
            return SubscribeOutcome.Failure(
                StatusCodes.Status400BadRequest, ErrorCodes.AMOUNT_BELOW_MINIMUM,
                $"认购金额 {request.Amount:F2} 低于产品起投金额 {product.MinAmount:F2}。");
        }

        if (request.Amount > product.MaxAmount)
        {
            return SubscribeOutcome.Failure(
                StatusCodes.Status400BadRequest, ErrorCodes.AMOUNT_ABOVE_MAXIMUM,
                $"认购金额 {request.Amount:F2} 超过单笔上限 {product.MaxAmount:F2}。");
        }

        if (request.Amount > product.RemainingAmount)
        {
            return SubscribeOutcome.Failure(
                StatusCodes.Status400BadRequest, ErrorCodes.PRODUCT_SOLD_OUT,
                $"产品 {product.Code} 剩余可募集金额 {product.RemainingAmount:F2} 不足。");
        }

        var savings = store.GetAccountsByUser(customer.UserId)
            .FirstOrDefault(a => a.AccountType == AccountType.Savings);
        if (savings is null)
        {
            return SubscribeOutcome.Failure(
                StatusCodes.Status404NotFound, ErrorCodes.ACCOUNT_NOT_FOUND,
                $"客户 {customer.UserId} 没有可用于认购扣款的储蓄账户。");
        }

        if (savings.Status != AccountStatus.Normal)
        {
            return SubscribeOutcome.Failure(
                StatusCodes.Status400BadRequest, ErrorCodes.ACCOUNT_STATUS_ABNORMAL,
                $"扣款账户 {savings.AccountNo} 状态为 {savings.StatusText}，不予受理。");
        }

        await store.LedgerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 等待闸门期间余额可能被并发请求改变，这里重新读取一次。
            savings = store.GetAccount(savings.AccountNo)!;
            if (request.Amount > savings.AvailableBalance)
            {
                return SubscribeOutcome.Failure(
                    StatusCodes.Status400BadRequest, ErrorCodes.INSUFFICIENT_FUNDS,
                    $"账户 {savings.AccountNo} 可用余额 {savings.AvailableBalance:F2} 不足以认购 {request.Amount:F2}。");
            }

            var updatedAccount = accounts.AdjustBalance(savings, -request.Amount);
            var latestProduct = store.GetProduct(product.Code)!;
            store.UpdateProduct(latestProduct with { RaisedAmount = latestProduct.RaisedAmount + request.Amount });

            var now = DateTimeOffset.Now;
            var subscribeNo = store.NextTransactionNo(now);
            store.AddTransaction(new BankTransaction
            {
                TxNo = subscribeNo,
                UserId = customer.UserId,
                AccountNo = savings.AccountNo,
                Direction = TransactionDirection.Expense,
                Amount = request.Amount,
                Category = "理财",
                Merchant = $"理财认购-{product.Name}",
                CounterpartyName = $"{MockBankStore.BankName} 理财中心",
                CounterpartyAccountNo = product.Code,
                Remark = $"认购 {product.Name}（{product.TermDays}天）",
                Channel = TransactionChannel.MobileApp,
                Currency = savings.Currency,
                Timestamp = now,
            });

            logger.LogInformation(
                "理财认购成功 {SubscribeNo}: 用户 {UserId} 认购 {Code} 金额 {Amount:F2}, 扣款后余额 {Balance:F2}",
                subscribeNo, customer.UserId, product.Code, request.Amount, updatedAccount.Balance);

            var message = $"认购 {product.Name} 成功，金额 {request.Amount:F2} 元";
            return SubscribeOutcome.Success(new SubscribeResponse(
                subscribeNo, customer.UserId, product.Code, product.Name, request.Amount,
                savings.AccountNo, updatedAccount.Balance, product.RiskLevel.ToText(),
                customer.RiskLevel.ToText(), product.AnnualRate, product.TermDays, now, message));
        }
        finally
        {
            store.LedgerGate.Release();
        }
    }
}
