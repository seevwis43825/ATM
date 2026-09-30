# 快速运行指南（Run Guide）

> **环境**：.NET SDK 9.0.200+（解决方案使用 `.slnx`，项目目标为 `net8.0`）
> **验证口径**：单元测试 149 项已实测通过；E2E/Stress 以程序最终输出和 CI 为准。
> **插件开发**：见 [`01-plugin-onboarding-guide.md`](01-plugin-onboarding-guide.md)。

---

## 1. 当前组成

```text
BankingAgent.Host :5243
  - JWT + RBAC + 分级限流
  - /api/chat 与 /api/chat/confirm
  - /api/orchestrate 与轨迹查询
  - 加载 plugins/ 下 4 个插件

BankingAgent.Base
  - PluginRegistry / AgentRouter / AgentOrchestrator
  - DomainEvent / InMemoryEventBus
  - BankingDbContext / DatabaseInitializer
  - ComplianceGuard / AuditLogger / DataMasker

plugins/
  - banking.transfer  智能转账
  - banking.bill      账单分析
  - banking.card      卡片管理
  - banking.wealth    理财（产品查询与推荐、余额查询）

MockBank.Api :5200
  - 账户、收款人、转账、流水、账单、卡片、理财产品
```

## 2. 从仓库根目录启动

构建与启动必须使用同一配置。以下统一使用 `Release`：

```powershell
# 终端 1：构建
dotnet build src/BankingAgent.slnx -c Release

# 终端 2：模拟银行
dotnet run --project src/mock-bank/MockBank.Api -c Release

# 终端 3：宿主
dotnet run --project src/src/BankingAgent.Host -c Release
```

打开 <http://localhost:5243> 使用内置对话控制台。

```powershell
Invoke-RestMethod http://localhost:5200/health
Invoke-RestMethod http://localhost:5243/health
Invoke-RestMethod http://localhost:5243/health/ready
Invoke-RestMethod http://localhost:5243/health/live
```

`/health` 与 `/health/ready` 都真实探测数据库；数据库不健康时返回 503。`/health/live` 只检查进程存活。

> 若宿主报告插件不完整，检查 `src/src/BankingAgent.Host/bin/Release/net8.0/plugins/`
> 是否包含 4 个插件 DLL。Release 构建后用默认 Debug 启动会读取另一输出目录。

## 3. 获取令牌

健康探针和 `/api/auth/token` 公开，其余 `/api/*` 端点需要 Bearer 令牌。

```powershell
$login = @{ userId = "u_demo01"; password = "demo1234" } | ConvertTo-Json
$token = (Invoke-RestMethod http://localhost:5243/api/auth/token -Method Post `
  -ContentType "application/json" -Body $login).token
$headers = @{ Authorization = "Bearer $token" }
Invoke-RestMethod http://localhost:5243/api/auth/me -Headers $headers
```

| userId | 密码 | 角色 |
|---|---|---|
| `u_demo01` / `u_demo02` / `u_demo03` | `demo1234` | user |
| `staff_01` | `staff1234` | staff |
| `audit_01` | `audit1234` | auditor |
| `admin_01` | `admin1234` | admin |

## 4. 运行测试

```powershell
# 单元测试：当前已实测 149 项
dotnet test src/UnitTests/UnitTests.csproj -c Release

# 以下需要 Host 与 MockBank 已启动；检查数量以程序最终输出和 CI 为准
dotnet run --project src/E2ETest -c Release -- http://localhost:5243 http://localhost:5200
dotnet run --project src/StressTest -c Release -- http://localhost:5243 http://localhost:5200
dotnet run --project src/LoadTest -c Release -- http://localhost:5243 http://localhost:5200

# 插件契约门禁
dotnet run --project src/PluginValidator -c Release -- `
  src/src/BankingAgent.Host/bin/Release/net8.0/plugins
```

测试套件每次运行生成唯一 `sessionId`，默认可连续重跑，无需清库。只有手工复用同一
`sessionId` 时，转账幂等保护才会返回原处理结果。

## 5. Host API 速查（Current）

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/health` | 完整健康检查，含数据库真实探活 |
| GET | `/health/ready` | 就绪探针，数据库异常返回 503 |
| GET | `/health/live` | 存活探针，不查数据库 |
| POST | `/api/auth/token` | 获取 JWT |
| GET | `/api/auth/me` | 当前身份 |
| GET | `/api/plugins` | 插件清单 |
| GET | `/api/plugins/agents` | Agent 路由表 |
| POST | `/api/orchestrate` | Supervisor 多 Agent 编排 |
| GET | `/api/trajectory/{sessionId}` | 指定会话轨迹 |
| GET | `/api/trajectory?limit=200` | 最近轨迹 |
| GET | `/api/plugins/compliance` | 合规规则 |
| GET | `/api/plugins/events` | 事件总线状态与死信 |
| POST | `/api/plugins/{pluginId}/stop` | 停用插件，仅 admin |
| POST | `/api/plugins/{pluginId}/start` | 启用插件，仅 admin |
| POST | `/api/chat` | 单跳对话入口 |
| POST | `/api/chat/confirm` | 转账人工确认 |

完整字段见 [`02-api-reference.md`](02-api-reference.md)。目标态 `/v1/*` 见
[`../02-api/01-rest-api-spec.md`](../02-api/01-rest-api-spec.md)，不要将其当成当前 Host 路由。

## 6. MockBank API 速查

前缀：`http://localhost:5200/api/corebank/v1`

- `GET /accounts?userId=`、`GET /accounts/{no}/balance`、`GET /accounts/{no}/limit`
- `GET /beneficiaries?keyword=`
- `POST /transfers`
- `GET /transactions?userId=`、`GET /transactions/{txNo}`
- `GET /statements/{no}?month=2026-09`
- `GET /cards?userId=`、`GET /cards/{no}`、`POST /cards/{no}/status|limit`
- `GET /products`、`GET /products/{code}`、`POST /wealth/subscribe`
- `GET /openapi/v1.json`

故障注入只在 Development（或显式开启配置）生效，详见实测 API 参考。

## 7. 调用示例

```powershell
$body = @{
  message = "我有哪些卡"
  userId = "u_demo01"
  sessionId = "demo-$([guid]::NewGuid().ToString('N'))"
} | ConvertTo-Json

Invoke-RestMethod http://localhost:5243/api/chat -Method Post `
  -Headers $headers -ContentType "application/json; charset=utf-8" -Body $body
```

资金操作返回 `requiresHumanInLoop: true` 时，使用同一个 `sessionId` 调
`/api/chat/confirm`。确认前不会提交资金副作用。

## 8. 数据、安全与当前边界

- 已实现 JWT、RBAC、越权防护和分级限流；生产仍需统一身份认证与 MFA/OTP。
- 审计为 HMAC 链式签名，双写 JSONL 文件与独立审计数据库；异地 WORM 归档待实现。
- `TransferRecord` 的 L3 字段已用 `[Encrypted]` 标注，字段加密链路已接通；生产仍需 KMS 与库静态加密。
- 已有 EF Core 基线 Migration；生产按迁移流程部署。
- 事件总线当前为 `DomainEvent` + `InMemoryEventBus`，不跨实例、不持久化。
- FeatureFlag 只在插件 Manifest 声明，运行时强制服务未实现，不能作为安全控制。
- Wealth 只读产品查询/推荐/余额已实现，申购赎回未实现。

## 9. 相关文档

- 插件接入：[`01-plugin-onboarding-guide.md`](01-plugin-onboarding-guide.md)
- Current API：[`02-api-reference.md`](02-api-reference.md)
- 实现状态：[`03-implementation-status.md`](03-implementation-status.md)
- Target API：[`../02-api/01-rest-api-spec.md`](../02-api/01-rest-api-spec.md)
- Current/Target 契约：[`../09-uml/03-api-contract.md`](../09-uml/03-api-contract.md)
