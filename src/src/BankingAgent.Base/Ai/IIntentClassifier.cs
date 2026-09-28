// ===== 意图识别契约 =====
// 宿主通过它把自然语言映射为意图，再交给 AgentRouter 路由。
// 实现可以替换（规则 / LLM / 混合），路由与插件都无需改动。

namespace BankingAgent.Base.Ai;

/// <summary>意图判定结果。</summary>
/// <param name="Intent">意图标识，例如 transfer.execute；无法判定时为 unknown。</param>
/// <param name="Source">判定来源：llm / rule。</param>
/// <param name="Confidence">置信度 0~1。</param>
/// <param name="Reason">降级或失败原因，用于排障与审计。</param>
public sealed record IntentDecision(
    string Intent,
    string Source,
    double Confidence,
    string? Reason = null);

/// <summary>意图识别契约。</summary>
public interface IIntentClassifier
{
    /// <summary>识别用户输入的意图。</summary>
    /// <param name="userInput">用户原话。</param>
    /// <param name="ct">取消令牌。</param>
    Task<IntentDecision> ClassifyAsync(string userInput, CancellationToken ct = default);
}
