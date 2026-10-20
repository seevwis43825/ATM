// ===== 契约：公募基金查询、详情与净值历史契约 =====
//
// 公募基金是代销产品，契约设计上刻意保留了两类信息：
//   * 基金自身的静态与动态数据（代码、类型、费率、净值、收益率）；
//   * 「哪些行在代销」（DistributorBankCodes / DistributorBankNames），
//     理财 Agent 用它判断「这只基金在客户所在的行能不能买到」。

using MockBank.Api.Domain;

namespace MockBank.Api.Contracts;

/// <summary>公募基金查询响应体（列表与详情共用同一形态）。</summary>
/// <param name="FundCode">基金代码，6 位数字。</param>
/// <param name="FundName">基金简称。</param>
/// <param name="FundFullName">基金全称。</param>
/// <param name="Category">基金类型中文名（货币型 / 债券型 / 混合型 / 股票型 / 指数型 / QDII / FOF）。</param>
/// <param name="RiskLevel">基金风险等级文本（R1-R5）。</param>
/// <param name="FundCompany">基金管理人（基金公司全称）。</param>
/// <param name="FundManager">现任基金经理。</param>
/// <param name="InceptionDate">基金成立日期。</param>
/// <param name="NavDate">最新净值日期。</param>
/// <param name="UnitNav">单位净值。</param>
/// <param name="AccumulatedNav">累计净值。</param>
/// <param name="DailyGrowthRate">最新一日单位净值增长率（百分数）。</param>
/// <param name="MonthlyReturn">近一月收益率（百分数）。</param>
/// <param name="YearlyReturn">近一年收益率（百分数）。</param>
/// <param name="SinceInceptionReturn">成立以来涨幅（百分数）。</param>
/// <param name="FundScale">基金规模（亿元）。</param>
/// <param name="ManagementFeeRate">管理费率（年化百分数）。</param>
/// <param name="CustodyFeeRate">托管费率（年化百分数）。</param>
/// <param name="SubscriptionFeeRate">申购费率（百分数）。</param>
/// <param name="RedemptionFeeRate">赎回费率（百分数）。</param>
/// <param name="InvestmentScope">投资范围。</param>
/// <param name="PerformanceBenchmark">业绩比较基准。</param>
/// <param name="SubscriptionStatus">申赎状态中文名。</param>
/// <param name="MinPurchaseAmount">起购金额（元）。</param>
/// <param name="DistributorBankCodes">代销该基金的银行行代码，逗号分隔。</param>
/// <param name="DistributorBankNames">代销该基金的银行简称，逗号分隔。</param>
/// <param name="RiskNote">风险提示。</param>
public sealed record FundResponse(
    string FundCode,
    string FundName,
    string FundFullName,
    string Category,
    string RiskLevel,
    string FundCompany,
    string FundManager,
    DateTime InceptionDate,
    DateTime NavDate,
    decimal UnitNav,
    decimal AccumulatedNav,
    decimal DailyGrowthRate,
    decimal MonthlyReturn,
    decimal YearlyReturn,
    decimal SinceInceptionReturn,
    decimal FundScale,
    decimal ManagementFeeRate,
    decimal CustodyFeeRate,
    decimal SubscriptionFeeRate,
    decimal RedemptionFeeRate,
    string InvestmentScope,
    string PerformanceBenchmark,
    string SubscriptionStatus,
    decimal MinPurchaseAmount,
    string DistributorBankCodes,
    string DistributorBankNames,
    string RiskNote)
{
    /// <summary>
    /// 净值历史点，按日期升序。
    /// 只有**详情**接口会填充（该行已存储的全部净值历史），列表接口为 null ——
    /// 列表一次返回几十只基金，附带净值序列会让响应体无谓地膨胀几十倍。
    /// </summary>
    public IReadOnlyList<FundNavPointResponse>? NavPoints { get; init; }
}

/// <summary>基金净值历史点响应体。</summary>
/// <param name="FundCode">基金代码。</param>
/// <param name="NavDate">净值日期。</param>
/// <param name="UnitNav">单位净值。</param>
/// <param name="AccumulatedNav">累计净值。</param>
/// <param name="DailyGrowthRate">当日单位净值增长率（百分数）。</param>
public sealed record FundNavPointResponse(
    string FundCode,
    DateTime NavDate,
    decimal UnitNav,
    decimal AccumulatedNav,
    decimal DailyGrowthRate);

/// <summary>基金详情 + 净值序列响应体。</summary>
/// <param name="Fund">基金主档。</param>
/// <param name="NavPoints">净值历史点，按日期升序。</param>
public sealed record FundNavResponse(
    FundResponse Fund,
    IReadOnlyList<FundNavPointResponse> NavPoints);

/// <summary>把公募基金领域对象映射为对外响应体。</summary>
public static class FundMapping
{
    /// <summary>基金主档转 DTO。</summary>
    /// <param name="fund">公募基金实体。</param>
    /// <returns>公募基金查询响应体。</returns>
    public static FundResponse ToResponse(this PublicFund fund) =>
        new(fund.FundCode, fund.FundName, fund.FundFullName, fund.CategoryText, fund.RiskLevelText,
            fund.FundCompany, fund.FundManager, fund.InceptionDate, fund.NavDate,
            fund.UnitNav, fund.AccumulatedNav, fund.DailyGrowthRate, fund.MonthlyReturn,
            fund.YearlyReturn, fund.SinceInceptionReturn, fund.FundScale,
            fund.ManagementFeeRate, fund.CustodyFeeRate, fund.SubscriptionFeeRate,
            fund.RedemptionFeeRate, fund.InvestmentScope, fund.PerformanceBenchmark,
            fund.SubscriptionStatusText, fund.MinPurchaseAmount,
            fund.DistributorBankCodes, DistributorNames(fund.DistributorBankCodes), fund.RiskNote);

    /// <summary>净值历史点转 DTO。</summary>
    /// <param name="point">净值历史点实体。</param>
    /// <returns>净值历史点响应体。</returns>
    public static FundNavPointResponse ToResponse(this FundNavPoint point) =>
        new(point.FundCode, point.NavDate, point.UnitNav, point.AccumulatedNav, point.DailyGrowthRate);

    /// <summary>基金详情 + 净值序列转 DTO。</summary>
    /// <param name="fund">公募基金实体。</param>
    /// <param name="navPoints">净值历史点。</param>
    /// <returns>基金详情与净值序列响应体。</returns>
    public static FundNavResponse ToResponse(this PublicFund fund, IReadOnlyList<FundNavPoint> navPoints) =>
        new(fund.ToResponse(), navPoints.Select(p => p.ToResponse()).ToList());

    /// <summary>把代销行代码翻译成银行简称，便于前端直接展示。</summary>
    /// <param name="bankCodes">逗号分隔的行代码；可为空。</param>
    /// <returns>逗号分隔的银行简称。</returns>
    private static string DistributorNames(string bankCodes)
    {
        if (string.IsNullOrWhiteSpace(bankCodes)) return string.Empty;
        return string.Join(",", bankCodes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(BankCatalog.ShortNameOf));
    }
}
