// ===== 账单分析插件 =====
// 演示「只读场景」插件：无需落库、无需合规拦截，但仍需审计与脱敏。
// 同时演示订阅 transfer.completed 事件实现跨插件联动。

using BankingAgent.Base.Agents;
using BankingAgent.Base.Security;
using BankingAgent.Base.Security.Audit;
using BankingAgent.PluginSdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Plugin.BillAnalysis;

/// <summary>账单分析插件入口。</summary>
public sealed class BillAnalysisPluginEntryPoint : IPluginEntryPoint
{
    /// <inheritdoc />
    public PluginManifest GetManifest() => new()
    {
        Id = new PluginId("banking.bill"),
        Name = "账单分析",
        Description = "月度账单汇总、消费分类统计、支出趋势分析",
        Version = PluginVersion.Parse("1.0.0"),
        Author = "AI 数据组",
        Scenarios = ["bill"],
        FeatureFlags = ["bill.enabled", "bill.trend_analysis"],
        // 账单含消费明细，属 L3
        MaxDataClassification = DataClassification.L3
    };

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton<IBankingAgent, BillAnalysisAgent>();

        // 订阅转账事件，实现「转账后自动更新账单视图」
        services.AddSingleton<IDomainEventHandler, TransferEventListener>();
    }
}

/// <summary>账单分析 Agent。</summary>
public sealed class BillAnalysisAgent : BankingAgentBase
{
    private readonly ICoreBankClient _coreBank;

    /// <summary>构造账单分析 Agent。</summary>
    public BillAnalysisAgent(ICoreBankClient coreBank, IAuditLogger audit, ILogger<BillAnalysisAgent> logger)
        : base(audit, logger)
    {
        _coreBank = coreBank;
    }

    /// <inheritdoc />
    public override AgentId Id => new("bill.agent");

    /// <inheritdoc />
    public override string Name => "账单分析 Agent";

    /// <inheritdoc />
    public override AgentRole Role => AgentRole.DomainExpert;

    /// <inheritdoc />
    public override IReadOnlyList<string> SupportedIntents => ["bill", "bill.summary"];

    /// <inheritdoc />
    public override IReadOnlyList<string> TriggerKeywords =>
        ["账单", "消费", "花了", "支出", "开销", "流水"];

    /// <inheritdoc />
    protected override async Task<AgentResult> HandleAsync(AgentRequest request, CancellationToken ct)
    {
        var accountNo = SlotReader.String(request, "account_no");
        var year = SlotReader.Int(request, "year") ?? DateTimeOffset.Now.Year;
        var month = SlotReader.Int(request, "month") ?? DateTimeOffset.Now.Month;

        // 未指定账户时取第一个账户
        if (string.IsNullOrEmpty(accountNo))
        {
            var accounts = await _coreBank.ListAccountsAsync(request.UserId, ct);
            var first = accounts.FirstOrDefault();
            if (first is null) return AgentResult.Fail("NO_ACCOUNT", "未找到账户");
            accountNo = first.AccountNo;
        }

        var statement = await _coreBank.GetMonthlyStatementAsync(accountNo, year, month, ct);
        if (statement is null)
        {
            return AgentResult.Fail("STATEMENT_NOT_FOUND", $"未找到 {year} 年 {month} 月账单");
        }

        // 脱敏：账户号以掩码形式返回
        var maskedAccount = DataMasker.MaskAccount(accountNo);
        var topCategory = statement.Categories
            .OrderByDescending(c => c.Amount)
            .FirstOrDefault();

        return AgentResult.Ok(
            $"{year}年{month}月共支出 {statement.TotalExpense:N2} 元，" +
            $"其中「{topCategory?.Category ?? "无"}」占比最高" +
            $"（{(topCategory?.Ratio ?? 0) * 100:F1}%）",
            "bill.summary",
            new Dictionary<string, object?>
            {
                ["account_no"] = maskedAccount,
                ["total_income"] = statement.TotalIncome,
                ["total_expense"] = statement.TotalExpense,
                ["transaction_count"] = statement.TransactionCount,
                ["categories"] = statement.Categories,
                ["daily_trend"] = statement.DailyTrend,
                ["data_classification"] = "L3-masked"
            });
    }
}

/// <summary>转账事件监听器。演示插件间零耦合联动。</summary>
public sealed class TransferEventListener : IDomainEventHandler
{
    private readonly ILogger<TransferEventListener> _logger;

    /// <summary>构造监听器。</summary>
    public TransferEventListener(ILogger<TransferEventListener> logger) => _logger = logger;

    /// <inheritdoc />
    public IReadOnlyCollection<string> SubscribedEventTypes => ["transfer.completed"];

    /// <inheritdoc />
    public Task HandleAsync(DomainEvent evt, CancellationToken ct = default)
    {
        // 真实场景：转账成功后刷新用户账单缓存、推送账单更新通知
        _logger.LogInformation(
            "收到转账事件，刷新账单视图: tx={TxNo} amount={Amount}",
            evt.Payload.GetValueOrDefault("transaction_no"),
            evt.Payload.GetValueOrDefault("amount"));

        return Task.CompletedTask;
    }
}
