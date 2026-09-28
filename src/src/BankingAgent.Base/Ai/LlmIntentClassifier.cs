// ===== 意图识别：LLM 优先 + 规则兜底 =====
//
// 为什么是两层而不是二选一：
//   1. LLM 能处理规则表覆盖不到的说法（「手头有点紧，想给家里汇点钱」）；
//   2. 银行入口不能因为上游模型抖动、超时、欠费而整体不可用，
//      因此规则表必须在，且模型任何异常都静默降级到它；
//   3. 候选意图不硬编码，而是从「已注册 Agent（即插件）」自报的
//      意图前缀与触发关键词生成 —— 新增插件零改动即可被模型认识。
//
// 数据边界：送模型前对用户输入做强制脱敏（账号/手机号/身份证），
// 与 DataMasker 的「所有 LLM 上下文都必须经过此处理」一致。

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using BankingAgent.Base.Agents;
using BankingAgent.Base.Security;
using BankingAgent.Base.Security.Audit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BankingAgent.Base.Ai;

/// <summary>基于大模型的意图分类器，规则表作为降级路径。</summary>
public sealed class LlmIntentClassifier : IIntentClassifier
{
    /// <summary>无法判定时使用的意图标识。</summary>
    public const string UnknownIntent = "unknown";

    /// <summary>18 位身份证（先于账号匹配，避免被长数字规则误伤）。</summary>
    private static readonly Regex IdCardDigits = new(@"(?<!\d)\d{18}(?!\d)", RegexOptions.Compiled);

    /// <summary>11 位手机号。</summary>
    private static readonly Regex MobileDigits = new(@"(?<!\d)1[3-9]\d{9}(?!\d)", RegexOptions.Compiled);

    /// <summary>12-19 位账号 / 卡号。</summary>
    private static readonly Regex AccountDigits = new(@"(?<!\d)\d{12,19}(?!\d)", RegexOptions.Compiled);

    private readonly ILlmClient _llm;
    private readonly AgentRouter _router;
    private readonly IAuditLogger _audit;
    private readonly IOptions<LlmOptions> _options;
    private readonly ILogger<LlmIntentClassifier> _logger;

    /// <summary>构造分类器。</summary>
    public LlmIntentClassifier(
        ILlmClient llm,
        AgentRouter router,
        IAuditLogger audit,
        IOptions<LlmOptions> options,
        ILogger<LlmIntentClassifier> logger)
    {
        _llm = llm;
        _router = router;
        _audit = audit;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IntentDecision> ClassifyAsync(string userInput, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userInput))
        {
            return new IntentDecision(UnknownIntent, "rule", 0, "输入为空");
        }

        // 规则结果既用于降级，也用于校验模型输出是否越界
        var ruleIntent = _router.InferIntentFromInput(userInput);

        if (!_llm.IsAvailable)
        {
            return new IntentDecision(
                ruleIntent ?? UnknownIntent, "rule", ruleIntent is null ? 0 : 1, "未配置模型密钥");
        }

        var safeInput = Sanitize(userInput, _options.Value.MaxInputChars);
        var allowed = CollectIntents(out var candidateList);

        var sw = Stopwatch.StartNew();
        string? raw;
        try
        {
            raw = await _llm.CompleteAsync(BuildSystemPrompt(candidateList), safeInput, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        sw.Stop();

        var parsed = Validate(raw, allowed);

        await WriteAuditAsync(parsed, safeInput, raw, _llm.Model, sw.ElapsedMilliseconds, ct);

        if (parsed is not null)
        {
            _logger.LogInformation("意图识别：{Intent}（模型 {Model}，{Elapsed}ms）",
                parsed, _llm.Model, sw.ElapsedMilliseconds);
            return new IntentDecision(parsed, "llm", 0.9);
        }

        // 模型超时 / 答非所问 / 输出越界 —— 一律退回规则表，
        // 绝不因为模型的问题让用户收到「无法处理」
        _logger.LogWarning("模型输出无法映射到已知意图，降级规则表：{Raw}", Truncate(raw ?? "", 80));
        return new IntentDecision(
            ruleIntent ?? UnknownIntent, "rule", ruleIntent is null ? 0 : 0.6, "模型输出不可用");
    }

    /// <summary>
    /// 收集候选意图：从已注册 Agent 自报的意图前缀与触发关键词生成。
    /// 每个 Agent 只展示「第一个（场景级）」意图，让模型在场景粒度上作答，
    /// 而不是逼它在"列卡 / 改状态"这类动作粒度上猜。
    /// </summary>
    private HashSet<string> CollectIntents(out List<(string Intent, string Name, IReadOnlyList<string> Keywords)> candidates)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        candidates = [];

        foreach (var agent in _router.Agents)
        {
            var intents = agent.SupportedIntents
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();
            if (intents.Count == 0) continue;

            // 全部声明都算合法输出（路由按前缀匹配，动作级意图同样能被接住）
            foreach (var intent in intents) allowed.Add(intent);

            candidates.Add((intents[0], agent.Name, agent.TriggerKeywords));
        }

        return allowed;
    }

    /// <summary>组装系统提示：任务说明 + 候选清单 + 输出格式约束。</summary>
    private static string BuildSystemPrompt(
        List<(string Intent, string Name, IReadOnlyList<string> Keywords)> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是银行智能助手的意图识别模块。把用户的话映射到下面某一个意图。");
        sb.AppendLine();
        sb.AppendLine("可选意图：");
        foreach (var (intent, name, keywords) in candidates)
        {
            sb.Append("- ").Append(intent).Append("（").Append(name.Trim()).Append('）');
            if (keywords.Count > 0)
            {
                sb.Append("　常见说法：").Append(string.Join('、', keywords.Take(6)));
            }
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("输出要求：");
        sb.AppendLine("1. 只输出一个意图标识本身，不要输出解释、标点、引号或多余文字。");
        sb.AppendLine($"2. 无法判断时只输出 {UnknownIntent}。");
        sb.AppendLine("3. 提到给某人或某账户转出资金、汇款、打钱的，归为转账类意图。");
        return sb.ToString();
    }

    /// <summary>校验模型输出：必须是已注册意图之一，否则视为不可用。</summary>
    private static string? Validate(string? raw, HashSet<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // 容错：去掉常见包裹（引号、反引号、句号、换行）
        var cleaned = raw.Trim().Trim('"', '\'', '`', '。', '.', '\n', '\r', ' ');
        if (cleaned.Equals(UnknownIntent, StringComparison.OrdinalIgnoreCase)) return UnknownIntent;
        if (allowed.Contains(cleaned)) return cleaned;

        // 模型偶尔会多写一句解释，从中找出出现过的已知意图（最长优先）
        var hit = allowed
            .Where(i => cleaned.Contains(i, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.Length)
            .FirstOrDefault();
        return hit;
    }

    /// <summary>
    /// 送模型前的强制脱敏。账号、手机号、身份证不得离开系统边界，
    /// 而意图识别并不需要这些原文。
    /// </summary>
    private static string Sanitize(string input, int maxChars)
    {
        var masked = IdCardDigits.Replace(input, m => DataMasker.MaskIdCard(m.Value));
        masked = MobileDigits.Replace(masked, m => DataMasker.MaskPhone(m.Value));
        masked = AccountDigits.Replace(masked, m => DataMasker.MaskAccount(m.Value));

        return masked.Length <= maxChars ? masked : masked[..maxChars];
    }

    /// <summary>写审计：模型调用必须留痕（模型名、是否降级、脱敏后的输入）。</summary>
    private async Task WriteAuditAsync(
        string? parsedIntent, string safeInput, string? raw, string model, long elapsedMs, CancellationToken ct)
    {
        await _audit.WriteAsync(new AuditEvent
        {
            AuditId = Guid.NewGuid().ToString("N")[..12],
            Timestamp = DateTimeOffset.UtcNow,
            ActorType = "AI",
            ActorId = model,
            Operation = "llm.intent.classify",
            Scenario = "intent",
            Intent = parsedIntent ?? UnknownIntent,
            Decision = parsedIntent is null ? "RULE_FALLBACK" : "LLM",
            DecisionReason = Truncate(raw ?? "", 120),
            ElapsedMs = elapsedMs,
            Extra = new Dictionary<string, object?>
            {
                ["model"] = model,
                // 只留脱敏后的输入：审计可检索，但不构成新的泄漏面
                ["input_masked"] = safeInput
            }
        }, ct);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";
}
