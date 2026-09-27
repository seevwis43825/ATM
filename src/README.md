# 代码索引（Source Index）

> **对应设计文档**：[`../docs/00-architecture/00-overview.md`](../docs/00-architecture/00-overview.md)
> **插件接入**：[`../docs/plugin/01-plugin-onboarding-guide.md`](../docs/plugin/01-plugin-onboarding-guide.md)
> **代码规模**：54 个文件 / 6753 行

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

### 2.1 BankingAgent.Plugin.Sdk（契约层，387 行）

插件唯一需要引用的程序集。稳定契约，一经发布不随意改。

| 文件 | 行数 | 职责 |
|------|------|------|
| `PluginContracts.cs` | 148 | `IPluginEntryPoint`、`PluginManifest`、`PluginId`、`PluginVersion`、`PluginDependency`、`DataClassification` |
| `AgentContracts.cs` | 196 | `IBankingAgent`、`AgentRequest`、`AgentResult`、`IAgentTool`、`ToolInvocation`、`AgentRole` |
| `EventContracts.cs` | 40 | `DomainEvent`、`IDomainEventHandler`、`IEventPublisher` |
| `CoreBankContracts.cs` | 176 | `ICoreBankClient` + 13 个 DTO（Account/Transfer/Transaction/Statement/Card/Wealth） |

**关键设计**：`ICoreBankClient` 是抽象层。插件只认这个接口，不认 Mock Bank 的 DTO。切换到真实 CBS 时改 `CoreBankClient` 的实现即可，**插件代码零改动**。

---

### 2.2 BankingAgent.Base（框架层，1857 行 / 16 文件）

零业务逻辑，只提供能力。

#### Agents/（Agent 基础设施）

| 文件 | 行数 | 职责 |
|------|------|------|
| `BankingAgentBase.cs` | 120 | Agent 基类：自动计时、审计、异常兜底 |
| `AgentRouter.cs` | 78 | 意图路由，**最长前缀优先**（避免 `transfer` 抢占 `transfer.receive`） |
| `SlotReader.cs` | 89 | 槽位读取，兼容 JSON 反序列化产生的 `JsonElement` |

#### Plugins/（插件系统）

| 文件 | 行数 | 职责 |
|------|------|------|
| `PluginRegistry.cs` | 324 | 可回收 `AssemblyLoadContext`、依赖拓扑排序、循环依赖检测、版本兼容校验 |

#### Data/（数据访问）

| 文件 | 行数 | 职责 |
|------|------|------|
| `BankingDbContext.cs` | 256 | 审计字段自动填充、插件分区映射、脱敏快照 |
| `BaseEntity.cs` | 43 | 实体基类（CreatedAt/By、RowVersion、IsDeleted） |
| `UnitOfWork.cs` | 82 | 事务边界、幂等键登记 |
| `SimpleDbContextFactory.cs` | 33 | 自实现工厂（EF 内置工厂不支持多参构造） |
| `ICurrentUserAccessor` 等 | — | 当前用户（AsyncLocal）、时钟抽象 |

#### Security/（安全合规）

| 文件 | 行数 | 职责 |
|------|------|------|
| `DataMasker.cs` | 145 | 脱敏：手机/身份证/银行卡/姓名/邮箱/账号，L4 永不返回明文 |
| `AuditLogger.cs` | 240 | HMAC-SHA256 链式签名 + 文件归档 + 用户 ID 哈希 |
| `Compliance/ComplianceGuard.cs` | 92 | 合规规则执行器 |
| `Compliance/ComplianceRules.cs` | 152 | 4 条内置规则（金额阈值、自转、反洗钱、场景权限） |

#### 其他

| 文件 | 行数 | 职责 |
|------|------|------|
| `Events/InMemoryEventBus.cs` | 83 | 进程内事件总线，精确匹配 + 通配订阅、死信队列 |
| `CoreBank/CoreBankClient.cs` | 420 | 银行系统 HTTP 客户端，DTO 映射与容错 |
| `BankingCoreServiceCollectionExtensions.cs` | 133 | **一行装配**：`AddBankingCore()` |

---

### 2.3 BankingAgent.Host（宿主，256 行）

| 职责 | 说明 |
|------|------|
| 装配框架 | `builder.Services.AddBankingCore(config)` |
| 加载插件 | `PluginRegistry.LoadFromDirectoryAsync()` |
| 注入插件服务 | 插件注册的服务并入主容器 |
| 初始化数据库 | `DatabaseInitializer.InitializeAsync()` |
| 启动插件 | 按依赖拓扑顺序 |
| 暴露 API | 9 个端点 |

**为什么分两步（bootstrap 容器 → 正式容器）**：插件需要在容器 Build 之前注册服务。bootstrap 容器只用来拿 `ILoggerFactory` 和 `PluginRegistry`，用完即弃。

---

### 2.4 plugins/（示例插件，591 行）

| 插件 | 行数 | 演示重点 |
|------|------|---------|
| `BankingAgent.Plugin.Transfer` | 380 | 资金操作**完整范例**：槽位抽取 → 合规守卫 → 人工回环 → 幂等检查 → 调核心银行 → 落库 → 发事件 |
| `BankingAgent.Plugin.BillAnalysis` | 165 | 只读场景 + **订阅他人事件**实现零耦合联动 |
| `BankingAgent.Plugin.CardManagement` | 148 | 写操作但非资金 + **依赖声明** + L3 强制脱敏 |

---

### 2.5 mock-bank/MockBank.Api（模拟银行，3451 行 / 28 文件）

| 层 | 文件 | 职责 |
|----|------|------|
| Domain/ | 5 | Customer、Account、Transaction、Card、WealthProduct |
| Data/ | 2 | `Money` 值对象（decimal）、`MockBankStore`（线程安全内存账本 + 种子数据） |
| Contracts/ | 5 | 请求/响应 DTO，record 不可变 |
| Services/ | 7 | 账户、转账（含完整校验）、账单、卡、理财、业务日志、OpenAPI 生成 |
| Endpoints/ | 6 | 6 个端点组，Minimal API |
| Middleware/ | 2 | 全局异常处理、故障注入 |

**并发安全**：所有资金变动在单个 `SemaphoreSlim` 账本锁内完成，获取锁后**重新校验**，避免 TOCTOU。

---

### 2.6 E2ETest（端到端测试，211 行）

30 项断言覆盖 10 个场景，详见 [`../docs/plugin/00-quick-start.md`](../docs/plugin/00-quick-start.md) §3。

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
├─ 新业务功能（转账、账单、理财…）→ 写插件，参考 BillingAgent.Plugin.Transfer
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
| `04-audit-logging.md` | `Base/Security/Audit/AuditLogger.cs` | ✅ 已实现（文件版，未落库） |
| `02-compliance-matrix.md` | `Base/Security/Compliance/` | ⚠️ 4 条核心规则（设计中 14 条） |

完整状态见 [`../docs/plugin/03-implementation-status.md`](../docs/plugin/03-implementation-status.md)。

---

## 6. 编译与运行

```powershell
# 编译全部
cd G:\cunchu\大学\poject\ATM\src
dotnet build BankingAgent.slnx

# 启动模拟银行 :5200
cd mock-bank\MockBank.Api
dotnet run --urls http://localhost:5200

# 启动宿主 :5243（自动编译插件到 bin/plugins）
cd ..\..\src\BankingAgent.Host
dotnet run
```

宿主 csproj 里的 `CopyPluginsToOutput` 目标会在每次构建后把 3 个插件 DLL 复制到 `bin/Debug/net8.0/plugins/`。
