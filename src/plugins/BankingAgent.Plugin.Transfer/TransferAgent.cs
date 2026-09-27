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
    protected override async Task<AgentResult> HandleAsync(AgentRequest request, CancellationToken ct)
    {
        // ===== 1. 槽位抽取 =====
        var amount = SlotReader.Decimal(request, "amount") ?? _parser.ExtractAmount(request.UserInput);
        var target = SlotReader.String(request, "to_account") ?? _parser.ExtractTargetAccount(request.UserInput);
        var source = SlotReader.String(request, "from_account");
        var confirmed = SlotReader.Bool(request, "confirmed") ?? false;

        if (amount is null)
        {
            return AgentResult.Fail("AMOUNT_MISSING", "未能识别转账金额，请明确说明金额，例如「转账 5000 元」");
        }

        if (string.IsNullOrEmpty(target))
        {
            return AgentResult.Fail("TARGET_MISSING", "未能识别收款账户，请提供收款账号");
        }

        if (string.IsNullOrEmpty(source))
        {
            // 未指定付款账户时，取用户第一个可用账户
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
                    ["rule_id"] = compliance.RuleId
                });
        }

        // ===== 4. 幂等检查 =====
        var idempotencyKey = request.SessionId ?? Guid.NewGuid().ToString("N");

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
            Content = $"转账成功，金额 {amount.Value:N2} 元，流水号 {result.TransactionNo}",
            Data = new Dictionary<string, object?>
            {
                ["transaction_no"] = result.TransactionNo,
                ["amount"] = amount.Value,
                ["balance_after"] = result.BalanceAfter,
                ["human_approved"] = compliance.RequiresHumanApproval
            }
        };
    }
}
