// ===== 宿主接口契约：请求与响应体 =====
//
// 单独成文件的理由：这些是**对外契约**，被多个端点文件引用。
// 混在 Program.cs 里会让「接口长什么样」这个问题需要翻一个 700 行的装配文件才能回答。

namespace BankingAgent.Host.Contracts;

/// <summary>对话请求。</summary>
/// <param name="Message">用户原话。</param>
/// <param name="UserId">请求体里的用户标识。服务端一律以令牌身份为准，此字段仅用于代客操作声明。</param>
/// <param name="SessionId">会话标识，用于多轮上下文继承。</param>
/// <param name="Slots">结构化槽位。</param>
/// <param name="ImpersonationReason">代客操作原因；代客操作时必填，会记入审计。</param>
public sealed record ChatRequest(
    string Message, string UserId, string? SessionId, Dictionary<string, object?>? Slots,
    string? ImpersonationReason = null);

/// <summary>令牌签发请求。</summary>
/// <param name="UserId">账号。</param>
/// <param name="Password">口令。</param>
/// <param name="Role">请求的角色。服务端以账号表为准，绝不采信此值。</param>
public sealed record TokenRequest(string UserId, string Password, string? Role = null);

/// <summary>对话响应。</summary>
/// <param name="Echo">回显的用户原话。</param>
/// <param name="Intent">本轮路由使用的意图。</param>
/// <param name="Success">是否成功。</param>
/// <param name="Content">给用户看的结果描述。</param>
/// <param name="ResultIntent">Agent 产出的更精确的结果意图。</param>
/// <param name="RequiresHumanInLoop">是否需要人工确认。</param>
/// <param name="SideEffectCommitted">是否已产生副作用。</param>
/// <param name="ErrorCode">错误码。</param>
/// <param name="ErrorMessage">错误描述。</param>
/// <param name="Data">结构化结果数据。</param>
/// <param name="Confidence">置信度。</param>
/// <param name="ElapsedMs">耗时（毫秒）。</param>
/// <param name="IntentSource">意图来源：llm / rule / session（继承上一轮）。</param>
public sealed record ChatResponse(
    string Echo, string Intent, bool Success, string? Content, string? ResultIntent,
    bool RequiresHumanInLoop, bool SideEffectCommitted, string? ErrorCode, string? ErrorMessage,
    IReadOnlyDictionary<string, object?> Data, double Confidence, double ElapsedMs,
    string IntentSource = "rule");

/// <summary>人工确认请求。</summary>
/// <param name="UserId">确认人。服务端以令牌身份为准。</param>
/// <param name="SessionId">会话标识。</param>
/// <param name="Amount">确认的金额。</param>
/// <param name="Slots">确认时的槽位。</param>
public sealed record ConfirmRequest(
    string UserId, string? SessionId, decimal Amount, Dictionary<string, object?> Slots);

/// <summary>AI 连通性自检请求。</summary>
/// <param name="Text">自定义测试句；为空时使用内置的易混场景测试句。</param>
public sealed record AiProbeRequest(string? Text = null);
