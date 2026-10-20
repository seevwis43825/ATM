// ===== Agent 路由器 =====
// 按意图把请求路由到正确的领域 Agent。插件新增 Agent 无需修改本文件，
// 因为路由表在容器构建时从所有 IBankingAgent 实现自动收集。
//
// 路由分两层：
//   1. 场景层：决定「交给哪个 Agent」（意图前缀最长匹配）
//   2. 动作层：交给 Agent 自己去判断走只读分支还是写分支
//      —— 见 IntentContext 与各插件自己的语义解析器。

namespace BankingAgent.Base.Agents;

using System.Text.RegularExpressions;
using BankingAgent.PluginSdk;
using Microsoft.Extensions.Logging;

/// <summary>Agent 路由器。根据上游识别出的意图选择处理者。</summary>
public class AgentRouter
{
    /// <summary>
    /// 否定标记。出现在关键词前面时，该关键词不算命中 ——
    /// 「不要转账」「别冻结我的卡」里的「转账」「冻结」表达的是**不做**，不是**要做**。
    /// </summary>
    private static readonly string[] NegationMarkers =
    [
        "不要", "不想", "不打算", "不用", "别", "甭", "先不", "暂不",
        "取消", "撤销", "停止", "不必", "无需"
    ];

    private readonly IReadOnlyList<IBankingAgent> _agents;
    private readonly ILogger<AgentRouter> _logger;

    /// <summary>构造路由器，收集容器内所有 Agent。</summary>
    public AgentRouter(IEnumerable<IBankingAgent> agents, ILogger<AgentRouter> logger)
    {
        _agents = agents.ToList();
        _logger = logger;
    }

    /// <summary>已注册的 Agent 清单。</summary>
    public IReadOnlyList<IBankingAgent> Agents => _agents;

    /// <summary>按意图前缀查找最匹配的 Agent。</summary>
    public IBankingAgent? Resolve(string? intent)
    {
        if (string.IsNullOrWhiteSpace(intent)) return null;

        // 最长前缀优先，避免 transfer 与 transfer.receive 互相抢占
        return _agents
            .Where(a => a.SupportedIntents.Any(p =>
                intent.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(a => a.SupportedIntents
                .Where(p => intent.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                .Max(p => p.Length))
            .FirstOrDefault();
    }

    /// <summary>按场景查找领域专家 Agent。</summary>
    public IReadOnlyList<IBankingAgent> ResolveByScenario(string scenario) =>
        _agents.Where(a => a.SupportedIntents
            .Any(p => p.StartsWith(scenario, StringComparison.OrdinalIgnoreCase))).ToList();

    /// <summary>
    /// 用已注册 Agent 自报的信息反查意图，作为规则式意图识别的兜底。
    ///
    /// 为什么需要它：路由表是插件自动注册的，若意图识别只认宿主里硬编码的关键词表，
    /// 新插件装进来后能出现在 /api/plugins/agents 里，却永远拿不到请求 ——
    /// 「加功能只需加插件」这条设计承诺实际不成立。
    /// 这里让兜底逻辑从 Agent 自报的「意图前缀 + 自然语言关键词」推导意图，
    /// 插件即插即用，宿主不再需要认识任何业务词。
    ///
    /// 打分规则（这是**降级路径**，主路径是大模型）：
    ///   - 关键词越长越具体，得分越高（2 个字的「账单」比 1 个字的「卡」更可信）；
    ///   - 被否定词否定的关键词不计分；
    ///   - 得分相同时，命中关键词更多的 Agent 优先；再相同则按注册顺序取第一个（结果稳定可复现）。
    /// </summary>
    /// <param name="userInput">用户原始输入。</param>
    /// <returns>匹配到的意图；无法匹配时返回 null。</returns>
    public string? InferIntentFromInput(string? userInput)
    {
        if (string.IsNullOrWhiteSpace(userInput)) return null;

        // 一级：输入直接命中 Agent 声明的意图前缀（多为英文输入，如 "transfer 500"）
        var byPrefix = _agents
            .SelectMany(a => a.SupportedIntents)
            .Where(p => !string.IsNullOrWhiteSpace(p)
                        && userInput.Contains(p, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Length)
            .FirstOrDefault();

        if (byPrefix is not null) return byPrefix;

        // 二级：按 Agent 自报的自然语言关键词打分，取分最高者。
        var scores = new List<(IBankingAgent Agent, double Score, int Hits)>();

        foreach (var agent in _agents)
        {
            double best = 0;
            var hits = 0;

            foreach (var keyword in agent.TriggerKeywords)
            {
                if (string.IsNullOrWhiteSpace(keyword)) continue;
                if (!HasActiveOccurrence(userInput, keyword)) continue;

                hits++;
                best = Math.Max(best, keyword.Trim().Length);
            }

            if (hits > 0)
            {
                scores.Add((agent, best, hits));
            }
        }

        if (scores.Count == 0)
        {
            // 关键词一条都没命中时，才用插件自报的正则条件兜底
            //（补「用户只报一个 6 位基金代码」这类固定关键词表达不了的说法）。
            return InferIntentFromPatterns(userInput);
        }

        var winner = scores
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.Hits)
            .First();

        // 取该 Agent 声明的**第一个（场景级）**意图：用户请求的粒度是场景
        // （"查卡片"），具体动作（列卡 / 改状态）由 Agent 内部判定。
        return winner.Agent.SupportedIntents.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
    }

    /// <summary>
    /// 用 Agent 自报的正则触发条件反查意图。
    ///
    /// 只在关键词完全没命中时使用：正则条件是**兜底**而不是**优先**，
    /// 否则「转 500 给 622202」这种含 6 位数字的转账请求会被基金代码规则抢走。
    /// </summary>
    /// <param name="userInput">用户原始输入。</param>
    /// <returns>匹配到的意图；无法匹配时返回 null。</returns>
    private string? InferIntentFromPatterns(string userInput)
    {
        foreach (var agent in _agents)
        {
            foreach (var pattern in agent.TriggerPatterns)
            {
                if (string.IsNullOrWhiteSpace(pattern)) continue;

                bool matched;
                try
                {
                    matched = Regex.IsMatch(userInput, pattern);
                }
                catch (ArgumentException)
                {
                    // 插件写了非法正则：跳过而不是让整个路由崩掉。
                    _logger.LogWarning("Agent {AgentId} 的触发正则 {Pattern} 非法，已跳过",
                        agent.Id, pattern);
                    continue;
                }

                if (!matched) continue;

                var intent = agent.SupportedIntents.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
                if (intent is not null) return intent;
            }
        }

        return null;
    }

    /// <summary>执行请求并返回结果。找不到处理者时返回兜底结果。</summary>
    public async Task<AgentResult> RouteAsync(AgentRequest request, CancellationToken ct = default)
    {
        var intent = request.Upstream?.Intent
                     ?? request.SharedContext.GetValueOrDefault("intent")?.ToString();

        var agent = Resolve(intent);
        if (agent is null)
        {
            _logger.LogWarning("未找到处理意图 {Intent} 的 Agent", intent);
            return BuildUnresolvedResult(intent);
        }

        _logger.LogInformation("路由 {Intent} → {Agent}", intent, agent.Id);
        return await agent.ExecuteAsync(request, ct);
    }

    /// <summary>
    /// 意图无法落到任何 Agent 时的兜底结果。
    ///
    /// 为什么要专门写它：过去这里直接返回「系统暂时无法处理该请求，已转人工客服」，
    /// 对用户来说是一句死胡同 —— 既没说清哪里没听懂，也没告诉他这个助手能干什么。
    /// 银行入口的兜底应该像柜台一样：告诉对方我们能办哪些事，请他换个说法。
    /// </summary>
    /// <param name="intent">没能匹配到 Agent 的意图标识。</param>
    /// <returns>带可用能力清单的兜底结果。</returns>
    private AgentResult BuildUnresolvedResult(string? intent)
    {
        var capabilities = _agents
            .Select(agent => new
            {
                Scenario = agent.SupportedIntents.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "",
                Name = agent.Name,
                Examples = agent.TriggerKeywords.Take(4).ToList()
            })
            .Where(c => !string.IsNullOrWhiteSpace(c.Scenario))
            .ToList();

        var examples = string.Join("、", capabilities.SelectMany(c => c.Examples).Distinct().Take(8));

        return new AgentResult
        {
            Success = false,
            Intent = IntentContext.UnknownScenario,
            ErrorCode = "INTENT_UNRESOLVED",
            ErrorMessage = "未能理解这句话属于哪项业务。",
            Content = $"我没太理解这句话的意思，目前可以办理：{examples}。" +
                      "换一种说法再问我一次就行。",
            Confidence = 0,
            Data = new Dictionary<string, object?>
            {
                ["resolved"] = false,
                ["unresolved_intent"] = intent ?? "",
                ["available_scenarios"] = capabilities.Select(c => c.Scenario).ToList(),
                ["available_capabilities"] = capabilities
            }
        };
    }

    /// <summary>
    /// 判断关键词在原文中是否存在**未被否定**的出现位置。
    /// 只要有一处出现没有被否定，就算命中。
    /// </summary>
    /// <param name="text">用户原话。</param>
    /// <param name="keyword">关键词。</param>
    /// <returns>存在有效命中返回 true。</returns>
    private static bool HasActiveOccurrence(string text, string keyword)
    {
        var trimmed = keyword.Trim();
        if (trimmed.Length == 0) return false;

        var searchFrom = 0;
        while (searchFrom <= text.Length - trimmed.Length)
        {
            var index = text.IndexOf(trimmed, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;

            if (!IsNegated(text, index)) return true;
            searchFrom = index + 1;
        }

        return false;
    }

    /// <summary>判断关键词出现位置前是否紧邻否定词。</summary>
    /// <param name="text">用户原话。</param>
    /// <param name="keywordIndex">关键词的起始位置。</param>
    /// <returns>被否定返回 true。</returns>
    private static bool IsNegated(string text, int keywordIndex)
    {
        // 只看关键词前面最多 3 个字：否定词一定紧挨着关键词，
        // 再往前看会误伤「我不想说昨天转账的事」这类与当前动作无关的否定。
        var windowStart = Math.Max(0, keywordIndex - 3);
        if (windowStart >= keywordIndex) return false;

        var window = text[windowStart..keywordIndex];
        return NegationMarkers.Any(marker => window.Contains(marker, StringComparison.Ordinal));
    }
}
