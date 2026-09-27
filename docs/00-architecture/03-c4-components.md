# C4 架构模型 - Level 3：组件视图（Components）

> **状态**：评审中 · **所有者**：架构师 · **版本**：v1.0
> **最后更新**：2026-09-21

本文展示 **Agent Core (.NET)** 容器内部的关键组件。AI Service (Python) 的组件详见 [AI Service 单独设计文档](../03-development/01-coding-standards.md#python-部分)。

---

## 1. Agent Core 容器组件图

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

## 2. Core Layer 组件（不可变）

| 组件 | 职责 | 技术实现 |
|------|------|---------|
| **EventBus** | 进程内事件分发、解耦模块 | MediatR + 中间件 |
| **PluginRegistry** | 注册/查找可插拔能力 | DI 容器 + 命名约定 |
| **FeatureFlag** | 动态功能开关 | DB + 缓存，10 秒刷新 |
| **Guardrails (Compliance)** | 输入/输出合规拦截 | 规则链 + LLM Judge |

详见 [`../05-security-compliance/01-threat-model.md`](../05-security-compliance/01-threat-model.md)

---

## 3. Bounded Context 组件

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

## 4. 模块依赖规则

详见 [`05-module-boundaries.md`](05-module-boundaries.md)

**核心原则**：
- ✅ 允许：Bounded Context → Core（EventBus、Guardrails）
- ✅ 允许：Bounded Context → 其他 Bounded Context（通过接口或事件）
- ❌ 禁止：Core → Bounded Context（Core 是被调用方）
- ❌ 禁止：Bounded Context 之间直接调用服务（必须走接口抽象）

---

## 5. 关联文档

- **Level 1 上下文**：[`01-c4-context.md`](01-c4-context.md)
- **Level 2 容器视图**：[`02-c4-containers.md`](02-c4-containers.md)
- **模块边界**：[`05-module-boundaries.md`](05-module-boundaries.md)
- **扩展点**：[`06-extension-points.md`](06-extension-points.md)