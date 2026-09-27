// ===== 转账服务：行内转账的完整校验链、并发控制与双流水记账 =====

using MockBank.Api.Contracts;
using MockBank.Api.Data;
using MockBank.Api.Domain;

namespace MockBank.Api.Services;

/// <summary>行内转账服务。所有资金变动在 <see cref="MockBankStore.LedgerGate"/> 内串行执行。</summary>
/// <param name="store">模拟银行账本。</param>
/// <param name="accounts">账户服务。</param>
/// <param name="logger">日志记录器。</param>
public sealed class TransferService(MockBankStore store, AccountService accounts, ILogger<TransferService> logger)
{
    private static readonly string DefaultCurrency = "CNY";

    /// <summary>执行一次行内转账。</summary>
    /// <param name="request">转账请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>转账结果，成功时包含流水号与双边余额。</returns>
    public async Task<TransferOutcome> TransferAsync(TransferRequest? request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return TransferOutcome.Failure(StatusCodes.Status400BadRequest, ErrorCodes.BAD_REQUEST,
                "转账请求体不能为空。");
        }

        var validation = Validate(request);
        if (validation is not null)
        {
            return validation;
        }

        var fromAccount = store.GetAccount(request.FromAccountNo)!;
        var toAccount = store.GetAccount(request.ToAccountNo)!;
        var amount = Money.Cny(request.Amount).Round2();

        await store.LedgerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 二次校验：等待闸门期间账户状态可能被并发请求改变（挂失、再次扣款等）。
            var recheck = Validate(request);
            if (recheck is not null)
            {
                return recheck;
            }

            fromAccount = store.GetAccount(request.FromAccountNo)!;
            toAccount = store.GetAccount(request.ToAccountNo)!;

            var updatedFrom = accounts.AdjustBalance(fromAccount, -amount.Amount);
            var updatedTo = accounts.AdjustBalance(toAccount, amount.Amount);
            var now = DateTimeOffset.Now;
            var txNo = store.NextTransactionNo(now);

            store.AddTransaction(new BankTransaction
            {
                TxNo = txNo,
                UserId = fromAccount.UserId,
                AccountNo = fromAccount.AccountNo,
                Direction = TransactionDirection.Expense,
                Amount = amount.Amount,
                Category = "转账",
                Merchant = $"行内转账-{toAccount.ProductName}",
                CounterpartyName = $"{toAccount.UserId} {store.GetCustomer(toAccount.UserId)?.Name}",
                CounterpartyAccountNo = toAccount.AccountNo,
                Remark = string.IsNullOrWhiteSpace(request.Remark) ? "行内转账" : request.Remark!.Trim(),
                Channel = TransactionChannel.InternalTransfer,
                Currency = amount.Currency,
                Timestamp = now,
            });

            store.AddTransaction(new BankTransaction
            {
                TxNo = store.NextTransactionNo(now),
                UserId = toAccount.UserId,
                AccountNo = toAccount.AccountNo,
                Direction = TransactionDirection.Income,
                Amount = amount.Amount,
                Category = "转账",
                Merchant = $"行内转账-{fromAccount.ProductName}",
                CounterpartyName = $"{fromAccount.UserId} {store.GetCustomer(fromAccount.UserId)?.Name}",
                CounterpartyAccountNo = fromAccount.AccountNo,
                Remark = string.IsNullOrWhiteSpace(request.Remark) ? "行内转账" : request.Remark!.Trim(),
                Channel = TransactionChannel.InternalTransfer,
                Currency = amount.Currency,
                Timestamp = now,
            });

            logger.LogInformation(
                "转账成功 {TxNo}: {From} -> {To}, 金额 {Amount:F2} {Currency}, 付款方余额 {FromBalance:F2}",
                txNo, fromAccount.AccountNo, toAccount.AccountNo, amount.Amount, amount.Currency, updatedFrom.Balance);

            return TransferOutcome.Success(new TransferResponse(
                txNo,
                fromAccount.AccountNo,
                toAccount.AccountNo,
                amount.Amount,
                amount.Currency,
                updatedFrom.Balance,
                updatedTo.Balance,
                now));
        }
        finally
        {
            store.LedgerGate.Release();
        }
    }

    /// <summary>执行转账前置校验，返回首个不通过的错误。</summary>
    /// <param name="request">转账请求。</param>
    /// <returns>校验失败时返回 <see cref="TransferOutcome"/>，通过时返回 null。</returns>
    private TransferOutcome? Validate(TransferRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FromAccountNo) || string.IsNullOrWhiteSpace(request.ToAccountNo))
        {
            return TransferOutcome.Failure(StatusCodes.Status400BadRequest, ErrorCodes.VALIDATION_ERROR,
                "付款账号与收款账号均不能为空。",
                new Dictionary<string, object?>
                {
                    ["fromAccountNo"] = request.FromAccountNo ?? "",
                    ["toAccountNo"] = request.ToAccountNo ?? "",
                });
        }

        var fromAccount = store.GetAccount(request.FromAccountNo);
        if (fromAccount is null)
        {
            return TransferOutcome.Failure(StatusCodes.Status404NotFound, ErrorCodes.ACCOUNT_NOT_FOUND,
                $"付款账号 {request.FromAccountNo} 不存在。",
                new Dictionary<string, object?> { ["accountNo"] = request.FromAccountNo });
        }

        var toAccount = store.GetAccount(request.ToAccountNo);
        if (toAccount is null)
        {
            return TransferOutcome.Failure(StatusCodes.Status404NotFound, ErrorCodes.ACCOUNT_NOT_FOUND,
                $"收款账号 {request.ToAccountNo} 不存在。",
                new Dictionary<string, object?> { ["accountNo"] = request.ToAccountNo });
        }

        if (string.Equals(fromAccount.AccountNo, toAccount.AccountNo, StringComparison.Ordinal))
        {
            return TransferOutcome.Failure(StatusCodes.Status400BadRequest, ErrorCodes.SAME_ACCOUNT,
                "收款账号不能与付款账号相同。",
                new Dictionary<string, object?> { ["accountNo"] = fromAccount.AccountNo });
        }

        if (request.Amount <= 0m)
        {
            return TransferOutcome.Failure(StatusCodes.Status400BadRequest, ErrorCodes.VALIDATION_ERROR,
                "转账金额必须大于 0。",
                new Dictionary<string, object?> { ["amount"] = request.Amount });
        }

        var currency = string.IsNullOrWhiteSpace(request.Currency) ? DefaultCurrency : request.Currency.Trim();
        if (!string.Equals(currency, fromAccount.Currency, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(currency, toAccount.Currency, StringComparison.OrdinalIgnoreCase))
        {
            return TransferOutcome.Failure(StatusCodes.Status400BadRequest, ErrorCodes.CURRENCY_MISMATCH,
                "转账币种与账户币种不一致。",
                new Dictionary<string, object?>
                {
                    ["requested"] = currency,
                    ["fromAccountCurrency"] = fromAccount.Currency,
                    ["toAccountCurrency"] = toAccount.Currency,
                });
        }

        if (fromAccount.Status == AccountStatus.Frozen)
        {
            return TransferOutcome.Failure(StatusCodes.Status400BadRequest, ErrorCodes.ACCOUNT_FROZEN,
                $"付款账号 {fromAccount.AccountNo} 已被冻结，无法办理转账。",
                new Dictionary<string, object?> { ["accountNo"] = fromAccount.AccountNo });
        }

        if (fromAccount.Status != AccountStatus.Normal)
        {
            return TransferOutcome.Failure(StatusCodes.Status400BadRequest, ErrorCodes.ACCOUNT_STATUS_ABNORMAL,
                $"付款账号状态为 {fromAccount.StatusText}，无法办理转账。",
                new Dictionary<string, object?>
                {
                    ["accountNo"] = fromAccount.AccountNo,
                    ["status"] = fromAccount.StatusText,
                });
        }

        var card = store.GetCardByAccount(fromAccount.AccountNo);
        if (card is not null && card.Status != CardStatus.Normal)
        {
            return TransferOutcome.Failure(StatusCodes.Status400BadRequest, ErrorCodes.ACCOUNT_STATUS_ABNORMAL,
                $"付款卡片状态为 {card.StatusText}，无法办理转账。",
                new Dictionary<string, object?>
                {
                    ["cardNo"] = card.CardNo,
                    ["cardStatus"] = card.StatusText,
                });
        }

        if (toAccount.Status != AccountStatus.Normal)
        {
            return TransferOutcome.Failure(StatusCodes.Status400BadRequest, ErrorCodes.ACCOUNT_STATUS_ABNORMAL,
                $"收款账号状态为 {toAccount.StatusText}，无法入账。",
                new Dictionary<string, object?>
                {
                    ["accountNo"] = toAccount.AccountNo,
                    ["status"] = toAccount.StatusText,
                });
        }

        // 先判余额再判限额：金额远超可用余额时，优先返回更精确的 INSUFFICIENT_FUNDS。
        var available = fromAccount.AvailableBalance;
        if (request.Amount > available)
        {
            return TransferOutcome.Failure(StatusCodes.Status400BadRequest, ErrorCodes.INSUFFICIENT_FUNDS,
                $"付款账号 {fromAccount.AccountNo} 可用余额 {available:F2} 不足以支付 {request.Amount:F2}。",
                new Dictionary<string, object?>
                {
                    ["accountNo"] = fromAccount.AccountNo,
                    ["requested"] = request.Amount,
                    ["available"] = available,
                    ["shortage"] = request.Amount - available,
                });
        }

        if (request.Amount > fromAccount.DailyLimit)
        {
            return TransferOutcome.Failure(StatusCodes.Status400BadRequest, ErrorCodes.DAILY_LIMIT_EXCEEDED,
                $"转账金额 {request.Amount:F2} 超过账户 {fromAccount.AccountNo} 单日限额 {fromAccount.DailyLimit:F2}。",
                new Dictionary<string, object?>
                {
                    ["amount"] = request.Amount,
                    ["dailyLimit"] = fromAccount.DailyLimit,
                    ["accountNo"] = fromAccount.AccountNo,
                });
        }

        return null;
    }
}
