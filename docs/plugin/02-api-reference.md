# API 参考（实测版）

> **说明**：本文件记录**代码里真实存在**的端点。
> 设计层面的完整 API 规范见 [`../02-api/01-rest-api-spec.md`](../02-api/01-rest-api-spec.md)，
> 两者差异见 [`03-implementation-status.md`](03-implementation-status.md)。
> **验证口径**：单元测试 149 项已实测通过；E2E/Stress 的检查项以程序最终输出和 CI 为准。

---

## 1. 宿主服务 :5243

Base URL：`http://localhost:5243`

### 当前 Host 端点矩阵

| 方法 | 路径 | 用途 |
|---|---|---|
| GET | `/health`、`/health/ready`、`/health/live` | 健康、就绪、存活探针 |
| POST | `/api/auth/token` | 获取 JWT |
| GET | `/api/auth/me` | 当前身份 |
| GET | `/api/plugins`、`/api/plugins/agents` | 插件与 Agent 清单 |
| POST | `/api/orchestrate` | Supervisor 编排 |
| GET | `/api/trajectory/{sessionId}`、`/api/trajectory` | 轨迹回放与最近事件 |
| GET | `/api/plugins/compliance`、`/api/plugins/events` | 合规规则与事件总线状态 |
| POST | `/api/plugins/{pluginId}/stop|start` | 插件启停（仅 admin） |
| POST | `/api/chat`、`/api/chat/confirm` | 对话与人工确认 |

### 1.0 身份认证（必须先获取令牌）

#### 获取令牌

```http
POST /api/auth/token
Content-Type: application/json
```

```json
{ "userId": "u_demo01", "password": "demo1234" }
```

```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "tokenType": "Bearer",
  "userId": "u_demo01",
  "role": "user",
  "displayName": "张明",
  "expiresInSeconds": 1800
}
```

**演示账号**

| userId | 密码 | 姓名 | 角色 |
|--------|------|------|------|
| `u_demo01` | `demo1234` | 张明 | user |
| `u_demo02` | `demo1234` | 李华 | user |
| `u_demo03` | `demo1234` | 王芳 | user |
| `staff_01` | `staff1234` | 客服小李 | staff |
| `audit_01` | `audit1234` | 审计员小王 | auditor |
| `admin_01` | `admin1234` | 管理员 | admin |

> **角色由服务端账号表决定**，客户端传入的 `role` 字段会被忽略（防止自助提权）。
> **失败时统一返回 401**，不区分「用户不存在」与「密码错误」，防止账号枚举。

#### 带令牌访问

```http
Authorization: Bearer <token>
```

#### 免认证白名单

`/health`、`/health/ready`、`/health/live` 与 `/api/auth/token` 免认证，其余端点均需令牌。

#### 速率限制

所有端点受**双维度令牌桶**保护：按 IP（防扫描）+ 按身份（防单账号爆破）。

| 档位 | 端点 | 配额（每分钟） | 突发容量 |
|------|------|--------------|---------|
| 严格 | `/api/auth/token` | 5 | `min(BurstCapacity, 5)` |
| 中等 | 含 transfer / confirm / wealth | 20 | `min(BurstCapacity, 20)` |
| 普通 | 业务端点 | 120 | `min(BurstCapacity, 120)` |
| 只读 | `/api/plugins` `/api/trajectory` (GET) | 300 | `min(BurstCapacity, 300)` |
| IP 兜底 | 全部 | 600 | 120 |

超过配额返回 **429 Too Many Requests**：

```json
{
  "code": "RATE_LIMITED",
  "message": "请求过于频繁，请稍后重试",
  "dimension": "identity",
  "retryAfterSeconds": 12
}
```

响应头携带配额信息：

| 响应头 | 说明 |
|-------|------|
| `X-RateLimit-Limit` | 当前窗口配额 |
| `X-RateLimit-Remaining` | 剩余次数 |
| `Retry-After` | 限流触发时的建议重试秒数 |

> 配额可通过 `appsettings.json` 的 `RateLimit` 节调整。
> 未认证请求按「IP + 请求体中的 userId」分桶，避免同一 NAT 出口的用户互相牵连。

#### 查询当前身份

```http
GET /api/auth/me
```

```json
{
  "userId": "u_demo01",
  "role": "user",
  "displayName": "张明",
  "canAudit": false,
  "canAdminister": false
}
```

#### 权限矩阵

| 能力 | user | staff | auditor | admin |
|------|:----:|:-----:|:-------:|:-----:|
| 对话、转账、查卡 | ✅ | ✅ | ✅ | ✅ |
| 代客操作（需填原因） | ❌ | ✅ | ✅ | ✅ |
| 插件启停 | ❌ | ❌ | ❌ | ✅ |
| 审计查询 | ❌ | ❌ | ✅ | ✅ |

#### 越权防护

`/api/chat` 的 `userId` 字段**不会**被信任：

- 令牌身份与请求体 `userId` 不一致 → **403 FORBIDDEN**
- staff/auditor/admin 代客操作 → 必须提供 `impersonationReason`，并写入审计日志

---

### 1.1 健康检查

```http
GET /health
```

```json
{
  "status": "healthy",
  "plugins": 4,
  "ai": { "intentRecognition": "rule", "model": null },
  "database": { "healthy": true, "provider": "Sqlite", "latencyMs": 2 },
  "timestamp": "2026-09-27T10:01:02.4910164+00:00"
}
```

`/health` 会执行数据库真实探活；数据库不健康时返回 503。另有：

```http
GET /health/ready   # 就绪探针，检查数据库，不健康返回 503
GET /health/live    # 存活探针，不检查数据库
```

---

### 1.2 插件清单

```http
GET /api/plugins
```

```json
[
  {
    "id": "banking.transfer",
    "name": "智能转账",
    "version": "1.0.0",
    "description": "处理行内转账、跨行转账，含限额校验、反洗钱筛查与人工回环",
    "scenarios": ["transfer"],
    "featureFlags": ["transfer.enabled", "transfer.auto_confirm"],
    "maxClassification": "L3",
    "dependencies": [],
    "agents": ["TransferAgent"],
    "isActive": true,
    "loadedAt": "2026-09-27T09:57:19Z"
  },
  {
    "id": "banking.card",
    "name": "卡片管理",
    "version": "1.0.0",
    "maxClassification": "L3",
    "dependencies": [
      { "id": "banking.transfer", "minVersion": "1.0.0", "optional": false }
    ],
    "isActive": true
  }
]
```

| 字段 | 说明 |
|------|------|
| `maxClassification` | 该插件处理数据的最高敏感级别，驱动脱敏策略 |
| `featureFlags` | 插件声明的功能开关名（**当前仅声明，未在运行时强制**） |
| `dependencies` | 依赖的插件与最低版本，加载时校验 |
| `agents` | 该插件注册的 Agent 类型 |

---

### 1.3 Agent 路由表

```http
GET /api/plugins/agents
```

```json
[
  { "id": "transfer.agent", "name": "转账执行 Agent", "role": "DomainExpert",
    "intents": ["transfer", "transfer.execute"] },
  { "id": "bill.agent", "name": "账单分析 Agent", "role": "DomainExpert",
    "intents": ["bill", "bill.summary"] },
  { "id": "card.agent", "name": "卡片管理 Agent", "role": "DomainExpert",
    "intents": ["card", "card.status"] }
]
```

> **路由规则**：最长前缀优先。意图 `transfer.execute.retry` 会路由到 `transfer.execute` 而非 `transfer`。

---

### 1.4 合规规则清单

```http
GET /api/plugins/compliance
```

```json
[
  { "ruleId": "transfer.amount.threshold", "version": "v1.5", "scenarios": ["transfer"] },
  { "ruleId": "transfer.self",             "version": "v1.0", "scenarios": ["transfer"] },
  { "ruleId": "aml.large_amount",          "version": "v1.2", "scenarios": ["transfer", "wealth"] },
  { "ruleId": "scenario.permission",       "version": "v1.0", "scenarios": [] }
]
```

---

### 1.5 事件总线状态

```http
GET /api/plugins/events
```

```json
{
  "published": 2,
  "deadLetters": 0,
  "recent": [
    {
      "eventType": "transfer.completed",
      "source": "banking.transfer",
      "occurredAt": "2026-09-27T10:01:10Z",
      "payload": {
        "transaction_no": "TX202609270000086",
        "amount": 30000,
        "from_account": "6222020200000001",
        "to_account": "6222020200000003",
        "balance_after": 219200
      }
    }
  ]
}
```

---

### 1.6 插件启停（热插拔）

```http
POST /api/plugins/banking.card/stop
POST /api/plugins/banking.card/start
```

```json
{ "pluginId": "banking.card", "action": "stopped" }
```

> 停用是逻辑停用（保留程序集，可再次启动）。程序集卸载需调用 `PluginRegistry.UnloadPlugin()`，当前未开放 HTTP 端点。

---

### 1.7 对话入口

```http
POST /api/chat
Content-Type: application/json; charset=utf-8
```

**请求**

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `message` | string | ✅ | 用户自然语言输入 |
| `userId` | string | ✅ | 用户 ID |
| `sessionId` | string | | 会话 ID，**同时作为幂等键** |
| `slots` | object | | 预抽取槽位，避免 Agent 重复解析 |

```json
{
  "message": "给6222020200000003转账30000元",
  "userId": "u_demo01",
  "sessionId": "unique-session-001",
  "slots": { "to_account": "6222020200000003", "amount": 30000 }
}
```

**响应**

| 字段 | 说明 |
|------|------|
| `intent` | 识别出的意图 |
| `success` | 是否成功 |
| `content` | 自然语言回复 |
| `requiresHumanInLoop` | **是否需要人工确认** |
| `sideEffectCommitted` | **是否已产生不可回滚副作用** |
| `errorCode` / `errorMessage` | 失败原因 |
| `data` | 结构化数据（**已脱敏**） |
| `elapsedMs` | 耗时 |

**响应示例 A — 人工回环**

```json
{
  "echo": "给6222020200000003转账30000元",
  "intent": "transfer.execute",
  "success": true,
  "content": "单笔金额 30,000.00 元超过自动放行上限 5,000.00 元，需人工确认",
  "resultIntent": null,
  "requiresHumanInLoop": true,
  "sideEffectCommitted": false,
  "errorCode": null,
  "data": {
    "amount": 30000,
    "from_account": "6222020200000001",
    "to_account": "6222020200000003",
    "rule_id": "transfer.amount.threshold"
  },
  "confidence": 1,
  "elapsedMs": 42.3
}
```

**响应示例 B — 执行成功（已脱敏）**

```json
{
  "success": true,
  "content": "转账成功，金额 800.00 元，流水号 TX202609270000084",
  "requiresHumanInLoop": false,
  "sideEffectCommitted": true,
  "data": {
    "transaction_no": "TX202609270000084",
    "amount": 800,
    "balance_after": 249200,
    "human_approved": false
  }
}
```

**响应示例 C — 合规拒绝**

```json
{
  "success": false,
  "intent": "transfer.execute",
  "errorCode": "COMPLIANCE_REJECTED",
  "errorMessage": "禁止向本人账户转账",
  "sideEffectCommitted": false
}
```

**已知错误码**

| 错误码 | 含义 |
|--------|------|
| `AMOUNT_MISSING` | 未识别到金额 |
| `TARGET_MISSING` | 未识别到收款账户 |
| `COMPLIANCE_REJECTED` | 合规规则拒绝 |
| `AGENT_EXCEPTION` | Agent 执行异常 |
| `NO_AGENT` | 无匹配 Agent（意图未路由） |
| `TRANSFER_FAILED` | 核心系统拒绝 |

---

### 1.8 人工确认（完成 HITL 闭环）

```http
POST /api/chat/confirm
```

```json
{
  "userId": "u_demo01",
  "sessionId": "unique-session-001",
  "amount": 30000,
  "slots": {
    "to_account": "6222020200000003",
    "amount": 30000,
    "from_account": "6222020200000001"
  }
}
```

```json
{
  "success": true,
  "content": "转账成功，金额 30,000.00 元，流水号 TX202609270000086",
  "committed": true,
  "data": { "transaction_no": "TX202609270000086", "amount": 30000,
            "balance_after": 219200, "human_approved": true }
}
```

> **必须复用原 `sessionId`**，以便确认请求命中原操作的幂等语义；
> 不要把同一 `sessionId` 用于另一笔转账。
> 该端点会写一条 `human.approval` 审计记录。

---

## 2. 模拟银行 :5200

Base URL：`http://localhost:5200`
前缀：`/api/corebank/v1`

### 2.1 账户

```http
GET /api/corebank/v1/accounts?userId=u_demo01
GET /api/corebank/v1/accounts/6222020200000001/balance
GET /api/corebank/v1/accounts/6222020200000001/limit
GET /api/corebank/v1/beneficiaries?keyword=李华
```

**余额响应**

```json
{
  "accountNo": "6222020200000001",
  "userId": "u_demo01",
  "holderName": "张明",
  "accountType": "储蓄卡",
  "balance": 250000.00,
  "status": "正常",
  "currency": "CNY",
  "dailyTransferLimit": 500000
}
```

---

### 2.2 转账

```http
POST /api/corebank/v1/transfers
```

```json
{
  "userId": "u_demo01",
  "fromAccountNo": "6222020200000001",
  "toAccountNo": "6222020200000003",
  "amount": 1000,
  "currency": "CNY",
  "remark": "测试转账"
}
```

**成功响应**

```json
{
  "txNo": "TX202609270000084",
  "fromAccountNo": "6222020200000001",
  "toAccountNo": "6222020200000003",
  "amount": 1000,
  "balanceAfter": 249000,
  "completedAt": "2026-09-27T17:47:11Z"
}
```

**错误响应**

```json
{
  "code": "INSUFFICIENT_FUNDS",
  "message": "付款账户余额不足，当前余额 250000.00 元，需 999999999.00 元"
}
```

| 错误码 | 触发条件 |
|--------|---------|
| `INSUFFICIENT_FUNDS` | 余额不足 |
| `DAILY_LIMIT_EXCEEDED` | 超单日限额 |
| `SAME_ACCOUNT` | 收付款账户相同 |
| `ACCOUNT_NOT_FOUND` | 账户不存在 |
| `ACCOUNT_STATUS_ABNORMAL` | 账户冻结/挂失 |
| `INVALID_AMOUNT` | 金额 ≤ 0 |

**校验顺序**：账户存在 → 账户状态 → 自转 → 金额合法性 → 余额充足 → 单日限额

---

### 2.3 流水

```http
GET /api/corebank/v1/transactions?userId=u_demo01&page=1&pageSize=20
GET /api/corebank/v1/transactions?userId=u_demo01&category=餐饮&from=&to=
GET /api/corebank/v1/transactions/TX202609270000084
```

---

### 2.4 账单

```http
GET /api/corebank/v1/statements/6222020200000001?month=2026-09
```

```json
{
  "accountNo": "6222020200000001",
  "year": 2026,
  "month": 9,
  "totalIncome": 18937.50,
  "totalExpense": 15907.46,
  "transactionCount": 9,
  "categories": [
    { "category": "住房", "amount": 3200 },
    { "category": "餐饮", "amount": 1846.20 }
  ],
  "dailyTrend": [ { "day": 1, "amount": 120.50 } ]
}
```

> **注意**：核心系统**不返回**分类占比。Base 的 `CoreBankClient` 按 `amount / totalExpense` 计算并保留 4 位小数。

---

### 2.5 卡片

```http
GET  /api/corebank/v1/cards?userId=u_demo01
GET  /api/corebank/v1/cards/6222020200000001
POST /api/corebank/v1/cards/6222020200000001/status
POST /api/corebank/v1/cards/6222020200000001/limit
```

**状态变更**

```json
{ "newStatus": "挂失", "reason": "用户申请" }
```

| 状态 | 说明 |
|------|------|
| `正常` | 可用 |
| `挂失` | 不可交易，需解挂 |
| `冻结` | 被风控/司法冻结 |

> 英文别名 `normal` / `lost` / `frozen` 同样接受。
> 账户处于挂失/冻结时，转账返回 `ACCOUNT_STATUS_ABNORMAL`。

---

### 2.6 理财

```http
GET  /api/corebank/v1/products?riskLevel=R2
GET  /api/corebank/v1/products/WP001
POST /api/corebank/v1/wealth/subscribe
```

```json
{ "userId": "u_demo01", "productCode": "WP002", "amount": 5000 }
```

| 错误码 | 触发条件 |
|--------|---------|
| `RISK_MISMATCH` | 用户风险等级低于产品 |
| `BELOW_MIN_INVESTMENT` | 低于起投金额 |
| `PRODUCT_NOT_ON_SALE` | 产品已停售 |

---

### 2.7 运维

```http
GET /health
GET /api/corebank/v1/_admin/stats
GET /openapi/v1.json          # OpenAPI 3.0 文档，17 paths / 22 schemas
```

---

## 3. 故障注入

任意请求加头：

```http
X-Mock-Scenario: insufficient_funds | daily_limit_exceeded | account_frozen | timeout | downstream_error
```

| 值 | 效果 |
|----|------|
| `success` | 默认，正常处理 |
| `insufficient_funds` | 强制 400 `INSUFFICIENT_FUNDS` |
| `daily_limit_exceeded` | 强制 400 `DAILY_LIMIT_EXCEEDED` |
| `account_frozen` | 强制 400 `ACCOUNT_FROZEN` |
| `timeout` | 延迟 3 秒后正常响应 |
| `downstream_error` | 强制 503 |

仅在 `/transfers` 与 `/wealth/subscribe` 生效。

---

## 4. 调用示例（PowerShell）

> **中文编码**：PowerShell 5.1 需用 UTF-8 字节，否则中文乱码。
> **鉴权**：除健康探针与 `/api/auth/token` 外，均需 `Authorization: Bearer <token>`。

```powershell
# 1. 先取令牌
$login = @{ userId = "u_demo01"; password = "demo1234" } | ConvertTo-Json
$token = (Invoke-RestMethod http://localhost:5243/api/auth/token -Method Post `
  -ContentType "application/json" -Body $login).token

# 2. 带令牌访问
$headers = @{ Authorization = "Bearer $token" }
$b = [System.Text.Encoding]::UTF8.GetBytes('{"message":"\u6211\u6709\u54ea\u4e9b\u5361","userId":"u_demo01"}')
Invoke-RestMethod http://localhost:5243/api/chat -Method Post `
  -Headers $headers -ContentType "application/json; charset=utf-8" -Body $b
```

**或者**用测试项目（已处理编码问题）：

```powershell
# 在仓库根目录执行；先启动 MockBank(:5200) 与 Host(:5243)
dotnet run --project src/E2ETest/E2ETest.csproj -- `
  http://localhost:5243 http://localhost:5200
```

---

## 5. 演示数据

### 5.1 客户

| 用户 ID | 姓名 | 风险等级 |
|---------|------|---------|
| `u_demo01` | 张明 | R3 |
| `u_demo02` | 李华 | R2 |
| `u_demo03` | 王芳 | R4 |

### 5.2 账户

| 账号 | 归属 | 类型 | 余额/额度 |
|------|------|------|----------|
| `6222020200000001` | 张明 | 储蓄卡 | 250,000.00 |
| `6222020200000002` | 张明 | 信用卡 | 额度 50,000 / 已用 12,800 |
| `6222020200000003` | 李华 | 储蓄卡 | 88,000.50 |
| `6222020200000004` | 李华 | 信用卡 | 额度 30,000 / 已用 3,200 |
| `6222020200000005` | 王芳 | 储蓄卡 | 152,000.00 |
| `6222020200000006` | 王芳 | 信用卡 | 额度 80,000 / 已用 45,000 |

### 5.3 理财产品

| 代码 | 名称 | 风险 | 年化 | 起投 | 期限 |
|------|------|------|------|------|------|
| WP001 | 短债现金管理 | R1 | 2.15% | 100 | 30 天 |
| WP002 | 稳健增利90天 | R2 | 3.20% | 1000 | 90 天 |
| WP003 | 安心纯债180天 | R2 | 3.85% | 5000 | 180 天 |
| WP004 | 平衡优选混合 | R3 | 5.10% | 10000 | 365 天 |
| WP005 | 成长精选30天 | R3 | 4.25% | 1000 | 30 天 |
| WP006 | 进取科技主题 | R4 | 8.50% | 50000 | 365 天 |

---

## 6. 与设计文档的差异

| 端点 | 设计文档 | 实际实现 | 说明 |
|------|---------|---------|------|
| `/api/chat` SSE 流式 | ✅ 设计 | ❌ 未实现 | 当前一次性返回 |
| `/api/v1/conversations` | ✅ 设计 | ❌ 未实现 | 由 `/api/chat` 简化替代 |
| `/api/v1/transfers` | ✅ 设计 | ❌ 未实现 | 内部走 `ICoreBankClient` |
| `/api/v1/users/me/*` | ✅ 设计 | ❌ 未实现 | 用户权利响应接口未做 |
| `/api/plugins/*` | ❌ 未设计 | ✅ 已实现 | 插件管理是新增能力 |

设计文档的完整 API 规范在 [`../02-api/01-rest-api-spec.md`](../02-api/01-rest-api-spec.md)，接入真实业务时需补齐上述端点。
