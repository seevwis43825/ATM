// ===== 编排策略实现 =====
// 参考 DeepSeek Harness：编排策略本身是可替换的插件。
// 这里提供三种内置策略，覆盖不同复杂度场景。

namespace BankingAgent.Base.Agents;

using BankingAgent.PluginSdk;

/// <summary>
/// 直接路由策略：意图 → 单个 Agent，单步执行。
/// 最简单、延迟最低，适用于 90% 的确定性请求。
/// </summary>
public sealed class DirectRoutingStrategy : IOrchestrationStrategy
{
    /// <inheritdoc />
    public string Name => "direct-routing";

    /// <inheritdoc />
    public Task<IReadOnlyList<OrchestrationStep>> PlanAsync(
        AgentRequest request, IReadOnlyList<IBankingAgent> availableAgents, CancellationToken ct = default)
    {
        var intent = request.Upstream?.Intent
                     ?? request.SharedContext.GetValueOrDefault("intent")?.ToString();

        // 最长前缀优先，与 AgentRouter 保持一致
        var agent = availableAgents
            .Where(a => a.SupportedIntents.Any(p =>
                !string.IsNullOrEmpty(intent) &&
                intent.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(a => a.SupportedIntents
                .Where(p => intent!.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                .Max(p => p.Length))
            .FirstOrDefault();

        if (agent is null)
        {
            return Task.FromResult<IReadOnlyList<OrchestrationStep>>([]);
        }

        return Task.FromResult<IReadOnlyList<OrchestrationStep>>(
        [
            new OrchestrationStep
            {
                Id = "direct",
                Kind = StepKind.Direct,
                Targets = [agent.Id.Value],
                Description = $"直接执行 {agent.Name}"
            }
        ]);
    }
}

/// <summary>
/// 并行收集策略：同时调用多个只读 Agent，聚合结果。
/// 适用于「查卡 + 查账单 + 查理财」这类信息聚合请求。
/// </summary>
public sealed class ParallelGatherStrategy : IOrchestrationStrategy
{
    /// <inheritdoc />
    public string Name => "parallel-gather";

    /// <inheritdoc />
    public Task<IReadOnlyList<OrchestrationStep>> PlanAsync(
        AgentRequest request, IReadOnlyList<IBankingAgent> availableAgents, CancellationToken ct = default)
    {
        // 聚合类意图：一次给出多个维度的信息
        var aggregateIntents = new[]
        {
            "卡片与账单总览", "资产总览", "我的全部信息", "综合查询"
        };

        var isAggregate = aggregateIntents.Any(k => request.UserInput.Contains(k, StringComparison.Ordinal));

        if (!isAggregate || availableAgents.Count < 2)
        {
            return new DirectRoutingStrategy()
                .PlanAsync(request, availableAgents, ct);
        }

        return Task.FromResult<IReadOnlyList<OrchestrationStep>>(
        [
            new OrchestrationStep
            {
                Id = "gather",
                Kind = StepKind.Parallel,
                Targets = availableAgents.Select(a => a.Id.Value).ToList(),
                CanRunInParallel = true,
                // 聚合场景下单个失败不阻断整体
                Optional = true,
                Description = "并行聚合各领域信息"
            }
        ]);
    }
}

/// <summary>
/// 顺序流水线策略：意图识别 → 合规校验 → 领域执行。
/// 适用于资金类操作，强制串行以保证合规前置。
/// </summary>
public sealed class SequentialPipelineStrategy : IOrchestrationStrategy
{
    /// <inheritdoc />
    public string Name => "sequential-pipeline";

    /// <inheritdoc />
    public Task<IReadOnlyList<OrchestrationStep>> PlanAsync(
        AgentRequest request, IReadOnlyList<IBankingAgent> availableAgents, CancellationToken ct = default)
    {
        var intent = request.Upstream?.Intent ?? "";

        // 资金类意图走流水线，强制合规先行
        var isFundOperation = intent.StartsWith("transfer", StringComparison.OrdinalIgnoreCase);

        if (!isFundOperation)
        {
            return new DirectRoutingStrategy().PlanAsync(request, availableAgents, ct);
        }

        var executor = availableAgents.FirstOrDefault(a =>
            a.SupportedIntents.Any(p => intent.StartsWith(p, StringComparison.OrdinalIgnoreCase)));

        if (executor is null)
        {
            return Task.FromResult<IReadOnlyList<OrchestrationStep>>([]);
        }

        return Task.FromResult<IReadOnlyList<OrchestrationStep>>(
        [
            new OrchestrationStep
            {
                Id = "compliance-gate",
                Kind = StepKind.HumanGate,
                Targets = [],
                Description = "资金操作需人工确认后执行"
            },
            new OrchestrationStep
            {
                Id = "execute",
                Kind = StepKind.Direct,
                Targets = [executor.Id.Value],
                Description = $"执行 {executor.Name}"
            }
        ]);
    }
}

/// <summary>自适应策略：按意图复杂度自动选择。</summary>
public sealed class AdaptiveStrategy : IOrchestrationStrategy
{
    private readonly DirectRoutingStrategy _direct = new();
    private readonly ParallelGatherStrategy _parallel = new();
    private readonly SequentialPipelineStrategy _pipeline = new();

    /// <inheritdoc />
    public string Name => "adaptive";

    /// <inheritdoc />
    public async Task<IReadOnlyList<OrchestrationStep>> PlanAsync(
        AgentRequest request, IReadOnlyList<IBankingAgent> availableAgents, CancellationToken ct = default)
    {
        var intent = request.Upstream?.Intent ?? "";

        // 资金类 → 流水线
        if (intent.StartsWith("transfer", StringComparison.OrdinalIgnoreCase))
        {
            return await _pipeline.PlanAsync(request, availableAgents, ct);
        }

        // 聚合类 → 并行
        var aggregateKeywords = new[] { "总览", "全部", "综合", "所有" };
        if (aggregateKeywords.Any(k => request.UserInput.Contains(k, StringComparison.Ordinal)))
        {
            return await _parallel.PlanAsync(request, availableAgents, ct);
        }

        // 其余 → 直接路由
        return await _direct.PlanAsync(request, availableAgents, ct);
    }
}
