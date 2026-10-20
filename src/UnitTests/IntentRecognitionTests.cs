// ===== 语义识别与意图路由单元测试 =====
//
// 这些测试锁住的是「识别了语义 → 触发不同功能」这条主链路上的几个关键点：
//   1. 模型判不准时必须回退规则表，而不是把用户扔在「无法处理」上；
//   2. 规则表要比「最长关键词匹配」更聪明：更长关键词优先、否定词不算数；
//   3. 意图要能拆成场景与动作两层，Agent 才能据此走对分支；
//   4. 卡片写操作必须由**原话里的语义**触发（丢了 = 挂失），而不是死等「挂失」两个字；
//   5. 会话记忆必须按用户隔离，不能跨用户读到上下文。

using BankingAgent.Base.Agents;
using BankingAgent.Base.Ai;
using BankingAgent.Base.Security.Audit;
using BankingAgent.Plugin.CardManagement;
using BankingAgent.PluginSdk;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace UnitTests;

/// <summary>意图识别、规则表消歧与语义动作解析的测试。</summary>
public class IntentRecognitionTests
{
    // ================================================================ 意图分类器

    [Fact]
    public async Task Classify_ModelSaysUnknown_FallsBackToRuleTable()
    {
        // 这条锁的是一个真故障：模型回答 unknown 时，旧实现把它当成
        // 「模型成功识别了」，直接返回，已算好的规则表结果被整个丢弃 ——
        // 用户只会看到「已转人工客服」。现在必须回退规则表。
        var classifier = BuildClassifier(new ScriptedLlm { Reply = "unknown" }, out _);

        var decision = await classifier.ClassifyAsync("给李华转500元");

        Assert.Equal("transfer", decision.Intent);
        Assert.Equal("rule", decision.Source);
    }

    [Fact]
    public async Task Classify_ModelReturnsActionLevelIntent_KeepsIt()
    {
        // 动作级意图必须被完整保留：它决定 Agent 走只读分支还是写分支。
        var classifier = BuildClassifier(new ScriptedLlm { Reply = "card.status" }, out _);

        var decision = await classifier.ClassifyAsync("我的卡不见了");

        Assert.Equal("card.status", decision.Intent);
        Assert.Equal("llm", decision.Source);
    }

    [Fact]
    public async Task Classify_ModelReturnsUnknownAndRuleCannotInfer_ReportsUnknown()
    {
        // 模型与规则表都判不出来时，如实报 unknown，不编造意图。
        var classifier = BuildClassifier(new ScriptedLlm { Reply = "unknown" }, out _);

        var decision = await classifier.ClassifyAsync("今天天气怎么样");

        Assert.Equal(LlmIntentClassifier.UnknownIntent, decision.Intent);
        Assert.Equal("rule", decision.Source);
    }

    [Fact]
    public async Task Classify_PromptListsActionLevelIntents()
    {
        // 提示词必须列出**动作级**意图，模型才可能选出 card.status 而不是 card。
        var llm = new ScriptedLlm { Reply = "card.status" };
        var classifier = BuildClassifier(llm, out _);

        await classifier.ClassifyAsync("随便说点什么");

        Assert.NotNull(llm.LastSystemPrompt);
        Assert.Contains("card.status", llm.LastSystemPrompt);
        Assert.Contains("[动作级", llm.LastSystemPrompt);
    }

    // ================================================================ 规则表消歧

    [Fact]
    public void InferIntent_LongerKeywordWins()
    {
        // 「卡」是单字泛化词，「账单」是双字具体词。
        // 旧实现按「命中数量」与「先命中者」决定，会把「查卡里的账单」判成卡片。
        var router = CreateRouter();

        Assert.Equal("bill", router.InferIntentFromInput("查一下我上个月卡里的账单"));
    }

    [Fact]
    public void InferIntent_NegatedKeywordDoesNotCount()
    {
        // 「不要转账」表达的是不做，不是做。
        var router = CreateRouter();

        Assert.Equal("bill", router.InferIntentFromInput("不要转账，帮我看下上个月的账单"));
    }

    [Fact]
    public void InferIntent_TransferWinsOverCardInCompoundSentence()
    {
        // 「把工资卡里的钱转给李华」同时命中卡片与转账，
        // 必须按更具体的「转给」判成转账，否则会走错分支。
        var router = CreateRouter();

        Assert.Equal("transfer", router.InferIntentFromInput("把工资卡里的钱转给李华"));
    }

    [Fact]
    public void InferIntent_PatternIsUsedOnlyWhenNoKeywordMatched()
    {
        // 关键词先判：含业务词时，正则不得抢走。
        var router = CreateRouter();

        Assert.Equal("transfer", router.InferIntentFromInput("转 500 给 622202"));
    }

    [Fact]
    public void InferIntent_PatternCatchesBareCode()
    {
        // 纯 6 位代码没有任何业务关键词，只能靠插件自报的正则条件识别。
        var router = CreateRouterWithPatternAgent();

        Assert.Equal("wealth", router.InferIntentFromInput("000001 怎么样"));
    }

    [Fact]
    public void InferIntent_PatternDoesNotMatchLongNumbers()
    {
        // 前后断言保证 16 位卡号、账号不会被当成 6 位代码。
        var router = CreateRouterWithPatternAgent();

        Assert.Null(router.InferIntentFromInput("6222020200000001"));
    }

    // ================================================================ 意图拆层

    [Fact]
    public void IntentContext_SplitsScenarioAndAction()
    {
        var context = IntentContext.FromIntent("card.status");

        Assert.Equal("card", context.Scenario);
        Assert.Equal("card.status", context.Action);
        Assert.True(context.HasAction);
        Assert.True(context.IsAction("card.status"));
    }

    [Fact]
    public void IntentContext_ScenarioOnlyHasNoAction()
    {
        var context = IntentContext.FromIntent("card");

        Assert.Equal("card", context.Scenario);
        Assert.Null(context.Action);
        Assert.False(context.HasAction);
    }

    // ================================================================ 卡片语义动作

    [Theory]
    [InlineData("我的卡不见了", CardStatusAction.ReportLost)]
    [InlineData("银行卡丢了", CardStatusAction.ReportLost)]
    [InlineData("我的卡被偷了", CardStatusAction.ReportLost)]
    [InlineData("帮我把卡挂失", CardStatusAction.ReportLost)]
    [InlineData("这张卡被冻了", CardStatusAction.Freeze)]
    [InlineData("帮我冻结银行卡", CardStatusAction.Freeze)]
    [InlineData("卡找回来了，解除挂失", CardStatusAction.UndoLost)]
    [InlineData("取消冻结", CardStatusAction.Unfreeze)]
    [InlineData("解冻这张卡", CardStatusAction.Unfreeze)]
    public void CardStatusResolver_RecognizesSemanticActions(string input, CardStatusAction expected)
    {
        Assert.Equal(expected, CardStatusResolver.Resolve(input));
    }

    [Theory]
    [InlineData("我有哪些卡")]
    [InlineData("查一下卡状态")]
    [InlineData("卡不能用了")]
    public void CardStatusResolver_PlainQueryHasNoAction(string input)
    {
        // 只读问法不能被误判成写操作：写操作有副作用，宁可漏判不可错判。
        Assert.Equal(CardStatusAction.None, CardStatusResolver.Resolve(input));
    }

    [Theory]
    [InlineData("别挂失我的卡")]
    [InlineData("先不要冻结")]
    public void CardStatusResolver_NegatedRequestHasNoAction(string input)
    {
        Assert.Equal(CardStatusAction.None, CardStatusResolver.Resolve(input));
    }

    // ================================================================ 会话记忆

    [Fact]
    public void SessionMemory_IsolatesUsers()
    {
        // 会话标识是客户端提供的，不与用户绑定就能跨用户读到上下文。
        var memory = new InMemorySessionMemory();
        memory.Remember("u_1", "s1", new SessionSnapshot { LastIntent = "transfer" });

        Assert.NotNull(memory.Get("u_1", "s1"));
        Assert.Null(memory.Get("u_2", "s1"));
    }

    [Fact]
    public void SessionMemory_ExpiresOldSnapshots()
    {
        var memory = new InMemorySessionMemory(ttl: TimeSpan.Zero);
        memory.Remember("u_1", "s1", new SessionSnapshot { LastIntent = "transfer" });

        Assert.Null(memory.Get("u_1", "s1"));
    }

    [Fact]
    public void SessionMemory_EvictsWhenOverCapacity()
    {
        var memory = new InMemorySessionMemory(maxSessions: 2);
        memory.Remember("u_1", "s1", new SessionSnapshot { LastIntent = "transfer" });
        memory.Remember("u_1", "s2", new SessionSnapshot { LastIntent = "bill" });
        memory.Remember("u_1", "s3", new SessionSnapshot { LastIntent = "wealth" });

        Assert.Null(memory.Get("u_1", "s1"));
        Assert.NotNull(memory.Get("u_1", "s3"));
    }

    // ================================================================ 测试替身

    private static LlmIntentClassifier BuildClassifier(ILlmClient llm, out RecordingAudit audit)
    {
        audit = new RecordingAudit();
        var router = new AgentRouter(
            [new FakeTransferAgent(), new FakeBillAgent(), new FakeCardAgent()],
            NullLogger<AgentRouter>.Instance);

        return new LlmIntentClassifier(
            llm, router, audit, Options.Create(new LlmOptions()),
            NullLogger<LlmIntentClassifier>.Instance);
    }

    private static AgentRouter CreateRouter() => new(
        [new FakeTransferAgent(), new FakeBillAgent(), new FakeCardAgent()],
        NullLogger<AgentRouter>.Instance);

    /// <summary>构造一个带正则触发条件的路由（模拟理财插件声明 6 位基金代码）。</summary>
    /// <returns>路由器。</returns>
    private static AgentRouter CreateRouterWithPatternAgent() => new(
        [new FakeTransferAgent(), new FakeBillAgent(), new FakeCardAgent(), new FakeWealthAgent()],
        NullLogger<AgentRouter>.Instance);

    private sealed class FakeTransferAgent : IBankingAgent
    {
        public AgentId Id => new("transfer.agent");
        public string Name => "转账执行 Agent";
        public AgentRole Role => AgentRole.DomainExpert;
        public IReadOnlyList<string> SupportedIntents => ["transfer", "transfer.execute"];
        public IReadOnlyList<string> TriggerKeywords => ["转账", "转帐", "汇款", "打钱", "转给", "付给", "汇给", "转"];

        public Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct = default) =>
            Task.FromResult(AgentResult.Ok());
    }

    private sealed class FakeBillAgent : IBankingAgent
    {
        public AgentId Id => new("bill.agent");
        public string Name => "账单分析 Agent";
        public AgentRole Role => AgentRole.DomainExpert;
        public IReadOnlyList<string> SupportedIntents => ["bill", "bill.summary"];
        public IReadOnlyList<string> TriggerKeywords => ["账单", "消费", "花了", "支出", "开销", "流水"];

        public Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct = default) =>
            Task.FromResult(AgentResult.Ok());
    }

    private sealed class FakeCardAgent : IBankingAgent
    {
        public AgentId Id => new("card.agent");
        public string Name => "卡片管理 Agent";
        public AgentRole Role => AgentRole.DomainExpert;
        public IReadOnlyList<string> SupportedIntents => ["card", "card.status"];
        public IReadOnlyList<string> TriggerKeywords => ["银行卡", "卡片", "挂失", "解挂", "冻结", "限额", "卡"];

        public Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct = default) =>
            Task.FromResult(AgentResult.Ok());
    }

    /// <summary>带正则触发条件的 Agent：用户只报一个 6 位代码时也该被识别。</summary>
    private sealed class FakeWealthAgent : IBankingAgent
    {
        public AgentId Id => new("wealth.agent");
        public string Name => "理财 Agent";
        public AgentRole Role => AgentRole.DomainExpert;
        public IReadOnlyList<string> SupportedIntents => ["wealth", "wealth.funds"];
        public IReadOnlyList<string> TriggerKeywords => ["理财", "基金", "净值"];
        public IReadOnlyList<string> TriggerPatterns => [@"(?<!\d)\d{6}(?!\d)"];

        public Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct = default) =>
            Task.FromResult(AgentResult.Ok());
    }

    /// <summary>可编程的模型替身，同时记录真正发出去的提示词。</summary>
    private sealed class ScriptedLlm : ILlmClient
    {
        public bool IsAvailable { get; init; } = true;
        public string Model => "scripted-model";
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
