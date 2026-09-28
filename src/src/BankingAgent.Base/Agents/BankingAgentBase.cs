// ===== Agent 基类 =====
// 插件继承此类即可获得：计时、审计、异常兜底、合规透传的统一行为。

namespace BankingAgent.Base.Agents;

using System.Diagnostics;
using BankingAgent.Base.Security.Audit;
using BankingAgent.PluginSdk;
using Microsoft.Extensions.Logging;

/// <summary>Agent 抽象基类。统一处理计时、审计与异常兜底。</summary>
public abstract class BankingAgentBase(
    IAuditLogger audit,
    ILogger logger) : IBankingAgent
{
    /// <inheritdoc />
    public abstract AgentId Id { get; }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract AgentRole Role { get; }

    /// <inheritdoc />
    public virtual IReadOnlyList<string> SupportedIntents => [];

    /// <inheritdoc />
    public virtual IReadOnlyList<string> TriggerKeywords => [];

    /// <summary>执行具体业务逻辑，由子类实现。</summary>
    protected abstract Task<AgentResult> HandleAsync(AgentRequest request, CancellationToken ct);

    /// <summary>审计记录器，由宿主注入。</summary>
    protected IAuditLogger Audit { get; } = audit;

    /// <summary>日志记录器，由宿主注入。</summary>
    protected ILogger Logger { get; } = logger;

    /// <inheritdoc />
    public async Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var requestId = Guid.NewGuid().ToString("N")[..12];

        try
        {
            Logger.LogInformation("[{AgentId}] 开始执行 | user={UserId} | input={Input}",
                Id, request.UserId, Truncate(request.UserInput, 60));

            var result = await HandleAsync(request, ct);
            sw.Stop();

            var withTiming = result with { Elapsed = sw.Elapsed };

            await Audit.WriteAsync(new AuditEvent
            {
                AuditId = requestId,
                Timestamp = DateTimeOffset.UtcNow,
                ActorType = "AGENT",
                ActorId = Id.Value,
                Operation = "agent.execute",
                Scenario = request.SharedContext.GetValueOrDefault("scenario")?.ToString() ?? "",
                Intent = withTiming.Intent ?? "",
                Decision = withTiming.Success ? "SUCCESS" : "FAILED",
                DecisionReason = withTiming.ErrorMessage ?? "",
                RequestId = requestId,
                ElapsedMs = sw.ElapsedMilliseconds
            }, ct);

            Logger.LogInformation("[{AgentId}] 执行完成 | success={Success} | 耗时={Elapsed}ms | intent={Intent}",
                Id, withTiming.Success, sw.ElapsedMilliseconds, withTiming.Intent);

            return withTiming;
        }
        catch (OperationCanceledException)
        {
            Logger.LogWarning("[{AgentId}] 执行被取消", Id);
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Logger.LogError(ex, "[{AgentId}] 执行异常", Id);

            await Audit.WriteAsync(new AuditEvent
            {
                AuditId = requestId,
                Timestamp = DateTimeOffset.UtcNow,
                ActorType = "AGENT",
                ActorId = Id.Value,
                Operation = "agent.execute",
                Decision = "EXCEPTION",
                DecisionReason = ex.Message,
                RequestId = requestId,
                ElapsedMs = sw.ElapsedMilliseconds
            }, CancellationToken.None);

            return AgentResult.Fail("AGENT_EXCEPTION", ex.Message) with { Elapsed = sw.Elapsed };
        }
    }

    /// <summary>判断当前请求是否应路由到本 Agent。</summary>
    public bool CanHandle(AgentRequest request)
    {
        if (SupportedIntents.Count == 0) return false;
        var intent = request.Upstream?.Intent ?? request.SharedContext.GetValueOrDefault("intent")?.ToString();
        if (string.IsNullOrEmpty(intent)) return SupportedIntents.Contains("*");
        return SupportedIntents.Any(p => intent.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";
}
