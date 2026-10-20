// ===== 对话端点 =====
//
// 对话入口的三条职责线：
//   1. 身份与越权防护（userId 一律以令牌为准，请求体里的 userId 只能用于代客声明）
//   2. 意图识别（LLM 优先、规则兜底、上一轮继承）
//   3. 路由到 Agent 并把结果整理成响应
//
// 人工回环确认单独成端点：资金类操作**必须**由用户显式确认后才提交副作用。

using BankingAgent.Base.Agents;
using BankingAgent.Base.Ai;
using BankingAgent.Base.Security.Audit;
using BankingAgent.Base.Security.Auth;
using BankingAgent.Host.Contracts;
using BankingAgent.PluginSdk;

namespace BankingAgent.Host.Endpoints;

/// <summary>对话与人工确认相关端点。</summary>
public static class ChatEndpoints
{
    /// <summary>注册对话端点。</summary>
    /// <param name="app">端点路由构建器。</param>
    /// <returns>路由构建器。</returns>
    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/chat", (ChatRequest req, HttpContext ctx, CancellationToken ct) =>
                HandleChatAsync(req, ctx, ct))
            .WithTags("对话")
            .Produces<ChatResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapPost("/api/chat/confirm", (ConfirmRequest req, HttpContext ctx, CancellationToken ct) =>
                HandleConfirmAsync(req, ctx, ct))
            .WithTags("对话")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapPost("/api/orchestrate", (ChatRequest req, HttpContext ctx, CancellationToken ct) =>
                HandleOrchestrateAsync(req, ctx, ct))
            .WithTags("多 Agent 编排")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }

    /// <summary>POST /api/chat — 对话入口。</summary>
    /// <param name="req">对话请求。</param>
    /// <param name="ctx">当前请求上下文。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>对话响应。</returns>
    private static async Task<IResult> HandleChatAsync(
        ChatRequest req, HttpContext ctx, CancellationToken ct)
    {
        var principal = (CurrentPrincipal?)ctx.Items["Principal"];
        if (principal is null) return Results.Unauthorized();

        // ===== 1. 身份与越权防护 =====
        var effectiveUserId = principal.UserId;

        // 客服角色可代客操作，但必须显式声明目标用户，且会被审计
        if (principal.CanImpersonateSupport &&
            !string.IsNullOrWhiteSpace(req.UserId) &&
            req.UserId != effectiveUserId)
        {
            if (req.ImpersonationReason is null)
            {
                return Results.BadRequest(new
                {
                    code = "IMPERSONATION_REASON_REQUIRED",
                    message = "代客操作必须提供 impersonationReason，将记入审计日志"
                });
            }
        }
        else if (!string.IsNullOrWhiteSpace(req.UserId) && req.UserId != effectiveUserId)
        {
            return HostSecurity.Forbidden("无权访问其他用户的数据");
        }

        var targetUserId = principal.CanImpersonateSupport && !string.IsNullOrWhiteSpace(req.UserId)
            ? req.UserId
            : effectiveUserId;

        // 代客操作必须留痕
        if (targetUserId != effectiveUserId)
        {
            var audit = ctx.RequestServices.GetRequiredService<IAuditLogger>();
            await audit.WriteAsync(new AuditEvent
            {
                AuditId = Guid.NewGuid().ToString("N")[..12],
                Timestamp = DateTimeOffset.UtcNow,
                ActorType = "STAFF",
                ActorId = effectiveUserId,
                Operation = "user.impersonation",
                Scenario = "compliance",
                Decision = "APPROVED",
                DecisionReason = req.ImpersonationReason ?? ""
            }, ct);
        }

        // ===== 2. 意图识别 =====
        // 意图识别已内聚到 IIntentClassifier：
        //   - 配置了 Ai:ApiKey 时由大模型判定（候选意图来自插件自报的意图与关键词）；
        //   - 未配置或模型不可用时自动降级到规则表。
        var classifier = ctx.RequestServices.GetRequiredService<IIntentClassifier>();
        var decision = await classifier.ClassifyAsync(req.Message, ct);
        var intent = decision.Intent;
        var intentSource = decision.Source;

        // ===== 3. 会话记忆：让省略句能接上一轮的语义 =====
        //
        // 「给李华转 500」之后接一句「改成 800」，第二句本身没有任何业务关键词，
        // 规则表和模型都可能识别不出来。记住上一轮的意图与原话后，
        // 这一句就能被路由到转账 Agent，并由 Agent 从上一句里补出收款人。
        var sessionMemory = ctx.RequestServices.GetRequiredService<ISessionMemory>();
        var previous = sessionMemory.Get(targetUserId, req.SessionId);

        // 槽位：本轮显式传入的优先，缺失的从上一轮继承。
        var slots = new Dictionary<string, object?>(req.Slots ?? new Dictionary<string, object?>());
        if (previous is not null)
        {
            foreach (var pair in previous.LastSlots)
            {
                if (!slots.ContainsKey(pair.Key)) slots[pair.Key] = pair.Value;
            }
        }

        // 本轮完全识别不出来时，继承上一轮的场景意图。
        if (intent == LlmIntentClassifier.UnknownIntent
            && previous is not null
            && !string.IsNullOrWhiteSpace(previous.LastIntent))
        {
            intent = previous.LastIntent;
            intentSource = "session";
            decision = decision with
            {
                Intent = intent,
                Source = intentSource,
                Confidence = 0.5,
                Reason = "本轮未识别出意图，继承上一轮意图"
            };
        }

        // ===== 4. 路由到 Agent =====
        var router = ctx.RequestServices.GetRequiredService<AgentRouter>();
        var request = new AgentRequest
        {
            UserInput = req.Message,
            UserId = targetUserId,   // 来自令牌，绝不使用请求体中的 userId
            SessionId = req.SessionId,
            CancellationToken = ct,
            Slots = slots,
            SharedContext = new Dictionary<string, object?>
            {
                ["intent"] = intent,
                // 上一轮的原话与意图交给 Agent，让它能在本轮缺信息时回溯。
                ["previous_user_input"] = previous?.LastUserInput ?? "",
                ["previous_intent"] = previous?.LastIntent ?? ""
            },
            Upstream = AgentResult.Ok(intent: intent)
        };

        var result = await router.RouteAsync(request, ct);

        // 记住本轮，供下一轮省略句使用。
        sessionMemory.Remember(targetUserId, req.SessionId, new SessionSnapshot
        {
            LastIntent = intent,
            LastUserInput = req.Message.Length > 200 ? req.Message[..200] : req.Message,
            LastSlots = slots
        });

        return Results.Ok(new ChatResponse(
            req.Message, intent, result.Success,
            // 失败时把错误描述放进 content：前端只渲染 content，
            // 若失败一律返回空字符串，用户看到的就是一片空白。
            result.Content ?? result.ErrorMessage,
            result.Intent,
            result.RequiresHumanInLoop, result.SideEffectCommitted,
            result.ErrorCode, result.ErrorMessage, result.Data,
            result.Confidence, Math.Round(result.Elapsed.TotalMilliseconds, 1),
            intentSource));
    }

    /// <summary>POST /api/chat/confirm — 人工回环确认。</summary>
    /// <param name="req">确认请求。</param>
    /// <param name="ctx">当前请求上下文。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>确认结果。</returns>
    private static async Task<IResult> HandleConfirmAsync(
        ConfirmRequest req, HttpContext ctx, CancellationToken ct)
    {
        var principal = (CurrentPrincipal?)ctx.Items["Principal"];
        if (principal is null) return Results.Unauthorized();

        // 越权防护：确认操作也必须以令牌身份为准
        if (!string.IsNullOrWhiteSpace(req.UserId)
            && req.UserId != principal.UserId
            && !principal.CanImpersonateSupport)
        {
            return HostSecurity.Forbidden("无权为其他用户确认操作");
        }

        var operatorId = principal.UserId;
        var audit = ctx.RequestServices.GetRequiredService<IAuditLogger>();

        await audit.WriteAsync(new AuditEvent
        {
            AuditId = Guid.NewGuid().ToString("N")[..12],
            Timestamp = DateTimeOffset.UtcNow,
            ActorType = "USER",
            ActorId = operatorId,
            Operation = "human.approval",
            Scenario = "transfer",
            Decision = "APPROVED",
            DecisionReason = "用户通过指纹或短信确认",
            Amount = req.Amount,
            RequestId = req.SessionId
        }, ct);

        var slots = new Dictionary<string, object?>(req.Slots) { ["confirmed"] = true };

        var router = ctx.RequestServices.GetRequiredService<AgentRouter>();
        var result = await router.RouteAsync(new AgentRequest
        {
            UserInput = "用户已确认",
            UserId = operatorId,   // 来自令牌
            SessionId = req.SessionId,
            Slots = slots,
            SharedContext = new Dictionary<string, object?> { ["intent"] = "transfer.execute" },
            Upstream = AgentResult.Ok(intent: "transfer.execute"),
            CancellationToken = ct
        }, ct);

        return Results.Ok(new
        {
            success = result.Success,
            content = result.Content,
            committed = result.SideEffectCommitted,
            data = result.Data,
            error = result.ErrorMessage
        });
    }

    /// <summary>POST /api/orchestrate — 多 Agent 编排（Supervisor）。</summary>
    /// <param name="req">对话请求。</param>
    /// <param name="ctx">当前请求上下文。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>编排结果与逐步轨迹。</returns>
    private static async Task<IResult> HandleOrchestrateAsync(
        ChatRequest req, HttpContext ctx, CancellationToken ct)
    {
        var principal = (CurrentPrincipal?)ctx.Items["Principal"];
        if (principal is null) return Results.Unauthorized();

        var classifier = ctx.RequestServices.GetRequiredService<IIntentClassifier>();
        var intent = (await classifier.ClassifyAsync(req.Message, ct)).Intent;

        var request = new AgentRequest
        {
            UserInput = req.Message,
            UserId = principal.UserId,
            SessionId = req.SessionId,
            CancellationToken = ct,
            Slots = req.Slots ?? new Dictionary<string, object?>(),
            SharedContext = new Dictionary<string, object?> { ["intent"] = intent },
            Upstream = AgentResult.Ok(intent: intent)
        };

        var orchestrator = ctx.RequestServices.GetRequiredService<AgentOrchestrator>();
        var result = await orchestrator.RunAsync(request, ct: ct);

        return Results.Ok(new
        {
            success = result.Success,
            content = result.FinalContent,
            requiresHumanApproval = result.RequiresHumanApproval,
            pendingStepId = result.PendingStepId,
            stepsExecuted = result.StepsExecuted,
            totalElapsedMs = result.TotalElapsedMs,
            steps = result.Steps.Select(s => new
            {
                stepId = s.StepId,
                success = s.Success,
                elapsedMs = s.ElapsedMs,
                failureReason = s.FailureReason,
                agents = s.AgentResults.Select(a => new
                {
                    agentId = a.Key,
                    success = a.Value.Success,
                    intent = a.Value.Intent,
                    content = a.Value.Content,
                    requiresHumanInLoop = a.Value.RequiresHumanInLoop
                })
            })
        });
    }
}
