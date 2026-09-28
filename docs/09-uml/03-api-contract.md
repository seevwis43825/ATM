# 接口契约文档 — AI Banking Agent

> **定位**：本文是**结构化契约**——字段级 Schema、错误码全表、鉴权矩阵、版本化与幂等性/限流约定。
> 可直接跑通的实测 API 清单见 [`../plugin/02-api-reference.md`](../plugin/02-api-reference.md)（含 PowerShell 调用示例与演示数据）。
> 设计层面的完整 API 规范见 [`../02-api/01-rest-api-spec.md`](../02-api/01-rest-api-spec.md)。
>
> **所有字段与错误码均来自源码核实**，未实现的部分一律显式标注 `【未实现】`。
> **最后核对**：2026-09-27

---

## 0. 目录

| 章节 | 内容 |
|------|------|
| [1](#1-端点总览) | 端点总览（11 个 Host 端点 + 17 个 CoreBank 端点） |
| [2](#2-通用约定) | 通用约定：Base URL、编码、鉴权头、响应包裹、耗时 |
| [3](#3-错误码全表) | 错误码全表（HTTP 状态码 + 业务码 + 触发条件 + 客户端应对） |
| [4](#4-鉴权要求矩阵) | 鉴权要求矩阵（逐端点 × 角色） |
| [5](#5-请求响应-schema) | 请求/响应 Schema（逐端点字段表） |
| [6](#6-幂等性契约) | 幂等性契约 |
| [7](#7-限流契约) | 限流契约（**已实现**，双维度令牌桶） |
| [8](#8-版本化策略) | 版本化策略建议 |
| [9](#9-与设计文档的差异) | 与设计文档的差异（未实现端点清单） |
| [10](#10-本次核对说明) | 本次核对说明（源码变动同步记录） |

---

## 1. 端点总览

### 1.1 BankingAgent.Host（`http://localhost:5243`）

| # | 方法 | 路径 | 认证 | 最小角色 | 幂等 | 源码 |
|---|------|------|:----:|----------|:----:|------|
| 1 | GET | `/health` | ❌ 公开 | — | 是 | `Program.cs:119` |
| 2 | POST | `/api/auth/token` | ❌ 公开 | — | 否 | `Program.cs:127` |
| 3 | GET | `/api/auth/me` | ✅ | 任意已认证 | 是 | `Program.cs:157` |
| 4 | GET | `/api/plugins` | ✅ | 任意已认证 | 是 | `Program.cs:172` |
| 5 | GET | `/api/plugins/agents` | ✅ | 任意已认证 | 是 | `Program.cs:193` |
| 6 | GET | `/api/plugins/compliance` | ✅ | 任意已认证 | 是 | `Program.cs:296` |
| 7 | GET | `/api/plugins/events` | ✅ | 任意已认证 | 是 | `Program.cs:307` |
| 8 | POST | `/api/plugins/{pluginId}/stop` | ✅ | **admin** | 否 | `Program.cs:322` |
| 9 | POST | `/api/plugins/{pluginId}/start` | ✅ | **admin** | 否 | `Program.cs:331` |
| 10 | POST | `/api/chat` | ✅ | 任意已认证 | **条件幂等** | `Program.cs:341` |
| 11 | POST | `/api/chat/confirm` | ✅ | 任意已认证 | **条件幂等** | `Program.cs:418` |
| 12 | POST | `/api/orchestrate` | ✅ | 任意已认证 | ❌ | `Program.cs:206` |
| 13 | GET | `/api/trajectory/{sessionId}` | ✅ | 任意已认证 | 是 | `Program.cs:253` |
| 14 | GET | `/api/trajectory?limit=` | ✅ | 任意已认证 | 是 | `Program.cs:278` |

> **HTTP 管道顺序**（`Program.cs:85` 与 `Program.cs:87`）：
> `RateLimitingMiddleware` → 鉴权中间件 → 端点。
> 限流在**鉴权之前**，所以限流中间件读不到 `ctx.Items["Principal"]`，
> 已认证请求的身份维度实际退化为「`Authorization` 头的 SHA-256 哈希」——
> 由于 JWT 在 30 分钟内不变，效果接近按用户，但对同一用户的多个令牌会分成不同桶。详见 [§7.4](#74-已发现的限流实现问题)。

**免认证白名单**：仅 `/health` 与 `/api/auth/token`（`Program.cs:486-489`）。
白名单用 `StartsWith` 前缀匹配（`Program.cs:487-489`），新增端点时注意不要误放行。

### 1.2 MockBank.Api（`http://localhost:5200`，前缀 `/api/corebank/v1`）

**仅开发/演示环境存在**，生产环境由真实 CBS 替代。插件通过 `ICoreBankClient` 访问，**不直接调用这些端点**。

| # | 方法 | 路径 | 源码 |
|---|------|------|------|
| 1 | GET | `/health` | `OpsEndpoints.cs:31` |
| 2 | GET | `/api/corebank/v1/_admin/stats` | `OpsEndpoints.cs:36` |
| 3 | GET | `/openapi/v1.json` | `OpsEndpoints.cs:41` |
| 4 | GET | `/api/corebank/v1/accounts` | `AccountEndpoints.cs:22` |
| 5 | GET | `/api/corebank/v1/accounts/{accountNo}/balance` | `AccountEndpoints.cs:27` |
| 6 | GET | `/api/corebank/v1/accounts/{accountNo}/limit` | `AccountEndpoints.cs:32` |
| 7 | POST | `/api/corebank/v1/transfers` | `TransferEndpoints.cs:24` |
| 8 | GET | `/api/corebank/v1/transactions` | `TransferEndpoints.cs:31` |
| 9 | GET | `/api/corebank/v1/transactions/{txNo}` | `TransferEndpoints.cs:36` |
| 10 | GET | `/api/corebank/v1/statements/{accountNo}` | `StatementEndpoints.cs:22` |
| 11 | GET | `/api/corebank/v1/cards` | `CardEndpoints.cs:22` |
| 12 | GET | `/api/corebank/v1/cards/{cardNo}` | `CardEndpoints.cs:27` |
| 13 | POST | `/api/corebank/v1/cards/{cardNo}/status` | `CardEndpoints.cs:32` |
| 14 | POST | `/api/corebank/v1/cards/{cardNo}/limit` | `CardEndpoints.cs:39` |
| 15 | GET | `/api/corebank/v1/products` | `WealthEndpoints.cs:23` |
| 16 | GET | `/api/corebank/v1/products/{code}` | `WealthEndpoints.cs:28` |
| 17 | POST | `/api/corebank/v1/wealth/subscribe` | `WealthEndpoints.cs:33` |

> MockBank 侧**无鉴权**。它的定位是"核心系统模拟器"，不是对外服务，
> 生产部署时不应暴露到网络。真实 CBS 的鉴权由 `CoreBankOptions.ServiceToken`（`CoreBankClient.cs:24`）承担，
> 但该字段**当前未在 `CoreBankClient` 中被使用**（`CoreBankClient.cs:59-108` 未附加任何认证头）。

---

## 2. 通用约定

| 约定 | 值 | 依据 |
|------|-----|------|
| 请求体 Content-Type | `application/json; charset=utf-8` | 中文必须显式 UTF-8，否则 PowerShell 5.1 会乱码 |
| 响应 Content-Type | `application/json`（`WriteAsJsonAsync` 默认） | `Program.cs:112` |
| JSON 命名风格 | **camelCase**（ASP.NET Core 默认 `JsonSerializerDefaults.Web`） | `Program.cs:410` 返回的 `ChatResponse` 字段即 `echo`/`intent`/`success` |
| 金额类型 | JSON number，精度 18,2 | `TransferPlugin.cs:81` `HasPrecision(18, 2)` |
| 时间格式 | ISO 8601（`DateTimeOffset`，带时区偏移） | `AuditEvent.Timestamp`、`ChatResponse` 无时间字段但事件有 |
| 鉴权头 | `Authorization: Bearer <token>` | `Program.cs:91-92`（容忍无 `Bearer ` 前缀的裸 token，`TokenService.cs:81-83`） |
| 令牌有效期 | 1800 秒（30 分钟） | `JwtOptions.cs:21`、`Program.cs:152` |
| 时钟偏移容忍 | 30 秒 | `TokenService.cs:96` |
| 核心系统超时 | 15 秒 | `CoreBankOptions.TimeoutSeconds`，`Host/appsettings.json:18` |
| 业务失败 HTTP 状态 | **200**（错误在响应体的 `success`/`errorCode` 里） | ⚠️ 见 [3.1](#31-最容易被忽略的约定) |

### 2.1 错误响应体的两种形态

| 服务 | 形态 | 示例 |
|------|------|------|
| Host | `{ "code": "...", "message": "..." }` | `Program.cs:112-116`（401）、`Program.cs:482-484`（403） |
| Host（限流） | `{ "code", "message", "dimension", "retryAfterSeconds" }` | `RateLimitingMiddleware.cs:133-134`（429） |
| MockBank | `{ "code", "message", "details", "traceId" }` | `ErrorContracts.cs:10-14` |

⚠️ **字段名一致（`code` / `message`）但结构不同**：Host 不返回 `details` 与 `traceId`；
限流响应多出 `dimension` 与 `retryAfterSeconds`。客户端若要统一处理，需要容错。

---

## 3. 错误码全表

### 3.1 最容易被忽略的约定

> **Host 的业务失败一律返回 HTTP 200。**
> `/api/chat` 的合规拒绝、核心系统拒绝、参数缺失、无匹配 Agent，HTTP 状态码都是 `200`，
> 错误信息在响应体的 `success = false` 与 `errorCode` 里。
>
> **客户端必须检查 `success` 字段，不能只看状态码。**
> 这是当前实现最容易导致"UI 显示成功但实际失败"的坑。

HTTP 非 200 的场景**只有 6 种**：

| 状态码 | 场景 | 响应体 |
|:------:|------|--------|
| 400 | `/api/auth/token` 的 `userId` 或 `password` 为空 | `{code: "INVALID_REQUEST", ...}`（`Program.cs:132`） |
| 400 | `/api/chat` 代客操作未提供 `impersonationReason` | `{code: "IMPERSONATION_REASON_REQUIRED", ...}`（`Program.cs:363-367`） |
| 401 | 缺失 / 无效 / 过期令牌；或密码错误 | `{code: "UNAUTHORIZED", ...}`（`Program.cs:112-116`）/ 空体（`Program.cs:138`） |
| 403 | 越权（令牌 userId ≠ 请求体 userId 且无代客权限） | `{code: "FORBIDDEN", ...}`（`Program.cs:482-484`） |
| 404 | 插件启停时 `pluginId` 不存在 | 空体（`Program.cs:328`、`Program.cs:337`） |
| 404 | `/api/trajectory/{sessionId}` 会话轨迹不存在 | `{code: "SESSION_NOT_FOUND"}`（`Program.cs:257`） |
| **429** | **触发限流**（`RateLimit:Enabled = true` 时默认开启） | `{code: "RATE_LIMITED", message, dimension, retryAfterSeconds}`（`RateLimitingMiddleware.cs:130-134`） |

### 3.2 Host 业务错误码（出现在 `/api/chat` 与 `/api/chat/confirm` 的 `errorCode`）

| 错误码 | HTTP | 触发条件 | `sideEffectCommitted` | 客户端应对 | 源码 |
|--------|:----:|----------|:---------------------:|------------|------|
| `AMOUNT_MISSING` | 200 | 槽位与正则都没识别出金额 | false | 追问用户具体金额 | `TransferAgent.cs:70` |
| `TARGET_MISSING` | 200 | 未识别出收款账号 | false | 追问收款账号 | `TransferAgent.cs:75` |
| `NO_ACCOUNT` | 200 | 用户没有状态为「正常」的账户 | false | 引导用户先开户/解冻 | `TransferAgent.cs:85` |
| `COMPLIANCE_REJECTED` | 200 | 任一合规规则 `Allowed = false` | false | **不可重试**；展示 `errorMessage` | `TransferAgent.cs:122`、`CardPlugin.cs:123` |
| `AGENT_EXCEPTION` | 200 | Agent 内部抛未捕获异常 | false | 展示通用错误 + 引导转人工 | `AgentBase.cs:96` |
| `NO_AGENT` | 200 | 意图无匹配 Agent（如 `account.balance`、`unknown`） | false | 转人工 | `AgentRouter.cs:56` |
| `TRANSFER_FAILED` | 200 | 核心系统返回失败但未带错误码 | false | 可重试 | `TransferAgent.cs:191` |
| `CORE_BANK_UNAVAILABLE` | 200 | 核心系统网络异常 / 超时（`HttpRequestException` / `TaskCanceledException`） | false | **可重试**（幂等键保护） | `CoreBankClient.cs:103` |
| `STATEMENT_NOT_FOUND` | 200 | 指定年月的账单不存在 | false | 改查询月份 | `BillPlugin.cs:84` |
| `NO_CARD` | 200 | 用户未绑定银行卡 | false | 引导绑卡 | `CardPlugin.cs:83` |
| `CARD_MISSING` | 200 | 挂失/冻结操作未提供 `card_no` 槽位 | false | 追问卡号 | `CardPlugin.cs:105` |
| `CARD_NOT_FOUND` | 200 | 核心系统返回 404 | false | 核对卡号 | `CardPlugin.cs:131` |
| `ACCOUNT_FROZEN` | 400※ | 核心系统判定账户冻结（经故障注入） | false | 引导解冻 | `ErrorContracts.cs:47` |
| `INSUFFICIENT_FUNDS` | 400※ | 余额不足 | false | 不可重试，改金额 | `ErrorContracts.cs:41` |
| `DAILY_LIMIT_EXCEEDED` | 400※ | 超单日限额 | false | 明日再试 | `ErrorContracts.cs:44` |
| `SAME_ACCOUNT` | 400※ | 收付款账号相同 | false | 不可重试 | `ErrorContracts.cs:53` |
| `ACCOUNT_STATUS_ABNORMAL` | 400※ | 账户销户 / 挂失 | false | 先解挂 | `ErrorContracts.cs:50` |
| `INVALID_AMOUNT` / `VALIDATION_ERROR` | 400※ | 金额 ≤ 0 或参数非法 | false | 不可重试 | `ErrorContracts.cs:20` |
| `SERVICE_UNAVAILABLE` | 503※ | 下游故障（故障注入） | false | 退避重试 | `ErrorContracts.cs:74` |
| `INTERNAL_ERROR` | 500※ | MockBank 未捕获异常 | false | 重试 + 上报 | `GlobalExceptionMiddleware.cs:45` |

> **※ 标记的含义**：这些错误码来自 **MockBank**。当它们经 `CoreBankClient` 透传时，
> **在 Host 侧的 HTTP 状态码是 200**（`TransferAgent.cs:191-192` 把 `result.ErrorCode` 原样塞进 `AgentResult.Fail`）。
> 表中的 400/503/500 是**直接调用 MockBank 时**的状态码。
> 客户端调 Host 时，应统一从 `errorCode` 判断，而不是从 HTTP 状态码判断。

### 3.3 错误码的"可重试性"分类（客户端最重要的信息）

| 分类 | 错误码 | 处理策略 |
|------|--------|----------|
| ✅ **可重试**（幂等键保护，重复调用不会重复扣款） | `CORE_BANK_UNAVAILABLE`、`SERVICE_UNAVAILABLE`、`INTERNAL_ERROR` | 指数退避，最多 3 次 |
| ⚠️ **可重试但需用户确认** | `DAILY_LIMIT_EXCEEDED` | 提示明日再试或分笔 |
| ❌ **不可重试**（业务规则拒绝） | `COMPLIANCE_REJECTED`、`INSUFFICIENT_FUNDS`、`SAME_ACCOUNT`、`ACCOUNT_STATUS_ABNORMAL`、`ACCOUNT_FROZEN`、`INVALID_AMOUNT` | 展示原因，终止流程 |
| 🔁 **需补参数后重试** | `AMOUNT_MISSING`、`TARGET_MISSING`、`NO_ACCOUNT`、`CARD_MISSING`、`STATEMENT_NOT_FOUND` | 反问用户补齐槽位 |
| 🚪 **转人工** | `NO_AGENT`、`AGENT_EXCEPTION` | 转人工客服 |
| 🔒 **权限问题** | `UNAUTHORIZED`(401)、`FORBIDDEN`(403)、`IMPERSONATION_REASON_REQUIRED`(400) | 重新登录 / 申请权限 / 补填原因 |

### 3.4 MockBank 完整错误码表

来源 `ErrorContracts.cs:17-77`，共 **20 个**常量，全部已实现：

| 错误码 | HTTP | 触发条件 |
|--------|:----:|----------|
| `VALIDATION_ERROR` | 400 | 通用参数校验失败 |
| `BAD_REQUEST` | 400 | 请求体缺失或格式错误 |
| `ACCOUNT_NOT_FOUND` | 404 | 账户不存在 |
| `CUSTOMER_NOT_FOUND` | 404 | 客户不存在 |
| `CARD_NOT_FOUND` | 404 | 卡片不存在 |
| `PRODUCT_NOT_FOUND` | 404 | 理财产品不存在 |
| `TRANSACTION_NOT_FOUND` | 404 | 流水不存在 |
| `INSUFFICIENT_FUNDS` | 400 | 余额不足 |
| `DAILY_LIMIT_EXCEEDED` | 400 | 超单日限额 |
| `ACCOUNT_FROZEN` | 400 | 账户被冻结 |
| `ACCOUNT_STATUS_ABNORMAL` | 400 | 账户状态异常（销户/挂失） |
| `SAME_ACCOUNT` | 400 | 收付款账号相同 |
| `CURRENCY_MISMATCH` | 400 | 币种不一致 |
| `RISK_LEVEL_MISMATCH` | 400 | 客户风险等级低于产品 |
| `AMOUNT_BELOW_MINIMUM` | 400 | 低于起投金额 |
| `AMOUNT_ABOVE_MAXIMUM` | 400 | 超单笔认购上限 |
| `PRODUCT_SOLD_OUT` | 400 | 募集规模已满 |
| `PRODUCT_NOT_ON_SALE` | 400 | 产品停售 |
| `SERVICE_UNAVAILABLE` | 503 | 下游不可用 |
| `INTERNAL_ERROR` | 500 | 未捕获异常 |

**转账校验顺序**（`TransferService`，错误码出现的先后顺序）：
账户存在 → 账户状态 → 自转 → 金额合法性 → 余额充足 → 单日限额。
客户端可据此预测"哪个错误码先出现"。

### 3.5 故障注入（仅 MockBank，开发/测试用）

请求头 `X-Mock-Scenario`（`MockScenarioMiddleware.cs:22`），仅对
`POST /api/corebank/v1/transfers` 与 `POST /api/corebank/v1/wealth/subscribe` 生效
（`MockScenarioMiddleware.cs:31-32`、`:93-94`）。

| 值 | 效果 | 状态码 |
|----|------|:------:|
| `success` | 正常流程（默认） | 200 |
| `insufficient_funds` | 强制余额不足 | 400 |
| `daily_limit_exceeded` | 强制超单日限额 | 400 |
| `account_frozen` | 强制账户冻结 | 400 |
| `downstream_error` | 强制下游不可用 | 503 |
| `timeout` | **延迟 3000ms 后仍正常响应**（`MockScenarioMiddleware.cs:29`） | 200 |

> ⚠️ **`timeout` 不会返回错误**。它用于验证客户端的 15 秒超时（`CoreBankOptions.TimeoutSeconds`）是否生效。
> 由于 3s < 15s，**当前配置下这个场景不会触发超时**，该场景实际上只验证了"慢响应不影响正确性"。
> 若要验证超时，需要把 `TimeoutSeconds` 调到 2 以下。
>
> ⚠️ **生产环境必须移除此中间件**。它能让任意调用方强制制造余额不足 / 服务不可用。

---

## 4. 鉴权要求矩阵

### 4.1 端点 × 角色

| 端点 | 未认证 | user | staff | auditor | admin | 判定位置 |
|------|:------:|:----:|:-----:|:-------:|:-----:|----------|
| `GET /health` | ✅ | ✅ | ✅ | ✅ | ✅ | 白名单 `Program.cs:487` |
| `POST /api/auth/token` | ✅ | ✅ | ✅ | ✅ | ✅ | 白名单 `Program.cs:488` |
| `GET /api/auth/me` | ❌ 401 | ✅ | ✅ | ✅ | ✅ | 中间件 `Program.cs:87` |
| `GET /api/plugins` | ❌ 401 | ✅ | ✅ | ✅ | ✅ | 中间件 |
| `GET /api/plugins/agents` | ❌ 401 | ✅ | ✅ | ✅ | ✅ | 中间件 |
| `GET /api/plugins/compliance` | ❌ 401 | ✅ | ✅ | ✅ | ✅ | 中间件 |
| `GET /api/plugins/events` | ❌ 401 | ✅ | ✅ | ✅ | ✅ | 中间件 |
| `POST /api/plugins/{id}/stop` | ❌ 401 | ❌ 403 | ❌ 403 | ❌ 403 | ✅ | `Program.cs:325` |
| `POST /api/plugins/{id}/start` | ❌ 401 | ❌ 403 | ❌ 403 | ❌ 403 | ✅ | `Program.cs:334` |
| `POST /api/chat`（本人） | ❌ 401 | ✅ | ✅ | ✅ | ✅ | `Program.cs:346-349` |
| `POST /api/chat`（代客） | ❌ 401 | ❌ 403 | ✅ 需填原因 | ✅ 需填原因 | ✅ 需填原因 | `Program.cs:357-373` |
| `POST /api/chat/confirm`（本人） | ❌ 401 | ✅ | ✅ | ✅ | ✅ | `Program.cs:424` |
| `POST /api/chat/confirm`（代客） | ❌ 401 | ❌ 403 | ✅ | ✅ | ✅ | `Program.cs:427-432` |

### 4.2 能力模型

`CurrentPrincipal`（`JwtOptions.cs:39-54`）：

| 能力 | 判定规则 | 覆盖角色 |
|------|----------|----------|
| `CanAudit` | `Role is auditor or admin` | auditor、admin |
| `CanAdminister` | `Role == admin` | admin |
| `CanImpersonateSupport` | `Role is staff or auditor or admin` | staff、auditor、admin |

`agent` 角色（`JwtRoles.Agent`，`JwtOptions.cs:32`）在代码中**已定义但无账号、无端点使用**——
它是为"服务间调用 Agent"预留的。

### 4.3 越权防护的三条规则

| 规则 | 行为 | 源码 |
|------|------|------|
| **userId 一律以令牌为准** | 请求体的 `userId` **不作为身份来源**，只用于越权比对 | `Program.cs:354`、`Program.cs:398`、`Program.cs:455` |
| **不一致即 403** | `req.UserId != principal.UserId` 且无 `CanImpersonateSupport` → `FORBIDDEN` | `Program.cs:370-373` |
| **代客必须留痕** | 缺 `impersonationReason` → 400；提供则写 `user.impersonation` 审计，`ActorId` 是**客服本人** | `Program.cs:361-368`、`Program.cs:382-392` |

### 4.4 演示账号（`Program.cs:542-550`）

| userId | 密码 | 姓名 | 角色 |
|--------|------|------|------|
| `u_demo01` | `demo1234` | 张明 | user |
| `u_demo02` | `demo1234` | 李华 | user |
| `u_demo03` | `demo1234` | 王芳 | user |
| `staff_01` | `staff1234` | 客服小李 | staff |
| `audit_01` | `audit1234` | 审计员小王 | auditor |
| `admin_01` | `admin1234` | 管理员 | admin |

> 🔴 **生产环境必须替换**。密码以明文 `==` 比较（`Program.cs:557`），代码注释已明确要求对接统一身份认证 / OTP / MFA。
> 角色由服务端账号表决定，客户端传入的 `role` 字段被**完全忽略**（`Program.cs:141-143`）。

---

## 5. 请求/响应 Schema

### 5.1 `POST /api/auth/token`

**请求**（`TokenRequest`，`Program.cs:524`）

| 字段 | 类型 | 必填 | 约束 | 示例 |
|------|------|:----:|------|------|
| `userId` | string | ✅ | 非空白，否则 400 `INVALID_REQUEST` | `"u_demo01"` |
| `password` | string | ✅ | 非空白，否则 400 `INVALID_REQUEST` | `"demo1234"` |
| `role` | string | ❌ | **会被忽略**，角色以服务端账号表为准 | `"admin"`（无效） |

**响应 200**（`Program.cs:145-153`）

| 字段 | 类型 | 说明 | 示例 |
|------|------|------|------|
| `token` | string | JWT（HS256） | `"eyJhbGciOiJIUzI1NiIs..."` |
| `tokenType` | string | 固定 `"Bearer"` | `"Bearer"` |
| `userId` | string | 回显 | `"u_demo01"` |
| `role` | string | 服务端决定的角色 | `"user"` |
| `displayName` | string | 中文姓名 | `"张明"` |
| `expiresInSeconds` | number | 固定 1800（**与 `JwtOptions.ExpirationMinutes` 硬编码重复**，改配置不会同步） | `1800` |

**JWT Payload 结构**（`TokenService.cs:48-58`）

| Claim | 值 | 说明 |
|-------|-----|------|
| `sub` | userId | 标准用户标识 |
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier` | userId | 兼容旧客户端 |
| `http://schemas.microsoft.com/ws/2008/06/identity/claims/role` | role | 角色 |
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name` | displayName | 可选，空白时不含此 claim |

其他标准字段：`iss = banking-agent`、`aud = banking-agent-client`、`nbf`、`exp`（`JwtOptions.cs:17-21`）。

### 5.2 `GET /api/auth/me`

**响应 200**（`Program.cs:161-168`）

| 字段 | 类型 | 说明 |
|------|------|------|
| `userId` | string | 令牌中的用户标识 |
| `role` | string | 角色 |
| `displayName` | string | 姓名 |
| `canAudit` | bool | 审计只读权限 |
| `canAdminister` | bool | 管理权限 |

> `CurrentPrincipal` 另有 `CanImpersonateSupport`（`JwtOptions.cs:53`），但**本端点未返回**该字段。
> 客户端若需要判断能否代客，只能自己根据 `role` 推断。建议后续补上。

### 5.3 `POST /api/chat`

**请求**（`ChatRequest`，`Program.cs:518-521`）

| 字段 | 类型 | 必填 | 约束 | 说明 |
|------|------|:----:|------|------|
| `message` | string | ✅ | 非空白 | 自然语言输入。用于意图识别与槽位抽取 |
| `userId` | string | ⚠️ | 与令牌一致，否则 403 | **不作为身份来源**。`user` 角色可传空（因为 `IsNullOrWhiteSpace` 判断放过） |
| `sessionId` | string | ❌ | 建议必填 | **同时是幂等键**（`TransferAgent.cs:141`）。为空时降级为随机 GUID（`TransferAgent.cs:141`） |
| `slots` | object | ❌ | 键值对 | 预抽取槽位，避免 Agent 重复解析。已识别的键：`amount`、`to_account`、`from_account`、`confirmed`、`account_no`、`year`、`month`、`card_no` |
| `impersonationReason` | string | 条件 | 代客时必填 | 代客操作原因，写入审计 |

**槽位取值类型**：JSON number / string / bool 都接受，由 `SlotReader` 统一转换（见
[ADR-013](02-architecture-decision-record.md#adr-013)）。

**响应 200**（`ChatResponse`，`Program.cs:526-530`）

| 字段 | 类型 | 说明 | 约束 |
|------|------|------|------|
| `echo` | string | 回显原始输入 | 调试用 |
| `intent` | string | 场景级意图（`IIntentClassifier` 判定：配了 `Ai:ApiKey` 走大模型，否则规则表） | `transfer` / `bill` / `card` / `wealth` / `unknown` |
| `intentSource` | string | 意图判定来源 | `llm` / `rule` |
| `resultIntent` | string | Agent 判定的具体动作 | `card.list` / `transfer.completed` / `bill.summary` |
| `success` | bool | **业务是否成功** | ⚠️ 业务失败也是 HTTP 200 |
| `content` | string | 自然语言回复 | 可直接展示给用户 |
| `resultIntent` | string | Agent 产出的意图 | 如 `transfer.completed`、`card.list` |
| `requiresHumanInLoop` | bool | **是否需人工确认** | true 时应展示确认卡片 |
| `sideEffectCommitted` | bool | **是否已产生不可回滚副作用** | **客户端据此决定能否自动重试** |
| `errorCode` | string | 错误码 | 成功时为 `null` |
| `errorMessage` | string | 错误描述 | 成功时为 `null` |
| `data` | object | 结构化数据 | **已脱敏**（`DataMasker`） |
| `confidence` | number | 置信度 0~1 | 当前恒为 1.0（未接 LLM） |
| `elapsedMs` | number | 耗时（毫秒，保留 1 位小数） | `Math.Round(result.Elapsed.TotalMilliseconds, 1)`（`Program.cs:414`） |

**`data` 的结构随 `resultIntent` 变化**

| `resultIntent` | `data` 字段 | 类型 | 脱敏 | 源码 |
|---------------|-------------|------|:----:|------|
| `transfer.completed` | `transaction_no` | string | 否（流水号非敏感） | `TransferAgent.cs:239` |
| | `amount` | number | 否 | `TransferAgent.cs:240` |
| | `balance_after` | number | ⚠️ **未脱敏**（L3 余额明文返回） | `TransferAgent.cs:241` |
| | `human_approved` | bool | 否 | `TransferAgent.cs:242` |
| | `idempotent_replay` | bool | 否（仅重复调用时出现） | `TransferAgent.cs:160` |
| （待确认） | `amount` | number | 否 | `TransferAgent.cs:133` |
| | `from_account` | string | ❌ **明文，未脱敏** | `TransferAgent.cs:134` |
| | `to_account` | string | ❌ **明文，未脱敏** | `TransferAgent.cs:135` |
| | `rule_id` | string | 否 | `TransferAgent.cs:136` |
| `bill.summary` | `account_no` | string | ✅ `MaskAccount` | `BillPlugin.cs:100` |
| | `total_income` / `total_expense` | number | ⚠️ 未脱敏 | `BillPlugin.cs:101-102` |
| | `transaction_count` | number | 否 | `BillPlugin.cs:103` |
| | `categories` | array | 含分类名与金额 | `BillPlugin.cs:104` |
| | `daily_trend` | array | 含日金额 | `BillPlugin.cs:105` |
| | `data_classification` | string | 固定 `"L3-masked"` | `BillPlugin.cs:106` |
| `card.list` | `cards[].card_no` | string | ✅ `MaskBankCard` | `CardPlugin.cs:88` |
| | `cards[].bound_phone` | string | ✅ `MaskPhone` | `CardPlugin.cs:91` |
| | `cards[].card_type` / `status` / `daily_limit` | string/number | 否 | `CardPlugin.cs:89-92` |
| `card.status.updated` | `card_no` | string | ✅ `MaskBankCard` | `CardPlugin.cs:156` |
| | `new_status` | string | 否 | `CardPlugin.cs:157` |

> 🔴 **发现的脱敏缺口**：待确认响应里的 `from_account` / `to_account` 是**明文账号**
> （`TransferAgent.cs:134-135`），成功响应里的 `balance_after` 是**明文余额**（`TransferAgent.cs:241`）。
> 这与 `docs/05-security-compliance/03-data-classification.md` 的 L3 要求冲突。
> 对照：`BillAnalysisAgent` 与 `CardManagementAgent` 都调了 `DataMasker`，`TransferAgent` 的响应构造漏了。
> 已记入 [04-data-model.md §9](04-data-model.md#9-已知缺口与改进项) 与
> [01-uml-diagrams.md §13](01-uml-diagrams.md#13-阅读源码时发现的架构不一致诚实记录)。

**三态响应示例**

<details>
<summary>① 人工回环（<code>requiresHumanInLoop: true</code>）</summary>

```json
{
  "echo": "给6E2E020200000003转账30000元",
  "intent": "transfer.execute",
  "success": true,
  "content": "单笔金额 30,000.00 元超过自动放行上限 5,000.00 元，需人工确认",
  "resultIntent": null,
  "requiresHumanInLoop": true,
  "sideEffectCommitted": false,
  "errorCode": null,
  "errorMessage": null,
  "data": {
    "amount": 30000,
    "from_account": "6E2E020200000001",
    "to_account": "6E2E020200000003",
    "rule_id": "transfer.amount.threshold"
  },
  "confidence": 1,
  "elapsedMs": 42.3
}
```
</details>

<details>
<summary>② 执行成功</summary>

```json
{
  "echo": "转账800元",
  "intent": "transfer.execute",
  "success": true,
  "content": "转账成功，金额 800.00 元，流水号 TX202609270000084",
  "resultIntent": "transfer.completed",
  "requiresHumanInLoop": false,
  "sideEffectCommitted": true,
  "errorCode": null,
  "errorMessage": null,
  "data": {
    "transaction_no": "TX202609270000084",
    "amount": 800,
    "balance_after": 249200,
    "human_approved": false
  },
  "confidence": 1,
  "elapsedMs": 118.7
}
```
</details>

<details>
<summary>③ 合规拒绝</summary>

```json
{
  "echo": "给6E2E020200000001转账100元",
  "intent": "transfer.execute",
  "success": false,
  "content": null,
  "resultIntent": null,
  "requiresHumanInLoop": false,
  "sideEffectCommitted": false,
  "errorCode": "COMPLIANCE_REJECTED",
  "errorMessage": "禁止向本人账户转账",
  "data": {},
  "confidence": 0,
  "elapsedMs": 8.1
}
```
</details>

### 5.4 `POST /api/chat/confirm`

**请求**（`ConfirmRequest`，`Program.cs:532-534`）

| 字段 | 类型 | 必填 | 约束 | 说明 |
|------|------|:----:|------|------|
| `userId` | string | ⚠️ | 与令牌一致，否则 403 | 不作为身份来源（`Program.cs:427-432`） |
| `sessionId` | string | ✅ **语义必填** | 必须与原 `/api/chat` 一致 | **幂等键**。不一致会重复扣款 |
| `amount` | number | ✅ | `> 0` | 仅写入审计（`Program.cs:446`），不参与路由判断 |
| `slots` | object | ✅ | 必须包含 `to_account`、`from_account`、`amount` | 端点会补 `slots["confirmed"] = true`（`Program.cs:450`） |

> ⚠️ **端点硬编码了意图**：`SharedContext["intent"] = "transfer.execute"` 且
> `Upstream = AgentResult.Ok(intent: "transfer.execute")`（`Program.cs:458-459`）。
> 这意味着**本端点只支持转账**。账单/卡片的确认流程没有对应端点。

**响应 200**（`Program.cs:463-470`）——**注意结构与 `/api/chat` 不同**：

| 字段 | 类型 | 说明 |
|------|------|------|
| `success` | bool | 是否成功 |
| `content` | string | 自然语言回复 |
| `committed` | bool | 是否已提交（对应 `sideEffectCommitted`，**字段名不同**） |
| `data` | object | 结构化数据 |
| `error` | string | 错误描述（对应 `errorMessage`，**字段名不同**） |

> ⚠️ **响应结构不一致**：本端点用 `committed` / `error`，`/api/chat` 用 `sideEffectCommitted` / `errorMessage`，
> 且**不返回 `errorCode`**、`requiresHumanInLoop`、`confidence`、`elapsedMs`。
> 客户端需要写两套解析逻辑。建议后续统一。

**响应示例**

```json
{
  "success": true,
  "content": "转账成功，金额 30,000.00 元，流水号 TX202609270000086",
  "committed": true,
  "data": {
    "transaction_no": "TX202609270000086",
    "amount": 30000,
    "balance_after": 219200,
    "human_approved": true
  },
  "error": null
}
```

**重复调用的响应**（`TransferAgent.cs:149-162`）：

```json
{
  "success": true,
  "content": "该转账已处理过，流水号 TX202609270000086",
  "committed": true,
  "data": {
    "transaction_no": "TX202609270000086",
    "amount": 30000,
    "idempotent_replay": true
  },
  "error": null
}
```

### 5.5 `GET /health`

**响应 200**（`Program.cs:119-124`）

| 字段 | 类型 | 说明 |
|------|------|------|
| `status` | string | 固定 `"healthy"`。⚠️ **不做真实健康检查**（不探测 DB / 核心系统） |
| `plugins` | number | 已加载插件数 |
| `timestamp` | string | ISO 8601 |

### 5.6 `GET /api/plugins`

**响应 200**（`Program.cs:172-190`）——返回**数组**，元素 Schema：

| 字段 | 类型 | 说明 |
|------|------|------|
| `id` | string | 插件 ID，如 `banking.transfer` |
| `name` | string | 中文名 |
| `version` | string | 语义化版本 |
| `description` | string | 描述 |
| `scenarios` | string[] | 业务场景标识 |
| `featureFlags` | string[] | 功能开关名。⚠️ **仅声明，运行时无强制** |
| `maxClassification` | string | `L1`~`L4` |
| `dependencies` | object[] | `{id, minVersion, optional}` |
| `agents` | string[] | 该插件的 Agent 类型名 |
| `isActive` | bool | 是否运行中 |
| `loadedAt` | string | 加载时间 |

### 5.7 `GET /api/plugins/agents`

**响应 200**（`Program.cs:193-203`）——返回**数组**：

| 字段 | 类型 | 说明 |
|------|------|------|
| `id` | string | Agent ID |
| `name` | string | 中文名 |
| `role` | string | `DomainExpert`（当前唯一使用的角色） |
| `intents` | string[] | 支持的意图前缀 |

### 5.8 `GET /api/plugins/compliance`

**响应 200**（`Program.cs:296-305`）——返回**数组**：

| 字段 | 类型 | 说明 |
|------|------|------|
| `ruleId` | string | 规则标识 |
| `version` | string | 规则版本 |
| `scenarios` | string[] | 适用场景，空数组表示全部 |

### 5.9 `GET /api/plugins/events`

**响应 200**（`Program.cs:307-319`）

| 字段 | 类型 | 说明 |
|------|------|------|
| `published` | number | 进程生命周期内累计发布数（重启归零） |
| `deadLetters` | number | 死信队列长度 |
| `recent` | object[] | 最近 20 条事件（`History` 上限 500，`EventBus.cs:57`） |
| `recent[].eventType` | string | 如 `transfer.completed` |
| `recent[].source` | string | 发布方插件 ID |
| `recent[].occurredAt` | string | 发生时间 |
| `recent[].payload` | object | 事件载荷 |

> ⚠️ **运维端点未做角色限制**：`/api/plugins`、`/api/plugins/agents`、`/api/plugins/compliance`、`/api/plugins/events`
> 任何已认证角色都能访问（`Program.cs:172` 等，仅中间件鉴权）。
> `payload` 里含 `from_account` / `to_account` **明文账号**（`TransferAgent.cs:224-225`），
> 任何 `user` 角色都能通过本端点看到**全系统**的转账账号（不限于自己的）。
> 这是本次文档核对发现的**信息泄露面**，建议：
> 要么限制为 `CanAudit`，要么按 `UserId` 过滤 `recent`。
> 已记入 [01-uml-diagrams.md §13](01-uml-diagrams.md#13-阅读源码时发现的架构不一致诚实记录)。

### 5.10 `POST /api/plugins/{pluginId}/stop` 与 `/start`

**请求**：无请求体

**路径参数**

| 参数 | 类型 | 约束 |
|------|------|------|
| `pluginId` | string | 必须是已加载的 `PluginId`，否则 404（空响应体） |

**响应 200**（`Program.cs:328`、`Program.cs:337`）

```json
{ "pluginId": "banking.card", "action": "stopped" }
```

| 字段 | 类型 | 取值 |
|------|------|------|
| `pluginId` | string | 回显 |
| `action` | string | `"stopped"` / `"started"` |

**响应 403**（`Program.cs:325`、`Program.cs:334`）：非 admin 角色

```json
{ "code": "FORBIDDEN", "message": "只有管理员可以停用插件" }
```

> **语义说明**：`stop` 是**逻辑停用**，程序集仍驻留内存（`PluginRegistry.cs:229-245`）。
> ⚠️ **但停用后 Agent 仍可被路由到**——`AgentRouter` 在构造时已把 Agent 收集到只读列表，
> `Resolve` 不检查 `IsActive`。停用插件后其 Agent 依然会响应请求。
> **程序集真卸载**需调用 `PluginRegistry.UnloadPlugin()`，**当前无 HTTP 端点**（`PluginRegistry.cs:248-266`）。

### 5.11 MockBank 关键契约（供集成参考）

`POST /api/corebank/v1/transfers` 请求（`TransferContracts.cs:12-18`）：

| 字段 | 类型 | 必填 | 约束 |
|------|------|:----:|------|
| `fromAccountNo` | string | ⚠️ | 为空时 400 |
| `toAccountNo` | string | ⚠️ | 为空时 400 |
| `amount` | number | ✅ | 必须 > 0 |
| `currency` | string | ❌ | 默认 `"CNY"`；与账户币种不符 → `CURRENCY_MISMATCH` |
| `remark` | string | ❌ | 附言 |
| `userId` | string | ❌ | 发起客户号，用于风控与审计 |

成功响应（`TransferContracts.cs:30-39`）：

| 字段 | 类型 | 说明 |
|------|------|------|
| `txNo` | string | 交易流水号，如 `TX202609270000084` |
| `fromAccountNo` / `toAccountNo` | string | 账号 |
| `amount` | number | 金额 |
| `currency` | string | 币种 |
| `fromBalanceAfter` | number | 付款账户余额 |
| `toBalanceAfter` | number | 收款账户余额 |
| `completedAt` | string | 完成时间 |
| `message` | string | 默认 `"转账成功"` |

> ⚠️ **字段名兼容问题**：`CoreBankClient` 对响应做了双字段兼容（`CoreBankClient.cs:330-336`）：
> `TransactionNo ?? TxNo`、`BalanceAfter ?? FromBalance`。
> 真实 CBS 若返回 `transactionNo` / `balanceAfter` 也能解析。这是**契约防御**，
> 但也说明 MockBank 与契约 DTO 的字段名**并不统一**（`transactionNo` vs `txNo`），
> 真实对接时需确认字段名。

---

## 6. 幂等性契约

### 6.1 端点幂等性

| 端点 | 幂等 | 说明 |
|------|:----:|------|
| `GET /health` | ✅ | 无副作用 |
| `POST /api/auth/token` | ❌ | 每次签发新令牌（无 `jti`，无法识别重放） |
| `GET /api/auth/me` | ✅ | 无副作用 |
| `GET /api/plugins*` | ✅ | 无副作用 |
| `POST /api/plugins/{id}/stop` | ✅ | 重复停用返回 200（`PluginRegistry.cs:234` 已停用则直接返回 `true`） |
| `POST /api/plugins/{id}/start` | ✅ | 重复启动返回 200（`PluginRegistry.cs:209`） |
| `POST /api/chat`（只读意图） | ✅ | 无副作用 |
| `POST /api/chat`（转账） | ⚠️ **条件幂等** | 依赖 `sessionId`，见 6.2 |
| `POST /api/chat/confirm` | ⚠️ **条件幂等** | 同上 |

### 6.2 幂等键的契约

| 事项 | 当前实现 | 源码 |
|------|----------|------|
| 幂等键来源 | `ChatRequest.SessionId` | `Program.cs:399` |
| 传递到 Agent | `AgentRequest.SessionId` | `Program.cs:399` |
| 确认端点复用 | `ConfirmRequest.SessionId` 必须与原请求一致 | `Program.cs:456` |
| 键为空时 | 降级为 `Guid.NewGuid().ToString("N")` | `TransferAgent.cs:141` |
| 传给核心系统 | ⚠️ **没有传**（`CoreBankClient.cs:63-71` 的请求体不含 `IdempotencyKey`） | — |
| 传给合规上下文 | ✅ `IdempotencyKey = request.SessionId` | `TransferAgent.cs:99` |
| 持久化 | `TransferRecord.IdempotencyKey`，**唯一索引** | `TransferPlugin.cs:62`、`:83` |
| 命中重复的返回 | `success: true` + `data.idempotent_replay: true` + 原 `transaction_no` | `TransferAgent.cs:149-162` |

### 6.3 重复调用的三种情形

| 情形 | 结果 | 是否有副作用 |
|------|------|:------------:|
| 串行重复同一 `sessionId` + 相同槽位 | 返回原 `transaction_no`，`idempotent_replay: true` | 无（E2E 已验证，`Program.cs:305`） |
| **同一 `sessionId` 但槽位不同**（用户想转第二笔） | ⚠️ **被误判为重复**，返回第一笔的 `transaction_no`，第二笔不执行 | 无（但用户拿到错误信息） |
| **并发同一 `sessionId`** | ⚠️ **可能两次都通过检查 → 重复扣款** | 有（**风险**） |

后两项是已确认的缺陷，完整分析见
[ADR-021 影响与已知缺陷](02-architecture-decision-record.md#影响与已知缺陷必须记录) 与
[ADR-021 改造方向](02-architecture-decision-record.md#改造方向分三步建议按序推进)。

### 6.4 建议的幂等性契约（目标态，【规划】）

```
幂等键 = SHA256( userId | sessionId | canonical(槽位) )
```

- **范围**：按用户隔离，跨用户复用同一 `sessionId` 不会互相干扰（当前实现存在这个风险）
- **粒度**：同一会话的不同转账得到不同键（修 §6.3 情形 2）
- **双写防线**：键同时随请求体发给 CBS（修 §6.3 情形 3）
- **HTTP 标准做法**：支持 `Idempotency-Key` 请求头作为覆盖（`?key=xxx` 优先级更高），
  24 小时 TTL，命中时回放原响应（含原状态码）

---

## 7. 限流契约

> **当前状态：✅ 已实现**（自研令牌桶，无第三方依赖）。
> 算法：`src/src/BankingAgent.Base/Security/RateLimit/RateLimiter.cs`（133 行，纯逻辑，不依赖 ASP.NET Core）。
> 接入：`src/src/BankingAgent.Host/Middleware/RateLimitingMiddleware.cs`（111 行），注册于 `Program.cs:85`。
> 配置：`Host/appsettings.json` 的 `RateLimit` 节 → `RateLimitOptions`（`RateLimiter.cs:12-31`）。

### 7.1 实际的档位与配额

`RateLimitingMiddleware.Classify`（`RateLimitingMiddleware.cs:69-89`）按**路径字符串**分档：

| 档位 | `RateLimitTier` | 匹配路径 | 配额（次/分钟） | 突发容量 | 定义位置 |
|------|-----------------|----------|----------------|---------|----------|
| 免限流 | `None` | 含 `/health`、含 `/api/auth/me` | — | — | `RateLimitingMiddleware.cs:73-74` |
| 严格 | `Strict` | 含 `/api/auth/token` | `AuthPermitsPerMinute` = **5** | `max(3, Burst/10)` = **3** | `RateLimitingMiddleware.cs:77-78`、`:93` |
| 中等 | `Standard` | 含 `transfer` / `confirm` / `wealth` | `TransferPermitsPerMinute` = **20** | `Burst/2` = **15** | `RateLimitingMiddleware.cs:81-82`、`:94` |
| 只读 | `ReadOnly` | 含 `/api/plugins` 或 `/api/trajectory` 且 GET | `ReadPermitsPerMinute` = **300** | `Burst*2` = **60** | `RateLimitingMiddleware.cs:85-86`、`:95` |
| 宽松 | `Relaxed` | 其他全部（兜底，含 `/api/chat`） | `ApiPermitsPerMinute` = **120** | `Burst` = **30** | `RateLimitingMiddleware.cs:88`、`:96` |

> ⚠️ **匹配用的是 `string.Contains` 而不是精确路径**（`RateLimitingMiddleware.cs:71-88`）。
> 这意味着任何路径里含 `transfer` / `confirm` / `wealth` 子串的请求都会被划入 `Standard` 档。
> 当前端点集合下结果正确，但**新增端点时要注意命中的档位可能不是预期的**。

### 7.2 实际的限流维度（双维度）

`RateLimitingMiddleware.InvokeAsync`（`RateLimitingMiddleware.cs:16-66`）依次检查两个维度，**任一不通过即 429**：

| 维度 | 桶键 | 配额 | 突发 | 位置 | 作用 |
|------|------|------|------|------|------|
| **IP** | `"ip:" + RemoteIpAddress` | `IpPermitsPerMinute` = **600** | `Burst*4` = **120** | `RateLimitingMiddleware.cs:39` | 防全站泛刷与端口扫描 |
| **身份** | `"user:" + <身份>` | 当前档位的 `permits` | 当前档位的 `burst` | `RateLimitingMiddleware.cs:49` | 防单账号暴力破解 |

**身份键的解析**（`RateLimitingMiddleware.cs:110-122`）：

| 情况 | 桶键 |
|------|------|
| `ctx.Items["Principal"]` 是已认证主体 | `principal.UserId` |
| 无 Principal 但有 `Authorization` 头 | `"anon:" + SHA256(header)[..16]` |
| 两者都无 | `null` → **跳过身份维度，只受 IP 维度约束** |

哈希的是 `Authorization` 头而不是令牌本身：同一用户的不同令牌会落到不同桶。
**副作用：令牌刷新后配额会重置**（见 [§7.4](#74-已发现的限流实现问题)）。

### 7.3 实际的响应契约

**放行时**（`RateLimitingMiddleware.cs:56-63`）：

| 响应头 | 值 | 有身份时 | 无身份时 |
|--------|----|:--------:|:--------:|
| `X-RateLimit-Limit` | 该维度配额 | 档位配额 | `IpPermitsPerMinute`（600） |
| `X-RateLimit-Remaining` | 向下取整的剩余令牌 | 身份桶剩余 | IP 桶剩余 |

**限流时**（`RateLimitingMiddleware.cs:124-137`）—— HTTP **429**：

| 响应头 | 值 |
|--------|-----|
| `Retry-After` | 向上取整的秒数 |
| `X-RateLimit-Limit` | 该维度配额 |
| `X-RateLimit-Remaining` | `"0"` |

```json
{
  "code": "RATE_LIMITED",
  "message": "请求过于频繁，请稍后重试",
  "dimension": "ip",
  "retryAfterSeconds": 42
}
```

| `dimension` 取值 | 含义 |
|-----------------|------|
| `"ip"` | 触发的是 IP 维度 |
| `"identity"` | 触发的是身份维度 |

同时打 `LogWarning`（`RateLimitingMiddleware.cs:136-137`）。

> **客户端应对**：
> 1. 读 `Retry-After` 退避重试，不要立即重试；
> 2. `dimension = "ip"` 说明是网络出口问题（如 NAT 后的多人共用），`dimension = "identity"` 说明是该账号行为过于频繁；
> 3. **资金类端点（`Standard` 档）被限流时，不要改用其他端点绕过**——换个路径仍是同一 IP 桶。

### 7.4 已发现的限流实现问题

| # | 问题 | 位置 | 影响 |
|---|------|------|------|
| 1 | **中间件顺序：限流在鉴权之前** | `Program.cs:85` 在 `Program.cs:87` 之前 | `ctx.Items["Principal"]` 恒为 `null`，`ResolveIdentity` 的 `CurrentPrincipal` 分支（`RateLimitingMiddleware.cs:112-115`）是**死代码**。已认证请求实际按 `Authorization` 头哈希分桶 |
| 2 | **令牌桶 `Sweep` 永不回收** | `RateLimiter.cs:145` | `if (kv.Value.Remaining >= kv.Value.Remaining) continue;` 是**恒真条件**（自己与自己比较），导致 `TryRemove` 永不执行。长时间运行 + 高基数 IP 会让 `_buckets` 无限增长。只有 `> 10000` 时打 Warning（`RateLimiter.cs:152-155`），**不会自动清理** |
| 3 | **`X-RateLimit-Reset` / `X-RateLimit-Policy` 未实现** | — | 客户端只能靠 `Retry-After` 推算恢复时刻 |
| 4 | **无全局限流开关的热更新** | `RateLimiter.cs:15` | `Enabled` 只在启动时读一次，改配置需重启 |
| 5 | **无分租户/分用户优先级** | — | 内部系统与外部用户共享同一配额池 |
| 6 | **无 `X-Forwarded-For` 信任配置** | `RateLimitingMiddleware.cs:100-107` | 只用 `Connection.RemoteIpAddress`。**部署在反向代理后，所有请求的 IP 都是代理 IP**，IP 维度会退化为「全站一个桶」，合法用户会被误伤。注释已提醒（`RateLimitingMiddleware.cs:99`）但未实现 |
| 7 | **MockBank 无限流** | — | 攻击者可直接打 MockBank（开发环境无鉴权） |

### 7.5 与合规风控的区别

| | 限流（RateLimit） | 频率风控（Compliance） |
|---|------------------|---------------------|
| 层次 | 基础设施 / HTTP 管道 | 业务 / 合规守卫 |
| 动作 | **拒绝**（429） | **放行但要求人工确认**（`RequiresHumanApproval`） |
| 计数范围 | 按 IP / 按身份，按分钟 | ⚠️ `ComplianceOptions.HighFrequencyThreshold = 5`（`ComplianceRules.cs:21`）**已配置但无任何规则读取** |
| 依据 | 容量保护 | 监管要求 |

两者**不可互相替代**。当前状态是：限流已实现，**频率风控未实现**（见
[01-uml-diagrams.md §6.3](01-uml-diagrams.md#63-四个配置项中两个当前未被任何规则消费)）。

### 7.6 尚未覆盖的限流维度

| 建议维度 | 理由 | 当前状态 |
|----------|------|---------|
| 按插件 `pluginId` | 插件级隔离，单插件故障不影响全局 | ❌ 未实现 |
| 按会话 `sessionId` | 防单会话刷幂等键探测 | ❌ 未实现 |
| `/api/chat` 单独收紧 | 对话是 LLM 密集型操作，成本高。当前 `/api/chat` 落在兜底的 `Relaxed` 档（120/分钟） | ❌ 未实现 |
| 分级配额（内部 vs 外部） | 内部系统不受外部配额影响 | ❌ 未实现 |
---

## 8. 版本化策略建议

### 8.1 现状

| 维度 | 现状 | 位置 |
|------|------|------|
| Host 路径 | `POST /api/chat`、`/api/auth/token` | `Program.cs` |
| Host 版本前缀 | ❌ **无版本号** | — |
| CoreBank 路径 | `/api/corebank/v1` | `CoreBankOptions.ApiPrefix`（`CoreBankClient.cs:20`） |
| 核心系统版本策略 | 由 `CoreBankOptions.ApiPrefix` 一个配置项控制 | `Host/appsettings.json:17` |
| 插件版本 | `PluginVersion` 语义化，主版本必须相同才兼容 | `SDK-Plugin.cs:29-32` |
| 插件兼容校验 | 加载时拓扑排序阶段校验 | `PluginRegistry.cs:292-297` |

> **不对称**：下游（CoreBank）有 `v1`，上游（Host 客户端 API）**没有版本号**。

### 8.2 建议的版本化策略

**（1）Host 对外 API 引入 `/api/v1/` 前缀**

| 阶段 | 做法 | 兼容性 |
|------|------|-------|
| 立即 | 保留现有无前缀路径**不动** | 现有客户端零改动 |
| 下一版 | 新增 `/api/v1/chat` 等，与旧路径并存（同一 handler 注册两次） | 双写期，新客户端用 `/v1` |
| 双写期满 | 旧路径返回 410 Gone + `Deprecation` 头 | 明确告知 |
| 稳定后 | 删除旧路径 | 破坏性变更已完成公告 |

**（2）版本号用 URL 路径，不用 Header**

| 方案 | 评价 |
|------|------|
| ✅ URL 路径 `/api/v1/` | 主流（Stripe、GitHub、Twitter）；curl 与浏览器可直接调试；网关易配置 |
| ❌ Header `Accept: application/vnd.bank.v1+json` | 移动端与 IM 通道实现成本高；调试不直观 |
| ❌ Query `?version=1` | 易被缓存忽略，语义弱 |

**（3）兼容性规则**

| 变更类型 | 是否需要新版本 | 说明 |
|----------|:--------------:|------|
| 新增可选请求字段 | ❌ | 向后兼容 |
| 新增响应字段 | ❌ | 客户端应忽略未知字段 |
| 新增端点 | ❌ | — |
| 新增错误码 | ❌ | 客户端应按"未知错误码"兜底 |
| **删除/重命名字段** | ✅ | 破坏性 |
| **收紧校验规则** | ✅ | 破坏性（原本合法的请求变非法） |
| **改变默认值** | ✅ | 破坏性 |
| **改变状态码语义** | ✅ | 破坏性 |
| **新增必填字段** | ✅ | 破坏性 |

**（4）弃用公告契约**

| 响应头 | 示例 |
|--------|------|
| `Deprecation` | `true` |
| `Sunset` | `Sat, 31 Dec 2026 23:59:59 GMT`（RFC 8594） |
| `Link` | `<https://docs.internal/api/deprecation/v1>; rel="deprecation"` |
| `Warning` | `299 - "v1 将于 2026-12-31 停止服务，请迁移至 v2"` |

**（5）契约测试要求（【规划】）**

MockBank 已有 OpenAPI 3.0 文档端点 `/openapi/v1.json`（`OpsEndpoints.cs:41`，17 paths / 22 schemas），
**Host 没有**。建议：

1. Host 接入 `Swashbuckle` / `NSwag` 生成 OpenAPI 3.0 契约
2. 契约文件纳入版本控制（`openapi/host.v1.json`）
3. CI 中做**契约 diff 检查**：契约变更必须与 `docs/09-uml/03-api-contract.md` 同步更新
4. 用契约生成客户端 SDK，避免手写 DTO 漂移

---

## 9. 与设计文档的差异

### 9.1 设计了但未实现的端点

| 设计端点 | 状态 | 替代方案 |
|----------|------|----------|
| `POST /api/chat` SSE 流式 | ❌ 未实现 | 当前一次性返回全量 JSON |
| `GET/POST /api/v1/conversations` | ❌ 未实现 | 由 `/api/chat` 的 `sessionId` 简化替代 |
| `POST /api/v1/transfers` | ❌ 未实现 | 走 `/api/chat` + `ICoreBankClient` 内部调用 |
| `GET /api/v1/users/me/*` | ❌ 未实现 | 用户权利响应接口（可携带/查询个人信息）未做。**个保法下这是必需能力** |
| 审计查询端点 | ❌ 未实现 | 只能读 `logs/audit-chain.log` 文件 |
| LLM 意图分类 | ✅ 可选接入 | `IIntentClassifier`（`Base/Ai/LlmIntentClassifier.cs`）：配 `Ai:ApiKey` 走模型，否则/失败时降级规则表 |
| 反洗钱报送 | ❌ 未实现 | `AmlThresholdRule` 只打标不外发 |
| IM 推送确认卡片 | ❌ 未实现 | 客户端轮询或同步等待 |
| FeatureFlag 强制 | ❌ 未实现 | 清单里声明但运行时无检查（[ADR-0010](../00-architecture/04-architecture-decisions.md#adr-0010) 仍"评审中"） |
| 限流 | ❌ 未实现 | 见 [§7](#7-限流契约) |
| OpenAPI 文档（Host） | ❌ 未实现 | MockBank 有，Host 无 |

### 9.2 实现了但未设计的端点

| 端点 | 说明 |
|------|------|
| `GET /api/plugins` | 插件清单 |
| `GET /api/plugins/agents` | Agent 路由表 |
| `GET /api/plugins/compliance` | 合规规则清单 |
| `GET /api/plugins/events` | 事件总线状态 |
| `POST /api/plugins/{id}/stop\|start` | 插件热插拔 |

这些是插件化架构的**运维刚需**，但设计阶段（[`../02-api/01-rest-api-spec.md`](../02-api/01-rest-api-spec.md)）没有覆盖。

---

## 10. 关联文档

- UML 图集（含转账时序图、状态图）：[01-uml-diagrams.md](01-uml-diagrams.md)
- 架构决策 ADR-011 ~ ADR-021：[02-architecture-decision-record.md](02-architecture-decision-record.md)
- 数据模型：[04-data-model.md](04-data-model.md)
- 实测 API 参考（含调用示例与演示数据）：[`../plugin/02-api-reference.md`](../plugin/02-api-reference.md)
- 插件接入指南：[`../plugin/01-plugin-onboarding-guide.md`](../plugin/01-plugin-onboarding-guide.md)
- 实现状态对照：[`../plugin/03-implementation-status.md`](../plugin/03-implementation-status.md)
- REST API 设计规范：[../02-api/01-rest-api-spec.md](../02-api/01-rest-api-spec.md)
- 事件 Schema：[../02-api/02-event-schema.md](../02-api/02-event-schema.md)
- 功能开关设计：[../02-api/03-feature-flags.md](../02-api/03-feature-flags.md)
- 数据分级：[../05-security-compliance/03-data-classification.md](../05-security-compliance/03-data-classification.md)
