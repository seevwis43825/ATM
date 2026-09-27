# 事件 Schema 规范（Event Schema）

> **状态**：评审中 · **所有者**：架构师 + 业务开发 · **版本**：v1.0
> **最后更新**：2026-09-21

> 事件契约详见 [`../00-architecture/07-event-driven-contract.md`](../00-architecture/07-event-driven-contract.md)。本文给出**具体的事件 JSON Schema**。

---

## 1. 通用外壳（CloudEvents 1.0）

```json
{
  "specversion": "1.0",
  "id": "01HMZK8XQ5J3W9N7Y6V4C2B1A0",
  "source": "/transfer/context",
  "type": "com.bankagent.transfer.completed.v1",
  "subject": "transfer-order/01HMZK8XQ5J3W9N7Y6V4C2B1A0",
  "time": "2026-09-21T10:30:00.123Z",
  "datacontenttype": "application/json",
  "dataschema": "https://schemas.bankagent.com/transfer-completed/v1.json",
  "traceparent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
  "data": { /* 业务负载 */ }
}
```

---

## 2. Transfer Context 事件

### 2.1 TransferInitiated.v1

```json
{
  "type": "com.bankagent.transfer.initiated.v1",
  "source": "/transfer/context",
  "subject": "transfer-order/{orderId}",
  "data": {
    "orderId": "01HMZK8XQ5J3W9N7Y6V4C2B1A0",
    "userId": "U123456",
    "amount": {
      "value": 500.00,
      "currency": "CNY"
    },
    "payee": {
      "type": "named",  // named | account | phone | memo
      "name": "小李",
      "phoneNumber": "138****8888",
      "memo": "聚餐 AA"
    },
    "channel": "AI_AGENT",
    "agentId": "TransferAgent@v1.2",
    "requireHumanConfirm": true,
    "humanConfirmationToken": "tok-xyz",
    "scheduledAt": null,
    "occurredAt": "2026-09-21T10:29:30.000Z"
  }
}
```

**订阅者**：Audit Context、AMLChecker、MemoryContext（更新转账频率）

### 2.2 TransferCompleted.v1

```json
{
  "type": "com.bankagent.transfer.completed.v1",
  "source": "/transfer/context",
  "subject": "transfer-order/{orderId}",
  "data": {
    "orderId": "01HMZK8XQ5J3W9N7Y6V4C2B1A0",
    "userId": "U123456",
    "amount": { "value": 500.00, "currency": "CNY" },
    "coreBankReference": "CBS202609211030001",
    "completedAt": "2026-09-21T10:30:00.000Z",
    "agentId": "TransferAgent@v1.2"
  }
}
```

### 2.3 TransferFailed.v1

```json
{
  "type": "com.bankagent.transfer.failed.v1",
  "source": "/transfer/context",
  "subject": "transfer-order/{orderId}",
  "data": {
    "orderId": "01HMZK8XQ5J3W9N7Y6V4C2B1A0",
    "userId": "U123456",
    "reason": "INSUFFICIENT_BALANCE",
    "failureCode": "E_CORE_BANK_002",
    "failedAt": "2026-09-21T10:30:00.000Z"
  }
}
```

### 2.4 AA Transfer Completed

```json
{
  "type": "com.bankagent.transfer.aa_completed.v1",
  "source": "/transfer/context",
  "subject": "aa-transfer/{parentOrderId}",
  "data": {
    "parentOrderId": "01HMZK8XQ5J3W9N7Y6V4C2B1A0",
    "userId": "U123456",
    "totalAmount": { "value": 800.00, "currency": "CNY" },
    "subOrders": [
      { "subOrderId": "...", "payee": "...", "amount": 200 },
      { "subOrderId": "...", "payee": "...", "amount": 200 },
      { "subOrderId": "...", "payee": "...", "amount": 200 },
      { "subOrderId": "...", "payee": "...", "amount": 200 }
    ]
  }
}
```

---

## 3. BillAnalysis Context 事件

### 3.1 BillAnalyzed.v1

```json
{
  "type": "com.bankagent.bill_analysis.analyzed.v1",
  "source": "/bill_analysis/context",
  "subject": "bill/{billId}",
  "data": {
    "billId": "BILL_202609_U123456",
    "userId": "U123456",
    "period": "2026-09",
    "totalExpense": { "value": 8500.00, "currency": "CNY" },
    "totalIncome": { "value": 12000.00, "currency": "CNY" },
    "categoryBreakdown": [
      { "category": "Food", "amount": { "value": 2500, "currency": "CNY" }, "ratio": 0.294 },
      { "category": "Shopping", "amount": { "value": 1800, "currency": "CNY" }, "ratio": 0.212 }
    ],
    "topMerchants": [
      { "name": "美团", "amount": { "value": 1500, "currency": "CNY" }, "count": 18 }
    ]
  }
}
```

### 3.2 AnomalyDetected.v1

```json
{
  "type": "com.bankagent.bill_analysis.anomaly_detected.v1",
  "source": "/bill_analysis/context",
  "subject": "bill/{billId}",
  "data": {
    "billId": "BILL_202609_U123456",
    "userId": "U123456",
    "anomalies": [
      {
        "type": "LARGE_TRANSACTION",
        "transactionId": "TXN123456",
        "amount": { "value": 8000, "currency": "CNY" },
        "merchantName": "某商家",
        "deviationFromMean": 5.2,
        "severity": "HIGH"
      }
    ]
  }
}
```

---

## 4. Wealth Context 事件

### 4.1 ProductRecommended.v1

```json
{
  "type": "com.bankagent.wealth.recommended.v1",
  "source": "/wealth/context",
  "subject": "user/{userId}",
  "data": {
    "userId": "U123456",
    "agentId": "WealthAgent@v1.0",
    "recommendations": [
      {
        "productId": "PROD123",
        "name": "稳健理财 90 天",
        "riskLevel": "R2",
        "expectedAnnualReturn": 0.035,
        "reason": "匹配您的保守型风险偏好"
      }
    ],
    "basedOnRiskProfile": {
      "level": "Conservative",
      "score": 25,
      "assessedAt": "2026-08-01T00:00:00Z"
    }
  }
}
```

### 4.2 ProductSubscribed.v1

```json
{
  "type": "com.bankagent.wealth.subscribed.v1",
  "source": "/wealth/context",
  "subject": "wealth-order/{orderId}",
  "data": {
    "orderId": "WO20260921001",
    "userId": "U123456",
    "productId": "PROD123",
    "amount": { "value": 50000, "currency": "CNY" },
    "snapshotRiskProfile": {
      "level": "Conservative",
      "score": 25
    },
    "agentId": "WealthAgent@v1.0",
    "occurredAt": "2026-09-21T10:30:00Z"
  }
}
```

---

## 5. CrossScenario Context 事件

### 5.1 ScenarioTriggered.v1

```json
{
  "type": "com.bankagent.cross_scenario.triggered.v1",
  "source": "/cross_scenario/context",
  "subject": "scenario/{scenarioId}",
  "data": {
    "scenarioId": "SC20260921001",
    "userId": "U123456",
    "scenarioType": "Birthday",
    "title": "我爱人生日",
    "triggerAt": "2026-10-18T00:00:00Z",
    "triggerReason": "MEMORY_MATCH",
    "tasksCount": 3,
    "tasks": [
      {
        "taskId": "T001",
        "actionType": "LockFunds",
        "scheduledAt": "2026-10-01T08:00:00Z",
        "parameters": { "amount": { "value": 1000, "currency": "CNY" } }
      },
      {
        "taskId": "T002",
        "actionType": "OrderFlower",
        "scheduledAt": "2026-10-17T08:00:00Z",
        "parameters": { "productType": "鲜花", "budget": { "value": 300, "currency": "CNY" } }
      },
      {
        "taskId": "T003",
        "actionType": "OrderCake",
        "scheduledAt": "2026-10-17T08:00:00Z",
        "parameters": { "productType": "蛋糕", "budget": { "value": 400, "currency": "CNY" } }
      }
    ]
  }
}
```

### 5.2 TaskScheduled.v1

```json
{
  "type": "com.bankagent.cross_scenario.task_scheduled.v1",
  "source": "/cross_scenario/context",
  "subject": "task/{taskId}",
  "data": {
    "taskId": "T001",
    "scenarioId": "SC20260921001",
    "actionType": "LockFunds",
    "scheduledAt": "2026-10-01T08:00:00Z",
    "status": "Scheduled"
  }
}
```

### 5.3 TaskCompleted.v1

```json
{
  "type": "com.bankagent.cross_scenario.task_completed.v1",
  "source": "/cross_scenario/context",
  "subject": "task/{taskId}",
  "data": {
    "taskId": "T001",
    "scenarioId": "SC20260921001",
    "actionType": "LockFunds",
    "result": "SUCCESS",
    "details": {
      "lockedAmount": { "value": 1000, "currency": "CNY" },
      "lockedUntil": "2026-10-20T23:59:59Z"
    },
    "completedAt": "2026-10-01T08:00:15Z"
  }
}
```

---

## 6. Memory Context 事件

### 6.1 UserPreferenceUpdated.v1

```json
{
  "type": "com.bankagent.memory.preference_updated.v1",
  "source": "/memory/context",
  "subject": "user/{userId}",
  "data": {
    "userId": "U123456",
    "preferenceKey": "spouse.name",
    "oldValue": null,
    "newValue": "小美",
    "source": "UserStatement",
    "conversationId": "CONV123",
    "agentId": "ConversationAgent@v1.0"
  }
}
```

### 6.2 RelationshipDiscovered.v1

```json
{
  "type": "com.bankagent.memory.relationship_discovered.v1",
  "source": "/memory/context",
  "subject": "user/{userId}",
  "data": {
    "userId": "U123456",
    "relationshipId": "REL001",
    "relationshipType": "spouse",  // spouse, parent, child, friend, colleague
    "name": "小美",
    "fullName": "王小美",
    "birthday": "10-20",
    "anniversary": null,
    "strength": 0.95,
    "source": "UserStatement"
  }
}
```

---

## 7. Audit Context 事件

### 7.1 AuditLogged.v1（终点事件）

```json
{
  "type": "com.bankagent.audit.logged.v1",
  "source": "/audit/context",
  "subject": "audit-log/{logId}",
  "data": {
    "logId": "AUDIT_01HMZK...",
    "traceId": "00-4bf92f...",
    "agentId": "TransferAgent@v1.2",
    "decisionType": "ExecuteTransfer",
    "userId": "U123456",
    "inputs": { /* 输入 JSON */ },
    "outputs": { /* 输出 JSON */ },
    "reasoning": "用户输入'转500给小李'，意图识别为转账，解析收款人小李为账户AC987654",
    "requiresHumanConfirm": true,
    "humanConfirmed": true,
    "humanConfirmationTime": "2026-09-21T10:29:45Z",
    "createdAt": "2026-09-21T10:29:30Z"
  }
}
```

---

## 8. 事件 Schema 注册表

| 事件 Type | Schema URL | 版本 | 最后更新 |
|----------|-----------|------|---------|
| `com.bankagent.transfer.initiated.v1` | `https://schemas.bankagent.com/transfer-initiated/v1.json` | v1 | 2026-09-21 |
| `com.bankagent.transfer.completed.v1` | `https://schemas.bankagent.com/transfer-completed/v1.json` | v1 | 2026-09-21 |
| `com.bankagent.transfer.failed.v1` | `https://schemas.bankagent.com/transfer-failed/v1.json` | v1 | 2026-09-21 |
| `com.bankagent.bill_analysis.analyzed.v1` | `https://schemas.bankagent.com/bill-analyzed/v1.json` | v1 | 2026-09-21 |
| `com.bankagent.wealth.subscribed.v1` | `https://schemas.bankagent.com/wealth-subscribed/v1.json` | v1 | 2026-09-21 |
| `com.bankagent.cross_scenario.triggered.v1` | `https://schemas.bankagent.com/scenario-triggered/v1.json` | v1 | 2026-09-21 |
| `com.bankagent.audit.logged.v1` | `https://schemas.bankagent.com/audit-logged/v1.json` | v1 | 2026-09-21 |

所有 schema 文件存放于 `docs/02-api/schemas/`，并通过 CI/CD 自动发布到 schema 服务。

---

## 9. 关联文档

- **事件契约（CloudEvents）**：[`../00-architecture/07-event-driven-contract.md`](../00-architecture/07-event-driven-contract.md)
- **REST API 规范**：[`01-rest-api-spec.md`](01-rest-api-spec.md)
- **功能开关**：[`03-feature-flags.md`](03-feature-flags.md)