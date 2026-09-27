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
