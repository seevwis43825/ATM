// ===== AI 连通性探针 =====
//
// 它解决一个很实际的问题：以前「AI 功能有没有生效」只能靠感觉 ——
// 日志里看到模型被调用了就算数，模型返回了什么、映射成什么意图，无人验证。
//
// 探针的做法：**用真实的提示词、真实地调一次模型**，然后把整条链路的结果摊开：
//   模型名 → 原始输出 → 归一化后的输出 → 是否命中已注册意图 → 耗时。
//
// 关键约束：探针必须复用意图分类器的同一份提示词（见 IntentPromptBuilder），
// 否则「探针通过」不能代表真实链路通过 —— 那就只是探针自己在骗自己。

namespace BankingAgent.Base.Ai;

using System.Diagnostics;
using BankingAgent.Base.Agents;
using BankingAgent.Base.Security.Audit;
using Microsoft.Extensions.Logging;

/// <summary>一次连通性探测的结果。</summary>
public sealed record AiProbeResult
{
    /// <summary>是否配置了模型密钥。</summary>
    public required bool ModelConfigured { get; init; }

    /// <summary>模型调用是否成功返回。</summary>
    public required bool CallSucceeded { get; init; }

    /// <summary>模型输出是否映射到了已注册意图。</summary>
    public required bool MappedToKnownIntent { get; init; }

    /// <summary>模型名。</summary>
    public string Model { get; init; } = "";

    /// <summary>模型原始输出。</summary>
    public string? RawOutput { get; init; }

    /// <summary>归一化后的输出。</summary>
    public string? NormalizedOutput { get; init; }

    /// <summary>映射到的意图；未命中时为 null。</summary>
    public string? ResolvedIntent { get; init; }

    /// <summary>探测用的测试句。</summary>
    public string ProbeText { get; init; } = "";

    /// <summary>调用耗时（毫秒）。</summary>
    public long ElapsedMs { get; init; }

    /// <summary>失败原因；成功时为 null。</summary>
    public string? Error { get; init; }

    /// <summary>探测时间。</summary>
    public DateTimeOffset CheckedAt { get; init; }

    /// <summary>一句话结论，便于直接展示。</summary>
    public string Summary =>
        !ModelConfigured ? "未配置模型密钥，当前走规则表意图识别"
        : !CallSucceeded ? $"模型调用失败：{Error}"
        : MappedToKnownIntent ? $"模型连通，测试句被识别为 {ResolvedIntent}"
        : $"模型有响应，但输出未能映射到已注册意图（原始输出：{RawOutput}）";
}

/// <summary>AI 连通性探针：真实调用一次模型并保留结果。</summary>
public sealed class AiConnectivityProbe(
    ILlmClient llm,
    AgentRouter router,
    IAuditLogger audit,
    ILogger<AiConnectivityProbe> logger)
{
    /// <summary>默认探测句：覆盖「账单 vs 卡片 vs 转账」这类最易混淆的场景。</summary>
    public const string DefaultProbeText = "我上个月花了多少钱";

    private AiProbeResult? _lastResult;

    /// <summary>最近一次探测结果；尚未探测过时为 null。</summary>
    public AiProbeResult? LastResult => _lastResult;

    /// <summary>
    /// 执行一次探测。
    /// </summary>
    /// <param name="probeText">探测句；为空时用默认探测句。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>探测结果。</returns>
    public async Task<AiProbeResult> RunAsync(string? probeText = null, CancellationToken ct = default)
    {
        var text = string.IsNullOrWhiteSpace(probeText) ? DefaultProbeText : probeText.Trim();
        var candidates = IntentPromptBuilder.CollectCandidates(router);
        var allowed = IntentPromptBuilder.BuildAllowedSet(candidates);

        if (!llm.IsAvailable)
        {
            var notConfigured = new AiProbeResult
            {
                ModelConfigured = false,
                CallSucceeded = false,
                MappedToKnownIntent = false,
                Model = llm.Model,
                ProbeText = text,
                Error = "未配置模型密钥（Ai:ApiKey）",
                CheckedAt = DateTimeOffset.UtcNow
            };
            _lastResult = notConfigured;
            return notConfigured;
        }

        var safeText = SensitiveTextMasker.Mask(text, 500);
        var stopwatch = Stopwatch.StartNew();

        string? raw;
        try
        {
            raw = await llm.CompleteAsync(
                IntentPromptBuilder.BuildSystemPrompt(candidates), safeText, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var failed = new AiProbeResult
            {
                ModelConfigured = true,
                CallSucceeded = false,
                MappedToKnownIntent = false,
                Model = llm.Model,
                ProbeText = text,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
                Error = ex.Message,
                CheckedAt = DateTimeOffset.UtcNow
            };
            _lastResult = failed;
            logger.LogWarning(ex, "AI 连通性探测失败");
            return failed;
        }

        stopwatch.Stop();

        var normalized = Normalize(raw);
        var resolved = allowed.Contains(normalized) ? normalized : null;

        var result = new AiProbeResult
        {
            ModelConfigured = true,
            CallSucceeded = !string.IsNullOrWhiteSpace(raw),
            MappedToKnownIntent = resolved is not null,
            Model = llm.Model,
            RawOutput = raw,
            NormalizedOutput = normalized,
            ResolvedIntent = resolved,
            ProbeText = text,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
            Error = string.IsNullOrWhiteSpace(raw) ? "模型返回空内容" : null,
            CheckedAt = DateTimeOffset.UtcNow
        };

        _lastResult = result;

        await audit.WriteAsync(new AuditEvent
        {
            AuditId = Guid.NewGuid().ToString("N")[..12],
            Timestamp = DateTimeOffset.UtcNow,
            ActorType = "AI",
            ActorId = llm.Model,
            Operation = "llm.connectivity.probe",
            Scenario = "intent",
            Intent = resolved ?? "unknown",
            Decision = result.MappedToKnownIntent ? "PROBE_OK" : "PROBE_FAILED",
            DecisionReason = result.Summary,
            ElapsedMs = result.ElapsedMs
        }, ct);

        logger.LogInformation("AI 连通性探测：{Summary}（{Elapsed}ms）", result.Summary, result.ElapsedMs);
        return result;
    }

    /// <summary>归一化模型输出：去掉包裹字符与空白。</summary>
    /// <param name="raw">模型原始输出。</param>
    /// <returns>归一化后的文本。</returns>
    private static string Normalize(string? raw) =>
        (raw ?? string.Empty).Trim().Trim('"', '\'', '`', '。', '.', '\n', '\r', ' ');
}
