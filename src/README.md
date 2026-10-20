# 代码索引（Source Index）

> **对应设计文档**：[`../docs/00-architecture/00-overview.md`](../docs/00-architecture/00-overview.md)
> **插件接入**：[`../docs/plugin/01-plugin-onboarding-guide.md`](../docs/plugin/01-plugin-onboarding-guide.md)
> **代码规模**：持续变化；以当前仓库和编译结果为准，本文不再把行数作为事实源
>
> 下文表格中的行数是早期快照，仅用于理解各组件相对规模，不用于验收。

---

## 1. 分层与依赖方向

```
                    ┌──────────────────────────┐
   插件层           │  BankingAgent.Plugin.*   │  ← 可热插拔，互不引用
   Plugins/         └────────────┬─────────────┘
                                 │ 只引用 SDK + Base
                    ┌────────────▼─────────────┐
   契约层           │  BankingAgent.Plugin.Sdk │  ← 唯一稳定契约
   src/Plugin.Sdk   └────────────┬─────────────┘
                                 │
                    ┌────────────▼─────────────┐
   框架层           │    BankingAgent.Base     │  ← 零业务逻辑
   src/Base         └────────────┬─────────────┘
                                 │
                    ┌────────────▼─────────────┐
   宿主层           │    BankingAgent.Host     │  ← 零业务逻辑
   src/Host         └──────────────────────────┘

                    ┌──────────────────────────┐
   外部系统         │   MockBank.Api :5200     │  ← 独立进程，HTTP 通信
   mock-bank/       └──────────────────────────┘
```

**强制规则**：

| 规则 | 说明 |
|------|------|
| 插件**禁止**引用 Host | 会造成循环引用，破坏热插拔 |
| 插件**禁止**引用其他插件 | 只能通过事件通信 |
| 框架层**禁止**引用具体业务 | Base 不知道转账、账单是什么 |
| 宿主**禁止**包含业务逻辑 | 业务全在插件里 |
| 外部系统**只能**通过 `ICoreBankClient` 访问 | 切换真实 CBS 时插件零改动 |

---

## 2. 项目清单

### 2.1 BankingAgent.Plugin.Sdk（契约层）

插件唯一需要引用的程序集。稳定契约，一经发布不随意改。

| 文件 | 职责 |
|------|------|
| `PluginContracts.cs` | `IPluginEntryPoint`、`PluginManifest`、`PluginId`、`PluginVersion`、`PluginDependency`、`DataClassification` |
| `AgentContracts.cs` | `IBankingAgent`、`AgentRequest`、`AgentResult`、`IAgentTool`、`ToolInvocation`、`AgentRole` |
| `EventContracts.cs` | `DomainEvent`、`IDomainEventHandler`、`IEventPublisher` |
| `CoreBankContracts.cs` | `ICoreBankClient` + 各类 Snapshot record（Account / Transfer / Transaction / Statement / Card / Beneficiary / Wealth / Bank） |

**关键设计**：`ICoreBankClient` 是抽象层。插件只认这个接口，不认 Mock Bank 的 DTO。切换到真实 CBS 时改 `CoreBankClient` 的实现即可，**插件代码零改动**。

跨行支持相关字段（`BankCode` / `BankName`）也在这层，因为「这张卡是哪家银行发的」是插件必须知道的业务事实。

---

### 2.2 BankingAgent.Base（框架层）

零业务逻辑，只提供能力。

#### Agents/（Agent 基础设施）

| 文件 | 职责 |
|------|------|
| `BankingAgentBase.cs` | Agent 基类：自动计时、审计、异常兜底、`IntentOf()` 取意图上下文 |
| `AgentRouter.cs` | 意图路由（**最长前缀优先**）；规则表反查采用**加权打分 + 否定词剔除**；未命中时返回带能力清单的友好兜底 |
| `IntentContext.cs` | 把意图拆成**场景**（card / bill / transfer / wealth）与**动作**（card.status / transfer.execute）两层 |
| `SessionMemory.cs` | 会话记忆：让「改成 800」这类省略句能接上一轮的意图与槽位；按用户隔离、过期淘汰 |
| `SlotReader.cs` | 槽位读取，兼容 JSON 反序列化产生的 `JsonElement` |
| `AgentOrchestrator.cs` | 多 Agent 编排（Supervisor），产出可回放的轨迹日志 |

#### Ai/（大模型接入与意图识别）

| 文件 | 职责 |
|------|------|
| `IIntentClassifier.cs` | 意图识别契约与 `IntentDecision` |
| `LlmIntentClassifier.cs` | LLM 优先 + 规则兜底；模型判不准时**必定**退回规则表 |
| `IntentPromptBuilder.cs` | 候选意图收集与系统提示词拼装（消歧规则由已注册 Agent 自动生成） |
| `AiConnectivityProbe.cs` | 连通性探针：真实调一次模型，把「AI 通没通」变成可验证的事实 |
| `SensitiveTextMasker.cs` | 送模型前的强制脱敏（账号 / 手机号 / 身份证） |
| `OpenAiCompatibleLlmClient.cs` | OpenAI 兼容协议客户端（DeepSeek / 通义 / 智谱 / Ollama） |
| `LlmOptions.cs` | 接入配置；未配密钥即自动降级规则表 |

#### Plugins/（插件系统）

| 文件 | 职责 |
|------|------|
| `PluginRegistry.cs` | 可回收 `AssemblyLoadContext`、依赖拓扑排序、循环依赖检测、版本兼容校验 |

#### Data/（数据访问）

| 文件 | 职责 |
|------|------|
| `BankingDbContext.cs` | 审计字段自动填充、插件分区映射、脱敏快照 |
| `DbContextFactories.cs` | 运行时工厂 + 设计时工厂（迁移用） |
| `BaseEntity.cs` | 实体基类（CreatedAt/By、RowVersion、IsDeleted） |
| `UnitOfWork.cs` | 事务边界、幂等键登记 |
| `ICurrentUserAccessor` 等 | 当前用户（AsyncLocal）、时钟抽象 |

#### Security/（安全合规）

| 文件 | 职责 |
|------|------|
| `DataMasker.cs` | 脱敏：手机/身份证/银行卡/姓名/邮箱/账号，L4 永不返回明文 |
| `AuditLogger.cs` | HMAC-SHA256 链式签名 + 文件归档 + 用户 ID 哈希 |
| `Compliance/ComplianceGuard.cs` | 合规规则执行器 |
| `Compliance/ComplianceRules.cs` | 4 条内置规则（金额阈值、自转、反洗钱、场景权限） |

#### 其他

| 文件 | 职责 |
|------|------|
| `Events/InMemoryEventBus.cs` | 进程内事件总线，精确匹配 + 通配订阅、死信队列 |
| `CoreBank/CoreBankClient.cs` | 银行系统 HTTP 客户端，DTO 映射与容错 |
| `BankingCoreServiceCollectionExtensions.cs` | **一行装配**：`AddBankingCore()` |

---

### 2.3 BankingAgent.Host（宿主）

宿主只有**装配**与**中间件**两件事，端点实现按职责分文件，避免装配文件膨胀成几百行。

| 文件 | 职责 |
|------|------|
| `Program.cs` | 装配流程：框架 → 插件加载 → 数据库初始化 → 插件启动 → 中间件 → 端点映射 |
| `HostSecurity.cs` | 宿主安全辅助：403 响应、公开端点白名单、角色解析 |
| `PluginLifecycleBridge.cs` | 插件生命周期钩子 → 容器内 `IPlugin` 的桥接 |
| `Contracts/HostContracts.cs` | 对外请求/响应契约 |
| `Endpoints/HealthEndpoints.cs` | `/health`、`/health/ready`、`/health/live` |
| `Endpoints/AuthEndpoints.cs` | 令牌签发、身份查询、演示账号表 |
| `Endpoints/PluginEndpoints.cs` | 插件清单、路由表、合规规则、事件总线、热插拔 |
| `Endpoints/ChatEndpoints.cs` | 对话、人工确认、多 Agent 编排 |
| `Endpoints/TrajectoryEndpoints.cs` | 轨迹回放 |
| `Endpoints/AiEndpoints.cs` | **AI 连通性自检** `/api/ai/probe` |

**为什么分两步（bootstrap 容器 → 正式容器）**：插件需要在容器 Build 之前注册服务。bootstrap 容器只用来拿 `ILoggerFactory` 和 `PluginRegistry`，用完即弃。

---

### 2.4 plugins/（4 个业务插件）

| 插件 | 演示重点 |
|------|---------|
| `BankingAgent.Plugin.Transfer` | 资金操作**完整范例**：槽位抽取 → 合规守卫 → 人工回环 → 幂等检查 → 调核心银行（可跨行）→ 落库 → 发事件 |
| `BankingAgent.Plugin.BillAnalysis` | 只读场景 + **订阅他人事件**实现零耦合联动 |
| `BankingAgent.Plugin.CardManagement` | 写操作但非资金 + **依赖声明** + L3 强制脱敏 + **语义驱动的状态变更**（`CardStatusResolver`） |
| `BankingAgent.Plugin.Wealth` | 只读理财场景：产品查询与推荐、账户余额查询、公募基金查询；申购/赎回尚未实现 |

---

### 2.5 mock-bank/MockBank.Api（模拟银行，多银行核心系统）

一家银行 = 一个独立数据库文件，跨行能力由聚合门面提供。详见 [`../docs/13-database/02-multi-bank-databases.md`](../docs/13-database/02-multi-bank-databases.md)。

| 层 | 职责 |
|----|------|
| `Domain/` | 领域模型：Customer、Account、Transaction、Card、WealthProduct、PublicFund、**BankDescriptor** |
| `Data/` | `BankLedger`（单行账本）、`BankDbContext`（单行 EF 上下文）、`BankLedgerFactory`（建库与种子）、`MockBankStore`（跨行聚合门面）、`BankSeedData`（各行种子） |
| `Contracts/` | 请求/响应 DTO，record 不可变 |
| `Services/` | 账户、转账（含跨行清算）、账单、卡、理财、基金、业务日志、OpenAPI 生成 |
| `Endpoints/` | 端点组：账户、转账、账单、卡、理财、**银行目录**、基金、运维 |
| `Middleware/` | 全局异常处理、故障注入 |

**并发安全**：行内资金变动在**该行**的 `SemaphoreSlim` 锁内完成，获取锁后**重新校验**，避免 TOCTOU；跨行变动按行代码字典序取两把锁，避免死锁。

---

### 2.6 E2ETest（端到端测试）

覆盖鉴权、路由、人工确认、资金副作用、幂等、事件和审计等场景；执行结果以测试程序最终输出与 CI 为准。详见 [`../docs/plugin/00-quick-start.md`](../docs/plugin/00-quick-start.md) §3。

---

## 3. 命名空间约定

| 命名空间 | 归属 | 说明 |
|----------|------|------|
| `BankingAgent.PluginSdk` | SDK | 契约，插件可见 |
| `BankingAgent.Base.<层>` | Base | 框架内部，插件可引用 |
| `BankingAgent.Plugin.<插件>` | 各插件 | 插件私有，外部不可见 |
| `BankingAgent.Host` | Host | 宿主，外部不可见 |
| `MockBank.Api.<层>` | Mock Bank | 独立系统 |

---

## 4. 添加新能力的决策树

```
需要做什么？
│
├─ 新业务功能（转账、账单、理财…）→ 写插件，参考 BankingAgent.Plugin.Transfer
│
├─ 新业务规则（限额、风控…）       → 实现 IComplianceRule，加到 Base
│
├─ 新合规约束（脱敏、审计）       → 改 DataMasker / AuditLogger
│
├─ 新外部系统（风控、征信…）      → 在 SDK 加接口契约，Base 加客户端
│
├─ 新 Agent 类型                   → 继承 BankingAgentBase
│
└─ 修改框架核心                    → ⚠️ 需架构评审 + ADR，影响所有插件
```

**最后一项是红线**：改 Base 会影响所有插件，必须走 [`../docs/00-architecture/04-architecture-decisions.md`](../docs/00-architecture/04-architecture-decisions.md) 流程。

---

## 5. 代码与设计文档对照

| 设计文档 | 对应代码 | 状态 |
|----------|----------|------|
| `06-extension-points.md` | `Base/Plugins/PluginRegistry.cs` | ✅ 已实现 |
| `05-module-boundaries.md` | 各插件独立程序集 | ✅ 已实现 |
| `07-event-driven-contract.md` | `Base/Events/InMemoryEventBus.cs` | ⚠️ 内存版（设计为 Kafka） |
| `02-event-schema.md` | `SDK/EventContracts.cs` | ⚠️ 简化版（未用 CloudEvents 全字段） |
| `02-api/01-rest-api-spec.md` | `Host/Program.cs` | ⚠️ 演示版端点 |
| `03-data-classification.md` | `Base/Security/DataMasker.cs` | ✅ 已实现 |
| `04-audit-logging.md` | `Base/Security/Audit/AuditLogger.cs` | ✅ HMAC 链 + JSONL + 链签名（**权威记录**，请求返回前同步落盘）+ `audit_events`（**可检索副本**，有界队列批量异步提交，读前 flush） |
| `02-compliance-matrix.md` | `Base/Security/Compliance/` | ⚠️ 4 条核心规则（设计中 14 条） |

完整状态见 [`../docs/plugin/03-implementation-status.md`](../docs/plugin/03-implementation-status.md)。

---

## 6. 编译与运行

```powershell
# 从仓库根目录编译全部项目
dotnet build src/BankingAgent.slnx -c Release

# 在两个独立终端启动
dotnet run --project src/mock-bank/MockBank.Api -c Release
dotnet run --project src/src/BankingAgent.Host -c Release
```

宿主 csproj 里的 `CopyPluginsToOutput` 目标会在构建后把 4 个插件 DLL 复制到与构建配置对应的 `bin/<配置>/net8.0/plugins/`。编译和启动必须使用相同配置。
