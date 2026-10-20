# AI Banking Agent

> 人工智能赛道参赛作品：**用自然语言对话完成银行业务**的 Agent 系统。
>
> 架构：**模块化单体 + 插件化扩展 + 领域驱动设计 + 事件驱动**
> 技术栈：.NET 8（C#）、EF Core、SQLite / PostgreSQL、ASP.NET Core Minimal API
> 当前状态：**可运行基座**。单元测试 188 项已实测通过；端到端（70 断言）与并发压测（31 项）已在本机实测全绿（E2E 需干净的 MockBank 库，见 `docs/03-development/04-testing-strategy.md`），CI 持续复验。

---

## 一句话介绍

用户打开对话界面，说一句"给张三转 500 块，备注房租"，Agent 理解意图、自主规划、调用银行工具、生成确认卡片，用户点头才真正执行。账单分析、卡片管理、理财同理。

**核心设计立场：Agent 没有执行权。** 资金类操作一律先返回"待人工确认"，用户显式确认后才会提交副作用。这不是限制，而是金融场景的合规底线。

**两个近期重点能力**：

- **跨行卡片**：模拟三个彼此独立的银行核心系统（工行 / 建行 / 招行），**每家银行一个独立数据库文件**。一个客户在三家行各有一张卡，一次查询全部返回；跨行转账按行间清算双边记账。→ [`docs/13-database/02-multi-bank-databases.md`](docs/13-database/02-multi-bank-databases.md)
- **理财 + 公募基金**：除银行自营理财外，还还原了**公募基金代销清单**——真实 6 位基金代码、基金公司、基金经理、七类基金类型、R1-R5 风险等级、管理/托管/申购/赎回费率、规模、业绩基准、90 个自然日的净值序列与三类排行；风险匹配按**「基金 × 代销行」逐对**判断，R2 客户在评级 R2 的行买 R4 产品会被拦下。
- **语义驱动**：说"我的卡不见了"会真的去挂失，而不是回一张卡片列表；说"改成 800"能接上上一轮的转账意图。→ [`docs/plugin/03-implementation-status.md`](docs/plugin/03-implementation-status.md)

---

## 团队与分工

> **完整分工与插件对照表见 [`docs/07-team/04-分工与插件对照表.md`](docs/07-team/04-分工与插件对照表.md)**
> 下表是入口版；改动分工请改那份文档，避免两处不一致。

| 代号 | 角色 | 主责模块 | 主要 AI 工具 |
|---|---|---|---|
| **A** | 架构师 / 技术负责人 | 插件契约、框架层、宿主装配、数据层、CI、跨场景联动 | WorkBuddy |
| **B** | 业务开发 | 智能转账插件、模拟银行、部署材料、仓库维护 | Codex |
| **C** | AI / 数据开发 | 账单分析插件、意图与编排、Mock 数据、演示视频 | WorkBuddy |
| **D** | 安全 / 合规 | 脱敏、审计链、鉴权与限流、字段加密、安全自评报告 | Cursor |
| **E** | 前端 / 答辩 | Web 界面与卡片渲染、答辩 PPT、演讲稿 | Cursor |

### 插件 ↔ 负责人对照（核心表）

| 插件 ID | 显示名 | 负责人 | 意图前缀 | 状态 |
|---|---|---|---|---|
| `banking.transfer` | 智能转账 | B | `transfer`, `transfer.execute` | ✅ 完整实现，资金操作参考实现 |
| `banking.bill` | 账单分析 | C | `bill`, `bill.summary` | ✅ 完整实现 |
| `banking.card` | 卡片管理 | B | `card`, `card.status` | ✅ 完整实现 |
| `banking.wealth` | 理财 | B | `wealth`, `wealth.products`, `wealth.balance` | ✅ 已实现（只读：产品查询与推荐、余额查询） |

> 每个人都是“场景 Owner”：从插件代码、到测试用例、到 PPT 里那两页，全包。

---

## 系统组成（两个独立进程）

```text
BankingAgent.Host :5243  宿主（零业务逻辑）
  - 扫描 plugins/ 目录加载插件
  - JWT 鉴权 + RBAC + 速率限制
  - 审计链（JSONL + 链签名（**权威记录**，请求返回前同步落盘）+ `audit_events`（**可检索副本**，有界队列批量异步提交，读前 flush））
  - /api/chat 对话入口
  - /api/ai/probe AI 连通性自检

BankingAgent.Base  框架层（零业务）
  - PluginRegistry     可回收 AssemblyLoadContext 加载
  - AgentRouter        最长前缀意图路由 + 规则表加权消歧
  - InMemoryEventBus   插件间零耦合通信
  - BankingDbContext   插件分区隔离 + 字段级加密
  - AiConnectivityProbe 让「AI 通没通」成为可验证事实
  - SessionMemory      多轮省略句的上下文继承
  - ComplianceGuard / AuditLogger / DataMasker / 限流

plugins/  4 个插件 DLL（独立编译、独立部署）
  - banking.transfer  转账（L3，强制人工回环，支持跨行）
  - banking.bill      账单（只读，强制脱敏）
  - banking.card      卡片（写操作，依赖声明，语义驱动状态变更）
  - banking.wealth    理财（只读：产品查询与推荐、余额查询、公募基金）

MockBank.Api :5200  多银行模拟核心系统（独立进程）
  3 家银行，每家一个独立 SQLite 数据库文件
  3 客户 / 9 账户 / 12 跨行卡片 / 150+ 流水 / 理财产品 / 公募基金
  跨行转账（双边记账 + 行间清算）
  6 种故障注入（仅 Development，见下文安全说明）
```

---

## 目录结构

```text
ATM/
├── src/                                    <- 当前交付主线（.NET）
│   ├── BankingAgent.slnx                   解决方案（.slnx 需 SDK 9.0.200+）
│   ├── src/
│   │   ├── BankingAgent.Plugin.Sdk/        插件契约（唯一稳定依赖面）
│   │   ├── BankingAgent.Base/              框架层：Agents/Ai/Data/Security/Plugins/Events
│   │   └── BankingAgent.Host/              宿主：装配 + 端点（Endpoints/ 按职责分文件）
│   ├── plugins/                            4 个业务插件（各自独立程序集）
│   ├── mock-bank/MockBank.Api/             多银行模拟核心系统（:5200）
│   │   └── data/                           各行独立数据库文件（运行后生成）
│   ├── templates/banking-plugin/           [*] dotnet new 插件脚手架
│   ├── PluginValidator/                    [*] 插件契约校验器（CI 门禁）
│   ├── UnitTests/                          单元测试（当前 188 项）
│   ├── E2ETest/                            端到端场景测试（17 个场景）
│   ├── StressTest/                         并发与稳定性压测
│   ├── LoadTest/                           容量阶梯加压
│   ├── CryptoSelfTest/                     密码学自检（ML-KEM/ML-DSA）
│   ├── DbProbe/                            数据库诊断小工具
│   └── .config/dotnet-tools.json           锁定 dotnet-ef 版本
├── docs/                                   文档体系（见下）
├── papers/                                 参考文献与法规清单
├── scripts/                                辅助脚本
└── .github/                                CI 流水线 + CODEOWNERS + PR/Issue 模板
```

> **对话控制台**：宿主自带一个零构建的单页控制台（`src/src/BankingAgent.Host/wwwroot/`），
> 启动后直接访问 <http://localhost:5243> 即可登录、发消息、看确认卡片与插件清单。
> 早期的 Node + TypeScript 版 MVP 已删除，当前唯一交付主线是 `src/`。

---

## 快速开始

### 前置条件

- **.NET SDK 9.0.200+**（解决方案是 `.slnx` 格式；各项目本身目标 `net8.0`）
- 无需数据库、无需 AI Key

### 从克隆到运行

先把仓库克隆到本地并进入仓库根目录。已配置 GitHub SSH Key 的成员使用：

```bash
git clone git@github.com:seevwis43825/ATM.git
cd ATM
```

未配置 SSH Key 时，可改用 HTTPS（私有仓库仍需登录有权限的 GitHub 账号）：

```bash
git clone https://github.com/seevwis43825/ATM.git
cd ATM
```

首次运行先进入宿主目录保存 DeepSeek Key，再返回 `ATM` 仓库根目录：

```bash
# 1. 配置 DeepSeek API Key（首次运行或更换 Key 时执行）
cd src/src/BankingAgent.Host
dotnet user-secrets set "Ai:ApiKey" "自己的 DeepSeek API Key"
cd ../../..

# 2. 编译
dotnet build src/BankingAgent.slnx -c Release

# 3. 终端一：启动模拟银行（:5200）
dotnet run --project src/mock-bank/MockBank.Api -c Release

# 4. 终端二：启动 AI 宿主（:5243）
dotnet run --project src/src/BankingAgent.Host -c Release
```

`appsettings.json` 已配置 DeepSeek 模型及相关参数，因此这里只需设置 Key；同一个 Key 后续无需重复设置。

然后打开 **<http://localhost:5243>** 使用内置对话控制台（登录 → 发消息 → 人工确认）。

验证：

```bash
curl http://localhost:5200/health
curl http://localhost:5243/health        # 含真实数据库健康检查
```

> **插件目录为什么会"不完整"（两种成因，构建都不报错）**
>
> 1. **配置不一致**：插件复制是"配置相关"的 —— 用 Release 编译却用默认 Debug 启动，
>    `plugins/` 会是空的。**启动务必带 `-c Release`**。
> 2. **构建顺序竞态**：复制目标是 `AfterTargets="Build"` 且带 `Exists` 条件，
>    而插件与宿主之间原先没有任何顺序依赖 —— 解决方案并行构建时宿主可能先完成、
>    此时插件 DLL 还没生成，复制被**静默跳过**，于是"缺了某个插件"，
>    直到启动才抛 `插件 banking.card 依赖的 banking.transfer 未找到`。
>    （已在 `BankingAgent.Host.csproj` 用 `ProjectReference ReferenceOutputAssembly="false"`
>    声明顺序依赖修掉；CI 另有一道"插件产物完整性"校验。）
>
> 两种情况的共同点：**构建期不报错，只在启动时暴露**。排查时先看
> `bin/<配置>/net8.0/plugins/` 下 4 个 DLL 是否都在。

### 跑测试

```bash
# 单元测试
dotnet test src/UnitTests/UnitTests.csproj                    # 当前 188 项单元测试

# 以下需要两个服务已启动
dotnet run --project src/E2ETest   -- http://localhost:5243 http://localhost:5200
dotnet run --project src/StressTest -- http://localhost:5243 http://localhost:5200
dotnet run --project src/LoadTest  -- http://localhost:5243 http://localhost:5200   # 容量阶梯

# 插件契约校验（CI 阻断门禁）
dotnet run --project src/PluginValidator -- src/src/BankingAgent.Host/bin/Release/net8.0/plugins
```

> **重跑前置**：转账幂等键会落库（键 = 会话 + 付款方 + 收款方 + 金额）。测试套件本身已使用每次运行唯一的 sessionId，连续重跑无需手动清库；
> 但若你复用同一个 sessionId，第二次会被幂等拦截。必要时删除 `src/src/BankingAgent.Host/bankingagent*.db*` 清库。

---

## 接口清单（宿主 :5243）

所有 `/api/*` 端点需 `Authorization: Bearer <token>`；token 从 `/api/auth/token` 获取。

| 方法 | 路径 | 用途 | 鉴权 |
|---|---|---|---|
| GET | `/health` | 完整健康状态（含数据库真实探活 + AI 连通性自检结果） | 公开 |
| GET | `/health/ready` | 就绪探针（数据库不可用返回 503） | 公开 |
| GET | `/health/live` | 存活探针（不查数据库） | 公开 |
| POST | `/api/auth/token` | 获取 JWT 令牌 | 公开（限流 5/分钟） |
| GET | `/api/auth/me` | 查询当前身份 | 需令牌 |
| POST | `/api/chat` | **对话入口** | 需令牌 |
| POST | `/api/chat/confirm` | 人工确认 / 拒绝敏感操作 | 需令牌 |
| POST | `/api/orchestrate` | 多 Agent 编排（Supervisor） | 需令牌 |
| POST | `/api/ai/probe` | **AI 连通性自检**（真实调一次模型并回报结果） | 需令牌 |
| GET | `/api/plugins` | 插件清单 | 需令牌 |
| GET | `/api/plugins/agents` | 路由表（Agent 与意图） | 需令牌 |
| GET | `/api/plugins/compliance` | 已加载合规规则 | 需令牌 |
| GET | `/api/plugins/events` | 事件总线状态与死信 | 需令牌 |
| POST | `/api/plugins/{id}/stop` | 停用插件（热插拔） | 仅管理员 |
| POST | `/api/plugins/{id}/start` | 启用插件 | 仅管理员 |
| GET | `/api/trajectory/{sessionId}` | 轨迹回放 | 需令牌 |
| GET | `/api/trajectory` | 最近轨迹 | 需令牌 |

> **模拟银行（:5200）新增端点**：
>
> - `GET /api/corebank/v1/banks`（银行目录，含各行独立库文件与数据规模）、`GET /api/corebank/v1/banks/{bankCode}`、`POST /api/corebank/v1/_admin/reset`（重置各行数据库并重写种子）
> - `GET /api/corebank/v1/customers/{userId}`（客户档案，含**行内**风险评级，用于理财风险匹配）
> - `GET /api/corebank/v1/funds`（公募基金代销清单）、`/funds/{code}`、`/funds/{code}/nav`、`/funds/rankings`
>
> 完整清单见 [`docs/plugin/02-api-reference.md`](docs/plugin/02-api-reference.md)。

### 试一次对话

```bash
# 1. 取令牌（注意：该端点限流 5 次/分钟）
TOKEN=$(curl -s -X POST http://localhost:5243/api/auth/token \
  -H "Content-Type: application/json" \
  -d '{"userId":"u_demo01","password":"demo1234"}' | grep -o '"token":"[^"]*"' | cut -d'"' -f4)

# 2. 发一句话
curl -s -X POST http://localhost:5243/api/chat \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"message":"我有哪些卡","userId":"u_demo01","sessionId":"demo-1"}'
```

演示账号（见 `Host/Program.cs` 的 `DemoCredentials`）：
`u_demo01/demo1234`（用户）、`staff_01/staff1234`（客服）、`audit_01/audit1234`（审计员）、`admin_01/admin1234`（管理员）。

---

## 已实现的关键设计

| 设计 | 说明 | 状态 |
|---|---|---|
| **Agent 无执行权** | 资金操作返回 `PendingApproval`，用户确认后才提交 | ✅ 端到端验证 |
| **三级风险分级** | 合规规则决定 ALLOW / REQUIRE_APPROVAL / DENY | ✅ 4 条内置规则 |
| **全链路审计** | HMAC 链式签名，JSONL + 链签名（**权威记录**，请求返回前同步落盘）+ `audit_events`（**可检索副本**，有界队列批量异步提交，读前 flush） | ✅ 实测签名链逐条衔接 |
| **数据脱敏** | L1-L4 分级，L4 永不返回明文 | ✅ 29 项单测 |
| **字段级加密** | `[Encrypted]` 标注，支持确定性加密（可等值查询） | ✅ 已标注 L3 字段 |
| **插件化扩展** | 新场景 = 新插件，无需改宿主 | ✅ 模板 + 校验器 + 实测 |
| **数据库迁移** | 基线迁移 + 设计时插件发现 + 自动建 Schema | ✅ 已生成 |
| **多 Agent 编排** | Supervisor 编排器 + 轨迹日志（支持回放/分叉） | 🟡 缺 LLM 规划 |
| **意图识别** | 规则表（插件自报关键词）打底；配置 `Ai:ApiKey` 后由**大模型**判定，失败自动降级 | ✅ 两种模式均可运行 |
| **意图分场景/动作两层** | 场景决定路由，动作决定分支（只读 vs 写） | ✅ 已实现 |
| **语义驱动写操作** | 「我的卡不见了」→ 挂失；「别挂失」→ 不动 | ✅ 单元测试锁定 |
| **AI 连通性可验证** | `POST /api/ai/probe` 真实调模型并回报链路结果 | ✅ 已实现 |
| **多轮上下文** | 「改成 800」能接上上一轮的意图与收款人 | ✅ 端到端验证 |
| **跨行卡片** | 3 家银行各自独立数据库，卡片一次跨库聚合返回 | ✅ 端到端验证 |
| **跨行转账清算** | 双边记账、渠道标记「跨行转账」、双边流水号 | ✅ 端到端验证 |
| **公募基金代销** | 20 只基金主档 + 90 日净值序列 + 三类排行 | ✅ 端到端验证 |
| **理财风险匹配** | 按「基金 × 代销行」逐对比对客户行内评级 | ✅ 端到端验证 |
| **代码形式的触发** | 只报一个 6 位基金代码也能路由到理财 | ✅ 端到端验证 |
| **收款人解析** | 说「给李华转 500」即按姓名解析收款账户，无需背账号 | ✅ 端到端验证 |

---

## 安全须知（重要）

1. **故障注入仅在 Development 生效。** MockBank 的 `X-Mock-Scenario` 请求头可强制转账失败或注入延迟；非开发环境默认忽略。若确需在非开发环境演示，显式设 `MockBank:EnableFaultInjection=true` 并确保网络隔离。
2. **密钥不入库。** `appsettings.Development.json` 已被 git 忽略（含 `.example` 模板）。生产必须通过环境变量注入：`Jwt__SigningKey`、`Audit__SigningKey`、`Crypto__DataEncryptionKey`。缺失时 Development 会派生开发密钥并告警；**Production 会拒绝启动**。
3. **限流按档位分级**，认证档 5 次/分钟（防口令爆破）。压测前请先关闭限流：设 `RateLimit__Enabled=false` 环境变量并重启宿主。
4. **审计用户标识只存哈希**，库内与文件内均无明文用户 ID。
5. **仍未做**（生产前必须补）：MFA/OTP、审计异地 WORM 归档、KMS 密钥管理、数据库文件静态加密、合规规则补 P0 4 条。完整清单见 [`docs/plugin/03-implementation-status.md`](docs/plugin/03-implementation-status.md)。

---

## 写一个新插件

```bash
# 安装模板（从 src 目录）
cd src
dotnet new install ./templates/banking-plugin

# 生成骨架
dotnet new banking-plugin -n BankingAgent.Plugin.YourFeature \
  -o plugins/BankingAgent.Plugin.YourFeature \
  --pluginId banking.yourfeature --pluginName 你的场景 \
  --scenario yourfeature --intentPrefix yourfeature --withPersistence true
```

生成后**必须**完成三处手工接线（漏掉任何一处都不会报错，插件只是静默不生效）：

1. 在 `src/BankingAgent.slnx` 的 `/plugins/` 段登记项目
2. 在 `BankingAgent.Host.csproj` 的 `PluginArtifact` 列表补一行
3. 需要落库时注册 `IEntitySetContributor`

详细步骤与排查见 [`docs/plugin/01-plugin-onboarding-guide.md`](docs/plugin/01-plugin-onboarding-guide.md)。

---

## 接入大模型（可选）

**默认不接入也能完整运行** —— 意图识别走规则表（插件自报的关键词）。

### 推荐：使用 .NET User Secrets

从仓库根目录执行：

```bash
dotnet user-secrets set "Ai:ApiKey" "自己的 DeepSeek API Key" --project src/src/BankingAgent.Host/BankingAgent.Host.csproj
```

宿主项目已经声明 `UserSecretsId`，无需另外执行 `dotnet user-secrets init`。
`appsettings.json` 已经配置 DeepSeek 的 `BaseUrl`、`deepseek-chat` 模型及相关参数，
所以使用 DeepSeek 时只需设置 Key。User Secrets 保存在当前操作系统用户的本地配置目录，
不会创建仓库内文件，也不会被 Git 提交。设置完成后重启宿主即可生效。

### 或者用环境变量

```powershell
$env:Ai__ApiKey = "sk-xxx"
dotnet run --project src/src/BankingAgent.Host -c Release
```

> 优先级：**环境变量 > User Secrets > appsettings.Development.json > appsettings.json**。
> 若发现"填了却没生效"，先用 `Test-Path Env:Ai__ApiKey` 确认终端里没有残留的同名变量（空值同样会覆盖配置文件）。

| 配置项 | 说明 |
|---|---|
| `Ai:Enabled` | 总开关，默认 `true`；关闭后完全不调用模型 |
| `Ai:BaseUrl` | OpenAI 兼容基地址，默认 `https://api.deepseek.com/v1` |
| `Ai:Model` | 模型名，默认 `deepseek-chat` |
| `Ai:ApiKey` | 使用 User Secrets（推荐）或环境变量 `Ai__ApiKey` 注入，禁止写入入库配置 |
| `Ai:TimeoutSeconds` | 生成超时，默认 8 秒；连接超时取其一半（上限 3 秒） |

可直接替换的端点：通义千问 `https://dashscope.aliyuncs.com/compatible-mode/v1`、
智谱 `https://open.bigmodel.cn/api/paas/v4`、
本地 Ollama `http://localhost:11434/v1`（ApiKey 填任意占位值，零成本离线演示）。

**接入后的行为（已实测）**

- 候选意图**不是硬编码**的，而是从已注册 Agent（插件）自报的意图与触发关键词生成 → 新增插件零改动即可被模型认识；
- 提示词里的**场景消歧规则也是自动生成的**：每个 Agent 的关键词被归到它自己的场景名下，模型据此知道「账单」和「卡」同时出现时该选哪个；
- 送模型前对用户输入**强制脱敏**（账号 / 手机号 / 身份证），意图识别并不需要这些原文；
- 模型超时、欠费、答非所问、输出越界、**回答 unknown** → 一律降级规则表，接口不返回失败；
  （早期实现里模型答 unknown 会把已经算好的规则表结果整个丢弃，用户只会看到「已转人工客服」——已修复并有单元测试锁定）
- 每次模型调用写审计：`Operation=llm.intent.classify`，含模型名、是否降级、脱敏后的输入。

### 怎么确认「AI 真的通了」

`GET /health` 的 `ai` 字段只说明"配置了没有"，**不能证明模型真的可用**。因此提供了一个自检端点：

```bash
# 真实调用一次模型，把整条链路摊开给你看
curl -X POST http://localhost:5243/api/ai/probe \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"text":"我上个月花了多少钱"}'
```

```json
{
  "modelConfigured": true,
  "callSucceeded": true,
  "mappedToKnownIntent": true,
  "model": "deepseek-chat",
  "rawOutput": "bill.summary",
  "resolvedIntent": "bill.summary",
  "elapsedMs": 412,
  "summary": "模型连通，测试句被识别为 bill.summary"
}
```

自检走的是**与真实意图识别完全相同的提示词**（`IntentPromptBuilder`），
因此「探针通过」就等于「真实链路通」。探针结果会留在 `/health` 的 `ai.lastProbe` 里。

### 意图分两层：场景与动作

| 层 | 取值示例 | 决定什么 |
|---|---|---|
| **场景** | `card` / `bill` / `transfer` / `wealth` | 路由到哪个 Agent |
| **动作** | `card.status` / `transfer.execute` / `bill.summary` | Agent 走只读分支还是写分支 |

过去这两层被压成同一个字符串，产生了一个很隐蔽的故障：
模型已经判定意图是 `card.status`（要改卡状态），但 Agent 内部仍然只按
「原文里有没有『挂失』两个字」来决定要不要写 —— 于是用户说「我的卡不见了」时，
**语义识别对了，功能却没触发**。

现在：

- 模型负责**场景与动作**（`IntentContext` 拆层）；
- 各插件用**语义解析器**决定具体动作（`CardStatusResolver` 认得「丢了 / 不见了 / 被冻了 / 找回来了」），
  且**只认原文里明确表达的动作**——「卡不能用了」这种含糊说法不触发写操作，宁可漏判不可错判；
- 模型判不出时，规则表按**关键词长度加权 + 否定词剔除**兜底（「不要转账」里的「转账」不算命中）。

### 多轮上下文

「给李华转 500」之后接一句「改成 800」，第二句没有任何业务关键词。
`SessionMemory` 记住上一轮的意图、原话与槽位（**按用户隔离**，会话标识由客户端提供，
不与用户绑定就能跨用户读到上下文），第二句因此能路由到转账 Agent，
并从上一句里补出收款人。

> 幂等键因此也改了：早期直接拿 `sessionId` 当键，等于**一个会话里只能发生一次转账**，
> 多轮的「先转 500 再改成 800」第二次会被静默跳过。
> 现在键是「会话 + 操作要素（付款方 / 收款方 / 金额）」：
> 重复提交**同样的**转账仍被幂等拦截，改了金额则正常执行。

---

## 文档索引

| 我想知道 | 看哪份 |
|---|---|
| **谁负责什么 / 插件与人的对照** | [`docs/07-team/04-分工与插件对照表.md`](docs/07-team/04-分工与插件对照表.md) |
| 文档总索引 | [`docs/README.md`](docs/README.md) |
| 怎么跑起来 + 故障排查 | [`docs/plugin/00-quick-start.md`](docs/plugin/00-quick-start.md) |
| 怎么写插件 | [`docs/plugin/01-plugin-onboarding-guide.md`](docs/plugin/01-plugin-onboarding-guide.md) |
| 实测 API 参考 | [`docs/plugin/02-api-reference.md`](docs/plugin/02-api-reference.md) |
| **哪些做了、哪些没做（诚实清单）** | [`docs/plugin/03-implementation-status.md`](docs/plugin/03-implementation-status.md) |
| 代码索引 | [`src/README.md`](src/README.md) |
| 系统架构 | [`docs/00-architecture/`](docs/00-architecture/) |
| 数据模型与迁移 | [`docs/09-uml/04-data-model.md`](docs/09-uml/04-data-model.md)、[`docs/13-database/`](docs/13-database/) |
| 安全与合规 | [`docs/05-security-compliance/`](docs/05-security-compliance/)、[`docs/10-security/`](docs/10-security/) |
| 容量与性能 | [`docs/11-performance/01-capacity-test-report.md`](docs/11-performance/01-capacity-test-report.md) |

---

## 交付物对照表

| # | 交付物 | 硬性要求 | 负责人 | 存放位置 | 当前状态 |
|---|---|---|---|---|---|
| 01 | 技术文档 | PDF/DOCX ≤50MB | A | `docs/01-tech/` | 🟡 仅大纲 |
| 02 | 答辩材料 | PPT/PPTX/PDF ≤80MB | E | `docs/02-ppt/` | 🟡 仅结构稿 |
| 03 | 源代码与部署 | ZIP ≤300MB | B | `docs/03-deploy/` | 🟡 部署说明待按 .NET 更新 |
| 04 | 安全自评报告 | PDF/DOCX ≤50MB | D | `docs/04-security/` | 🟡 仅模板 |
| 05 | 演示视频 | MP4 ≤500MB，≤5 分钟 | C | `docs/05-video/` | 🟡 仅脚本 |

> 五项均为独立计进度，缺一不可。

---

## 协作铁律

1. **不直接推 main。** 所有改动走 `feature/xxx` 分支 + Pull Request。
2. **契约优先。** 改 `BankingAgent.Plugin.Sdk/` 里的任何东西，先在群里说，改完更新版本号。
3. **插件先行。** 每人先把自己的插件跑通，不依赖别人。
4. **文档跟代码同分支提交。** 不要最后一周才补文档。
5. **新增插件必过契约校验。** `PluginValidator` 是 CI 阻断门禁。
