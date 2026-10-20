// ===== 公募基金服务：代销清单查询、筛选参数校验、净值历史与排行 =====
//
// 职责边界：
//   * 参数解析与合法性判断放在这里，端点只负责把结果翻译成 HTTP 响应；
//   * 真正的数据访问在 MockBankStore（跨行聚合）与 BankLedger（单行库）；
//   * 本服务只读，不做任何写操作 —— 公募基金申购是另一个业务域，本模块不涉及。

using MockBank.Api.Contracts;
using MockBank.Api.Data;
using MockBank.Api.Domain;

namespace MockBank.Api.Services;

/// <summary>公募基金服务。</summary>
/// <param name="store">模拟银行账本门面（跨行聚合基金代销清单）。</param>
public sealed class FundService(MockBankStore store)
{
    /// <summary>基金列表默认返回条数。</summary>
    public const int DefaultListLimit = 20;

    /// <summary>基金列表最大返回条数。</summary>
    public const int MaxListLimit = 100;

    /// <summary>净值历史默认回溯天数。</summary>
    public const int DefaultNavDays = 60;

    /// <summary>净值历史最大回溯天数。</summary>
    public const int MaxNavDays = 365;

    /// <summary>排行默认返回条数。</summary>
    public const int DefaultRankingLimit = 10;

    /// <summary>排行最大返回条数。</summary>
    public const int MaxRankingLimit = 50;

    /// <summary>按条件查询公募基金列表。</summary>
    /// <param name="category">基金类型文本，可为空表示不限。</param>
    /// <param name="riskLevel">基金风险等级文本，可为空表示不限。</param>
    /// <param name="company">基金公司，可为空表示不限。</param>
    /// <param name="keyword">基金代码或简称，可为空表示不限。</param>
    /// <param name="bankCode">限定代销行代码，可为空表示不限。</param>
    /// <param name="limit">最多返回多少只。</param>
    /// <param name="error">参数非法时输出错误码。</param>
    /// <param name="message">参数非法时输出中文错误描述。</param>
    /// <returns>基金响应列表；参数非法时返回 null。</returns>
    public IReadOnlyList<FundResponse>? QueryFunds(
        string? category,
        string? riskLevel,
        string? company,
        string? keyword,
        string? bankCode,
        int limit,
        out string error,
        out string message)
    {
        error = string.Empty;
        message = string.Empty;

        if (!TryResolveCategory(category, out var parsedCategory, out error, out message)) return null;
        if (!TryResolveRiskLevel(riskLevel, out var parsedRiskLevel, out error, out message)) return null;
        if (!TryResolveLimit(limit, DefaultListLimit, MaxListLimit, "limit", out var resolvedLimit, out error, out message)) return null;

        var funds = store.QueryFunds(parsedCategory, parsedRiskLevel, company, keyword, bankCode);
        return funds.Take(resolvedLimit).Select(f => f.ToResponse()).ToList();
    }

    /// <summary>查询单只公募基金详情，含该行已存储的全部净值历史。</summary>
    /// <param name="fundCode">基金代码。</param>
    /// <returns>基金响应；没有任何行代销该基金时返回 null。</returns>
    public FundResponse? GetFund(string? fundCode)
    {
        var fund = store.GetFund(fundCode);
        if (fund is null) return null;

        // 详情附带净值历史：回溯天数取上限，等于「该行已存储的全部净值点」。
        var navPoints = store.GetFundNav(fund.FundCode, MaxNavDays);
        return fund.ToResponse() with
        {
            NavPoints = navPoints.Select(p => p.ToResponse()).ToList(),
        };
    }

    /// <summary>查询单只公募基金的净值历史。</summary>
    /// <param name="fundCode">基金代码。</param>
    /// <param name="days">回溯天数。</param>
    /// <param name="error">参数非法时输出错误码。</param>
    /// <param name="message">参数非法时输出中文错误描述。</param>
    /// <returns>基金详情与净值序列；参数非法或基金不存在时返回 null。</returns>
    public FundNavResponse? GetFundNav(string? fundCode, int days, out string error, out string message)
    {
        error = string.Empty;
        message = string.Empty;

        if (!TryResolveLimit(days, DefaultNavDays, MaxNavDays, "days", out var resolvedDays, out error, out message)) return null;

        var fund = store.GetFund(fundCode);
        if (fund is null) return null;

        var navPoints = store.GetFundNav(fund.FundCode, resolvedDays);
        return fund.ToResponse(navPoints);
    }

    /// <summary>查询公募基金排行。</summary>
    /// <param name="metric">排行维度文本：daily / monthly / yearly。</param>
    /// <param name="limit">最多返回多少只。</param>
    /// <param name="error">参数非法时输出错误码。</param>
    /// <param name="message">参数非法时输出中文错误描述。</param>
    /// <returns>排行后的基金响应列表；参数非法时返回 null。</returns>
    public IReadOnlyList<FundResponse>? GetRankings(
        string? metric, int limit, out string error, out string message)
    {
        error = string.Empty;
        message = string.Empty;

        if (!TryResolveRankingMetric(metric, out var parsedMetric, out error, out message)) return null;
        if (!TryResolveLimit(limit, DefaultRankingLimit, MaxRankingLimit, "limit", out var resolvedLimit, out error, out message)) return null;

        return store.FundRankings(parsedMetric, resolvedLimit).Select(f => f.ToResponse()).ToList();
    }

    // ---------------------------------------------------------------- 内部

    /// <summary>解析基金类型参数。</summary>
    /// <param name="category">基金类型文本。</param>
    /// <param name="parsed">解析成功时输出基金类型。</param>
    /// <param name="error">解析失败时输出错误码。</param>
    /// <param name="message">解析失败时输出中文错误描述。</param>
    /// <returns>参数合法（或为空）返回 true。</returns>
    private static bool TryResolveCategory(
        string? category, out FundCategory? parsed, out string error, out string message)
    {
        parsed = null;
        error = string.Empty;
        message = string.Empty;

        if (string.IsNullOrWhiteSpace(category)) return true;
        if (FundCategoryExtensions.TryParseFundCategory(category, out var value))
        {
            parsed = value;
            return true;
        }

        error = ErrorCodes.VALIDATION_ERROR;
        message = $"无法识别的基金类型：{category}。支持值：货币型 / 债券型 / 混合型 / 股票型 / 指数型 / QDII / FOF。";
        return false;
    }

    /// <summary>解析基金风险等级参数。</summary>
    /// <param name="riskLevel">风险等级文本。</param>
    /// <param name="parsed">解析成功时输出风险等级。</param>
    /// <param name="error">解析失败时输出错误码。</param>
    /// <param name="message">解析失败时输出中文错误描述。</param>
    /// <returns>参数合法（或为空）返回 true。</returns>
    private static bool TryResolveRiskLevel(
        string? riskLevel, out FundRiskLevel? parsed, out string error, out string message)
    {
        parsed = null;
        error = string.Empty;
        message = string.Empty;

        if (string.IsNullOrWhiteSpace(riskLevel)) return true;
        if (FundRiskLevelExtensions.TryParseFundRiskLevel(riskLevel, out var value))
        {
            parsed = value;
            return true;
        }

        error = ErrorCodes.VALIDATION_ERROR;
        message = $"无法识别的基金风险等级：{riskLevel}。支持值：R1 / R2 / R3 / R4 / R5。";
        return false;
    }

    /// <summary>解析排行维度参数。</summary>
    /// <param name="metric">排行维度文本。</param>
    /// <param name="parsed">解析成功时输出排行维度。</param>
    /// <param name="error">解析失败时输出错误码。</param>
    /// <param name="message">解析失败时输出中文错误描述。</param>
    /// <returns>参数合法（或为空）返回 true。</returns>
    private static bool TryResolveRankingMetric(
        string? metric, out FundRankingMetric parsed, out string error, out string message)
    {
        parsed = FundRankingMetric.Daily;
        error = string.Empty;
        message = string.Empty;

        if (string.IsNullOrWhiteSpace(metric)) return true;
        switch (metric.Trim().ToLowerInvariant())
        {
            case "daily":
                parsed = FundRankingMetric.Daily;
                return true;
            case "monthly":
                parsed = FundRankingMetric.Monthly;
                return true;
            case "yearly":
                parsed = FundRankingMetric.Yearly;
                return true;
            default:
                error = ErrorCodes.VALIDATION_ERROR;
                message = $"无法识别的排行维度：{metric}。支持值：daily / monthly / yearly。";
                return false;
        }
    }

    /// <summary>校验条数 / 天数类参数是否落在允许区间内。</summary>
    /// <param name="value">调用方传入的值。</param>
    /// <param name="defaultValue">未指定时使用的默认值。</param>
    /// <param name="maxValue">允许的最大值。</param>
    /// <param name="name">参数名，用于错误提示。</param>
    /// <param name="resolved">解析后的实际取值。</param>
    /// <param name="error">参数非法时输出错误码。</param>
    /// <param name="message">参数非法时输出中文错误描述。</param>
    /// <returns>参数合法返回 true。</returns>
    private static bool TryResolveLimit(
        int value, int defaultValue, int maxValue, string name,
        out int resolved, out string error, out string message)
    {
        // 0 表示调用方没传这个参数，用默认值兜底。
        resolved = value == 0 ? defaultValue : value;
        error = string.Empty;
        message = string.Empty;

        if (resolved < 1 || resolved > maxValue)
        {
            error = ErrorCodes.VALIDATION_ERROR;
            message = $"参数 {name} 超出允许范围：1 - {maxValue}。";
            return false;
        }

        return true;
    }
}
