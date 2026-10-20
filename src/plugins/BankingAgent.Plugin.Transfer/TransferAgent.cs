// ===== 转账 Agent =====
// 关键设计：资金操作必须依次通过
// 1. 参数校验
// 2. ComplianceGuard 合规检查
// 3. 人工回环（大额/可疑时阻断，返回 PendingApproval）
// 4. 调用核心银行
// 5. 落库 + 审计 + 发布事件
// 任何一步失败都不得产生副作用。

using BankingAgent.Base.Agents;
using BankingAgent.Base.Data;
using BankingAgent.Base.Security;
using BankingAgent.Base.Security.Audit;
using BankingAgent.Base.Security.Compliance;
using BankingAgent.PluginSdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Plugin.Transfer;

/// <summary>转账领域 Agent。</summary>
public sealed class TransferAgent : BankingAgentBase
{
    private readonly ICoreBankClient _coreBank;
    private readonly ComplianceGuard _compliance;
    private readonly ITransferIntentParser _parser;
    private readonly IDbContextFactory<BankingDbContext> _dbFactory;
    private readonly IEventPublisher _events;

    /// <summary>构造转账 Agent。</summary>
    public TransferAgent(
        ICoreBankClient coreBank,
        ComplianceGuard compliance,
        ITransferIntentParser parser,
        IDbContextFactory<BankingDbContext> dbFactory,
        IEventPublisher events,
        IAuditLogger audit,
        ILogger<TransferAgent> logger)
        : base(audit, logger)
    {
        _coreBank = coreBank;
        _compliance = compliance;
        _parser = parser;
        _dbFactory = dbFactory;
        _events = events;
    }

    /// <inheritdoc />
    public override AgentId Id => new("transfer.agent");

    /// <inheritdoc />
    public override string Name => "转账执行 Agent";

    /// <inheritdoc />
    public override AgentRole Role => AgentRole.DomainExpert;

    /// <inheritdoc />
    public override IReadOnlyList<string> SupportedIntents => ["transfer", "transfer.execute"];

    /// <inheritdoc />
    public override IReadOnlyList<string> TriggerKeywords =>
        ["转账", "转帐", "汇款", "打钱", "转给", "付给", "汇给", "转"];

    /// <inheritdoc />
    protected override async Task<AgentResult> HandleAsync(AgentRequest request, CancellationToken ct)
    {
        // ===== 1. 槽位抽取 =====
        var amount = SlotReader.Decimal(request, "amount") ?? _parser.ExtractAmount(request.UserInput);
        var target = SlotReader.String(request, "to_account") ?? _parser.ExtractTargetAccount(request.UserInput);
        var source = SlotReader.String(request, "from_account");
        var confirmed = SlotReader.Bool(request, "confirmed") ?? false;
        string? targetName = SlotReader.String(request, "to_name");

        if (amount is null)
        {
            return AgentResult.Fail("AMOUNT_MISSING", "未能识别转账金额，请明确说明金额，例如「给李华转 500 元」");
        }

        // 只说了收款人姓名时，向核心系统解析收款账户。
        // 用户不应该为了转 500 块去背 19 位账号。
        if (string.IsNullOrEmpty(target))
        {
            targetName ??= _parser.ExtractTargetName(request.UserInput);

            // 省略句场景：「给李华转 500」之后说「改成 800」，
            // 这一句里没有收款人，回溯上一轮的原话把它找回来。
            if (string.IsNullOrEmpty(targetName))
            {
                var previousInput = request.SharedContext.GetValueOrDefault("previous_user_input")?.ToString();
                if (!string.IsNullOrWhiteSpace(previousInput))
                {
                    targetName = _parser.ExtractTargetName(previousInput);
                }
            }

            if (!string.IsNullOrEmpty(targetName))
            {
                var candidates = await _coreBank.SearchBeneficiariesAsync(targetName, ct);

                if (candidates.Count == 1)
                {
                    target = candidates[0].AccountNo;
                    targetName = candidates[0].Name;
                }
                else if (candidates.Count > 1)
                {
                    // 同名客户在多家银行都有账户时，不能替用户瞎猜一个 ——
                    // 把候选连同开户行列出来，让用户指认，跨行转账才不会转错人。
                    var options = string.Join("；", candidates.Select(c =>
                        $"{c.BankName} {DataMasker.MaskAccount(c.AccountNo)}"));

                    return AgentResult.Fail("TARGET_AMBIGUOUS",
                        $"「{targetName}」在 {candidates.Count} 家银行有账户：{options}。请指定开户行或收款账号。");
                }
                else
                {
                    return AgentResult.Fail("TARGET_NOT_FOUND",
                        $"未找到收款人「{targetName}」，请核对姓名，或直接提供收款账号");
                }
            }
        }

        if (string.IsNullOrEmpty(target))
        {
            return AgentResult.Fail("TARGET_MISSING",
                "未能识别收款账户，请说明收款人姓名或收款账号，例如「给李华转 500 元」");
        }

        if (string.IsNullOrEmpty(source))
        {
            // 未指定付款账户时，取用户第一个可用账户（跨行场景下按账号排序，取到的可能是任意一家银行）
            var accounts = await _coreBank.ListAccountsAsync(request.UserId, ct);
            var payable = accounts.FirstOrDefault(a => a.Status == "正常");
            if (payable is null)
            {
                return AgentResult.Fail("NO_ACCOUNT", "未找到可用的付款账户");
            }
            source = payable.AccountNo;
        }

        // ===== 2. 合规检查 =====
        var compliance = _compliance.Evaluate(new ComplianceContext
        {
            UserId = request.UserId,
            Scenario = "transfer",
            Intent = "transfer.execute",
            Amount = amount.Value,
            SourceAccount = source,
            TargetAccount = target,
            IdempotencyKey = request.SessionId
        });

        await Audit.WriteAsync(new AuditEvent
        {
            AuditId = Guid.NewGuid().ToString("N")[..12],
            Timestamp = DateTimeOffset.UtcNow,
            ActorType = "AGENT",
            ActorId = Id.Value,
            Operation = "compliance.evaluate",
            Scenario = "transfer",
            Intent = "transfer.execute",
            Decision = compliance.Allowed ? (compliance.RequiresHumanApproval ? "REQUIRE_APPROVAL" : "ALLOW") : "DENY",
            DecisionReason = compliance.Reason,
            RuleId = compliance.RuleId,
            Amount = amount.Value,
            RiskScore = compliance.RiskScore,
            ComplianceRule = compliance.RuleVersion
        }, ct);

        if (!compliance.Allowed)
        {
            Logger.LogWarning("合规拒绝转账: {Reason}", compliance.Reason);
            return AgentResult.Fail("COMPLIANCE_REJECTED", compliance.Reason);
        }

        // ===== 3. 人工回环 =====
        if (compliance.RequiresHumanApproval && !confirmed)
        {
            Logger.LogInformation("触发人工确认: {Rule}", compliance.RuleId);
            return AgentResult.PendingApproval(
                compliance.Reason,
                new Dictionary<string, object?>
                {
                    ["amount"] = amount.Value,
                    ["from_account"] = source,
                    ["to_account"] = target,
                    ["to_name"] = targetName,
                    ["rule_id"] = compliance.RuleId
                });
        }

        // ===== 4. 幂等检查 =====
        var idempotencyKey = BuildIdempotencyKey(request, source!, target!, amount.Value);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Set<TransferRecord>()
            .FirstOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, ct);

        if (existing is not null)
        {
            return new AgentResult
            {
                Success = true,
                SideEffectCommitted = true,
                Content = $"该转账已处理过，流水号 {existing.TransactionNo}",
                Intent = "transfer.completed",
                Confidence = 1.0,
                Data = new Dictionary<string, object?>
                {
                    ["transaction_no"] = existing.TransactionNo,
                    ["amount"] = existing.Amount,
                    ["idempotent_replay"] = true
                }
            };
        }

        // ===== 5. 调用核心银行 =====
        var result = await _coreBank.ExecuteTransferAsync(new TransferCommand
        {
            UserId = request.UserId,
            FromAccountNo = source!,
            ToAccountNo = target!,
            Amount = amount.Value,
            IdempotencyKey = idempotencyKey,
            Remark = request.UserInput.Length > 50 ? request.UserInput[..50] : request.UserInput
        }, ct);

        if (!result.Success)
        {
            await Audit.WriteAsync(new AuditEvent
            {
                AuditId = Guid.NewGuid().ToString("N")[..12],
                Timestamp = DateTimeOffset.UtcNow,
                ActorType = "CORE_BANK",
                ActorId = "mock-bank",
                Operation = "transfer.execute",
                Scenario = "transfer",
                Decision = "FAILED",
                DecisionReason = result.ErrorMessage ?? "",
                Amount = amount.Value
            }, ct);

            return AgentResult.Fail(result.ErrorCode ?? "TRANSFER_FAILED",
                result.ErrorMessage ?? "转账失败，请稍后重试");
        }

        // ===== 6. 落库 =====
        var record = new TransferRecord
        {
            UserId = request.UserId,
            FromAccountNo = source!,
            ToAccountNo = target!,
            Amount = amount.Value,
            Status = "Completed",
            TransactionNo = result.TransactionNo,
            IdempotencyKey = idempotencyKey,
            HumanApproved = compliance.RequiresHumanApproval
        };

        db.Set<TransferRecord>().Add(record);
        await db.SaveChangesAsync(ct);

        // ===== 7. 发布事件，解耦账单/理财等插件 =====
        await _events.PublishAsync(new DomainEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventType = "transfer.completed",
            Source = "banking.transfer",
            OccurredAt = DateTimeOffset.UtcNow,
            CorrelationId = request.SessionId,
            UserId = request.UserId,
            Payload = new Dictionary<string, object?>
            {
                ["transaction_no"] = result.TransactionNo,
                ["amount"] = amount.Value,
                ["from_account"] = source,
                ["to_account"] = target,
                ["balance_after"] = result.BalanceAfter
            }
        }, ct);

        return new AgentResult
        {
            Success = true,
            SideEffectCommitted = true,
            Intent = "transfer.completed",
            Confidence = 1.0,
            Content = string.IsNullOrEmpty(targetName)
                ? $"转账成功，金额 {amount.Value:N2} 元，流水号 {result.TransactionNo}"
                : $"已向 {targetName} 转账 {amount.Value:N2} 元，流水号 {result.TransactionNo}",
            Data = new Dictionary<string, object?>
            {
                ["transaction_no"] = result.TransactionNo,
                ["amount"] = amount.Value,
                ["to_name"] = targetName,
                ["balance_after"] = result.BalanceAfter,
                ["human_approved"] = compliance.RequiresHumanApproval
            }
        };
    }

    /// <summary>
    /// 生成幂等键。
    ///
    /// 早期实现直接拿 sessionId 当键，于是**一个会话里只能发生一次转账**：
    /// 多轮对话中「先转 500，再改成 800」的第二轮会被当成重复提交而静默跳过，
    /// 用户看到的是「该转账已处理过」，但他明明改过金额。
    ///
    /// 现在键的组成是「会话 + 操作要素（付款方 / 收款方 / 金额）」：
    ///   - 同一会话里重复提交**同样的**转账 → 键相同 → 被幂等拦截（这才是幂等要防的）；
    ///   - 同一会话里改了金额或对象 → 键不同 → 正常执行。
    /// </summary>
    /// <param name="request">当前请求。</param>
    /// <param name="source">付款账号。</param>
    /// <param name="target">收款账号。</param>
    /// <param name="amount">转账金额。</param>
    /// <returns>幂等键。</returns>
    private static string BuildIdempotencyKey(
        AgentRequest request, string source, string target, decimal amount)
    {
        var session = string.IsNullOrWhiteSpace(request.SessionId)
            ? Guid.NewGuid().ToString("N")
            : request.SessionId.Trim();

        return string.Join('|', session, source, target, amount.ToString("F2"));
    }
}
