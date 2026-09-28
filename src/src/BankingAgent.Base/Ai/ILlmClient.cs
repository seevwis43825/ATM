// ===== 大模型客户端契约 =====
// 框架层唯一的模型出口。插件不直接持有密钥、不直接访问模型 ——
// 否则脱敏无法前置、密钥无法统一管理、模型调用无法统一审计。

namespace BankingAgent.Base.Ai;

/// <summary>对话补全契约。屏蔽各家协议差异（OpenAI 兼容接口 / 本地模型）。</summary>
public interface ILlmClient
{
    /// <summary>是否已具备调用条件（开关打开且密钥非空）。</summary>
    bool IsAvailable { get; }

    /// <summary>当前模型名，用于日志与审计留痕。</summary>
    string Model { get; }

    /// <summary>
    /// 发起一次对话补全。
    /// 不可用或调用失败时返回 null，由调用方降级 —— 本方法不抛业务异常。
    /// </summary>
    /// <param name="systemPrompt">系统提示，描述任务与输出格式。</param>
    /// <param name="userPrompt">用户输入（调用方保证已脱敏）。</param>
    /// <param name="ct">取消令牌。</param>
    Task<string?> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
}
