# 快速运行指南（Run Guide）

> **环境**：.NET 8 SDK（本机 .NET 10.0.400 可编译 net8.0 目标）
> **验证状态**：端到端测试 30/30 全部通过

---

## 1. 架构总览

```
┌──────────────────────────────────────────────────────┐
│  BankingAgent.Host  :5243   宿主（零业务逻辑）        │
│  ├─ 扫描 plugins/ 目录热加载                          │
│  ├─ /api/plugins  插件管理                           │
│  └─ /api/chat     对话入口                           │
├──────────────────────────────────────────────────────┤
│  BankingAgent.Base  框架基础设施                      │
│  ├─ PluginRegistry  可回收 ALC + 依赖拓扑             │
│  ├─ AgentRouter     最长前缀意图路由                  │
│  ├─ InMemoryEventBus 插件间零耦合通信                 │
│  ├─ BankingDbContext 审计字段 + 插件分区              │
│  └─ ComplianceGuard / AuditLogger / DataMasker       │
├──────────────────────────────────────────────────────┤
│  plugins/  3 个插件 DLL（独立编译、独立部署）         │
│  ├─ banking.transfer  转账（L3，HITL 强校验）         │
│  ├─ banking.bill      账单（只读，脱敏）              │
│  └─ banking.card      卡片（写操作，依赖声明）         │
└──────────────────────────────────────────────────────┘
                    ⬡ HTTP :5200
┌──────────────────────────────────────────────────────┐
│  MockBank.Api  模拟银行核心系统                        │
│  3 客户 / 6 账户 / 90+ 流水 / 6 只理财 / 卡状态变更    │
└──────────────────────────────────────────────────────┘
```

---

## 2. 一键启动

### 2.1 首次编译

```powershell
cd G:\cunchu\大学\poject\ATM\src
dotnet build BankingAgent.slnx
```

### 2.2 启动模拟银行（端口 5200）

```powershell
cd G:\cunchu\大学\poject\ATM\src\mock-bank\MockBank.Api
dotnet run --urls http://localhost:5200
```

### 2.3 启动 AI 宿主（端口 5243）

新开一个终端：

```powershell
cd G:\cunchu\大学\poject\ATM\src\src\BankingAgent.Host
dotnet run
```

启动日志应显示：

```
Bootstrap  扫描插件目录: ...\plugins
Bootstrap  已加载 3 个插件: banking.transfer, banking.bill, banking.card
DatabaseInitializer  数据库初始化完成。Provider=...Sqlite, 分区=[plugin_transfer]
BankingAgent.Host  AI Banking Agent 宿主就绪，插件数 3
Now listening on: http://localhost:5243
```

### 2.4 验证

```powershell
Invoke-RestMethod http://localhost:5243/health
# => status: healthy, plugins: 3
```

### 2.5 获取访问令牌

除 `/health` 与 `/api/auth/token` 外，所有端点都需要 JWT 令牌。

```powershell
$login = @{ userId = "u_demo01"; password = "demo1234" } | ConvertTo-Json
$token = (Invoke-RestMethod http://localhost:5243/api/auth/token -Method Post `
  -ContentType "application/json" -Body $login).token
```

**演示账号**

| userId | 密码 | 角色 |
|--------|------|------|
| `u_demo01` / `u_demo02` / `u_demo03` | `demo1234` | user |
| `staff_01` | `staff1234` | staff（可代客） |
| `audit_01` | `audit1234` | auditor（可查审计） |
| `admin_01` | `admin1234` | admin（可管理插件） |

---

## 3. 测试

### 3.1 单元测试

```powershell
cd G:\cunchu\大学\poject\ATM\src
dotnet test UnitTests
# 预期：112 个断言全部通过
```

### 3.2 端到端测试

```powershell
cd G:\cunchu\大学\poject\ATM\src
dotnet run --project E2ETest
```

覆盖 11 个场景，38 项断言：

| # | 场景 | 验证点 |
|---|------|-------|
| 0 | 环境连通性 | 两个服务在线 |
| 0B | **JWT 鉴权与越权防护** | 签发令牌、无令牌拒绝、伪造令牌拒绝、**越权 403**、RBAC 分级 |
| 1 | 查询银行卡 | 意图路由 + **L3 卡号脱敏** |
| 2 | 月度账单 | 只读场景 + 账户号脱敏 + 分类占比 |
| 3 | 小额转账 800 | 直接执行 + **余额真实扣减** |
| 4 | 大额转账 30000 | **触发人工回环 + 不扣款** |
| 5 | 人工确认 | **HITL 闭环 + 确认后扣款** |
| 6 | 转给自己 | **合规拒绝 + 无副作用** |
| 7 | 80000 元 | **反洗钱阈值要求复核** |
| 8 | 重复提交 | **幂等生效，不重复扣款** |
| 9 | 事件总线 | **跨插件联动事件已发布** |
| 10 | 审计链 | **HMAC 链式签名 + 用户 ID 哈希** |

### 3.3 并发压力测试

```powershell
dotnet run --project StressTest
```

覆盖 6 个场景，29 项断言，详见 [`03-implementation-status.md`](03-implementation-status.md)。

### 3.4 重跑前置

**幂等键会阻止重复扣款**，重跑前须清数据库：

```powershell
cd G:\cunchu\大学\poject\ATM\src\src\BankingAgent.Host
# 停掉宿主后删除
bankingagent.db
bankingagent.db-wal
bankingagent.db-shm
```

模拟银行每次重启自动重置种子数据，无需手动清理。

---

## 4. API 速查

### 宿主 :5243

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/health` | 健康检查 |
| GET | `/api/plugins` | 插件清单（含版本、场景、依赖） |
| GET | `/api/plugins/agents` | Agent 路由表 |
| GET | `/api/plugins/compliance` | 已加载合规规则 |
| GET | `/api/plugins/events` | 事件总线状态 + 最近事件 |
| POST | `/api/plugins/{id}/stop` | 停用插件 |
| POST | `/api/plugins/{id}/start` | 启用插件 |
| POST | `/api/chat` | 对话入口 |
| POST | `/api/chat/confirm` | 人工确认（完成 HITL 闭环） |

### 模拟银行 :5200

| 方法 | 路径 |
|------|------|
| GET | `/health` |
| GET | `/api/corebank/v1/accounts?userId=` |
| GET | `/api/corebank/v1/accounts/{no}/balance` |
| POST | `/api/corebank/v1/transfers` |
| GET | `/api/corebank/v1/transactions?userId=` |
| GET | `/api/corebank/v1/statements/{no}?month=2026-09` |
| GET | `/api/corebank/v1/cards?userId=` |
| POST | `/api/corebank/v1/cards/{no}/status` |
| GET | `/api/corebank/v1/products` |
| POST | `/api/corebank/v1/wealth/subscribe` |
| GET | `/openapi/v1.json` |

**故障注入**：任意请求加头 `X-Mock-Scenario: insufficient_funds | daily_limit_exceeded | account_frozen | timeout | downstream_error`

---

## 5. 调用示例

### 5.1 普通对话

```powershell
$b = [System.Text.Encoding]::UTF8.GetBytes('{"message":"\u6211\u6709\u54ea\u4e9b\u5361","userId":"u_demo01"}')
Invoke-RestMethod http://localhost:5243/api/chat -Method Post `
  -ContentType "application/json; charset=utf-8" -Body $b
```

> 中文请用 `\uXXXX` 转义或 UTF-8 编码字节，避免 PowerShell 5.1 编码问题。

### 5.2 转账（触发人工回环）

```json
POST /api/chat
{
  "message": "给6222020200000003转账30000元",
  "userId": "u_demo01",
  "sessionId": "unique-session-001",
  "slots": { "to_account": "6222020200000003", "amount": 30000 }
}
```

返回 `requiresHumanInLoop: true` 后，用同一 `sessionId` 确认：

```json
POST /api/chat/confirm
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

---

## 6. 演示账号

| 用户 | 姓名 | 风险等级 | 储蓄卡余额 | 信用卡额度/已用 |
|------|------|---------|-----------|--------------|
| `u_demo01` | 张明 | R3 | 250,000.00 | 50,000 / 12,800 |
| `u_demo02` | 李华 | R2 | 88,000.50 | 30,000 / 3,200 |
| `u_demo03` | 王芳 | R4 | 152,000.00 | 80,000 / 45,000 |

卡号：`6222020200000001` ~ `6222020200000006`

---

## 7. 合规阈值（可在 appsettings.json 调整）

```json
"Compliance": {
  "TransferAutoApproveLimit": 5000,    // 超过此额需人工确认
  "TransferDailyLimit": 200000,        // 单日累计上限
  "AmlReportThreshold": 50000,         // 反洗钱大额报告阈值
  "TransferHardCap": 500000,           // 硬上限，超过直接拒绝
  "HighFrequencyThreshold": 5          // 高频操作阈值
}
```

> 阈值全部来自配置，改阈值不改代码，符合央行 AI 指导意见「参数可审计」要求。

---

## 8. 切换到 PostgreSQL

```json
"Database": {
  "Provider": "PostgreSql",
  "ConnectionString": "Host=localhost;Database=banking;Username=app;Password=***"
}
```

并安装 provider：

```powershell
cd G:\cunchu\大学\poject\ATM\src\src\BankingAgent.Base
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL
```

未安装时框架会给出明确报错指引，不会静默失败。

---

## 9. 目录结构

```
src/
├── BankingAgent.slnx
├── src/
│   ├── BankingAgent.Plugin.Sdk/       # 契约层（插件唯一需引用的程序集）
│   │   ├── PluginContracts.cs         # 插件身份、依赖、生命周期
│   │   ├── AgentContracts.cs          # Agent、工具（Function Call）
│   │   ├── EventContracts.cs          # 领域事件
│   │   └── CoreBankContracts.cs       # 银行系统抽象（屏蔽模拟/真实差异）
│   ├── BankingAgent.Base/             # 框架基础设施
│   │   ├── Agents/                    # Agent 基类、路由器、槽位读取
│   │   ├── Data/                      # DbContext、工作单元、工厂、实体基类
│   │   ├── Events/                    # 事件总线
│   │   ├── Plugins/                   # 插件加载器（可回收 ALC）
│   │   ├── Security/                  # 审计链、脱敏、合规守卫
│   │   ├── CoreBank/                  # 银行系统 HTTP 客户端
│   │   └── BankingCoreServiceCollectionExtensions.cs  # 一行装配
│   └── BankingAgent.Host/             # 宿主
│       ├── Program.cs                 # 入口：装配 → 加载插件 → 暴露 API
│       └── appsettings.json
├── plugins/
│   ├── BankingAgent.Plugin.Transfer/      # 资金操作完整范例
│   ├── BankingAgent.Plugin.BillAnalysis/  # 只读 + 事件订阅范例
│   └── BankingAgent.Plugin.CardManagement/# 写操作 + 依赖声明范例
├── mock-bank/
│   └── MockBank.Api/                 # 模拟银行核心系统
├── UnitTests/                        # 单元测试（112 断言）
├── E2ETest/                          # 端到端测试（38 断言）
└── StressTest/                       # 并发压测（29 断言）
```

---

## 10. CI 与协作

| 机制 | 位置 | 作用 |
|------|------|------|
| CI 流水线 | `.github/workflows/ci.yml` | 编译 + 单测 + 静态检查 + 集成测试 + 汇总门禁 |
| CODEOWNERS | `.github/CODEOWNERS` | 按路径绑定 owner，PR 自动通知 |
| 忽略规则 | `.gitignore` | 排除构建产物、依赖、密钥 |

**首次使用需做的配置**（详见 [`03-implementation-status.md` §9.3](03-implementation-status.md)）：

1. 把 CODEOWNERS 中的 `@architect` 等占位符替换为真实 GitHub 用户名
2. 推送到远程仓库（CI 与 CODEOWNERS 仅在远程生效）
3. 在 GitHub 上手动触发一次 CI 确认可用
4. 通过环境变量配置 `Jwt__SigningKey`（禁止入库）

---

## 10. 安全合规设计要点

| 要求 | 实现 |
|------|------|
| **L3/L4 数据脱敏** | `DataMasker` 强制脱敏，L4 永不返回明文 |
| **审计不可篡改** | HMAC-SHA256 链式签名，篡改任一条即链断裂 |
| **审计用户哈希** | 审计文件中 actor 为 SHA256 前 12 位 |
| **资金人工回环** | 超 5000 元强制 `PendingApproval`，无副作用 |
| **反洗钱筛查** | 超 50000 元要求人工复核 |
| **幂等保护** | `IdempotencyKey` 唯一索引，防重复扣款 |
| **数据分区隔离** | 每插件独立 Schema / 表前缀 |
| **审计字段自动填充** | `CreatedAt/By`、`UpdatedAt/By`、`RowVersion` |
| **软删除** | `IsDeleted` 标记，禁止物理删除 |
| **参数可审计** | 合规阈值全部来自配置 |

对应文档：
- 数据分级：[`docs/05-security-compliance/03-data-classification.md`](../../docs/05-security-compliance/03-data-classification.md)
- 审计规范：[`docs/05-security-compliance/04-audit-logging.md`](../../docs/05-security-compliance/04-audit-logging.md)
- 合规矩阵：[`docs/05-security-compliance/02-compliance-matrix.md`](../../docs/05-security-compliance/02-compliance-matrix.md)

---

## 11. 已知限制

1. **意图识别用关键词而非 LLM** — `DetectIntent` 是演示实现，生产应替换为 Qwen3/DeepSeek 分类器
2. **内存事件总线** — 进程内，不跨实例；生产应换 Kafka（见 ADR-004）
3. **Mock Bank 数据在内存** — 重启即重置，符合模拟定位
4. **SQLite 单文件** — 不支持真正的 Schema 隔离；PostgreSQL 下每插件独立 Schema
5. **无鉴权中间件** — 当前所有请求匿名，生产须接 JWT + MFA（见威胁模型 S-01）
6. **审计仅文件 + 内存** — 生产须落独立库表并做异地归档

---

## 12. 参考

- 插件接入：[`01-plugin-onboarding-guide.md`](01-plugin-onboarding-guide.md)
- 扩展点设计：[`../../docs/00-architecture/06-extension-points.md`](../../docs/00-architecture/06-extension-points.md)
- 模块边界：[`../../docs/00-architecture/05-module-boundaries.md`](../../docs/00-architecture/05-module-boundaries.md)
- 业务开发分册：[`../../docs/07-team/by-role/backend.md`](../../docs/07-team/by-role/backend.md)
