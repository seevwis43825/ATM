# 接口契约文档 — AI Banking Agent

> **定位**：本文区分 Current（当前代码）与 Target（目标规范）。当前 Host 端点以
> `BankingAgent.Host.Program` 的 Minimal API 映射为事实源；Target `/v1/*` 资源 API
> 见 [`../02-api/01-rest-api-spec.md`](../02-api/01-rest-api-spec.md)。
> 可运行示例见 [`../plugin/02-api-reference.md`](../plugin/02-api-reference.md)。

---

## 1. Current Host 端点矩阵

Base URL：`http://localhost:5243`

| 方法 | 路径 | 认证 | 最小角色 | 说明 |
|---|---|---:|---|---|
| GET | `/health` | 公开 | — | 完整健康检查，真实探测数据库；不健康返回 503 |
| GET | `/health/ready` | 公开 | — | 就绪探针，探测数据库；不健康返回 503 |
| GET | `/health/live` | 公开 | — | 存活探针，不访问数据库 |
| POST | `/api/auth/token` | 公开 | — | 演示账号换取 JWT |
| GET | `/api/auth/me` | 是 | 已认证 | 当前身份与能力 |
| GET | `/api/plugins` | 是 | 已认证 | 插件清单与 Manifest 元数据 |
| GET | `/api/plugins/agents` | 是 | 已认证 | Agent、意图与关键词 |
| POST | `/api/orchestrate` | 是 | 已认证 | Supervisor 编排入口 |
| GET | `/api/trajectory/{sessionId}` | 是 | 已认证 | 指定会话轨迹；无记录返回 404 |
| GET | `/api/trajectory?limit=` | 是 | 已认证 | 最近轨迹，默认最多 200 条 |
| GET | `/api/plugins/compliance` | 是 | 已认证 | 当前合规规则 |
| GET | `/api/plugins/events` | 是 | 已认证 | `InMemoryEventBus` 计数、历史与死信 |
| POST | `/api/plugins/{pluginId}/stop` | 是 | admin | 逻辑停用插件 |
| POST | `/api/plugins/{pluginId}/start` | 是 | admin | 启用插件 |
| POST | `/api/chat` | 是 | 已认证 | 单跳对话入口 |
| POST | `/api/chat/confirm` | 是 | 已认证 | 转账人工确认 |

公开白名单由 `Program.IsPublic` 定义：所有 `/health*` 探针与 `/api/auth/token`。

## 2. Current MockBank 端点矩阵

Base URL：`http://localhost:5200`，业务前缀为 `/api/corebank/v1`。MockBank 是开发/演示模拟器。

| 方法 | 路径 | 符号来源 |
|---|---|---|
| GET | `/health` | `OpsEndpoints.GetHealth` |
| GET | `/api/corebank/v1/_admin/stats` | `OpsEndpoints.GetAdminStats` |
| GET | `/openapi/v1.json` | `OpsEndpoints.GetOpenApiDocument` |
| GET | `/api/corebank/v1/accounts` | `AccountEndpoints.GetAccounts` |
| GET | `/api/corebank/v1/accounts/{accountNo}/balance` | `AccountEndpoints.GetBalance` |
| GET | `/api/corebank/v1/accounts/{accountNo}/limit` | `AccountEndpoints.GetLimit` |
| GET | `/api/corebank/v1/beneficiaries?keyword=` | `TransferEndpoints.GetBeneficiaries` |
| POST | `/api/corebank/v1/transfers` | `TransferEndpoints.PostTransfer` |
| GET | `/api/corebank/v1/transactions` | `TransferEndpoints.GetTransactions` |
| GET | `/api/corebank/v1/transactions/{txNo}` | `TransferEndpoints.GetTransaction` |
| GET | `/api/corebank/v1/statements/{accountNo}` | `StatementEndpoints.GetMonthlyStatement` |
| GET | `/api/corebank/v1/cards` | `CardEndpoints.GetCards` |
| GET | `/api/corebank/v1/cards/{cardNo}` | `CardEndpoints.GetCard` |
| POST | `/api/corebank/v1/cards/{cardNo}/status` | `CardEndpoints.PostCardStatus` |
| POST | `/api/corebank/v1/cards/{cardNo}/limit` | `CardEndpoints.PostCardLimit` |
| GET | `/api/corebank/v1/products` | `WealthEndpoints.GetProducts` |
| GET | `/api/corebank/v1/products/{code}` | `WealthEndpoints.GetProduct` |
| POST | `/api/corebank/v1/wealth/subscribe` | `WealthEndpoints.PostSubscribe` |

插件通过 `ICoreBankClient` 访问 MockBank/真实 CBS，不直接引用 MockBank DTO。

## 3. 健康检查契约

`GET /health` 调用 `DatabaseInitializer.CheckHealthAsync` 做数据库真实探活，并返回 Provider、
数据库名、延迟、已应用/待应用 Migration 与错误信息。健康时返回 200：

```json
{
  "status": "healthy",
  "plugins": 4,
  "ai": { "intentRecognition": "rule", "model": null },
  "database": {
    "healthy": true,
    "provider": "Sqlite",
    "database": "main",
    "latencyMs": 2,
    "appliedMigrations": 0,
    "pendingMigrations": [],
    "error": null
  },
  "timestamp": "2026-10-01T00:00:00Z"
}
```

数据库不健康时结构不变，`status = "unhealthy"` 且 HTTP 503。

- `/health/ready`：数据库可用返回 200；不可用返回 503，并带 `reason`。
- `/health/live`：进程存活即返回 200，不访问数据库。

## 4. 认证与授权

`TokenService` 使用 HS256。`DemoCredentials` 仅供演示；角色由服务端账号表决定，
请求中的 `role` 被忽略。生产必须替换为统一身份认证与 MFA/OTP。

- 普通已认证用户可访问对话、编排、轨迹和只读插件端点。
- 插件启停要求 `CurrentPrincipal.CanAdminister`（admin）。
- `/api/chat` 的实际用户取自令牌；普通用户提交不同 `userId` 返回 403。
- staff/auditor/admin 代客操作必须提供 `impersonationReason`，并写审计。
- `/api/chat/confirm` 同样校验确认者身份。

## 5. 对话契约

### 5.1 `POST /api/chat`

| 请求字段 | 类型 | 说明 |
|---|---|---|
| `message` | string | 自然语言输入 |
| `userId` | string | 仅用于本人一致性/代客目标校验，不是身份来源 |
| `sessionId` | string | 当前转账幂等键来源；每笔操作应唯一 |
| `slots` | object | 可选预抽取槽位 |
| `impersonationReason` | string | 代客时必填 |

`ChatResponse` 字段：`echo`、`intent`、`intentSource`、`success`、`content`、
`resultIntent`、`requiresHumanInLoop`、`sideEffectCommitted`、`errorCode`、
`errorMessage`、`data`、`confidence`、`elapsedMs`。字段不重复定义。

业务失败仍可能返回 HTTP 200；客户端必须检查 `success` 与 `sideEffectCommitted`。

### 5.2 `POST /api/chat/confirm`

当前只确认转账。`sessionId` 应复用原待确认请求；端点设置 `slots.confirmed = true`
后路由到 `transfer.execute`。响应字段为 `success`、`content`、`committed`、`data`、`error`，
目前与 `ChatResponse` 尚未统一。

## 6. 编排与轨迹

- `POST /api/orchestrate`：接收 `ChatRequest`，由 `AgentOrchestrator` 与 `AdaptiveStrategy`
  执行 Direct、Parallel、Sequential 或 HumanGate 步骤。
- `GET /api/trajectory/{sessionId}`：返回指定会话轨迹。
- `GET /api/trajectory?limit=`：返回最近轨迹。

轨迹当前由 `InMemoryTrajectoryLog` 保存，宿主重启后丢失。

## 7. 插件、事件与 FeatureFlag

- 当前 4 个插件：transfer、bill、card、wealth。
- Wealth 只读产品查询/推荐/余额已实现，申购赎回未实现。
- 当前事件契约是 `DomainEvent`，总线是 `InMemoryEventBus`，主要事件为 `transfer.completed`。
- Manifest 的 `featureFlags` 仅声明；运行时强制服务未实现，不能作为安全控制。

## 8. 幂等性

转账使用 `AgentRequest.SessionId`（为空时生成 GUID）作为 `TransferRecord.IdempotencyKey`，
数据库有唯一索引；串行重复请求返回原流水。测试套件每次运行生成唯一 sessionId，连续重跑无需清库。

已知边界：同一 `sessionId` 不应复用于不同转账；并发同键仍需持久化抢占；
`CoreBankClient` 尚未把幂等键传给 CBS。

## 9. 限流契约

`RateLimitingMiddleware` 位于鉴权之前，做 IP 与身份键双维度令牌桶：登录按
`客户端 IP + 请求体 userId` 分桶；带令牌请求按 Authorization 头哈希分桶。

| 档位 | 匹配 | 默认每分钟 | 桶容量 |
|---|---|---:|---|
| Strict | `/api/auth/token` | 5 | `min(BurstCapacity, 5)` |
| Standard | transfer/confirm/wealth；插件写操作 | 20 | `min(BurstCapacity, 20)` |
| ReadOnly | GET `/api/plugins*`、`/api/trajectory*` | 300 | `min(BurstCapacity, 300)` |
| Relaxed | 其他业务端点 | 120 | `min(BurstCapacity, 120)` |
| IP 兜底 | 全部受限请求 | 600 | `BurstCapacity * 4` |

全局 `BurstCapacity` 只能收紧业务档位，不能放宽认证档。触发时返回 HTTP 429、
`Retry-After`、`X-RateLimit-*` 和 `RATE_LIMITED` 响应体。

## 10. 审计、加密与 Migration

- 审计使用 HMAC 链式签名，双写 JSONL 与独立审计数据库；异地 WORM 待实现。
- `TransferRecord` 的 L3 字段已标注 `[Encrypted]`，字段加密转换器已接入。
- 基线 Migration、设计时插件发现与 Schema 初始化已存在。

## 11. Current 与 Target 边界

Target 中的 `/v1/transfers`、`/v1/bills`、`/v1/wealth/orders`、`/v1/audit/logs`
当前没有映射到 Host。当前客户端使用 §1 的端点。MockBank 有 `/openapi/v1.json`；Host
当前没有 OpenAPI 端点，Target 落地后应生成契约并做 diff。

## 12. 关联文档

- [UML 图集](01-uml-diagrams.md)
- [实现阶段 ADR](02-architecture-decision-record.md)
- [实测 API 参考](../plugin/02-api-reference.md)
- [实现状态](../plugin/03-implementation-status.md)
- [Target REST API](../02-api/01-rest-api-spec.md)
- [Current vs Target 事件](../02-api/02-event-schema.md)
- [Current vs Target FeatureFlag](../02-api/03-feature-flags.md)
