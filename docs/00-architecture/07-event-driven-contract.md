# 事件驱动契约（Event-Driven Contract）

> **状态**：评审中 · **所有者**：架构师 · **版本**：v1.0
> **最后更新**：2026-09-21

---

## 1. 为什么需要事件契约？

事件总线是模块间通信的**唯一受支持方式**。事件契约保证：
- **解耦**：发布方不知道订阅方是谁
- **可演进**：新增订阅方无需修改发布方
- **可追溯**：所有事件有唯一 ID
- **可重放**：支持事件溯源

---

## 2. 事件规范（基于 CloudEvents 1.0）

所有事件遵循 [CloudEvents 1.0 规范](https://cloudevents.io/)：

```json
{
  "specversion": "1.0",
  "id": "evt-01HMZK8XQ5J3W9N7Y6V4C2B1A0",
  "source": "/transfer/context",
  "type": "com.bankagent.transfer.completed.v1",
  "subject": "transfer-order/01HMZK8XQ5J3W9N7Y6V4C2B1A0",
  "time": "2026-09-21T10:30:00.123Z",
  "datacontenttype": "application/json",
  "dataschema": "https://schemas.bankagent.com/transfer-completed/v1.json",
  "traceparent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
  "data": {
    "orderId": "01HMZK8XQ5J3W9N7Y6V4C2B1A0",
    "userId": "U123456",
    "amount": { "value": 100000, "currency": "CNY" },
    "payee": { "accountId": "AC987654", "name": "小李" },
    "channel": "AI_AGENT",
    "agentId": "TransferAgent@v1.2",
    "completedAt": "2026-09-21T10:30:00.000Z"
  }
}
```

### 必填字段

| 字段 | 类型 | 说明 |
|------|------|------|
| `specversion` | string | 固定为 "1.0" |
| `id` | string (ULID/UUID) | 全局唯一，幂等键 |
| `source` | string | 格式 `/<context>/<ModuleName>` |
| `type` | string | 格式 `com.bankagent.<context>.<event>.v<n>` |
| `time` | string (RFC3339) | 事件创建时间（UTC） |
| `data` | object | 业务负载 |

### 可选字段

| 字段 | 用途 |
|------|------|
| `subject` | 聚合根 ID |
| `traceparent` | W3C Trace Context，与 OpenTelemetry 集成 |
| `dataschema` | JSON Schema URL（用于验证） |

---

## 3. 事件命名规范

```
com.bankagent.<bounded-context>.<event-name>.v<version>
```

**示例**：
- `com.bankagent.transfer.initiated.v1`
- `com.bankagent.transfer.completed.v1`
- `com.bankagent.transfer.failed.v1`
- `com.bankagent.wealth.subscribed.v1`
- `com.bankagent.audit.logged.v1`

**版本管理**：
- 不兼容变更：`v1` → `v2`
- 新增字段：`v1` → `v1.1` 或保持 `v1`
- **永不删除 v1 事件**，至少保留向后兼容 1 个大版本

---

## 4. 核心事件目录

### 4.1 Transfer Context

```yaml
TransferInitiated.v1:
  触发时机: 用户确认转账后
  必填字段: orderId, userId, amount, payee, channel
  订阅者:
    - AuditContext（合规审计）
    - AMLChecker（反洗钱检查）

TransferCompleted.v1:
  触发时机: 转账成功
  必填字段: orderId, userId, amount, completedAt
  订阅者:
    - AuditContext
    - NotificationContext
    - MemoryContext（更新用户偏好）

TransferFailed.v1:
  触发时机: 转账失败
  必填字段: orderId, userId, reason, failedAt
  订阅者:
    - AuditContext
    - NotificationContext
```

### 4.2 Wealth Context

```yaml
ProductRecommended.v1:
  触发时机: 理财 Agent 推荐产品后
  必填字段: userId, productIds, recommendationReason
  订阅者:
    - AuditContext
    - ConversationContext（生成回复）

ProductSubscribed.v1:
  触发时机: 用户申购成功
  必填字段: userId, productId, amount
  订阅者:
    - AuditContext
    - ComplianceReportService（合规报送）
```

### 4.3 Cross-Scenario Context

```yaml
ScenarioTriggered.v1:
  触发时机: 检测到跨场景触发条件（如生日临近）
  必填字段: userId, scenarioType, triggerData
  订阅者:
    - AuditContext
    - NotificationContext

TaskScheduled.v1:
  触发时机: 长程任务创建
  必填字段: taskId, userId, actionType, scheduledAt
  订阅者:
    - TaskScheduler
    - AuditContext

TaskCompleted.v1:
  触发时机: 长程任务执行完毕
  必填字段: taskId, userId, result
  订阅者:
    - AuditContext
    - NotificationContext
```

### 4.4 Memory Context

```yaml
UserPreferenceUpdated.v1:
  触发时机: 用户偏好更新
  必填字段: userId, preferenceKey, newValue
  订阅者:
    - AuditContext

RelationshipDiscovered.v1:
  触发时机: 从对话中发现新关系（如"我爱人是小美"）
  必填字段: userId, relatedPerson, relatedPersonId (optional)
  订阅者:
    - AuditContext
    - CrossScenarioContext（可能触发场景）
```

### 4.5 Audit Context

```yaml
AuditLogged.v1:
  触发时机: 所有 Agent 决策记录
  必填字段: traceId, agentId, decisionType, inputs, outputs, reasoning
  订阅者:
    - 无（终点事件，写入审计存储）
```

---

## 5. 事件存储

### 5.1 当前架构
- 进程内事件总线（MediatR）→ 同步处理
- 事件**不持久化**（重启即丢失）

### 5.2 未来架构（ADR-0003）
- 所有事件持久化到 Kafka
- 保留期 90 天
- 支持事件溯源（Event Sourcing）

---

## 6. 实现示例（C#）

### 6.1 定义事件

```csharp
// Transfer.Domain/Events/TransferCompletedEvent.cs
public record TransferCompletedEvent : IIntegrationEvent
{
    public string EventId { get; init; } = Ulid.NewUlid();
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;

    public required Guid OrderId { get; init; }
    public required string UserId { get; init; }
    public required Money Amount { get; init; }
    public required PayeeInfo Payee { get; init; }
    public required TransferChannel Channel { get; init; }
    public required string AgentId { get; init; }
}
```

### 6.2 发布事件

```csharp
// Transfer.Application/TransferExecutionService.cs
public class TransferExecutionService : ITransferExecutionService
{
    private readonly IEventBus _eventBus;
    private readonly ICoreBankAdapter _coreBank;

    public async Task<TransferResult> ExecuteAsync(TransferOrder order)
    {
        // 1. 执行转账
        var result = await _coreBank.ExecuteAsync(order);

        // 2. 发布事件
        await _eventBus.PublishAsync(new TransferCompletedEvent
        {
            OrderId = order.Id,
            UserId = order.FromUser.Value,
            Amount = order.Amount,
            Payee = new PayeeInfo(order.Payee.AccountId, order.Payee.Name),
            Channel = TransferChannel.Agent,
            AgentId = "TransferAgent@v1.2"
        });

        return result;
    }
}
```

### 6.3 订阅事件

```csharp
// Audit.Application/EventHandlers/TransferCompletedAuditHandler.cs
public class TransferCompletedAuditHandler : IEventHandler<TransferCompletedEvent>
{
    private readonly IAuditLogger _audit;

    public async Task HandleAsync(TransferCompletedEvent @event, CancellationToken ct)
    {
        await _audit.LogTransferAsync(new TransferAuditLog
        {
            OrderId = @event.OrderId,
            UserId = @event.UserId,
            Amount = @event.Amount,
            Timestamp = @event.OccurredAt,
            AgentId = @event.AgentId
        });
    }
}
```

---

## 7. 事件演进规则

### 7.1 向后兼容变更（保持版本号）
- ✅ 增加可选字段
- ✅ 增加新事件类型
- ✅ 增加订阅方

### 7.2 不兼容变更（升级版本号 v1 → v2）
- ❌ 移除字段
- ❌ 重命名字段
- ❌ 修改字段类型
- ❌ 修改事件语义

**处理流程**：
1. 发布 v2 事件
2. v1 订阅方继续接收 v1
3. 通知所有订阅方升级
4. 6 个月后停发 v1（须 ADR）

---

## 8. 调试与追踪

每个事件携带 `traceparent`，自动接入 OpenTelemetry。可以通过：

```
Grafana Tempo → 搜索 traceparent → 查看整个事件链路
```

或：
```
Jaeger → 按 traceId 查询 → 查看事件发布、订阅、执行链路
```

---

## 9. 关联文档

- **架构总览**：[`00-overview.md`](00-overview.md)
- **模块边界**：[`05-module-boundaries.md`](05-module-boundaries.md)
- **REST API 规范**：[`../02-api/01-rest-api-spec.md`](../02-api/01-rest-api-spec.md)
- **审计日志**：[`../05-security-compliance/04-audit-logging.md`](../05-security-compliance/04-audit-logging.md)