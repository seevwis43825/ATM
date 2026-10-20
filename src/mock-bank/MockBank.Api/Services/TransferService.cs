// ===== 转账服务：行内 / 跨行转账的完整校验链、并发控制与双边记账 =====
//
// 跨行转账的处理方式（与真实清算系统一致）：
//   1. 付款行与收款行是**两个独立的核心系统、两个独立的数据库**；
//   2. 转账时一次性取到两行的写锁（按行代码字典序固定顺序，避免死锁）；
//   3. 付款行扣款并记一条支出流水，收款行入账并记一条收入流水；
//   4. 两笔流水各自带本行代码，渠道标记为「跨行转账」；
//   5. 返回值同时给出付款行流水号与收款行流水号。

using MockBank.Api.Contracts;
using MockBank.Api.Data;
using MockBank.Api.Domain;

namespace MockBank.Api.Services;

/// <summary>转账服务。所有资金变动都在涉及行的写锁内串行执行。</summary>
/// <param name="store">模拟银行账本门面。</param>
/// <param name="accounts">账户服务。</param>
/// <param name="logger">日志记录器。</param>
public sealed class TransferService(MockBankStore store, AccountService accounts, ILogger<TransferService> logger)
{
    private static readonly string DefaultCurrency = "CNY";

    /// <summary>执行一次转账（行内或跨行）。</summary>
    /// <param name="request">转账请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>转账结果，成功时包含双边流水号与双边余额。</returns>
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
        var isInterBank = !string.Equals(
            fromAccount.BankCode, toAccount.BankCode, StringComparison.OrdinalIgnoreCase);

        // ===== 跨行加锁：一次性取到两把锁，顺序固定，不会死锁 =====
        // 锁在方法退出时按相反顺序自动归还。
        await using var ledgerLocks = await store.AcquireLedgerLocksAsync(
            [fromAccount.BankCode, toAccount.BankCode], cancellationToken).ConfigureAwait(false);

        // 二次校验：等待锁期间账户状态可能被并发请求改变（挂失、再次扣款等）。
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
        var channel = isInterBank
            ? TransactionChannel.InterBankTransfer
            : TransactionChannel.InternalTransfer;

        // ===== 付款行：扣款流水 =====
        var fromTxNo = store.NextTransactionNo(fromAccount.BankCode, now);
        store.AddTransaction(new BankTransaction
        {
            TxNo = fromTxNo,
            BankCode = fromAccount.BankCode,
            UserId = fromAccount.UserId,
            AccountNo = fromAccount.AccountNo,
            Direction = TransactionDirection.Expense,
            Amount = amount.Amount,
            Category = "转账",
            Merchant = TransferLabel(isInterBank, toAccount),
            CounterpartyName = CounterpartyLabel(toAccount),
            CounterpartyAccountNo = toAccount.AccountNo,
            Remark = string.IsNullOrWhiteSpace(request.Remark) ? DefaultRemark(isInterBank) : request.Remark!.Trim(),
            Channel = channel,
            Currency = amount.Currency,
            Timestamp = now,
        });

        // ===== 收款行：入账流水 =====
        var toTxNo = store.NextTransactionNo(toAccount.BankCode, now);
        store.AddTransaction(new BankTransaction
        {
            TxNo = toTxNo,
            BankCode = toAccount.BankCode,
            UserId = toAccount.UserId,
            AccountNo = toAccount.AccountNo,
            Direction = TransactionDirection.Income,
            Amount = amount.Amount,
            Category = "转账",
            Merchant = TransferLabel(isInterBank, fromAccount),
            CounterpartyName = CounterpartyLabel(fromAccount),
            CounterpartyAccountNo = fromAccount.AccountNo,
            Remark = string.IsNullOrWhiteSpace(request.Remark) ? DefaultRemark(isInterBank) : request.Remark!.Trim(),
            Channel = channel,
            Currency = amount.Currency,
            Timestamp = now,
        });

        logger.LogInformation(
            "转账成功 {TxNo}: {FromBank}.{From} -> {ToBank}.{To}, 金额 {Amount:F2} {Currency}, 跨行 {InterBank}, 付款方余额 {FromBalance:F2}",
            fromTxNo, fromAccount.BankCode, fromAccount.AccountNo, toAccount.BankCode, toAccount.AccountNo,
            amount.Amount, amount.Currency, isInterBank, updatedFrom.Balance);

        return TransferOutcome.Success(new TransferResponse(
            fromTxNo,
            toTxNo,
            fromAccount.AccountNo,
            toAccount.AccountNo,
            fromAccount.BankCode,
            toAccount.BankCode,
            isInterBank,
            amount.Amount,
            amount.Currency,
            updatedFrom.Balance,
            updatedTo.Balance,
            now,
            isInterBank ? "跨行转账成功" : "行内转账成功"));
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

    /// <summary>生成流水的对手方摘要前缀，区分行内与跨行。</summary>
    /// <param name="isInterBank">是否跨行。</param>
    /// <param name="counterpartyAccount">对手方账户。</param>
    /// <returns>摘要文本。</returns>
    private static string TransferLabel(bool isInterBank, Account counterpartyAccount) =>
        isInterBank
            ? $"跨行转账-{BankCatalog.ShortNameOf(counterpartyAccount.BankCode)}"
            : $"行内转账-{counterpartyAccount.ProductName}";

    /// <summary>生成流水的对手方名称，含对手方姓名与开户行。</summary>
    /// <param name="counterpartyAccount">对手方账户。</param>
    /// <returns>对手方名称。</returns>
    private string CounterpartyLabel(Account counterpartyAccount)
    {
        var name = store.GetCustomer(counterpartyAccount.UserId, counterpartyAccount.BankCode)?.Name ?? string.Empty;
        var bank = BankCatalog.ShortNameOf(counterpartyAccount.BankCode);
        return $"{name} {bank} {counterpartyAccount.BranchName}".Trim();
    }

    /// <summary>生成缺省附言。</summary>
    /// <param name="isInterBank">是否跨行。</param>
    /// <returns>附言文本。</returns>
    private static string DefaultRemark(bool isInterBank) => isInterBank ? "跨行转账" : "行内转账";
}
