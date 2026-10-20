// ===== 理财 Agent =====
//
// 继承 BankingAgentBase 即可自动获得：耗时统计、审计留痕、异常兜底。
// 你只需要实现 HandleAsync。
//
// 本插件是**只读**场景：查余额、查理财产品、查公募基金都不动账。
//
// 关于公募基金的两条业务判断：
//   1. 公募基金是**代销产品**，银行只是销售渠道，没有「本行客户评级才能买」的硬门槛；
//      因此这里只做**风险匹配提示**，不做拦截。
//   2. 基金代码、净值、收益率都是**公开市场数据**（L2 级），
//      不属于个人敏感信息，因此不经过 DataMasker 脱敏，原样返回；
//      账户、余额等 L3 数据仍然必须脱敏（见下方余额分支）。

using System.Text.RegularExpressions;
using BankingAgent.Base.Agents;
using BankingAgent.Base.Security;
using BankingAgent.Base.Security.Audit;
using BankingAgent.PluginSdk;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Plugin.Wealth;

/// <summary>理财 的业务 Agent。</summary>
public sealed class 理财Agent : BankingAgentBase
{
    /// <summary>基金代码形如 6 位数字。</summary>
    private static readonly Regex FundCodePattern = new(@"(?<!\d)\d{6}(?!\d)", RegexOptions.Compiled);

    /// <summary>用户口语里的基金类型 → 核心系统的类型参数。</summary>
    private static readonly (string Keyword, string Category)[] CategoryKeywords =
    [
        ("货币", "货币型"),
        ("现金宝", "货币型"),
        ("债券", "债券型"),
        ("纯债", "债券型"),
        ("混合", "混合型"),
        ("平衡", "混合型"),
        ("股票", "股票型"),
        ("指数", "指数型"),
        ("etf", "指数型"),
        ("qdii", "QDII"),
        ("纳斯达克", "QDII"),
        ("fof", "FOF"),
    ];

    /// <summary>用户口语里的基金公司简称 → 核心系统的基金公司筛选参数。</summary>
    private static readonly (string Keyword, string Company)[] CompanyKeywords =
    [
        ("易方达", "易方达基金管理有限公司"),
        ("华夏", "华夏基金管理有限公司"),
        ("天弘", "天弘基金管理有限公司"),
        ("中欧", "中欧基金管理有限公司"),
        ("招商", "招商基金管理有限公司"),
        ("南方", "南方基金管理有限公司"),
        ("富国", "富国基金管理有限公司"),
        ("博时", "博时基金管理有限公司"),
        ("嘉实", "嘉实基金管理有限公司"),
        ("广发", "广发基金管理有限公司"),
        ("银河", "银河基金管理有限公司"),
        ("诺安", "诺安基金管理有限公司"),
        ("前海", "前海开源基金管理有限公司"),
        ("信澳", "信达澳亚基金管理有限公司"),
    ];

    private readonly ICoreBankClient _coreBank;

    /// <summary>构造 Agent。需要的依赖由宿主容器注入。</summary>
    public 理财Agent(
        ICoreBankClient coreBank,
        IAuditLogger audit,
        ILogger<理财Agent> logger)
        : base(audit, logger)
    {
        _coreBank = coreBank;
    }

    /// <inheritdoc />
    public override AgentId Id => new("wealth.agent");

    /// <inheritdoc />
    public override string Name => "理财 Agent";

    /// <inheritdoc />
    public override AgentRole Role => AgentRole.DomainExpert;

    /// <summary>
    /// 该 Agent 能处理的意图前缀。宿主按「最长前缀优先」路由，
    /// 因此 "wealth" 不会抢占 "wealth.products"。
    /// 数组第一个元素必须是**场景级**意图（wealth）：路由器在无法细分场景时用它兜底。
    /// </summary>
    public override IReadOnlyList<string> SupportedIntents =>
        ["wealth", "wealth.products", "wealth.balance", "wealth.funds"];

    /// <inheritdoc />
    public override IReadOnlyList<string> TriggerKeywords =>
        ["理财", "基金", "公募", "净值", "基金经理", "定投", "投资", "产品", "收益", "余额", "资产"];

    /// <inheritdoc />
    public override IReadOnlyList<string> TriggerPatterns =>
        // 用户常常只报一个 6 位基金代码（「000001 怎么样」），
        // 这类说法里没有任何业务关键词，只能靠代码格式识别。
        // 前后断言保证不会把账号、银行卡号的长数字段误当成基金代码。
        [@"(?<!\d)\d{6}(?!\d)"];

    /// <inheritdoc />
    protected override async Task<AgentResult> HandleAsync(AgentRequest request, CancellationToken ct)
    {
        // ===== 1. 余额 / 资产查询（只读，取用户第一个账户）=====
        if (request.UserInput.Contains("余额", StringComparison.Ordinal)
            || request.UserInput.Contains("资产", StringComparison.Ordinal))
        {
            var accounts = await _coreBank.ListAccountsAsync(request.UserId, ct);
            var account = accounts.FirstOrDefault();
            if (account is null) return AgentResult.Fail("NO_ACCOUNT", "未找到账户");

            return AgentResult.Ok(
                content: $"账户 {DataMasker.MaskAccount(account.AccountNo)} 当前可用 {account.AvailableAmount:N2} {account.Currency}",
                intent: "wealth.balance",
                data: new Dictionary<string, object?>
                {
                    // L3 字段必须脱敏，账号只返回掩码
                    ["account_no"] = DataMasker.MaskAccount(account.AccountNo),
                    ["balance"] = account.Balance,
                    ["available_amount"] = account.AvailableAmount,
                    ["currency"] = account.Currency,
                    ["data_classification"] = "L3-masked"
                });
        }

        // ===== 2. 公募基金查询（只读）=====
        if (IsFundRequest(request.UserInput))
        {
            return await HandleFundAsync(request, ct);
        }

        // ===== 3. 理财产品查询与推荐（只读）=====
        var products = await _coreBank.ListProductsAsync(
            SlotReader.String(request, "risk_level"), ct);

        if (products.Count == 0)
        {
            return AgentResult.Fail("NO_PRODUCT", "暂无在售理财产品");
        }

        var list = products.Select(p => new Dictionary<string, object?>
        {
            ["code"] = p.Code,
            ["name"] = p.Name,
            ["type"] = p.Type,
            ["risk_level"] = p.RiskLevel,
            ["annual_rate"] = p.AnnualRate,
            ["min_amount"] = p.MinInvestment,
            ["term_days"] = p.TermDays,
            ["status"] = p.Status
        }).ToList();

        var best = products.OrderByDescending(p => p.AnnualRate).First();

        return AgentResult.Ok(
            content: $"当前在售 {products.Count} 只理财产品，年化最高为 {best.Name}"
                     + $"（{best.RiskLevel}，{best.AnnualRate:F2}%，{best.TermDays} 天，起投 {best.MinInvestment:N0} 元）",
            intent: "wealth.products",
            data: new Dictionary<string, object?>
            {
                ["product_count"] = products.Count,
                ["products"] = list
            });
    }

    // ===== 公募基金 =====

    /// <summary>判断这次请求是不是在问公募基金。</summary>
    /// <param name="userInput">用户原始输入。</param>
    /// <returns>命中基金相关关键词或 6 位基金代码时返回 true。</returns>
    private static bool IsFundRequest(string userInput)
    {
        if (string.IsNullOrWhiteSpace(userInput)) return false;
        return userInput.Contains("基金", StringComparison.Ordinal)
            || userInput.Contains("公募", StringComparison.Ordinal)
            || userInput.Contains("净值", StringComparison.Ordinal)
            || userInput.Contains("定投", StringComparison.Ordinal)
            || userInput.Contains("基金经理", StringComparison.Ordinal)
            || FundCodePattern.IsMatch(userInput);
    }

    /// <summary>处理公募基金请求：问单只就查详情，问清单就查列表。</summary>
    /// <param name="request">Agent 请求。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>Agent 结果。</returns>
    private async Task<AgentResult> HandleFundAsync(AgentRequest request, CancellationToken ct)
    {
        var fundCode = ExtractFundCode(request.UserInput);
        if (fundCode is not null)
        {
            return await DescribeFundAsync(fundCode, request, ct);
        }

        return await ListFundsAsync(request, ct);
    }

    /// <summary>查询单只基金详情并给出近期走势摘要。</summary>
    /// <param name="fundCode">基金代码。</param>
    /// <param name="request">Agent 请求。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>Agent 结果。</returns>
    private async Task<AgentResult> DescribeFundAsync(
        string fundCode, AgentRequest request, CancellationToken ct)
    {
        var fund = await _coreBank.GetFundAsync(fundCode, ct);
        if (fund is null)
        {
            return AgentResult.Fail("FUND_NOT_FOUND", $"未找到基金代码 {fundCode} 对应的公募基金");
        }

        var navPoints = await _coreBank.GetFundNavAsync(fundCode, 30, ct);
        var clientBankCodes = await GetClientBankCodesAsync(request.UserId, ct);
        var riskProfiles = await GetClientRiskProfilesAsync(request, ct);

        var trend = DescribeTrend(navPoints);
        var distribution = DescribeDistribution(fund.DistributorBankCodes, clientBankCodes);
        var riskMatch = DescribeRiskMatch(fund.RiskLevel, fund.DistributorBankCodes, riskProfiles);

        return AgentResult.Ok(
            content: $"{fund.FundName}（{fund.FundCode}）{fund.Category} / {fund.RiskLevel}，"
                     + $"最新单位净值 {fund.UnitNav:F4}（{fund.NavDate:yyyy-MM-dd}），"
                     + $"当日 {fund.DailyGrowthRate:F2}%，近一月 {fund.MonthlyReturn:F2}%，近一年 {fund.YearlyReturn:F2}%。"
                     + $"{trend}{distribution}{riskMatch}",
            intent: "wealth.funds",
            data: new Dictionary<string, object?>
            {
                // 基金代码、净值、收益率是公开市场数据（L2），不是个人敏感信息，原样返回即可。
                ["fund_code"] = fund.FundCode,
                ["fund_name"] = fund.FundName,
                ["category"] = fund.Category,
                ["risk_level"] = fund.RiskLevel,
                ["fund_company"] = fund.FundCompany,
                ["fund_manager"] = fund.FundManager,
                ["unit_nav"] = fund.UnitNav,
                ["accumulated_nav"] = fund.AccumulatedNav,
                ["daily_growth_rate"] = fund.DailyGrowthRate,
                ["monthly_return"] = fund.MonthlyReturn,
                ["yearly_return"] = fund.YearlyReturn,
                ["since_inception_return"] = fund.SinceInceptionReturn,
                ["fund_scale"] = fund.FundScale,
                ["subscription_status"] = fund.SubscriptionStatus,
                ["distributor_bank_codes"] = fund.DistributorBankCodes,
                ["distributable_at_client_banks"] = IsDistributableAt(fund.DistributorBankCodes, clientBankCodes),
                ["risk_match_note"] = riskMatch,
                ["client_risk_levels"] = riskProfiles
                    .ToDictionary(p => p.BankCode, p => p.RiskLevel, StringComparer.OrdinalIgnoreCase),
                ["nav_points"] = navPoints
                    .Select(p => new Dictionary<string, object?>
                    {
                        ["nav_date"] = p.NavDate,
                        ["unit_nav"] = p.UnitNav,
                        ["accumulated_nav"] = p.AccumulatedNav,
                        ["daily_growth_rate"] = p.DailyGrowthRate,
                    })
                    .ToList(),
                ["data_classification"] = "L2-public-market-data",
            });
    }

    /// <summary>查询基金清单，并按用户口语里的排行维度排序。</summary>
    /// <param name="request">Agent 请求。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>Agent 结果。</returns>
    private async Task<AgentResult> ListFundsAsync(AgentRequest request, CancellationToken ct)
    {
        var query = BuildFundQuery(request);
        var funds = await _coreBank.ListFundsAsync(query, ct);
        if (funds.Count == 0)
        {
            return AgentResult.Fail("NO_FUND", "没有符合条件在售的公募基金");
        }

        var metric = DetectRankingMetric(request.UserInput);
        var ordered = OrderByMetric(funds, metric);
        var clientBankCodes = await GetClientBankCodesAsync(request.UserId, ct);
        var riskProfiles = await GetClientRiskProfilesAsync(request, ct);

        // 列表里的每只基金按各自的代销行做风险匹配，
        // 因此这里逐只算，不能只算一次。
        string RiskNoteFor(FundSnapshot fund) =>
            DescribeRiskMatch(fund.RiskLevel, fund.DistributorBankCodes, riskProfiles);

        var list = ordered.Select(f => new Dictionary<string, object?>
        {
            ["fund_code"] = f.FundCode,
            ["fund_name"] = f.FundName,
            ["category"] = f.Category,
            ["risk_level"] = f.RiskLevel,
            ["fund_company"] = f.FundCompany,
            ["fund_manager"] = f.FundManager,
            ["unit_nav"] = f.UnitNav,
            ["daily_growth_rate"] = f.DailyGrowthRate,
            ["monthly_return"] = f.MonthlyReturn,
            ["yearly_return"] = f.YearlyReturn,
            ["fund_scale"] = f.FundScale,
            ["distributor_bank_codes"] = f.DistributorBankCodes,
            ["distributable_at_client_banks"] = IsDistributableAt(f.DistributorBankCodes, clientBankCodes),
            ["risk_match_note"] = RiskNoteFor(f),
        }).ToList();

        var top = ordered[0];

        // 风险提示要跟着「榜首推荐」走：用户看到的第一只基金能不能买，
        // 是他最关心的事，不该只藏在明细里。
        var riskNote = riskProfiles.Count == 0
            ? "未能获取您的风险承受能力评级，本次未做风险匹配判断。"
            : RiskNoteFor(top);

        return AgentResult.Ok(
            content: $"共筛出 {funds.Count} 只公募基金，按{DescribeMetric(metric)}排序，"
                     + $"居首的是 {top.FundName}（{top.FundCode}，{top.Category}，{top.RiskLevel}，"
                     + $"单位净值 {top.UnitNav:F4}，当日 {top.DailyGrowthRate:F2}%，规模 {top.FundScale:F2} 亿元）。"
                     + riskNote,
            intent: "wealth.funds",
            data: new Dictionary<string, object?>
            {
                // 基金代码、净值、收益率是公开市场数据（L2），不是个人敏感信息，原样返回即可。
                ["fund_count"] = funds.Count,
                ["ranking_metric"] = metric.ToString(),
                ["query"] = new Dictionary<string, object?>
                {
                    ["category"] = query.Category ?? "",
                    ["risk_level"] = query.RiskLevel ?? "",
                    ["company"] = query.Company ?? "",
                },
                ["client_risk_level"] = riskProfiles.Count > 0
                    ? string.Join("、", riskProfiles.Select(p => $"{p.BankName} {p.RiskLevel}"))
                    : "",
                ["funds"] = list,
                ["data_classification"] = "L2-public-market-data",
            });
    }

    /// <summary>从用户输入里取出 6 位基金代码。</summary>
    /// <param name="userInput">用户原始输入。</param>
    /// <returns>基金代码；没识别到时返回 null。</returns>
    private static string? ExtractFundCode(string userInput)
    {
        if (string.IsNullOrWhiteSpace(userInput)) return null;
        var match = FundCodePattern.Match(userInput);
        return match.Success ? match.Value : null;
    }

    /// <summary>把用户口语翻译成核心系统的基金查询条件。</summary>
    /// <param name="request">Agent 请求。</param>
    /// <returns>基金查询条件。</returns>
    private static FundQuery BuildFundQuery(AgentRequest request)
    {
        var userInput = request.UserInput;

        var category = CategoryKeywords
            .FirstOrDefault(pair => userInput.Contains(pair.Keyword, StringComparison.OrdinalIgnoreCase))
            .Category;

        var company = CompanyKeywords
            .FirstOrDefault(pair => userInput.Contains(pair.Keyword, StringComparison.Ordinal))
            .Company;

        // 风险等级既可能来自用户话术（R2），也可能来自上游槽位。
        var riskLevel = SlotReader.String(request, "fund_risk_level")
            ?? ExtractRiskLevel(userInput);

        return new FundQuery
        {
            Category = string.IsNullOrEmpty(category) ? null : category,
            Company = string.IsNullOrEmpty(company) ? null : company,
            RiskLevel = string.IsNullOrEmpty(riskLevel) ? null : riskLevel,
            Limit = 50,
        };
    }

    /// <summary>从用户输入里识别风险等级。</summary>
    /// <param name="userInput">用户原始输入。</param>
    /// <returns>形如 "R2" 的风险等级；没识别到时返回 null。</returns>
    private static string? ExtractRiskLevel(string userInput)
    {
        foreach (var level in new[] { "R1", "R2", "R3", "R4", "R5" })
        {
            if (userInput.Contains(level, StringComparison.OrdinalIgnoreCase)) return level;
        }

        return null;
    }

    /// <summary>判断用户想看哪个维度的排行。</summary>
    /// <param name="userInput">用户原始输入。</param>
    /// <returns>排行维度。</returns>
    private static FundRankingMetric DetectRankingMetric(string userInput)
    {
        if (userInput.Contains("今日", StringComparison.Ordinal)
            || userInput.Contains("今天", StringComparison.Ordinal)
            || userInput.Contains("日增长", StringComparison.Ordinal))
        {
            return FundRankingMetric.Daily;
        }

        if (userInput.Contains("近一月", StringComparison.Ordinal)
            || userInput.Contains("本月", StringComparison.Ordinal)
            || userInput.Contains("月度", StringComparison.Ordinal))
        {
            return FundRankingMetric.Monthly;
        }

        return FundRankingMetric.Yearly;
    }

    /// <summary>按排行维度排序基金。</summary>
    /// <param name="funds">基金列表。</param>
    /// <param name="metric">排行维度。</param>
    /// <returns>排序后的基金列表。</returns>
    private static IReadOnlyList<FundSnapshot> OrderByMetric(
        IReadOnlyList<FundSnapshot> funds, FundRankingMetric metric) =>
        metric switch
        {
            FundRankingMetric.Daily => funds
                .OrderByDescending(f => f.DailyGrowthRate)
                .ThenBy(f => f.FundCode, StringComparer.Ordinal)
                .ToList(),
            FundRankingMetric.Monthly => funds
                .OrderByDescending(f => f.MonthlyReturn)
                .ThenBy(f => f.FundCode, StringComparer.Ordinal)
                .ToList(),
            _ => funds
                .OrderByDescending(f => f.YearlyReturn)
                .ThenBy(f => f.FundCode, StringComparer.Ordinal)
                .ToList(),
        };

    /// <summary>取客户在各行的开户行，用于判断「本行有没有代销这只基金」。</summary>
    /// <param name="userId">客户号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>客户开户行代码集合；查不到时返回空集合。</returns>
    private async Task<IReadOnlyCollection<string>> GetClientBankCodesAsync(
        string userId, CancellationToken ct)
    {
        try
        {
            var accounts = await _coreBank.ListAccountsAsync(userId, ct);
            return accounts
                .Select(a => a.BankCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // 取不到客户开户行不该让整个基金查询失败：代销提示降级为「未确认」即可。
            return [];
        }
    }

    /// <summary>判断基金是否在客户所在的行有代销。</summary>
    /// <param name="distributorBankCodes">代销行代码（逗号分隔）。</param>
    /// <param name="clientBankCodes">客户开户行代码。</param>
    /// <returns>有交集返回 true。</returns>
    private static bool IsDistributableAt(
        string distributorBankCodes, IReadOnlyCollection<string> clientBankCodes)
    {
        if (clientBankCodes.Count == 0) return false;
        var distributors = distributorBankCodes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return distributors.Any(code => clientBankCodes.Contains(code, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>生成代销情况说明。</summary>
    /// <param name="distributorBankCodes">代销行代码（逗号分隔）。</param>
    /// <param name="clientBankCodes">客户开户行代码。</param>
    /// <returns>代销说明；没有额外信息时返回空字符串。</returns>
    private static string DescribeDistribution(
        string distributorBankCodes, IReadOnlyCollection<string> clientBankCodes)
    {
        if (clientBankCodes.Count == 0) return string.Empty;
        if (IsDistributableAt(distributorBankCodes, clientBankCodes)) return string.Empty;
        return "该基金在您所在的银行暂未代销，可关注其他行的代销情况。";
    }

    /// <summary>
    /// 取客户在各开户行的风险承受能力评级。
    ///
    /// 为什么要问核心系统：客户评级是**行内**评级（同一客户在建行可能是 R2、在工行是 R3），
    /// 而理财风险匹配必须按**实际申购的那一行**的评级来判断。
    /// 插件自己无从得知，只能问核心系统。
    ///
    /// 显式槽位 <c>client_risk_level</c> 优先：调用方已知评级时不必再问一次。
    /// </summary>
    /// <param name="request">当前请求。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>各行评级档案；取不到时返回空集合。</returns>
    private async Task<IReadOnlyList<ClientRiskProfile>> GetClientRiskProfilesAsync(
        AgentRequest request, CancellationToken ct)
    {
        var explicitLevel = SlotReader.String(request, "client_risk_level");
        if (!string.IsNullOrWhiteSpace(explicitLevel))
        {
            var bankCodes = await GetClientBankCodesAsync(request.UserId, ct);
            return bankCodes
                .Select(code => new ClientRiskProfile(code, code, explicitLevel))
                .ToList();
        }

        try
        {
            var customers = await _coreBank.GetCustomersAsync(request.UserId, ct);
            return customers
                .Where(c => !string.IsNullOrWhiteSpace(c.BankCode))
                .Select(c => new ClientRiskProfile(c.BankCode, c.BankName, c.RiskLevel))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // 取不到评级不该让基金查询失败：风险匹配降级为「未做判断」即可。
            return [];
        }
    }

    /// <summary>
    /// 生成风险匹配提示。
    ///
    /// 匹配只在「客户有该行档案」且「该行代销这只基金」时才有意义 ——
    /// 在没有代销关系的行上，评级高低与能不能买这只基金无关。
    /// </summary>
    /// <param name="fundRiskLevel">基金风险等级（R1-R5）。</param>
    /// <param name="distributorBankCodes">代销行代码（逗号分隔）。</param>
    /// <param name="riskProfiles">客户在各行的评级档案。</param>
    /// <returns>风险匹配说明；无需提示时返回空字符串。</returns>
    private static string DescribeRiskMatch(
        string fundRiskLevel,
        string distributorBankCodes,
        IReadOnlyList<ClientRiskProfile> riskProfiles)
    {
        if (riskProfiles.Count == 0) return string.Empty;
        if (!TryParseRiskLevel(fundRiskLevel, out var fundLevel)) return string.Empty;

        var distributors = distributorBankCodes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // 只看「客户有档案且该行代销这只基金」的行。
        var relevant = riskProfiles
            .Where(p => distributors.Contains(p.BankCode, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (relevant.Count == 0) return string.Empty;

        var mismatched = relevant
            .Where(p => TryParseRiskLevel(p.RiskLevel, out var clientLevel) && fundLevel > clientLevel)
            .ToList();
        if (mismatched.Count == 0) return string.Empty;

        var banks = string.Join("、", mismatched.Select(p =>
            string.IsNullOrWhiteSpace(p.BankName) ? p.BankCode : p.BankName));

        return $"提示：该基金风险等级 {fundRiskLevel}，在{banks}高于您的风险承受能力"
               + $"（{string.Join("、", mismatched.Select(p => p.RiskLevel))}），暂不能申购。";
    }

    /// <summary>客户在某一行开户的风险承受能力档案。</summary>
    /// <param name="BankCode">开户行代码。</param>
    /// <param name="BankName">开户行名称。</param>
    /// <param name="RiskLevel">行内风险评级（R1-R4）。</param>
    private sealed record ClientRiskProfile(string BankCode, string BankName, string RiskLevel);

    /// <summary>把 R1-R5 文本解析成可比较的数值。</summary>
    /// <param name="value">风险等级文本。</param>
    /// <param name="level">解析成功时输出等级数值。</param>
    /// <returns>解析成功返回 true。</returns>
    private static bool TryParseRiskLevel(string value, out int level)
    {
        level = 0;
        var text = value.Trim().TrimStart('R', 'r');
        return int.TryParse(text, out level) && level is >= 1 and <= 5;
    }

    /// <summary>用近期净值点概括走势。</summary>
    /// <param name="navPoints">净值历史点（按日期升序）。</param>
    /// <returns>走势说明；没有数据时返回空字符串。</returns>
    private static string DescribeTrend(IReadOnlyList<FundNavPointSnapshot> navPoints)
    {
        if (navPoints.Count < 2) return string.Empty;

        var first = navPoints[0];
        var last = navPoints[^1];
        var change = first.UnitNav == 0m ? 0m : (last.UnitNav - first.UnitNav) / first.UnitNav * 100m;
        return $"近 {navPoints.Count} 个交易日单位净值从 {first.UnitNav:F4} 变为 {last.UnitNav:F4}（{change:F2}%）。";
    }

    /// <summary>排行维度的中文说明。</summary>
    /// <param name="metric">排行维度。</param>
    /// <returns>中文说明。</returns>
    private static string DescribeMetric(FundRankingMetric metric) =>
        metric switch
        {
            FundRankingMetric.Daily => "日增长率",
            FundRankingMetric.Monthly => "近一月收益率",
            _ => "近一年收益率",
        };

    // ===== 写操作的写法（资金类操作必须遵守，否则不要写成写操作）=====
    //
    // 1) 先过合规守卫，被拒就返回 Fail，不要继续执行：
    //    var decision = _compliance.Evaluate(new ComplianceContext
    //    {
    //        UserId = request.UserId,
    //        Scenario = "wealth",
    //        Intent = "wealth.execute"
    //    });
    //    if (!decision.Allowed) return AgentResult.Fail("COMPLIANCE_REJECTED", decision.Reason);
    //
    // 2) 资金操作必须走人工回环，绝不能直接提交副作用：
    //    return AgentResult.PendingApproval("金额 30000 元超过自动放行上限，需人工确认", data);
    //    用户确认后宿主会带 confirmed=true 再次路由到本 Agent，届时才真正提交。
    //
    // 3) 提交成功后置 SideEffectCommitted = true，并用 request.SessionId 作为幂等键，
    //    避免重复点击导致重复扣款。
}
