// ===== 意图识别：LLM 优先 + 规则兜底 =====
//
// 为什么是两层而不是二选一：
//   1. LLM 能处理规则表覆盖不到的说法（「手头有点紧，想给家里汇点钱」）；
//   2. 银行入口不能因为上游模型抖动、超时、欠费而整体不可用，
//      因此规则表必须在，且模型任何异常都静默降级到它；
//   3. 候选意图不硬编码，而是从「已注册 Agent（即插件）」自报的
//      意图前缀与触发关键词生成 —— 新增插件零改动即可被模型认识。
//
// 本文件修掉的一个真故障：
//   原来模型回答 unknown 时，Validate 会返回字符串 "unknown"（非 null），
//   于是 ClassifyAsync 把它当成「模型成功识别了」，直接返回，
//   早已算好的规则表结果被整个丢弃 —— 用户只会看到「已转人工客服」。
//   模型判不准，本来正是规则表该上场的时候。现在 unknown 与「输出不可用」
//   一样，都会退回规则表。
//
// 数据边界：送模型前对用户输入做强制脱敏（账号/手机号/身份证），
// 与 DataMasker 的「所有 LLM 上下文都必须经过此处理」一致。

using System.Diagnostics;
using BankingAgent.Base.Agents;
using BankingAgent.Base.Security.Audit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BankingAgent.Base.Ai;

/// <summary>基于大模型的意图分类器，规则表作为降级路径。</summary>
public sealed class LlmIntentClassifier : IIntentClassifier
{
    /// <summary>无法判定时使用的意图标识。</summary>
    public const string UnknownIntent = "unknown";

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

        // 规则结果既用于降级，也用于校验模型输出是否越界。
        // 注意：它必须在调模型**之前**算好 —— 模型一旦答 unknown，
        // 这份结果就是唯一的救命稻草。
        var ruleIntent = _router.InferIntentFromInput(userInput);

        if (!_llm.IsAvailable)
        {
            return new IntentDecision(
                ruleIntent ?? UnknownIntent, "rule", ruleIntent is null ? 0 : 1, "未配置模型密钥");
        }

        var safeInput = Sanitize(userInput, _options.Value.MaxInputChars);
        var candidates = IntentPromptBuilder.CollectCandidates(_router);
        var allowed = IntentPromptBuilder.BuildAllowedSet(candidates);

        var sw = Stopwatch.StartNew();
        string? raw;
        try
        {
            raw = await _llm.CompleteAsync(
                IntentPromptBuilder.BuildSystemPrompt(candidates), safeInput, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        sw.Stop();

        var verdict = Validate(raw, allowed);

        await WriteAuditAsync(verdict.Intent, safeInput, raw, _llm.Model, sw.ElapsedMilliseconds, ct);

        if (verdict.Intent is not null)
        {
            _logger.LogInformation("意图识别：{Intent}（模型 {Model}，{Elapsed}ms）",
                verdict.Intent, _llm.Model, sw.ElapsedMilliseconds);
            return new IntentDecision(verdict.Intent, "llm", 0.9, verdict.Reason);
        }

        // 模型超时 / 答非所问 / 输出越界 / 明确表示 unknown —— 一律退回规则表，
        // 绝不因为模型的问题让用户收到「无法处理」。
        var fallback = ruleIntent ?? UnknownIntent;
        _logger.LogWarning(
            "模型未能给出可用意图（{Reason}），降级规则表 → {Intent}",
            verdict.Reason, fallback);

        return new IntentDecision(
            fallback,
            "rule",
            fallback == UnknownIntent ? 0 : 0.6,
            verdict.Reason);
    }

    /// <summary>
    /// 校验模型输出：必须是已注册意图之一，否则视为不可用。
    ///
    /// 关键点：模型回答 unknown 时，这里返回 null 而不是 "unknown" ——
    /// 让调用方知道「模型没给出可用答案」，从而启用规则表兜底。
    /// </summary>
    private static ModelVerdict Validate(string? raw, HashSet<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new ModelVerdict(null, "模型无输出");
        }

        // 容错：去掉常见包裹（引号、反引号、句号、换行）
        var cleaned = raw.Trim().Trim('"', '\'', '`', '。', '.', '\n', '\r', ' ');
        if (cleaned.Length == 0)
        {
            return new ModelVerdict(null, "模型输出为空");
        }

        if (cleaned.Equals(UnknownIntent, StringComparison.OrdinalIgnoreCase))
        {
            return new ModelVerdict(null, "模型表示无法判断");
        }

        if (allowed.Contains(cleaned))
        {
            return new ModelVerdict(cleaned, "模型命中已知意图");
        }

        // 模型偶尔会多写一句解释，从中找出出现过的已知意图（最长优先）
        var hit = allowed
            .Where(i => cleaned.Contains(i, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.Length)
            .FirstOrDefault();

        return hit is not null
            ? new ModelVerdict(hit, "模型输出中包含已知意图")
            : new ModelVerdict(null, "模型输出了未知意图");
    }

    /// <summary>
    /// 送模型前的强制脱敏。账号、手机号、身份证不得离开系统边界，
    /// 而意图识别并不需要这些原文。
    /// </summary>
    private static string Sanitize(string input, int maxChars) =>
        SensitiveTextMasker.Mask(input, maxChars);

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

    /// <summary>模型输出的校验结论。</summary>
    /// <param name="Intent">识别到的意图；null 表示不可用，应降级规则表。</param>
    /// <param name="Reason">判定原因，写入审计与日志。</param>
    private sealed record ModelVerdict(string? Intent, string Reason);
}
