// ===== Agent 路由器 =====
// 按意图把请求路由到正确的领域 Agent。插件新增 Agent 无需修改本文件，
// 因为路由表在容器构建时从所有 IBankingAgent 实现自动收集。

namespace BankingAgent.Base.Agents;

using BankingAgent.PluginSdk;
using Microsoft.Extensions.Logging;

/// <summary>Agent 路由器。根据上游识别出的意图选择处理者。</summary>
public class AgentRouter
{
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

        // 二级：输入命中 Agent 声明的自然语言关键词（中文输入主要靠这条）。
        // 取该 Agent 声明的**第一个（场景级）**意图：用户请求的粒度是场景
        // （"查卡片"），具体动作（列卡 / 改状态）由 Agent 内部判定，
        // 并体现在结果意图（card.list / card.status.updated）里。
        return _agents
            .SelectMany(a => a.TriggerKeywords
                .Where(k => !string.IsNullOrWhiteSpace(k)
                            && userInput.Contains(k, StringComparison.OrdinalIgnoreCase))
                .Select(k => (Keyword: k, Agent: a)))
            .OrderByDescending(x => x.Keyword.Length)
            .Select(x => x.Agent.SupportedIntents.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)))
            .FirstOrDefault(intent => intent is not null);
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
            return AgentResult.Fail("NO_AGENT", "系统暂时无法处理该请求，已转人工客服");
        }

        _logger.LogInformation("路由 {Intent} → {Agent}", intent, agent.Id);
        return await agent.ExecuteAsync(request, ct);
    }
}
