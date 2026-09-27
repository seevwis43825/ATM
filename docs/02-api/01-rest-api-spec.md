# REST API 设计规范

> **状态**：评审中 · **所有者**：业务开发 + 平台 · **版本**：v1.0
> **最后更新**：2026-09-21

---

## 1. 设计原则

| 原则 | 说明 |
|------|------|
| **RESTful 资源导向** | URL 表示资源，HTTP 方法表示动作 |
| **统一错误响应** | 所有错误返回相同结构 |
| **版本化** | URL 路径中包含 `/v1/`，不破坏向后兼容 |
| **分页标准** | `?page=` + `?limit=` 或 `?cursor=` |
| **过滤 / 搜索** | `?field=value`，支持多值 |
| **字段筛选** | `?fields=id,name`（GraphQL-like） |
| **幂等性** | POST 必须支持 `Idempotency-Key` 头 |
| **限流** | 所有端点都有 Rate Limit |

---

## 2. URL 设计

### 2.1 基础格式

```
https://api.bankagent.com/v1/{context}/{resource}/{id?}/{action?}
```

**示例**：
```
GET    /v1/transfers/{orderId}
POST   /v1/transfers
POST   /v1/transfers/{orderId}/confirm       # 人工确认
GET    /v1/bills/{userId}?period=2026-09
POST   /v1/wealth/products/recommend
POST   /v1/wealth/orders
POST   /v1/cards/{cardId}/report-lost
GET    /v1/subscriptions?userId=xxx&status=active
POST   /v1/scenarios
GET    /v1/memory/profile/{userId}
GET    /v1/audit/logs?traceId=xxx
```

### 2.2 命名规范

| 类型 | 规则 | 例子 |
|------|------|------|
| 资源（复数） | 全小写、连字符分隔 | `/transfers`, `/bill-analyses` |
| 资源（单数） | 复数 + `/{id}` | `/transfers/{id}` |
| 子资源 | 嵌套 | `/transfers/{id}/confirmations` |
| 动作（非 CRUD） | 资源后 + 动词 | `/transfers/{id}/cancel` |

### 2.3 HTTP 方法

| 方法 | 用途 | 幂等 | 安全 |
|------|------|------|------|
| GET | 查询 | ✅ | ✅ |
| POST | 创建 / 触发非幂等动作 | ❌ | ❌ |
| PUT | 完整更新 | ✅ | ❌ |
| PATCH | 部分更新 | ❌ | ❌ |
| DELETE | 删除 | ✅ | ❌ |

---

## 3. 通用请求规范

### 3.1 请求头

| Header | 必填 | 说明 |
|--------|------|------|
| `Authorization` | ✅ | `Bearer <jwt>` |
| `Content-Type` | ✅ | `application/json` |
| `X-Request-ID` | ✅ | 客户端生成的 UUID |
| `Traceparent` | 可选 | W3C Trace Context（自动注入） |
| `Idempotency-Key` | POST 必填 | 幂等键（UUID） |
| `X-Channel` | 可选 | `APP`, `IM`, `H5`, `VOICE` |
| `X-Agent-ID` | 可选 | 触发请求的 Agent ID |

### 3.2 请求体规范

```json
{
  "data": { ... },              // 业务字段
  "metadata": {                  // 可选，元数据
    "clientTimestamp": "2026-09-21T10:30:00Z",
    "userLocale": "zh-CN"
  }
}
```

**示例：创建转账订单**
```json
POST /v1/transfers
{
  "data": {
    "fromUserId": "U123456",
    "payee": {
      "name": "小李",
      "phoneNumber": "138****8888"
    },
    "amount": {
      "value": 500.00,
      "currency": "CNY"
    },
    "memo": "聚餐 AA",
    "channel": "AI_AGENT",
    "agentId": "TransferAgent@v1.2"
  },
  "metadata": {
    "clientTimestamp": "2026-09-21T10:30:00Z"
  }
}
```

---

## 4. 通用响应规范

### 4.1 成功响应

```json
HTTP/1.1 200 OK
Content-Type: application/json
X-Request-ID: req-abc123
X-RateLimit-Remaining: 99

{
  "data": { ... },
  "metadata": {
    "requestId": "req-abc123",
    "serverTimestamp": "2026-09-21T10:30:00.123Z",
    "version": "1.0.0"
  }
}
```

### 4.2 错误响应

```json
HTTP/1.1 400 Bad Request
Content-Type: application/json

{
  "error": {
    "code": "VALIDATION_FAILED",
    "message": "转账金额必须为正数",
    "details": [
      { "field": "amount", "reason": "must be greater than 0" }
    ],
    "traceId": "trace-xyz789",
    "requestId": "req-abc123"
  }
}
```

### 4.3 标准错误码

| HTTP | 业务码 | 说明 |
|------|--------|------|
| 400 | `VALIDATION_FAILED` | 请求参数校验失败 |
| 401 | `UNAUTHENTICATED` | 未认证 |
| 403 | `UNAUTHORIZED` | 已认证但权限不足 |
| 404 | `NOT_FOUND` | 资源不存在 |
| 409 | `CONFLICT` | 业务冲突（如已转账） |
| 422 | `BUSINESS_RULE_VIOLATED` | 业务规则违反 |
| 429 | `RATE_LIMITED` | 限流 |
| 500 | `INTERNAL_ERROR` | 服务器内部错误 |
| 502 | `UPSTREAM_ERROR` | 上游服务错误 |
| 503 | `SERVICE_UNAVAILABLE` | 服务暂不可用 |

### 4.4 分页响应

```json
{
  "data": [
    { "id": "...", "amount": { "value": 100, "currency": "CNY" } },
    ...
  ],
  "pagination": {
    "page": 1,
    "limit": 20,
    "total": 234,
    "hasMore": true,
    "nextCursor": "eyJpZCI6IjAxSE1aSzhYUTVKM1c5TjdZ..."
  }
}
```

---

## 5. 关键端点规范

### 5.1 转账

#### POST /v1/transfers（创建转账订单）

```yaml
请求体:
  type: object
  required: [fromUserId, payee, amount]
  properties:
    fromUserId: { type: string }
    payee:
      type: object
      required: []
      properties:
        name: { type: string }
        phoneNumber: { type: string }
        accountId: { type: string }
        memo: { type: string }
    amount:
      type: object
      required: [value, currency]
    scheduledAt: { type: string, format: date-time }  # 定时转账
    memo: { type: string }
    requireHumanConfirm: { type: boolean, default: true }

响应 200:
  data:
    orderId: { type: string }
    status: { enum: [PendingHumanConfirmation, Confirmed, Completed, Failed] }
    requiresHumanConfirm: { type: boolean }
    humanConfirmUrl: { type: string, format: uri }  # APP/IM 推送卡片 URL
    expiresAt: { type: string, format: date-time }
```

#### POST /v1/transfers/{orderId}/confirm（人工确认）

```yaml
请求体:
  type: object
  required: [confirmationToken, biometricVerified]
  properties:
    confirmationToken: { type: string }
    biometricVerified: { type: boolean }
    smsCode: { type: string }

响应 200:
  data:
    orderId: { type: string }
    status: { enum: [Confirmed, Completed] }
    completedAt: { type: string, format: date-time }
```

#### POST /v1/transfers/{orderId}/split（AA 拆分）

```yaml
请求体:
  type: object
  required: [totalAmount, participants]
  properties:
    totalAmount: { type: object }
    participants:
      type: array
      items:
        type: object
        properties:
          name: { type: string }
          phoneNumber: { type: string }
          share: { type: number }  # 占比或固定金额

响应 200:
  data:
    subOrders:  # 子订单列表
      type: array
      items: ...
```

### 5.2 账单

#### GET /v1/bills/{userId}（查询账单）

```yaml
查询参数:
  period: { type: string, example: "2026-09" }  # YYYY-MM
  type: { enum: [monthly, yearly] }

响应 200:
  data:
    billId: { type: string }
    period: { type: string }
    summary:
      totalIncome: { type: object }
      totalExpense: { type: object }
      categoryBreakdown: { type: array, items: ... }
    transactions: { type: array, items: ... }
    anomalies: { type: array, items: ... }
```

#### POST /v1/bills/analyze（触发分析）

```yaml
请求体:
  type: object
  required: [userId, period]
  properties:
    userId: { type: string }
    period: { type: string }

响应 202:
  { description: "分析任务已启动，结果通过 WebSocket 推送" }
```

### 5.3 理财

#### POST /v1/wealth/products/recommend（产品推荐）

```yaml
请求体:
  type: object
  required: [userId, amount]
  properties:
    userId: { type: string }
    amount: { type: object }
    riskPreference: { enum: [Conservative, Moderate, Aggressive] }

响应 200:
  data:
    recommendations:  # 推荐 Top 5
      type: array
      items:
        productId: { type: string }
        name: { type: string }
        riskLevel: { enum: [R1, R2, R3, R4, R5] }
        expectedReturn: { type: number }
        reason: { type: string }  # 推荐理由
    requiresRiskAssessment: { type: boolean }
    assessmentUrl: { type: string }  # 风险测评问卷 URL
```

#### POST /v1/wealth/orders（申购/赎回）

```yaml
请求体:
  type: object
  required: [userId, productId, amount, type]
  properties:
    userId: { type: string }
    productId: { type: string }
    amount: { type: object }
    type: { enum: [Subscribe, Redeem] }

响应 200:
  data:
    orderId: { type: string }
    status: { enum: [PendingHumanConfirmation, Confirmed, Completed] }
    humanConfirmUrl: { type: string }
```

### 5.4 跨场景

#### POST /v1/scenarios（创建跨场景任务）

```yaml
请求体:
  type: object
  required: [userId, scenarioType, parameters]
  properties:
    userId: { type: string }
    scenarioType: { enum: [Birthday, Anniversary, Custom] }
    parameters: { type: object }

响应 202:
  data:
    scenarioId: { type: string }
    tasks:  # 任务列表
      type: array
      items:
        taskId: { type: string }
        actionType: { enum: [LockFunds, OrderFlower, OrderCake, Notify] }
        scheduledAt: { type: string, format: date-time }
```

### 5.5 审计

#### GET /v1/audit/logs（审计日志查询）

```yaml
查询参数:
  traceId: { type: string }
  userId: { type: string }
  agentId: { type: string }
  decisionType: { type: string }
  startTime: { type: string, format: date-time }
  endTime: { type: string, format: date-time }
  page: { type: integer, default: 1 }
  limit: { type: integer, default: 20, max: 100 }

权限: 仅 auditor + admin 角色

响应 200:
  data:  # 审计日志列表
    type: array
    items:
      logId: { type: string }
      traceId: { type: string }
      agentId: { type: string }
      decisionType: { type: string }
      inputs: { type: string }  # JSON
      outputs: { type: string }
      reasoning: { type: string }
      requiresHumanConfirm: { type: boolean }
      humanConfirmed: { type: boolean }
      createdAt: { type: string, format: date-time }
```

---

## 6. 鉴权与限流

### 6.1 JWT Token

```yaml
Header: Authorization: Bearer <jwt>
Claims:
  sub: "U123456"               # 用户 ID
  iat: 1695234567
  exp: 1695238167
  roles: ["customer", "vip"]    # 角色
  permissions: ["transfer:write", "wealth:read"]
  agentId: "TransferAgent@v1.2"  # 触发的 Agent ID
```

### 6.2 限流策略

| 端点 | 限制 |
|------|------|
| `POST /v1/transfers` | 10 次/分钟/用户 |
| `POST /v1/transfers/{id}/confirm` | 5 次/分钟/用户 |
| `POST /v1/wealth/orders` | 5 次/小时/用户 |
| `GET /v1/bills/{userId}` | 60 次/分钟/用户 |
| `POST /v1/scenarios` | 5 次/小时/用户 |

超出限流返回 `429 Too Many Requests`。

---

## 7. OpenAPI 规范

完整 OpenAPI 3.0 文档：

```yaml
openapi: 3.0.3
info:
  title: AI Banking Agent API
  version: 1.0.0
  description: |
    AI Banking Agent System REST API
    详见 docs/02-api/01-rest-api-spec.md
servers:
  - url: https://api.bankagent.com/v1
    description: 生产
  - url: https://api-staging.bankagent.com/v1
    description: 预发
  - url: http://localhost:5000/v1
    description: 本地开发
```

完整 OpenAPI 文件位于 `docs/02-api/openapi.yaml`。

---

## 8. API 版本管理

### 8.1 URL 版本
```
/v1/transfers
/v2/transfers  ← 未来可能
```

### 8.2 不兼容变更处理
1. 发布 v2 前，v1 必须保持 ≥ 6 个月兼容期
2. v2 文档必须列出 v1→v2 的迁移指南
3. 通过 `Sunset` HTTP 头通知客户端即将弃用

### 8.3 字段弃用流程
```
1. 字段标 deprecated，在响应头加 Deprecation: field="amount"
2. 文档标注 deprecated，3 个月后删除
3. 删除前必须发送通知给所有已知客户端
```

---

## 9. 关联文档

- **事件 Schema**：[`02-event-schema.md`](02-event-schema.md)
- **功能开关**：[`03-feature-flags.md`](03-feature-flags.md)
- **编码规范**：[`../03-development/01-coding-standards.md`](../03-development/01-coding-standards.md)