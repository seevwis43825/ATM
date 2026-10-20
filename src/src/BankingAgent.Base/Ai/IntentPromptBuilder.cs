// ===== 意图提示词构建 =====
//
// 把「候选意图 → 系统提示词」这一步独立出来，有两个原因：
//   1. 意图分类器与连通性探针必须用**同一份**提示词。
//      探针如果自己拼一套提示词，测出来的「AI 通了」就不能代表真实链路是通的。
//   2. 提示词内容全部由**已注册 Agent 自报**生成，
//      新增插件无需改这里，它自报的关键词与意图会自动进入提示词。

namespace BankingAgent.Base.Ai;

using System.Text;
using BankingAgent.Base.Agents;

/// <summary>一个候选意图：模型作答时可选的全部信息。</summary>
/// <param name="Intent">意图标识，形如 card / card.status。</param>
/// <param name="AgentName">声明该意图的 Agent 名称。</param>
/// <param name="Keywords">该 Agent 自报的自然语言触发关键词。</param>
/// <param name="Patterns">该 Agent 自报的正则触发条件（补关键词表达不了的场景，如纯数字代码）。</param>
/// <param name="IsActionLevel">是否为动作级意图（形如 card.status）。</param>
public sealed record IntentCandidate(
    string Intent,
    string AgentName,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Patterns,
    bool IsActionLevel);

/// <summary>从已注册 Agent 收集候选意图并拼装系统提示词。</summary>
public static class IntentPromptBuilder
{
    /// <summary>无法判定时使用的意图标识。</summary>
    public const string UnknownIntent = "unknown";

    /// <summary>
    /// 收集候选意图。
    ///
    /// 所有声明过的意图都算合法输出；同时把**每个**意图都交给模型，
    /// 让它能选出比场景更具体的动作级意图（例如 card.status 优于 card）——
    /// 这是「识别语义 → 触发不同功能」的关键。
    /// </summary>
    /// <param name="router">Agent 路由器。</param>
    /// <returns>候选意图列表。</returns>
    public static IReadOnlyList<IntentCandidate> CollectCandidates(AgentRouter router)
    {
        var candidates = new List<IntentCandidate>();

        foreach (var agent in router.Agents)
        {
            var intents = agent.SupportedIntents
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();
            if (intents.Count == 0) continue;

            var keywords = agent.TriggerKeywords
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .ToList();

            var patterns = agent.TriggerPatterns
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();

            foreach (var intent in intents)
            {
                candidates.Add(new IntentCandidate(
                    intent,
                    agent.Name,
                    keywords,
                    patterns,
                    IsActionLevel: intent.Contains('.')));
            }
        }

        return candidates;
    }

    /// <summary>取出候选意图中所有合法的意图标识。</summary>
    /// <param name="candidates">候选意图列表。</param>
    /// <returns>合法意图标识集合。</returns>
    public static HashSet<string> BuildAllowedSet(IReadOnlyList<IntentCandidate> candidates) =>
        new(candidates.Select(c => c.Intent), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 组装系统提示：任务说明 + 候选清单 + 场景消歧规则 + 输出格式约束。
    ///
    /// 消歧规则是**按已注册 Agent 自动生成**的，不是写死在代码里的业务词表：
    /// 每个 Agent 的触发关键词被归到它自己的场景名下，模型据此知道
    /// 「账单」和「卡」同时出现时该选哪个。新增插件无需改这里。
    /// </summary>
    /// <param name="candidates">候选意图列表。</param>
    /// <returns>系统提示词。</returns>
    public static string BuildSystemPrompt(IReadOnlyList<IntentCandidate> candidates)
    {
        var sb = new StringBuilder();

        sb.AppendLine("你是银行智能助手的意图识别模块。把用户的话映射到下面某一个意图。");
        sb.AppendLine();

        sb.AppendLine("可选意图：");
        foreach (var candidate in candidates)
        {
            sb.Append("- ").Append(candidate.Intent).Append("（").Append(candidate.AgentName.Trim()).Append('）');
            if (candidate.Keywords.Count > 0)
            {
                sb.Append("　常见说法：").Append(string.Join('、', candidate.Keywords.Take(8)));
            }
            if (candidate.IsActionLevel)
            {
                sb.Append("　[动作级：表示对该场景执行该子动作]");
            }
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("场景消歧规则（关键词归属哪个场景，就选那个场景；关键词越长越具体）：");
        foreach (var group in candidates
                     .Where(c => !c.IsActionLevel)
                     .GroupBy(c => c.AgentName))
        {
            var keywords = group
                .SelectMany(c => c.Keywords)
                .Distinct()
                .Take(10)
                .ToList();
            if (keywords.Count == 0) continue;

            sb.Append("- ").Append(group.Key.Trim()).Append("：")
                .Append(string.Join('、', keywords)).AppendLine();
        }

        // 有些场景的触发条件不是固定词，而是代码格式（例如 6 位基金代码）。
        // 把它们单独列出来，模型才知道「000001 怎么样」该归到哪一类。
        var patternGroups = candidates
            .Where(c => c.Patterns.Count > 0)
            .GroupBy(c => c.AgentName)
            .ToList();
        if (patternGroups.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("代码/编号形式的触发条件（用户只报一个编号时按此归类）：");
            foreach (var group in patternGroups)
            {
                sb.Append("- ").Append(group.Key.Trim()).Append("：")
                    .Append(string.Join('、', group.SelectMany(c => c.Patterns).Distinct()))
                    .AppendLine();
            }
        }

        sb.AppendLine();
        sb.AppendLine("输出要求：");
        sb.AppendLine("1. 只输出一个意图标识本身，不要输出解释、标点、引号或多余文字。");
        sb.AppendLine($"2. 无法判断时只输出 {UnknownIntent}。");
        sb.AppendLine("3. 当同时存在场景级与动作级意图时，输出更具体的动作级意图。");
        sb.AppendLine("4. 提到给某人或某账户转出资金、汇款、打钱的，归为转账类意图。");
        sb.AppendLine("5. 句子同时提到多个场景时，优先采用**关键词更长**的那个场景的意图。");

        return sb.ToString();
    }
}
