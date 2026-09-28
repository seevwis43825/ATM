// ===== 主 Agent 编排器（Supervisor）=====
// 参考 DeepSeek Harness 的层级编排：父 Agent 负责任务拆解、分配与结果聚合，
// 子 Agent 负责执行。与现有 AgentRouter 的区别：
//   AgentRouter  —— 单跳路由（意图 → 执行者），无规划、无聚合
//   AgentOrchestrator —— 多跳规划、并行执行、结果聚合、预算控制、失败降级
//
// 设计原则（对齐 Harness）：
//   1. 能力即插件：编排策略本身可替换
//   2. 全程可追溯：每一步写入轨迹日志
//   3. 显式失败处理：定义降级路径，而非吞掉异常
//   4. 人工在环：关键节点挂起等人工决策

using System.Diagnostics;
using BankingAgent.Base.Security.Audit;
using BankingAgent.PluginSdk;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Base.Agents;

/// <summary>编排步骤类型。</summary>
public enum StepKind
{
    /// <summary>直接调用领域 Agent。</summary>
    Direct,
    /// <summary>并行执行多个 Agent。</summary>
    Parallel,
    /// <summary>按顺序执行多个 Agent，前一个结果注入后一个的上下文。</summary>
    Sequential,
    /// <summary>挂起等待人工决策。</summary>
    HumanGate
}

/// <summary>编排计划中的一个步骤。</summary>
public sealed record OrchestrationStep
{
    public required string Id { get; init; }
    public required StepKind Kind { get; init; }
    /// <summary>要执行的目标 Agent 标识。</summary>
    public required IReadOnlyList<string> Targets { get; init; }
    public string? Description { get; init; }
    /// <summary>可并行执行（仅 Parallel 生效）。</summary>
    public bool CanRunInParallel { get; init; } = true;
    /// <summary>失败时是否允许跳过继续。</summary>
    public bool Optional { get; init; }
    /// <summary>超时（秒）。</summary>
    public int TimeoutSeconds { get; init; } = 30;
}

/// <summary>单个步骤的执行结果。</summary>
public sealed record StepResult
{
    public required string StepId { get; init; }
    public required bool Success { get; init; }
    public required IReadOnlyDictionary<string, AgentResult> AgentResults { get; init; }
    public string? FailureReason { get; init; }
    public long ElapsedMs { get; init; }
    public DateTimeOffset StartedAt { get; init; }
}

/// <summary>完整编排结果。</summary>
public sealed record OrchestrationResult
{
    public required bool Success { get; init; }
    public required IReadOnlyList<StepResult> Steps { get; init; }
    public string? FinalContent { get; init; }
    public bool RequiresHumanApproval { get; init; }
    public string? PendingStepId { get; init; }
    public long TotalElapsedMs { get; init; }
    public int StepsExecuted => Steps.Count;
    public int StepsSkipped => Steps.Count(s => !s.Success && s.FailureReason?.Contains("跳过") == true);
}

/// <summary>编排预算，防止无限循环与成本失控。</summary>
public sealed record OrchestrationBudget
{
    /// <summary>最大步骤数。</summary>
    public int MaxSteps { get; init; } = 10;
    /// <summary>最大并行度。</summary>
    public int MaxParallelism { get; init; } = 4;
    /// <summary>整体超时（秒）。</summary>
    public int TotalTimeoutSeconds { get; init; } = 60;
    /// <summary>允许的人工挂起次数。</summary>
    public int MaxHumanGates { get; init; } = 1;

    public static OrchestrationBudget Default { get; } = new();
}

/// <summary>编排策略契约。不同策略可作为插件替换。</summary>
public interface IOrchestrationStrategy
{
    string Name { get; }
    /// <summary>根据请求与可用 Agent 规划执行步骤。</summary>
    Task<IReadOnlyList<OrchestrationStep>> PlanAsync(
        AgentRequest request,
        IReadOnlyList<IBankingAgent> availableAgents,
        CancellationToken ct = default);
}

/// <summary>轨迹事件类型（对齐 Harness 的 append-only 事件流）。</summary>
public enum TrajectoryEventType
{
    SessionStart,
    PlanCreated,
    StepStarted,
    AgentInvoked,
    AgentCompleted,
    StepCompleted,
    HumanGate,
    SessionCompleted,
    SessionFailed
}

/// <summary>轨迹事件。仅追加，不可修改。</summary>
public sealed record TrajectoryEvent
{
    public required string EventId { get; init; }
    public required string SessionId { get; init; }
    public required TrajectoryEventType Type { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public int Sequence { get; init; }
    public string? StepId { get; init; }
    public string? AgentId { get; init; }
    public string? Intent { get; init; }
    public bool Success { get; init; } = true;
    public string? Detail { get; init; }
    public IReadOnlyDictionary<string, object?> Data { get; init; }
        = new Dictionary<string, object?>();
}

/// <summary>轨迹日志契约。仅追加，支持回放与审计。</summary>
public interface ITrajectoryLog
{
    /// <summary>追加事件。返回该事件的序号。</summary>
    int Append(TrajectoryEvent evt);
    /// <summary>按会话读取完整轨迹。</summary>
    IReadOnlyList<TrajectoryEvent> GetSession(string sessionId);
    /// <summary>读取全部轨迹（按时间序）。</summary>
    IReadOnlyList<TrajectoryEvent> GetAll(int limit = 1000);
}

/// <summary>主 Agent 编排器。</summary>
public class AgentOrchestrator
{
    private readonly IReadOnlyList<IBankingAgent> _agents;
    private readonly IOrchestrationStrategy _strategy;
    private readonly ITrajectoryLog _trajectory;
    private readonly IAuditLogger _audit;
    private readonly ILogger<AgentOrchestrator> _logger;
    private int _sequence;

    /// <summary>构造编排器。</summary>
    public AgentOrchestrator(
        IEnumerable<IBankingAgent> agents,
        IOrchestrationStrategy strategy,
        ITrajectoryLog trajectory,
        IAuditLogger audit,
        ILogger<AgentOrchestrator> logger)
    {
        _agents = agents.ToList();
        _strategy = strategy;
        _trajectory = trajectory;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>可用 Agent 清单。</summary>
    public IReadOnlyList<IBankingAgent> Agents => _agents;

    /// <summary>
    /// 执行一次编排：规划 → 逐步执行 → 聚合结果。
    /// 遇到人工门则立即返回，交由外部恢复。
    /// </summary>
    public async Task<OrchestrationResult> RunAsync(
        AgentRequest request, OrchestrationBudget? budget = null, CancellationToken ct = default)
    {
        var b = budget ?? OrchestrationBudget.Default;
        var sw = Stopwatch.StartNew();
        var sessionId = request.SessionId ?? Guid.NewGuid().ToString("N");
        var steps = new List<StepResult>();
        var humanGateCount = 0;

        Append(sessionId, new TrajectoryEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
            Type = TrajectoryEventType.SessionStart,
            Timestamp = DateTimeOffset.UtcNow,
            Intent = request.Upstream?.Intent
        });

        try
        {
            var plan = await _strategy.PlanAsync(request, _agents, ct);
            Append(sessionId, new TrajectoryEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                SessionId = sessionId,
                Type = TrajectoryEventType.PlanCreated,
                Timestamp = DateTimeOffset.UtcNow,
                Data = new Dictionary<string, object?>
                {
                    ["strategy"] = _strategy.Name,
                    ["stepCount"] = plan.Count
                }
            });

            _logger.LogInformation("编排计划已生成: {Count} 步 (策略 {Strategy})", plan.Count, _strategy.Name);

            foreach (var step in plan.Take(b.MaxSteps))
            {
                ct.ThrowIfCancellationRequested();

                if (sw.Elapsed.TotalSeconds > b.TotalTimeoutSeconds)
                {
                    steps.Add(Failed(step.Id, "整体超时", sw));
                    break;
                }

                if (step.Kind == StepKind.HumanGate)
                {
                    humanGateCount++;
                    Append(sessionId, new TrajectoryEvent
                    {
                        EventId = Guid.NewGuid().ToString("N"),
                        SessionId = sessionId,
                        Type = TrajectoryEventType.HumanGate,
                        Timestamp = DateTimeOffset.UtcNow,
                        StepId = step.Id,
                        Success = false,
                        Detail = step.Description ?? "等待人工确认"
                    });

                    if (humanGateCount > b.MaxHumanGates)
                    {
                        steps.Add(Failed(step.Id, "超出人工确认次数上限", sw));
                        break;
                    }

                    return new OrchestrationResult
                    {
                        Success = false,
                        Steps = steps,
                        RequiresHumanApproval = true,
                        PendingStepId = step.Id,
                        FinalContent = step.Description ?? "需要人工确认后继续",
                        TotalElapsedMs = sw.ElapsedMilliseconds
                    };
                }

                var result = await ExecuteStepAsync(sessionId, step, request, b, sw, ct);
                steps.Add(result);

                if (!result.Success && !step.Optional)
                {
                    Append(sessionId, new TrajectoryEvent
                    {
                        EventId = Guid.NewGuid().ToString("N"),
                        SessionId = sessionId,
                        Type = TrajectoryEventType.SessionFailed,
                        Timestamp = DateTimeOffset.UtcNow,
                        StepId = step.Id,
                        Success = false,
                        Detail = result.FailureReason
                    });

                    return new OrchestrationResult
                    {
                        Success = false,
                        Steps = steps,
                        FinalContent = result.FailureReason,
                        TotalElapsedMs = sw.ElapsedMilliseconds
                    };
                }

                // 人工确认类结果直接挂起
                if (result.AgentResults.Values.Any(a => a.RequiresHumanInLoop))
                {
                    return new OrchestrationResult
                    {
                        Success = true,
                        Steps = steps,
                        RequiresHumanApproval = true,
                        PendingStepId = step.Id,
                        FinalContent = result.AgentResults.Values
                            .First(a => a.RequiresHumanInLoop).Content,
                        TotalElapsedMs = sw.ElapsedMilliseconds
                    };
                }
            }

            var success = steps.All(s => s.Success);
            Append(sessionId, new TrajectoryEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                SessionId = sessionId,
                Type = TrajectoryEventType.SessionCompleted,
                Timestamp = DateTimeOffset.UtcNow,
                Success = success
            });

            return new OrchestrationResult
            {
                Success = success,
                Steps = steps,
                FinalContent = AggregateContent(steps),
                TotalElapsedMs = sw.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("编排被取消: {SessionId}", sessionId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "编排执行异常: {SessionId}", sessionId);
            return new OrchestrationResult
            {
                Success = false,
                Steps = steps,
                FinalContent = $"编排异常: {ex.Message}",
                TotalElapsedMs = sw.ElapsedMilliseconds
            };
        }
    }

    /// <summary>执行单个步骤。</summary>
    private async Task<StepResult> ExecuteStepAsync(
        string sessionId,
        OrchestrationStep step,
        AgentRequest request,
        OrchestrationBudget budget,
        Stopwatch total,
        CancellationToken ct)
    {
        var stepSw = Stopwatch.StartNew();
        var startedAt = DateTimeOffset.UtcNow;

        Append(sessionId, new TrajectoryEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
            Type = TrajectoryEventType.StepStarted,
            Timestamp = startedAt,
            StepId = step.Id,
            Detail = step.Description ?? step.Kind.ToString()
        });

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSeconds));

        var results = new Dictionary<string, AgentResult>();

        try
        {
            switch (step.Kind)
            {
                case StepKind.Direct:
                {
                    var agent = ResolveAgent(step.Targets.FirstOrDefault());
                    if (agent is null)
                    {
                        return Failed(step.Id, $"未找到 Agent: {step.Targets.FirstOrDefault()}", total, startedAt);
                    }
                    results[agent.Id.Value] = await InvokeAsync(sessionId, step, agent, request, timeoutCts.Token);
                    break;
                }

                case StepKind.Parallel:
                {
                    var targets = step.Targets.Select(ResolveAgent).Where(a => a is not null).ToList();
                    if (targets.Count == 0)
                    {
                        return Failed(step.Id, "并行步骤无可用 Agent", total, startedAt);
                    }

                    var parallel = targets.Take(budget.MaxParallelism);
                    var tasks = parallel.Select(async agent =>
                    {
                        var r = await InvokeAsync(sessionId, step, agent!, request, timeoutCts.Token);
                        return (agent!.Id.Value, r);
                    });

                    foreach (var (id, r) in await Task.WhenAll(tasks))
                    {
                        results[id] = r;
                    }
                    break;
                }

                case StepKind.Sequential:
                {
                    AgentResult? carry = null;
                    foreach (var targetId in step.Targets)
                    {
                        var agent = ResolveAgent(targetId);
                        if (agent is null) continue;

                        var r = await InvokeAsync(sessionId, step, agent, request with { Upstream = carry },
                            timeoutCts.Token);
                        results[agent.Id.Value] = r;
                        carry = r;

                        if (!r.Success) break;
                    }
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            stepSw.Stop();
            return new StepResult
            {
                StepId = step.Id,
                Success = false,
                AgentResults = results,
                FailureReason = $"步骤超时（>{step.TimeoutSeconds}s）",
                ElapsedMs = stepSw.ElapsedMilliseconds,
                StartedAt = startedAt
            };
        }

        stepSw.Stop();
        // 并行聚合是「尽力而为」：Optional 的 Parallel 步骤只要有 Agent 成功即视为完成。
        // 否则一个不适用于该输入的 Agent（例如聚合查询里的资金操作 Agent 会返回
        // 「未识别到转账金额」）会把整步判为失败，进而让整个编排返回失败 ——
        // 而用户实际上已经拿到了其他 Agent 聚合出的结果。
        var allOk = step.Kind == StepKind.Parallel && step.Optional
            ? results.Values.Any(r => r.Success)
            : results.Count > 0 && results.Values.All(r => r.Success);

        Append(sessionId, new TrajectoryEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
            Type = TrajectoryEventType.StepCompleted,
            Timestamp = DateTimeOffset.UtcNow,
            StepId = step.Id,
            Success = allOk
        });

        return new StepResult
        {
            StepId = step.Id,
            Success = allOk,
            AgentResults = results,
            ElapsedMs = stepSw.ElapsedMilliseconds,
            StartedAt = startedAt,
            FailureReason = allOk ? null : results.Values.FirstOrDefault(r => !r.Success)?.ErrorMessage
        };
    }

    /// <summary>调用单个 Agent 并记录轨迹。</summary>
    private async Task<AgentResult> InvokeAsync(
        string sessionId, OrchestrationStep step, IBankingAgent agent,
        AgentRequest request, CancellationToken ct)
    {
        Append(sessionId, new TrajectoryEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
            Type = TrajectoryEventType.AgentInvoked,
            Timestamp = DateTimeOffset.UtcNow,
            StepId = step.Id,
            AgentId = agent.Id.Value
        });

        var result = await agent.ExecuteAsync(request, ct);

        Append(sessionId, new TrajectoryEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
            Type = TrajectoryEventType.AgentCompleted,
            Timestamp = DateTimeOffset.UtcNow,
            StepId = step.Id,
            AgentId = agent.Id.Value,
            Success = result.Success,
            Intent = result.Intent,
            Detail = result.ErrorMessage,
            Data = result.Data
        });

        await _audit.WriteAsync(new AuditEvent
        {
            AuditId = Guid.NewGuid().ToString("N")[..12],
            Timestamp = DateTimeOffset.UtcNow,
            ActorType = "ORCHESTRATOR",
            ActorId = "supervisor",
            Operation = "orchestration.invoke",
            Scenario = step.Id,
            Decision = result.Success ? "SUCCESS" : "FAILED",
            DecisionReason = result.ErrorMessage ?? "",
            Intent = result.Intent ?? "",
            RequestId = sessionId,
            ElapsedMs = (long)result.Elapsed.TotalMilliseconds
        }, ct);

        return result;
    }

    private IBankingAgent? ResolveAgent(string? id) =>
        string.IsNullOrEmpty(id) ? null : _agents.FirstOrDefault(a => a.Id.Value == id);

    private static StepResult Failed(string stepId, string reason, Stopwatch sw,
        DateTimeOffset? startedAt = null) => new()
        {
            StepId = stepId,
            Success = false,
            AgentResults = new Dictionary<string, AgentResult>(),
            FailureReason = reason,
            ElapsedMs = sw.ElapsedMilliseconds,
            StartedAt = startedAt ?? DateTimeOffset.UtcNow
        };

    /// <summary>聚合各步骤结果为最终回复。</summary>
    private static string AggregateContent(IReadOnlyList<StepResult> steps)
    {
        var contents = steps
            .SelectMany(s => s.AgentResults.Values)
            .Where(r => r.Success && !string.IsNullOrEmpty(r.Content))
            .Select(r => r.Content!)
            .ToList();

        return contents.Count > 0 ? string.Join("\n", contents) : "已处理";
    }

    private void Append(string sessionId, TrajectoryEvent evt)
    {
        var seq = Interlocked.Increment(ref _sequence);
        _trajectory.Append(evt with { Sequence = seq });
    }
}
