// ===== 意图识别单元测试 =====
// 锁定两条底线：
//   1. 送模型前必须脱敏 —— 账号、手机号、身份证不能出系统边界；
//   2. 模型不可用或答非所问时必须降级规则表，
//      而不是把用户扔在「无法处理」上（银行入口不能因上游抖动而不可用）。

using BankingAgent.Base.Agents;
using BankingAgent.Base.Ai;
using BankingAgent.Base.Security.Audit;
using BankingAgent.PluginSdk;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace UnitTests;

public class LlmIntentClassifierTests
{
    [Fact]
    public async Task Classify_ModelNotConfigured_FallsBackToRule()
    {
        var classifier = Build(new FakeLlm { IsAvailable = false }, out _);

        var decision = await classifier.ClassifyAsync("给李华转500元");

        // 规则兜底返回场景级意图（transfer），具体动作由 TransferAgent 内部判定
        Assert.Equal("transfer", decision.Intent);
        Assert.Equal("rule", decision.Source);
    }

    [Fact]
    public async Task Classify_ModelReturnsKnownIntent_UsesModelResult()
    {
        var classifier = Build(new FakeLlm { Reply = "bill.summary" }, out var audit);

        var decision = await classifier.ClassifyAsync("我上个月花了多少");

        Assert.Equal("bill.summary", decision.Intent);
        Assert.Equal("llm", decision.Source);
        Assert.Contains(audit.Events, e =>
            e.Operation == "llm.intent.classify" && e.Decision == "LLM");
    }

    [Fact]
    public async Task Classify_ModelReturnsHallucination_FallsBackToRule()
    {
        // 模型没有在本轮候选意图里作答，属于不可用输出
        var classifier = Build(new FakeLlm { Reply = "我觉得应该是查询天气吧" }, out var audit);

        var decision = await classifier.ClassifyAsync("给李华转500元");

        Assert.Equal("transfer", decision.Intent);
        Assert.Equal("rule", decision.Source);
        Assert.Contains(audit.Events, e => e.Decision == "RULE_FALLBACK");
    }

    [Fact]
    public async Task Classify_MasksSensitiveFieldsBeforeSendingToModel()
    {
        var llm = new FakeLlm { Reply = "transfer.execute" };
        var classifier = Build(llm, out _);

        await classifier.ClassifyAsync(
            "给6222020200000003转500元，手机13800138000，身份证110101199001011234，备注房租");

        Assert.NotNull(llm.LastUserPrompt);
        Assert.DoesNotContain("6222020200000003", llm.LastUserPrompt);
        Assert.DoesNotContain("13800138000", llm.LastUserPrompt);
        Assert.DoesNotContain("110101199001011234", llm.LastUserPrompt);
        // 保留后 4 位，仍足以让人工核对，同时不构成完整账号泄漏
        Assert.Contains("0003", llm.LastUserPrompt);
        // 与意图无关的文字必须原样保留，否则模型会读不懂用户想干什么
        Assert.Contains("备注房租", llm.LastUserPrompt);
    }

    [Fact]
    public async Task Classify_PromptCandidatesComeFromRegisteredAgents()
    {
        // 候选意图不是硬编码的：它来自已注册 Agent（插件）自报的意图与关键词，
        // 这样新增插件零改动就能被模型认识。
        var llm = new FakeLlm { Reply = "transfer.execute" };
        var classifier = Build(llm, out _);

        await classifier.ClassifyAsync("随便说点什么");

        Assert.NotNull(llm.LastSystemPrompt);
        // 候选意图是场景级的（transfer），并带上 Agent 名称与它自报的关键词
        Assert.Contains("- transfer（", llm.LastSystemPrompt);
        Assert.Contains("转账执行 Agent", llm.LastSystemPrompt);
        Assert.Contains("转账", llm.LastSystemPrompt);
    }

    [Fact]
    public async Task Classify_EmptyInput_ReturnsUnknownWithoutCallingModel()
    {
        var llm = new FakeLlm { Reply = "transfer.execute" };
        var classifier = Build(llm, out _);

        var decision = await classifier.ClassifyAsync("   ");

        Assert.Equal(LlmIntentClassifier.UnknownIntent, decision.Intent);
        Assert.Null(llm.LastUserPrompt);
    }

    // ===== 测试替身 =====

    private static LlmIntentClassifier Build(ILlmClient llm, out RecordingAudit audit)
    {
        audit = new RecordingAudit();
        var router = new AgentRouter(
            [new FakeTransferAgent(), new FakeBillAgent()],
            NullLogger<AgentRouter>.Instance);

        return new LlmIntentClassifier(
            llm, router, audit, Options.Create(new LlmOptions()),
            NullLogger<LlmIntentClassifier>.Instance);
    }

    private sealed class FakeTransferAgent : IBankingAgent
    {
        public AgentId Id => new("transfer.agent");
        public string Name => "转账执行 Agent";
        public AgentRole Role => AgentRole.DomainExpert;
        public IReadOnlyList<string> SupportedIntents => ["transfer", "transfer.execute"];

        // 与真实 TransferAgent 一致：含单字「转」，才能接住「给李华转500元」这类说法
        public IReadOnlyList<string> TriggerKeywords => ["转账", "汇款", "转"];

        public Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct = default) =>
            Task.FromResult(AgentResult.Ok());
    }

    private sealed class FakeBillAgent : IBankingAgent
    {
        public AgentId Id => new("bill.agent");
        public string Name => "账单分析 Agent";
        public AgentRole Role => AgentRole.DomainExpert;
        public IReadOnlyList<string> SupportedIntents => ["bill", "bill.summary"];
        public IReadOnlyList<string> TriggerKeywords => ["账单", "消费"];

        public Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct = default) =>
            Task.FromResult(AgentResult.Ok());
    }

    /// <summary>可编程的模型替身，同时记录真正发出去的提示词。</summary>
    private sealed class FakeLlm : ILlmClient
    {
        public bool IsAvailable { get; init; } = true;
        public string Model => "fake-model";
        public string? Reply { get; init; }
        public string? LastUserPrompt { get; private set; }
        public string? LastSystemPrompt { get; private set; }

        public Task<string?> CompleteAsync(
            string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            LastSystemPrompt = systemPrompt;
            LastUserPrompt = userPrompt;
            return Task.FromResult(Reply);
        }
    }

    private sealed class RecordingAudit : IAuditLogger
    {
        public List<AuditEvent> Events { get; } = [];

        public Task WriteAsync(AuditEvent evt, CancellationToken ct = default)
        {
            Events.Add(evt);
            return Task.CompletedTask;
        }

        public AuditChainVerification VerifyChain(IReadOnlyList<AuditEvent> events) =>
            new(true, events.Count, []);
    }
}
