// ===== 大模型接入配置 =====
//
// 设计立场：LLM 是「可选增强」，不是必需依赖。
// 未配置密钥时意图识别自动退回规则表，系统仍可完整运行 ——
// 否则任何一次密钥缺失或模型服务抖动，都会让整个对话入口不可用。

namespace BankingAgent.Base.Ai;

/// <summary>大模型接入配置。</summary>
public sealed class LlmOptions
{
    /// <summary>总开关。关闭后完全不调用模型，只走规则表。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>OpenAI 兼容接口基地址，例如 https://api.deepseek.com/v1 。</summary>
    public string BaseUrl { get; set; } = "https://api.deepseek.com/v1";

    /// <summary>模型名，例如 deepseek-chat / qwen-plus / glm-4-flash 。</summary>
    public string Model { get; set; } = "deepseek-chat";

    /// <summary>
    /// 接口密钥。只允许通过环境变量 <c>Ai__ApiKey</c> 或密钥管理服务注入，
    /// 禁止写入 appsettings.json 入库（与 Jwt/Audit/Crypto 同一套规则）。
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>单次调用超时（秒）。超时即降级规则表，不阻塞用户。</summary>
    public int TimeoutSeconds { get; set; } = 8;

    /// <summary>采样温度。意图分类要求稳定输出，默认 0。</summary>
    public double Temperature { get; set; }

    /// <summary>送模型的用户输入最大长度，超出截断（控制成本与超长输入）。</summary>
    public int MaxInputChars { get; set; } = 500;

    /// <summary>是否具备调用条件：开关打开且密钥与地址非空。</summary>
    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(Model);
}
