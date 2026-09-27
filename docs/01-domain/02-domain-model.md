# 领域模型（Domain Model）

> **状态**：评审中 · **所有者**：业务开发 · **版本**：v1.0
> **最后更新**：2026-09-21

本文展示核心 Context 的实体、值对象、聚合根，使用 C# 表达。

---

## 1. 通用值对象（Shared Kernel）

```csharp
// Shared/ValueObjects/Money.cs
public readonly record struct Money(decimal Value, Currency Currency)
{
    public static Money CNY(decimal value) => new(value, Currency.CNY);

    public static Money operator +(Money a, Money b)
    {
        if (a.Currency != b.Currency)
            throw new InvalidOperationException("币种不一致");
        return new Money(a.Value + b.Value, a.Currency);
    }

    public static Money operator -(Money a, Money b) => new(a.Value - b.Value, a.Currency);

    public bool IsPositive() => Value > 0;
}

// Shared/ValueObjects/UserId.cs
public readonly record struct UserId(string Value)
{
    public static UserId From(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("UserId cannot be empty");
        return new UserId(value);
    }
}

// Shared/ValueObjects/AccountId.cs
public readonly record struct AccountId(string Value)
```

---

## 2. Transfer Context

### 2.1 聚合根：TransferOrder

```csharp
// Transfer.Domain/Aggregates/TransferOrder.cs
public class TransferOrder : AggregateRoot<TransferOrderId>
{
    public UserId FromUser { get; private set; }
    public Payee ToPayee { get; private set; }
    public Money Amount { get; private set; }
    public TransferStatus Status { get; private set; }
    public TransferChannel Channel { get; private set; }
    public string? HumanConfirmationToken { get; private set; }  // 人工回环 token
    public DateTime CreatedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    // 工厂方法（业务规则：转账不能为 0 金额）
    public static TransferOrder Create(
        UserId fromUser,
        Payee toPayee,
        Money amount,
        TransferChannel channel)
    {
        if (!amount.IsPositive())
            throw new DomainException("Transfer amount must be positive");

        return new TransferOrder
        {
            Id = TransferOrderId.New(),
            FromUser = fromUser,
            ToPayee = toPayee,
            Amount = amount,
            Status = TransferStatus.PendingHumanConfirmation,
            Channel = channel
        };
    }

    // 业务方法
    public void ConfirmHuman(string token)
    {
        if (Status != TransferStatus.PendingHumanConfirmation)
            throw new DomainException($"Cannot confirm in status {Status}");

        HumanConfirmationToken = token;
        Status = TransferStatus.Confirmed;
        AddDomainEvent(new TransferInitiatedEvent(Id, FromUser, Amount, ToPayee, Channel));
    }

    public void MarkCompleted()
    {
        if (Status != TransferStatus.Confirmed)
            throw new DomainException($"Cannot complete in status {Status}");

        Status = TransferStatus.Completed;
        CompletedAt = DateTime.UtcNow;
        AddDomainEvent(new TransferCompletedEvent(Id, FromUser, Amount, CompletedAt.Value));
    }

    public void MarkFailed(string reason)
    {
        Status = TransferStatus.Failed;
        AddDomainEvent(new TransferFailedEvent(Id, FromUser, reason, DateTime.UtcNow));
    }
}
```

### 2.2 值对象：Payee

```csharp
public record struct Payee
{
    public AccountId? AccountId { get; init; }    // 已知账户
    public string? Name { get; init; }            // 姓名（用于解析）
    public string? PhoneNumber { get; init; }     // 手机号
    public string? Memo { get; init; }            // 备注转账

    public bool IsResolvable() => AccountId.HasValue || !string.IsNullOrEmpty(Name) || !string.IsNullOrEmpty(PhoneNumber);
}
```

### 2.3 值对象：TransferSchedule（定时转账）

```csharp
public record struct TransferSchedule
{
    public ScheduleType Type { get; init; }      // OneTime, Weekly, Monthly
    public DateTime? ExecuteAt { get; init; }   // 单次执行时间
    public RecurringPattern? Pattern { get; init; } // 重复模式
    public DateTime? EndAt { get; init; }       // 结束时间
    public int? MaxOccurrences { get; init; }   // 最大执行次数
}
```

---

## 3. BillAnalysis Context

### 3.1 聚合根：Bill

```csharp
// BillAnalysis.Domain/Aggregates/Bill.cs
public class Bill : AggregateRoot<BillId>
{
    public UserId UserId { get; private set; }
    public BillingPeriod Period { get; private set; }  // 月度/年度
    public IReadOnlyList<Transaction> Transactions { get; private set; } = new List<Transaction>();
    public BillSummary? Summary { get; private set; }
    public IReadOnlyList<AnomalyReport> Anomalies { get; private set; } = new List<AnomalyReport>();

    public void AddTransaction(Transaction tx) => Transactions.Append(tx);

    public void Analyze()
    {
        // 业务规则：账单必须经过分析后才能输出报告
        if (Transactions.Count == 0) throw new DomainException("账单无交易");
        Summary = BillSummary.Compute(Transactions);
        AddDomainEvent(new BillAnalyzedEvent(Id, UserId, Period));
    }

    public void MarkAnomaly(AnomalyReport anomaly)
    {
        Anomalies.Append(anomaly);
        AddDomainEvent(new AnomalyDetectedEvent(Id, UserId, anomaly));
    }
}
```

### 3.2 值对象：TransactionCategory

```csharp
public enum TransactionCategory
{
    Food,           // 餐饮
    Shopping,       // 购物
    Transport,      // 交通
    Entertainment,  // 娱乐
    Subscription,   // 订阅
    Transfer,       // 转账
    Income,         // 收入
    Other
}
```

### 3.3 值对象：Transaction

```csharp
public record struct Transaction
{
    public TransactionId Id { get; init; }
    public Money Amount { get; init; }
    public TransactionCategory Category { get; init; }
    public string MerchantName { get; init; }
    public DateTime OccurredAt { get; init; }
}
```

---

## 4. Wealth Context

### 4.1 聚合根：FinancialProduct

```csharp
// Wealth.Domain/Aggregates/FinancialProduct.cs
public class FinancialProduct : AggregateRoot<ProductId>
{
    public string Name { get; private set; }
    public ProductType Type { get; private set; }  // Fund, Bond, Structured
    public RiskLevel RiskLevel { get; private set; }  // R1-R5
    public Money MinAmount { get; private set; }
    public Money MaxAmount { get; private set; }
    public decimal ExpectedAnnualReturn { get; private set; }
    public string Currency { get; private set; }
    public bool Available { get; private set; }

    public bool IsSuitableFor(RiskProfile profile)
    {
        // 业务规则：产品风险等级 ≤ 用户风险等级
        return (int)RiskLevel <= (int)profile.Level;
    }
}
```

### 4.2 聚合根：InvestmentOrder

```csharp
public class InvestmentOrder : AggregateRoot<OrderId>
{
    public UserId UserId { get; private set; }
    public ProductId ProductId { get; private set; }
    public Money Amount { get; private set; }
    public OrderType Type { get; private set; }  // Subscribe, Redeem
    public OrderStatus Status { get; private set; }
    public RiskProfile SnapshotRiskProfile { get; private set; }  // 申购时的风险快照

    public static InvestmentOrder Subscribe(
        UserId userId, ProductId productId, Money amount, RiskProfile riskProfile)
    {
        return new InvestmentOrder
        {
            Id = OrderId.New(),
            UserId = userId,
            ProductId = productId,
            Amount = amount,
            Type = OrderType.Subscribe,
            Status = OrderStatus.PendingHumanConfirmation,
            SnapshotRiskProfile = riskProfile
        };
    }
}
```

---

## 5. CrossScenario Context

### 5.1 聚合根：Scenario

```csharp
// CrossScenario.Domain/Aggregates/Scenario.cs
public class Scenario : AggregateRoot<ScenarioId>
{
    public UserId UserId { get; private set; }
    public ScenarioType Type { get; private set; }  // Birthday, Anniversary, etc.
    public string Title { get; private set; }
    public DateTime TriggerAt { get; private set; }  // 触发时间
    public ScenarioStatus Status { get; private set; }
    public IReadOnlyList<ScenarioTask> Tasks { get; private set; } = new List<ScenarioTask>();

    public void AddTask(ScenarioTask task) => Tasks.Append(task);

    public void MarkScheduled()
    {
        Status = ScenarioStatus.Scheduled;
        AddDomainEvent(new ScenarioTriggeredEvent(Id, UserId, Type, TriggerAt));
    }

    public void MarkCompleted()
    {
        Status = ScenarioStatus.Completed;
        AddDomainEvent(new TaskCompletedEvent(Id, UserId, "scenario_completed"));
    }
}
```

### 5.2 实体：ScenarioTask

```csharp
public class ScenarioTask : Entity<TaskId>
{
    public ActionType ActionType { get; private set; }  // LockFunds, OrderFlower, OrderCake
    public TaskStatus Status { get; private set; }
    public DateTime ScheduledAt { get; private set; }
    public Money? Amount { get; private set; }
    public string? MerchantId { get; private set; }
    public string? Parameters { get; private set; }  // JSON 序列化

    public void Execute()
    {
        Status = TaskStatus.Executing;
        AddDomainEvent(new TaskScheduledEvent(Id, ScheduledAt));
    }
}
```

---

## 6. Memory&Profile Context

### 6.1 聚合根：UserProfile

```csharp
// MemoryProfile.Domain/Aggregates/UserProfile.cs
public class UserProfile : AggregateRoot<UserId>
{
    public RiskProfile RiskProfile { get; private set; }
    public List<UserRelationship> Relationships { get; private set; } = new();
    public Dictionary<string, object> Preferences { get; private set; } = new();
    public List<string> ImportantDates { get; private set; } = new();
    public DateTime LastUpdatedAt { get; private set; }

    public void AddRelationship(UserRelationship relationship)
    {
        Relationships.Add(relationship);
        LastUpdatedAt = DateTime.UtcNow;
        AddDomainEvent(new RelationshipDiscoveredEvent(Id, relationship));
    }

    public void SetPreference(string key, object value)
    {
        Preferences[key] = value;
        LastUpdatedAt = DateTime.UtcNow;
        AddDomainEvent(new UserPreferenceUpdatedEvent(Id, key, value));
    }
}
```

### 6.2 值对象：RiskProfile

```csharp
public record struct RiskProfile
{
    public RiskLevel Level { get; init; }  // Conservative, Moderate, Aggressive
    public DateTime AssessedAt { get; init; }
    public int Score { get; init; }  // 1-100

    public static RiskProfile Default() => new()
    {
        Level = RiskLevel.Conservative,
        AssessedAt = DateTime.UtcNow,
        Score = 30
    };
}
```

### 6.3 实体：UserRelationship

```csharp
public class UserRelationship : Entity<RelationshipId>
{
    public string Name { get; private set; }  // "妻子"、"父亲" 等
    public string? FullName { get; private set; }
    public string? PhoneNumber { get; private set; }
    public string? Birthday { get; private set; }  // MM-DD
    public string? Anniversary { get; private set; }
    public float RelationshipStrength { get; private set; }  // 0-1，关系强度
}
```

---

## 7. Audit Context

### 7.1 聚合根：AuditLog

```csharp
public class AuditLog : AggregateRoot<AuditLogId>
{
    public TraceContext TraceContext { get; private set; }
    public AgentDecision Decision { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public static AuditLog From(TraceContext trace, AgentDecision decision)
    {
        return new AuditLog
        {
            Id = AuditLogId.New(),
            TraceContext = trace,
            Decision = decision,
            CreatedAt = DateTime.UtcNow
        };
    }
}
```

### 7.2 值对象：AgentDecision

```csharp
public record struct AgentDecision
{
    public string AgentId { get; init; }        // "TransferAgent@v1.2"
    public string DecisionType { get; init; }  // "ExecuteTransfer"
    public string Inputs { get; init; }        // JSON
    public string Outputs { get; init; }       // JSON
    public string Reasoning { get; init; }     // LLM 推理过程
    public bool RequiresHumanConfirm { get; init; }
    public bool HumanConfirmed { get; init; }
}
```

---

## 8. 聚合根基类

```csharp
public abstract class AggregateRoot<TId> : Entity<TId>
{
    private readonly List<IDomainEvent> _domainEvents = new();

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    protected void AddDomainEvent(IDomainEvent @event) => _domainEvents.Add(@event);

    public void ClearDomainEvents() => _domainEvents.Clear();
}

public abstract class Entity<TId>
{
    public TId Id { get; protected set; } = default!;
}

public interface IDomainEvent
{
    DateTime OccurredAt { get; }
}
```

---

## 9. 防腐层示例

```csharp
// AgentOrchestration/Infrastructure/ACL/WealthACL.cs
public class WealthACL : IWealthAdapter
{
    public async Task<RecommendationDTO> RecommendAsync(UserId userId, Money available)
    {
        var profile = await _profileService.GetAsync(userId);
        var products = await _wealthService.GetSuitableProductsAsync(profile, available);

        // 防腐：只暴露给 Orchestration 必要字段
        return products.Select(p => new RecommendationDTO(
            ProductId: p.Id.Value,
            Name: p.Name,
            RiskLevel: p.RiskLevel.ToString(),
            ExpectedReturn: p.ExpectedAnnualReturn
        )).Take(5);
    }
}
```

---

## 10. 关联文档

- **限界上下文**：[`01-bounded-contexts.md`](01-bounded-contexts.md)
- **术语表**：[`03-glossary.md`](03-glossary.md)
- **架构决策**：[`../00-architecture/04-architecture-decisions.md`](../00-architecture/04-architecture-decisions.md)