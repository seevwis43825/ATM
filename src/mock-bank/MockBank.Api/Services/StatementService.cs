// ===== 账单服务：按月汇总收入、支出、分类聚合与日趋势 =====

using MockBank.Api.Contracts;
using MockBank.Api.Data;
using MockBank.Api.Domain;

namespace MockBank.Api.Services;

/// <summary>月度账单汇总服务。</summary>
/// <param name="store">模拟银行账本。</param>
public sealed class StatementService(MockBankStore store)
{
    /// <summary>构建指定账号的月度账单汇总。</summary>
    /// <param name="accountNo">账号。</param>
    /// <param name="year">年份，如 2026。</param>
    /// <param name="month">月份，1-12。</param>
    /// <returns>账单汇总；账号不存在时返回 null。该月无流水时返回全零的合法汇总。</returns>
    public MonthlyStatementResponse? BuildMonthlyStatement(string? accountNo, int year, int month)
    {
        var account = store.GetAccount(accountNo);
        if (account is null)
        {
            return null;
        }

        var start = new DateTimeOffset(year, month, 1, 0, 0, 0, store.LocalOffset);
        var end = start.AddMonths(1);
        var transactions = store.QueryTransactions(account.UserId, account.AccountNo, start, end);

        var totalIncome = Round(transactions.Where(t => t.Direction == TransactionDirection.Income).Sum(t => t.Amount));
        var totalExpense = Round(transactions.Where(t => t.Direction == TransactionDirection.Expense).Sum(t => t.Amount));

        var categories = transactions
            .Where(t => t.Direction == TransactionDirection.Expense)
            .GroupBy(t => t.Category, StringComparer.Ordinal)
            .Select(g => new CategoryAggregate(
                g.Key,
                Round(g.Sum(t => t.Amount)),
                totalExpense <= 0m ? 0m : Round(g.Sum(t => t.Amount) / totalExpense * 100m),
                g.Count()))
            .OrderByDescending(c => c.Amount)
            .ThenBy(c => c.Category, StringComparer.Ordinal)
            .ToList();

        var daysInMonth = DateTime.DaysInMonth(year, month);
        var today = DateTimeOffset.Now;
        var dailyTrend = new List<DailyTrendItem>(daysInMonth);
        for (var day = 1; day <= daysInMonth; day++)
        {
            var dayItems = transactions.Where(t => t.Timestamp.Day == day).ToList();
            var date = new DateTimeOffset(year, month, day, 0, 0, 0, store.LocalOffset);
            dailyTrend.Add(new DailyTrendItem(
                date.ToString("yyyy-MM-dd"),
                day,
                Round(dayItems.Where(t => t.Direction == TransactionDirection.Income).Sum(t => t.Amount)),
                Round(dayItems.Where(t => t.Direction == TransactionDirection.Expense).Sum(t => t.Amount)),
                dayItems.Count));
        }

        return new MonthlyStatementResponse(
            account.AccountNo,
            account.UserId,
            start.ToString("yyyy-MM"),
            account.Currency,
            totalIncome,
            totalExpense,
            Round(totalIncome - totalExpense),
            transactions.Count,
            account.Balance,
            categories,
            dailyTrend);
    }

    /// <summary>解析 yyyy-MM 形式的月份参数。</summary>
    /// <param name="month">月份文本，可为空表示当前月。</param>
    /// <param name="year">解析出的年份。</param>
    /// <param name="monthOfYear">解析出的月份。</param>
    /// <returns>解析成功返回 true。</returns>
    public static bool TryParseMonth(string? month, out int year, out int monthOfYear)
    {
        year = 0;
        monthOfYear = 0;

        if (string.IsNullOrWhiteSpace(month))
        {
            var now = DateTimeOffset.Now;
            year = now.Year;
            monthOfYear = now.Month;
            return true;
        }

        var parts = month.Trim().Split('-', '/');
        if (parts.Length == 2
            && int.TryParse(parts[0], out year)
            && int.TryParse(parts[1], out monthOfYear)
            && year is >= 1970 and <= 2999
            && monthOfYear is >= 1 and <= 12)
        {
            return true;
        }

        return false;
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
