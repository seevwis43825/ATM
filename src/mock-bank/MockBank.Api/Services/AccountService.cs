// ===== 账户服务：账户查询、余额与限额查询、余额增减 =====

using MockBank.Api.Contracts;
using MockBank.Api.Data;
using MockBank.Api.Domain;

namespace MockBank.Api.Services;

/// <summary>账户查询与余额维护服务。</summary>
/// <param name="store">模拟银行账本。</param>
public sealed class AccountService(MockBankStore store)
{
    /// <summary>查询指定客户名下的全部账户。</summary>
    /// <param name="userId">客户号。</param>
    /// <returns>账户响应列表。</returns>
    public IReadOnlyList<AccountResponse> GetAccountsByUser(string? userId) =>
        store.GetAccountsByUser(userId).Select(ToResponse).ToList();

    /// <summary>查询单个账户。</summary>
    /// <param name="accountNo">账号。</param>
    /// <returns>账户响应；账户不存在时返回 null。</returns>
    public AccountResponse? GetAccount(string? accountNo)
    {
        var account = store.GetAccount(accountNo);
        return account is null ? null : ToResponse(account);
    }

    /// <summary>查询账户余额。</summary>
    /// <param name="accountNo">账号。</param>
    /// <returns>余额响应；账户不存在时返回 null。</returns>
    public AccountBalanceResponse? GetBalance(string? accountNo)
    {
        var account = store.GetAccount(accountNo);
        if (account is null)
        {
            return null;
        }

        return new AccountBalanceResponse(
            account.AccountNo, account.UserId, account.AccountTypeText,
            account.Balance, account.AvailableBalance, account.CreditLimit, account.CreditUsed,
            account.Currency, account.StatusText, DateTimeOffset.Now);
    }

    /// <summary>查询账户限额配置。</summary>
    /// <param name="accountNo">账号。</param>
    /// <returns>限额响应；账户不存在时返回 null。</returns>
    public AccountLimitResponse? GetLimit(string? accountNo)
    {
        var account = store.GetAccount(accountNo);
        if (account is null)
        {
            return null;
        }

        var singleLimit = Math.Min(account.DailyLimit / 10m, 50_000m);
        return new AccountLimitResponse(
            account.AccountNo, account.UserId, account.AccountTypeText,
            account.DailyLimit, account.CreditLimit, account.CreditUsed,
            account.CreditLimit - account.CreditUsed,
            singleLimit <= 0m ? account.DailyLimit : singleLimit,
            account.Currency, account.StatusText);
    }

    /// <summary>按账户增减余额（储蓄账户）或占用 / 释放信用额度（信用账户）。</summary>
    /// <param name="account">账户实体。</param>
    /// <param name="delta">变动金额，正数表示账户资金增加，负数表示减少。</param>
    /// <returns>变动后的账户实体。</returns>
    public Account AdjustBalance(Account account, decimal delta)
    {
        var updated = account.AccountType == AccountType.Credit
            ? account with
            {
                CreditUsed = account.CreditUsed - delta,
                Balance = -(account.CreditUsed - delta),
            }
            : account with { Balance = account.Balance + delta };

        store.UpdateAccount(updated);
        return updated;
    }

    private AccountResponse ToResponse(Account account) =>
        new(account.AccountNo, account.UserId, account.AccountTypeText, account.ProductName,
            account.Balance, account.CreditLimit, account.CreditUsed, account.AvailableBalance,
            account.DailyLimit, account.StatusText, account.Currency, account.OpenDate,
            account.BranchName, store.GetCardByAccount(account.AccountNo)?.CardNo);
}
