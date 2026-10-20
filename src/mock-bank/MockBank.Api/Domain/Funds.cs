// ===== 领域模型：公募基金主档与净值历史 =====
//
// 为什么基金要单独一套风险等级：
//   Domain/Customers.cs 里的 RiskLevel 是**行内客户风险承受能力评级**（R1-R4），
//   衡量的是「这个人能承受多大波动」；而公募基金的 R1-R5 是**产品自身的风险等级**，
//   由基金合同与监管口径确定，衡量的是「这个产品本身有多险」。
//   两者区间不同、语义不同、来源不同，复用同一个枚举会让「客户能买什么」与
//   「产品有多险」互相污染，因此这里定义独立的 FundRiskLevel。
//
// 为什么基金是「代销产品」：
//   公募基金由基金管理公司发行，银行只是代销渠道，基金本身不是银行的自营产品。
//   同一只基金可以被多家银行同时代销，各家银行的核心系统里各自存一份独立副本
//   （这正是「一家银行一个库」架构的体现）；跨行聚合时按基金代码归并。
//   各行副本的静态数据刻意保持一致（费率按基金公司公布的挂牌费率，不建模各行折扣），
//   因此归并不会丢信息，只需额外记录「哪些行在代销」。
//
// 净值数据的自洽性约定（种子数据必须同时满足）：
//   * 累计净值 = 单位净值 + 成立以来每份累计分红，即 A = U + D，且 D ≥ 0；
//   * 净值点的 DailyGrowthRate 就是**单位净值**的日环比，而不是累计净值的日环比；
//   * 分红日单位净值会自然下跳（因为分红从单位净值里扣），这是真实基金的常态；
//   * 单位净值、累计净值、日增长率三者由同一段日收益率序列生成，不允许互相矛盾。

using System.ComponentModel.DataAnnotations.Schema;

namespace MockBank.Api.Domain;

/// <summary>公募基金类型。</summary>
public enum FundCategory
{
    /// <summary>货币型：以短期流动性管理为主，风险最低。</summary>
    MoneyMarket = 1,

    /// <summary>债券型：主要投资于债券。</summary>
    Bond = 2,

    /// <summary>混合型：股票与债券等资产混合配置。</summary>
    Mixed = 3,

    /// <summary>股票型：以股票为主要投资标的。</summary>
    Equity = 4,

    /// <summary>指数型：被动跟踪某一指数。</summary>
    Index = 5,

    /// <summary>QDII：合格境内机构投资者参与境外市场投资的基金。</summary>
    QualifiedForeign = 6,

    /// <summary>FOF：基金中基金，以其他基金为主要投资标的。</summary>
    FundOfFunds = 7,
}

/// <summary>公募基金风险等级（R1-R5）。与客户风险承受能力评级是两套体系，不可混用。</summary>
public enum FundRiskLevel
{
    /// <summary>R1 谨慎型。</summary>
    R1 = 1,

    /// <summary>R2 稳健型。</summary>
    R2 = 2,

    /// <summary>R3 平衡型。</summary>
    R3 = 3,

    /// <summary>R4 积极型。</summary>
    R4 = 4,

    /// <summary>R5 激进型。</summary>
    R5 = 5,
}

/// <summary>公募基金申购赎回状态。</summary>
public enum FundSubscriptionStatus
{
    /// <summary>开放申购：可正常申购与赎回。</summary>
    OpenSubscription = 1,

    /// <summary>暂停申购：暂不受理新申购，赎回照常。</summary>
    Suspended = 2,

    /// <summary>限额申购：单日或单笔有金额上限。</summary>
    Limited = 3,

    /// <summary>封闭期：不办理申赎。</summary>
    Closed = 4,
}

/// <summary>基金排行维度。</summary>
public enum FundRankingMetric
{
    /// <summary>按最新一日增长率排序。</summary>
    Daily = 1,

    /// <summary>按近一月收益率排序。</summary>
    Monthly = 2,

    /// <summary>按近一年收益率排序。</summary>
    Yearly = 3,
}

/// <summary>公募基金主档：模拟核心系统中某家银行代销的公募基金。</summary>
public sealed record PublicFund
{
    /// <summary>基金代码，6 位数字（如 005827）。同一只基金在各行数据库里代码相同。</summary>
    public required string FundCode { get; init; }

    /// <summary>代销该基金的银行行代码（如 ICBC）。一家银行一个库，副本各自独立存储。</summary>
    public required string BankCode { get; init; }

    /// <summary>基金简称（如 易方达蓝筹精选混合）。</summary>
    public required string FundName { get; init; }

    /// <summary>基金全称（含「证券投资基金」等法律名称）。</summary>
    public required string FundFullName { get; init; }

    /// <summary>基金类型。</summary>
    public required FundCategory Category { get; init; }

    /// <summary>基金风险等级（R1-R5）。</summary>
    public required FundRiskLevel RiskLevel { get; init; }

    /// <summary>基金管理人（基金公司全称）。</summary>
    public required string FundCompany { get; init; }

    /// <summary>现任基金经理。</summary>
    public required string FundManager { get; init; }

    /// <summary>
    /// 基金成立日期。
    /// 用 DateTime（Kind=Unspecified）而不是 DateTimeOffset：成立日是**日历日**，没有时区概念。
    /// 若走项目里「DateTimeOffset 一律转 UTC」的约定，北京时间零点会被折算成前一日 16:00，
    /// 展示时就会出现「成立日少一天」这种刺眼错误。
    /// </summary>
    public required DateTime InceptionDate { get; init; }

    /// <summary>最新净值日期。与 <see cref="InceptionDate"/> 同理，是日历日而非时间点。</summary>
    public required DateTime NavDate { get; init; }

    /// <summary>单位净值。</summary>
    public required decimal UnitNav { get; init; }

    /// <summary>累计净值（= 单位净值 + 成立以来每份累计分红）。</summary>
    public required decimal AccumulatedNav { get; init; }

    /// <summary>最新一日的单位净值增长率（百分数，0.35 表示 0.35%）。</summary>
    public required decimal DailyGrowthRate { get; init; }

    /// <summary>近一月收益率（百分数），按累计净值计算。</summary>
    public required decimal MonthlyReturn { get; init; }

    /// <summary>近一年收益率（百分数），按累计净值计算。</summary>
    public required decimal YearlyReturn { get; init; }

    /// <summary>成立以来涨幅（百分数），按累计净值相对成立时的 1.0000 计算。</summary>
    public required decimal SinceInceptionReturn { get; init; }

    /// <summary>基金规模（亿元）。</summary>
    public required decimal FundScale { get; init; }

    /// <summary>管理费率（年化百分数）。</summary>
    public required decimal ManagementFeeRate { get; init; }

    /// <summary>托管费率（年化百分数）。</summary>
    public required decimal CustodyFeeRate { get; init; }

    /// <summary>申购费率（百分数）。</summary>
    public required decimal SubscriptionFeeRate { get; init; }

    /// <summary>赎回费率（百分数）。</summary>
    public required decimal RedemptionFeeRate { get; init; }

    /// <summary>投资范围。</summary>
    public required string InvestmentScope { get; init; }

    /// <summary>业绩比较基准。</summary>
    public required string PerformanceBenchmark { get; init; }

    /// <summary>申赎状态。</summary>
    public required FundSubscriptionStatus SubscriptionStatus { get; init; }

    /// <summary>起购金额（元）。</summary>
    public required decimal MinPurchaseAmount { get; init; }

    /// <summary>风险提示。所有基金统一提示这是模拟演示数据。</summary>
    public string RiskNote { get; init; } = "本基金数据为模拟演示数据，不构成任何投资建议。";

    /// <summary>
    /// 代销该基金的银行行代码，逗号分隔。
    /// 这是**跨行聚合视图**上的信息：单行数据库里只有本行代码，
    /// 由 MockBankStore 在归并各家代销清单时回填，因此不映射到数据库列。
    /// </summary>
    [NotMapped]
    public string DistributorBankCodes { get; init; } = "";

    /// <summary>基金类型的中文描述。</summary>
    public string CategoryText => Category.ToText();

    /// <summary>风险等级的文本描述（R1-R5）。</summary>
    public string RiskLevelText => RiskLevel.ToText();

    /// <summary>申赎状态的中文描述。</summary>
    public string SubscriptionStatusText => SubscriptionStatus.ToText();
}

/// <summary>基金净值历史点：某只基金在某个自然日的单位净值、累计净值与日增长率。</summary>
public sealed record FundNavPoint
{
    /// <summary>基金代码（与 <see cref="PublicFund.FundCode"/> 对应）。</summary>
    public required string FundCode { get; init; }

    /// <summary>净值日期。与基金主档的 <see cref="PublicFund.NavDate"/> 同理，是日历日。</summary>
    public required DateTime NavDate { get; init; }

    /// <summary>单位净值。</summary>
    public required decimal UnitNav { get; init; }

    /// <summary>累计净值。</summary>
    public required decimal AccumulatedNav { get; init; }

    /// <summary>当日单位净值增长率（百分数）。</summary>
    public required decimal DailyGrowthRate { get; init; }
}

/// <summary>公募基金类型的解析与格式化辅助方法。</summary>
public static class FundCategoryExtensions
{
    /// <summary>把「债券型」「bond」之类的文本解析为 <see cref="FundCategory"/>。</summary>
    /// <param name="value">待解析文本。</param>
    /// <param name="category">解析成功时输出基金类型。</param>
    /// <returns>解析成功返回 true。</returns>
    public static bool TryParseFundCategory(string? value, out FundCategory category)
    {
        category = FundCategory.Mixed;
        if (string.IsNullOrWhiteSpace(value)) return false;

        switch (value.Trim().ToLowerInvariant())
        {
            case "货币型" or "货币" or "货币基金" or "moneymarket" or "money":
                category = FundCategory.MoneyMarket;
                return true;
            case "债券型" or "债券" or "债券基金" or "bond":
                category = FundCategory.Bond;
                return true;
            case "混合型" or "混合" or "混合基金" or "mixed" or "hybrid":
                category = FundCategory.Mixed;
                return true;
            case "股票型" or "股票" or "股票基金" or "equity" or "stock":
                category = FundCategory.Equity;
                return true;
            case "指数型" or "指数" or "指数基金" or "index" or "etf":
                category = FundCategory.Index;
                return true;
            case "qdii" or "qdii型" or "合格境外":
                category = FundCategory.QualifiedForeign;
                return true;
            case "fof" or "fof型" or "基金中基金":
                category = FundCategory.FundOfFunds;
                return true;
            default:
                return false;
        }
    }

    /// <summary>把基金类型格式化为中文。</summary>
    /// <param name="category">基金类型。</param>
    /// <returns>货币型 / 债券型 / 混合型 / 股票型 / 指数型 / QDII / FOF。</returns>
    public static string ToText(this FundCategory category) =>
        category switch
        {
            FundCategory.MoneyMarket => "货币型",
            FundCategory.Bond => "债券型",
            FundCategory.Mixed => "混合型",
            FundCategory.Equity => "股票型",
            FundCategory.Index => "指数型",
            FundCategory.QualifiedForeign => "QDII",
            FundCategory.FundOfFunds => "FOF",
            _ => "未知",
        };
}

/// <summary>公募基金风险等级的解析与格式化辅助方法。</summary>
public static class FundRiskLevelExtensions
{
    /// <summary>把 "R3" / "3" / "平衡型" 之类的文本解析为 <see cref="FundRiskLevel"/>。</summary>
    /// <param name="value">待解析文本。</param>
    /// <param name="level">解析成功时输出风险等级。</param>
    /// <returns>解析成功返回 true。</returns>
    public static bool TryParseFundRiskLevel(string? value, out FundRiskLevel level)
    {
        level = FundRiskLevel.R3;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim();
        if (text.StartsWith('R') || text.StartsWith('r'))
        {
            text = text[1..];
        }

        switch (text)
        {
            case "1" or "谨慎型" or "谨慎":
                level = FundRiskLevel.R1;
                return true;
            case "2" or "稳健型" or "稳健":
                level = FundRiskLevel.R2;
                return true;
            case "3" or "平衡型" or "平衡":
                level = FundRiskLevel.R3;
                return true;
            case "4" or "积极型" or "积极":
                level = FundRiskLevel.R4;
                return true;
            case "5" or "激进型" or "激进":
                level = FundRiskLevel.R5;
                return true;
            default:
                return false;
        }
    }

    /// <summary>把风险等级格式化为 "R3" 形式。</summary>
    /// <param name="level">风险等级。</param>
    /// <returns>形如 "R3" 的字符串。</returns>
    public static string ToText(this FundRiskLevel level) => $"R{(int)level}";
}

/// <summary>公募基金申赎状态的格式化辅助方法。</summary>
public static class FundSubscriptionStatusExtensions
{
    /// <summary>把申赎状态格式化为中文。</summary>
    /// <param name="status">申赎状态。</param>
    /// <returns>开放申购 / 暂停申购 / 限额申购 / 封闭期。</returns>
    public static string ToText(this FundSubscriptionStatus status) =>
        status switch
        {
            FundSubscriptionStatus.OpenSubscription => "开放申购",
            FundSubscriptionStatus.Suspended => "暂停申购",
            FundSubscriptionStatus.Limited => "限额申购",
            FundSubscriptionStatus.Closed => "封闭期",
            _ => "未知",
        };
}
