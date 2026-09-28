// ===== 多 Agent 抽象层 =====
// 定义 Agent、Agent 上下文、执行结果与工具（Function Call）契约。
// 插件继承或实现 IBankingAgent，即可获得标准 Agent 能力。

namespace BankingAgent.PluginSdk;

/// <summary>Agent 唯一标识。</summary>
public sealed record AgentId(string Value)
{
    public override string ToString() => Value;
    public static implicit operator string(AgentId id) => id.Value;
}

/// <summary>Agent 在编排中的职责定位。</summary>
public enum AgentRole
{
    /// <summary>意图识别：把用户自然语言映射为结构化意图。</summary>
    Intent,
    /// <summary>编排调度：决定调用哪些下游 Agent。</summary>
    Orchestrator,
    /// <summary>领域专家：处理特定业务场景。</summary>
    DomainExpert,
    /// <summary>合规守卫：在执行前做最终合规校验。</summary>
    Guard,
    /// <summary>记忆检索：读取用户画像与历史。</summary>
    Memory,
    /// <summary>响应生成：把结构化结果转成自然语言。</summary>
    Response
}

/// <summary>单次 Agent 调用的输入。</summary>
public sealed record AgentRequest
{
    public required string UserInput { get; init; }
    public required string UserId { get; init; }
    public string? SessionId { get; init; }
    public string? ConversationId { get; init; }
    /// <summary>上游 Agent 已抽取的结构化槽位，如 amount、recipient。</summary>
    public IReadOnlyDictionary<string, object?> Slots { get; init; }
        = new Dictionary<string, object?>();
    /// <summary>本轮对话累积的上下文，供下游 Agent 共享。</summary>
    public IReadOnlyDictionary<string, object?> SharedContext { get; init; }
        = new Dictionary<string, object?>();
    /// <summary>上一环 Agent 的结果，用于链路串联。</summary>
    public AgentResult? Upstream { get; init; }
    public CancellationToken CancellationToken { get; init; } = default;
}

/// <summary>Agent 执行结果。</summary>
public sealed record AgentResult
{
    /// <summary>空数据字典。</summary>
    public static IReadOnlyDictionary<string, object?> EmptyData { get; } =
        new Dictionary<string, object?>();

    public required bool Success { get; init; }
    public string? Content { get; init; }
    /// <summary>识别出的意图标识，如 transfer.execute。</summary>
    public string? Intent { get; init; }
    public IReadOnlyDictionary<string, object?> Data { get; init; } = EmptyData;
    /// <summary>置信度 0~1。低置信度应转人工或走兜底。</summary>
    public double Confidence { get; init; } = 1.0;
    /// <summary>是否需要人工回环。资金类操作必须为 true。</summary>
    public bool RequiresHumanInLoop { get; init; }
    /// <summary>是否已成功调用外部系统并产生副作用。</summary>
    public bool SideEffectCommitted { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan Elapsed { get; init; }

    public static AgentResult Ok(
        string? content = null,
        string? intent = null,
        IReadOnlyDictionary<string, object?>? data = null,
        double confidence = 1.0)
    {
        return new AgentResult
        {
            Success = true,
            Content = content,
            Intent = intent,
            Data = data ?? EmptyData,
            Confidence = confidence
        };
    }

    public static AgentResult Fail(string errorCode, string errorMessage)
    {
        return new AgentResult
        {
            Success = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            Confidence = 0
        };
    }

    /// <summary>要求人工确认。此时 SideEffectCommitted 必须为 false。</summary>
    public static AgentResult PendingApproval(string reason, IReadOnlyDictionary<string, object?>? data = null)
    {
        return new AgentResult
        {
            Success = true,
            RequiresHumanInLoop = true,
            Content = reason,
            Data = data ?? EmptyData,
            Confidence = 1.0
        };
    }
}

/// <summary>Agent 契约。每个插件内的 Agent 需实现此接口并注册到 DI。</summary>
public interface IBankingAgent
{
    AgentId Id { get; }
    string Name { get; }
    AgentRole Role { get; }
    /// <summary>该 Agent 能处理的意图前缀，用于路由。为空表示兜底 Agent。</summary>
    IReadOnlyList<string> SupportedIntents { get; }

    /// <summary>
    /// 该 Agent 的自然语言触发关键词（如「转账」「汇款」）。
    ///
    /// 用途：规则式意图识别未命中时，宿主用这些关键词从用户原话反推意图。
    /// 没有它，插件虽然注册了 Agent，却只能靠宿主里硬编码的关键词表才收得到请求，
    /// 「加插件即加能力」这条设计承诺就不成立。默认返回空集合，不影响既有插件。
    /// </summary>
    IReadOnlyList<string> TriggerKeywords => [];
    Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct = default);
}

/// <summary>工具（Function Call）契约。供 LLM 调用以完成具体动作。</summary>
public interface IAgentTool
{
    string ToolName { get; }
    string Description { get; }
    /// <summary>参数 JSON Schema 描述。LLM 依据此生成调用参数。</summary>
    string ParameterSchema { get; }
    /// <summary>该工具涉及的数据敏感级别，驱动脱敏策略。</summary>
    DataClassification Classification { get; }
    Task<ToolExecutionResult> ExecuteAsync(ToolInvocation invocation, CancellationToken ct = default);
}

/// <summary>工具调用入参。</summary>
public sealed record ToolInvocation
{
    public required string UserId { get; init; }
    public required IReadOnlyDictionary<string, object?> Arguments { get; init; }
    public string? SessionId { get; init; }
    public string? RequestId { get; init; }
    public CancellationToken CancellationToken { get; init; } = default;
}

/// <summary>工具执行结果。</summary>
public sealed record ToolExecutionResult
{
    public required bool Success { get; init; }
    public object? Data { get; init; }
    public string? ErrorMessage { get; init; }
    /// <summary>该操作是否已产生不可回滚的副作用（写操作、扣款）。</summary>
    public bool Committed { get; init; }

    public static ToolExecutionResult Ok(object? data = null, bool committed = false) =>
        new() { Success = true, Data = data, Committed = committed };

    public static ToolExecutionResult Fail(string error) =>
        new() { Success = false, ErrorMessage = error };
}
