# C4 架构模型 - Level 3：组件视图（Components）

> **状态**：与当前实现对齐 · **所有者**：架构师 · **版本**：v1.1
> **最后更新**：2026-10-01

本文先展示 `BankingAgent.Host` 进程内的 **Current** 组件。后半部分保留的细粒度领域组件均为 **Target**；仓库中没有 Python AI Service。

---

## 1. 当前组件图（Current）

```
BankingAgent.Host
├─ Minimal API / JWT 中间件 / 速率限制 / 安全响应头 / wwwroot
├─ PluginRegistry（扫描 plugins/，装配服务、生命周期、签名校验）
├─ AgentRouter（规则意图到单个 Agent 的前缀路由）
├─ AgentOrchestrator + AdaptiveStrategy + InMemoryTrajectoryLog
├─ IIntentClassifier
│  └─ LlmIntentClassifier（LLM 可用时调用，否则规则；异常回退规则）
├─ BankingAgent.Base
│  ├─ ComplianceGuard / IAuditLogger
│  ├─ ICoreBankClient
│  ├─ BankingDbContext / AuditDbContext / DatabaseInitializer
│  └─ IEventPublisher → InMemoryEventBus
└─ 插件 Agent
   ├─ TransferAgent（含 TransferRecord 持久化、合规、人工确认、事件发布）
   ├─ BillAnalysisAgent（账单查询；监听 transfer.completed）
   ├─ CardManagementAgent（查询/状态变更）
   └─ 理财Agent（产品与余额只读查询）
```

Current 没有 MediatR、FeatureFlag 服务、LangGraph、Semantic Kernel、Redis 对话状态、Vector DB、Subscription/CrossScenario/Memory Context 或独立 Conversation Context。

## 2. 目标组件图（Target）

```
┌─────────────────────────────────────────────────────────────────────┐
│                    Agent Core (.NET 8)                              │
│                                                                     │
│  ┌────────────────────────────────────────────────────────────────┐ │
│  │                     Core Layer (不可变)                         │ │
│  │                                                                │ │
│  │  ┌─────────────┐ ┌─────────────┐ ┌─────────────┐ ┌──────────┐ │ │
│  │  │  EventBus   │ │ │PluginReg. │ │ │FeatureFlag │ │ │Guardrails│   │ │
│  │  │  (MediatR)  │ │ │Registry   │ │ │  Service   │ │ │(Compliance)│
│  │  └─────────────┘ └─────────────┘ └─────────────┘ └──────────┘ │ │
│  │                                                                │ │
│  └────────────────────────────────────────────────────────────────┘ │
│                                ▲                                   │
│  ┌─────────────────────────────┼─────────────────────────────────┐ │
│  │       Bounded Contexts (可扩展领域)                            │ │
│  │                            依赖                                 │ │
│  │                                                                │ │
│  │  ┌─────────────────────────────────────────────────────────┐  │ │
│  │  │  AgentOrchestration Context                              │  │ │
│  │  │  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐    │  │ │
│  │  │  │Orchestrator  │  │PlanResolver  │  │DialogState   │    │  │ │
│  │  │  │ (LangGraph)  │  │              │  │  Manager     │    │  │ │
│  │  │  └──────────────┘  └──────────────┘  └──────────────┘    │  │ │
│  │  └─────────────────────────────────────────────────────────┘  │ │
│  │                                ▲                               │ │
│  │  ┌──────────────┐ ┌──────────────┐ ┌──────────────┐            │ │
│  │  │Conversation  │ │Memory&Profile│ │AI Service   │            │ │
│  │  │  Context     │ │   Context    │ │  Client     │            │ │
│  │  │ ┌──────────┐ │ │ ┌──────────┐ │ │ ┌──────────┐ │            │ │
│  │  │ │Intent    │ │ │ │Vector DB │ │ │ │LLM Router│ │            │ │
│  │  │ │Classifier│ │ │ │  Wrapper  │ │ │ │  + Cache  │ │            │ │
│  │  │ └──────────┘ │ │ └──────────┘ │ │ └──────────┘ │            │ │
│  │  └──────────────┘ └──────────────┘ └──────────────┘            │ │
│  │                                                               │ │
│  │  ┌──────────────┐ ┌──────────────┐ ┌──────────────┐            │ │
│  │  │  Transfer    │ │BillAnalysis  │ │   Wealth    │            │ │
│  │  │  Context     │ │   Context    │ │  Context    │            │ │
│  │  │ ┌──────────┐ │ │ ┌──────────┐ │ │ ┌──────────┐ │            │ │
│  │  │ │Transfer  │ │ │ │Categorizer│ │ │ │Product   │ │            │ │
│  │  │ │Executor  │ │ │ │ + Anomaly │ │ │ │ Catalog  │ │            │ │
│  │  │ └──────────┘ │ │ └──────────┘ │ │ │+ Risk Mgr│ │            │ │
│  │  └──────────────┘ └──────────────┘ │ └──────────┘ │            │ │
│  │                                   └──────────────┘            │ │
│  │  ┌──────────────┐ ┌──────────────┐ ┌──────────────┐            │ │
│  │  │CardManagement│ │Subscription  │ │CrossScenario │            │ │
│  │  │   Context    │ │   Context    │ │ Coordinator  │            │ │
│  │  └──────────────┘ └──────────────┘ └──────────────┘            │ │
│  │                                                               │ │
│  │  ┌─────────────────────────────────────────────────────────┐  │ │
│  │  │  Audit Context (横切)                                     │  │ │
│  │  │  ┌──────────┐  ┌──────────┐  ┌──────────┐                   │  │ │
│  │  │  │Action Log│  │Decision  │  │Compliance│                   │  │ │
│  │  │  │ Writer   │  │ Tracer   │  │ Reporter │                   │  │ │
│  │  │  └──────────┘  └──────────┘  └──────────┘                   │  │ │
│  │  └─────────────────────────────────────────────────────────┘  │ │
│  │                                                               │ │
│  └───────────────────────────────────────────────────────────────┘ │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 3. 目标 Core Layer 组件（Target）

| 组件 | 职责 | 技术实现 |
|------|------|---------|
| **EventBus** | 进程内事件分发、解耦模块 | Current 为自研 `InMemoryEventBus`；未来可替换 |
| **PluginRegistry** | 注册/查找可插拔能力 | Current 为目录扫描 + `AssemblyLoadContext` + DI |
| **FeatureFlag** | 动态功能开关 | DB + 缓存，10 秒刷新 |
| **Guardrails (Compliance)** | 输入/输出合规拦截 | 规则链 + LLM Judge |

详见 [`../05-security-compliance/01-threat-model.md`](../05-security-compliance/01-threat-model.md)

---

## 4. 目标 Bounded Context 组件（Target，除同名插件外未实现）

### 3.1 AgentOrchestration Context

| 组件 | 职责 | 依赖 |
|------|------|------|
| **Orchestrator** | 接收用户请求、调度 Plan | LangGraph（Python）/ Semantic Kernel |
| **PlanResolver** | 解析 LLM 返回的执行计划 | AI Service Client |
| **DialogStateManager** | 管理多轮对话状态 | Redis |

### 3.2 Conversation Context

| 组件 | 职责 |
|------|------|
| **IntentClassifier** | 自然语言 → 业务意图路由 |
| **ResponseGenerator** | 生成自然语言回复 |
| **ChannelAdapter** | 适配 IM/APP/语音不同渠道 |

### 3.3 Transfer Context

| 组件 | 职责 |
|------|------|
| **TransferExecutor** | 调用 CBS 执行转账 |
| **PayeeResolver** | 自然语言姓名 → 银行账户（通讯录匹配） |
| **Splitter** | AA 拆分算法 |
| **ScheduledTransferScheduler** | 定时转账调度器（基于 Quartz.NET） |
| **AMLChecker** | 反洗钱规则（KYC + 大额上报） |

### 3.4 BillAnalysis Context

| 组件 | 职责 |
|------|------|
| **Categorizer** | 交易分类器（ML 模型） |
| **AnomalyDetector** | 异常交易检测 |
| **InsightGenerator** | 自然语言洞察生成 |

### 3.5 Wealth Context

| 组件 | 职责 |
|------|------|
| **ProductCatalog** | 理财产品库检索 |
| **RiskProfiler** | 用户风险测评 |
| **OrderExecutor** | 申购/赎回 |

### 3.6 CardManagement Context

| 组件 | 职责 |
|------|------|
| **CardService** | 卡申请、查询 |
| **LimitManager** | 额度调整 |
| **LossReporter** | 卡片挂失 |

### 3.7 Subscription Context

| 组件 | 职责 |
|------|------|
| **Detector** | 周期扣费识别 |
| **ReminderScheduler** | 续费提醒调度 |
| **Canceller** | 一键取消（知识库匹配退订路径） |

### 3.8 CrossScenario Context

| 组件 | 职责 |
|------|------|
| **TaskPlanner** | 跨场景任务分解 |
| **EventChain** | 长任务状态机 |
| **MerchantAdapter** | 第三方商家集成（订蛋糕/鲜花） |

### 3.9 Memory&Profile Context

| 组件 | 职责 |
|------|------|
| **EventStore** | 用户事件流（向量化） |
| **ProfileBuilder** | 关系图谱 + 偏好画像 |
| **Retriever** | 上下文检索 |

### 3.10 Audit Context（横切）

| 组件 | 职责 |
|------|------|
| **ActionLogWriter** | 所有决策日志 |
| **DecisionTracer** | 调用链路追踪 |
| **ComplianceReporter** | 自动生成反洗钱/可疑交易报告 |

---

## 5. 当前依赖与装配规则（Current）

详见 [`05-module-boundaries.md`](05-module-boundaries.md)

**源码事实**：
- `BankingAgent.Host` 编译期引用 `BankingAgent.Base` 与 `BankingAgent.Plugin.Sdk`；对四个插件仅声明构建顺序，运行期从 `plugins/` 扫描。
- `BankingAgent.Base` 引用 `BankingAgent.Plugin.Sdk`；Sdk 不引用 Base 或插件。
- 各插件引用 Base 与 Sdk；插件之间不建立项目引用。
- CardManagement 在 manifest 中声明对 Transfer 的运行期版本依赖，但不直接调用其类型。
- 插件共享 Base 提供的 `ICoreBankClient`、`ComplianceGuard`、数据库、审计与事件发布器。

---

## 6. 关联文档

- **Level 1 上下文**：[`01-c4-context.md`](01-c4-context.md)
- **Level 2 容器视图**：[`02-c4-containers.md`](02-c4-containers.md)
- **模块边界**：[`05-module-boundaries.md`](05-module-boundaries.md)
- **扩展点**：[`06-extension-points.md`](06-extension-points.md)