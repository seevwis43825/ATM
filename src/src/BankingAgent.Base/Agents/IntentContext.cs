// ===== 意图上下文：把上游识别出的意图拆成「场景」与「动作」两层 =====
//
// 为什么要拆成两层：
//
//   路由器只关心**场景**（卡片 / 账单 / 转账 / 理财）—— 场景决定「交给哪个 Agent」；
//   Agent 关心**动作**（查列表 / 改状态 / 执行转账）—— 动作决定「走只读分支还是写分支」。
//
// 过去这两层被压成同一个字符串，产生了一个很隐蔽的故障：
//   模型已经判定意图是 card.status（要改卡状态），
//   但 Agent 内部仍然只按「原文里有没有『挂失』两个字」来决定要不要写。
//   于是用户说「我的卡不见了」时，语义明明被识别对了，却只返回了一张卡片列表 ——
//   这就是「识别了语义却没做出对应功能」的根因。
//
// 拆开之后，场景决定路由，动作决定分支，两者不再互相覆盖。

namespace BankingAgent.Base.Agents;

using BankingAgent.PluginSdk;

/// <summary>意图上下文：同时携带场景级与动作级意图。</summary>
public sealed record IntentContext
{
    /// <summary>无法判定场景时使用的标识。</summary>
    public const string UnknownScenario = "unknown";

    /// <summary>场景级意图，如 card / bill / transfer / wealth；无法判定时为 unknown。</summary>
    public string Scenario { get; init; } = UnknownScenario;

    /// <summary>
    /// 动作级意图，如 card.status / transfer.execute。
    /// 上游只给出场景级意图时为 null。
    /// </summary>
    public string? Action { get; init; }

    /// <summary>是否带有动作级意图。</summary>
    public bool HasAction => !string.IsNullOrWhiteSpace(Action);

    /// <summary>空上下文：表示上游没有给出任何可用意图。</summary>
    public static IntentContext Empty { get; } = new();

    /// <summary>从 Agent 请求中提取意图上下文。</summary>
    /// <param name="request">Agent 请求；为空时返回空上下文。</param>
    /// <returns>意图上下文。</returns>
    public static IntentContext From(AgentRequest? request)
    {
        if (request is null) return Empty;

        var intent = request.Upstream?.Intent
                     ?? request.SharedContext.GetValueOrDefault("intent")?.ToString();

        return FromIntent(intent);
    }

    /// <summary>从意图标识构造上下文。</summary>
    /// <param name="intent">意图标识，形如 card / card.status；可为空。</param>
    /// <returns>意图上下文。</returns>
    public static IntentContext FromIntent(string? intent)
    {
        if (string.IsNullOrWhiteSpace(intent)) return Empty;

        var trimmed = intent.Trim();
        var separator = trimmed.IndexOf('.');
        if (separator <= 0)
        {
            // 只有场景级意图：场景就是它本身，没有动作。
            return new IntentContext { Scenario = trimmed.ToLowerInvariant() };
        }

        return new IntentContext
        {
            Scenario = trimmed[..separator].ToLowerInvariant(),
            Action = trimmed.ToLowerInvariant()
        };
    }

    /// <summary>判断场景是否为指定值。</summary>
    /// <param name="scenario">场景名，忽略大小写。</param>
    /// <returns>命中返回 true。</returns>
    public bool IsScenario(string scenario) =>
        string.Equals(Scenario, scenario, StringComparison.OrdinalIgnoreCase);

    /// <summary>判断动作是否为指定值。</summary>
    /// <param name="action">动作名，忽略大小写。</param>
    /// <returns>命中返回 true。</returns>
    public bool IsAction(string action) =>
        string.Equals(Action, action, StringComparison.OrdinalIgnoreCase);

    /// <summary>人类可读的意图描述，用于日志与审计。</summary>
    /// <returns>描述文本。</returns>
    public string Describe() =>
        HasAction ? $"{Scenario}（动作 {Action}）" : Scenario;
}
