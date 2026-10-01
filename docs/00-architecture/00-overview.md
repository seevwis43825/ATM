# 系统总览（Architecture Overview）

> **状态**：与当前实现对齐 · **所有者**：架构师 · **版本**：v1.1
> **最后更新**：2026-10-01
>
> **醒目边界**
> - **Current（已实现）**：以下“当前实现”章节以及源码引用描述可运行事实。
> - **Target（目标设计）**：其余标为 Target 的内容是演进方向，不代表仓库已有对应服务或基础设施。

---

## 1. 一句话定义（Current）

**AI Banking Agent System** 当前是一个 **.NET 8 模块化单体**：`BankingAgent.Host` 负责 HTTP API、静态 UI、认证与装配，`BankingAgent.Base` 提供 Agent 编排、合规、审计、数据库、核心银行客户端和进程内事件总线，`BankingAgent.Plugin.Sdk` 提供插件契约；Transfer、BillAnalysis、CardManagement、Wealth 四个插件提供业务能力。

## 2. 当前实现（Current）

| 项目/组件 | 当前职责与事实 |
|---|---|
| `src/src/BankingAgent.Host` | ASP.NET Core 宿主，默认 `http://localhost:5243`；Minimal API；`wwwroot` 内置静态演示 UI |
| `src/src/BankingAgent.Base` | AgentRouter/AgentOrchestrator、合规与审计、EF Core、多 Provider 数据层、`InMemoryEventBus`、核心银行 HTTP 客户端 |
| `src/src/BankingAgent.Plugin.Sdk` | `IPluginEntryPoint`、`IBankingAgent`、`IEventPublisher`、`DomainEvent` 等共享契约；命名空间为 `BankingAgent.PluginSdk` |
| `src/plugins/*` | 四个动态加载插件：Transfer、BillAnalysis、CardManagement、Wealth |
| `src/mock-bank/MockBank.Api` | 独立模拟核心银行 API，默认 `http://localhost:5200`，数据为进程内 `MockBankStore` |
| 数据库 | 默认 SQLite：`bankingagent.db`，`InitMode=EnsureCreated`；另支持 PostgreSQL/SQL Server 配置，但不是默认运行态 |
| 事件 | `InMemoryEventBus`，同步逐个调用处理器；仅内存历史与死信，重启丢失 |
| 意图识别 | 默认规则路径来自插件声明的 `SupportedIntents`/`TriggerKeywords`；配置 `Ai:ApiKey` 后可用 `LlmIntentClassifier`，失败回退规则 |

当前仓库**没有**独立 Next.js 前端、Python/FastAPI AI 服务、API Gateway、K8s 运行单元，也没有默认 PostgreSQL、Redis、Kafka 服务。它们只可作为 Target 讨论。

---

## 3. 设计目标（Target，可衡量）

| 目标 | 度量 | 优先级 |
|------|------|--------|
| **可扩展性**（功能随时增加） | 新功能上线 ≤ 3 人日，零改动核心代码 | P0 |
| **合规性** | 100% 关键操作可审计 + 人工回环可执行 | P0 |
| **高可用** | SLA ≥ 99.9%，核心交易 P99 < 500ms | P0 |
| **可观测** | 100% Agent 决策可追溯、跨模块调用全链路 | P1 |
| **5 人协同** | 模块边界清晰，并行无冲突 | P0 |

---

## 4. 架构原则（Target；落地状态以源码为准）

### 4.1 模块化优先（Modular First）
- 每个 Bounded Context 独立、可单独演进、可单独测试
- 模块间禁止**直接调用**、**共享数据库**、**共享类型**
- 通信**只能**通过 EventBus 或显式接口

### 4.2 插件化扩展（Plugin over Hardcode）
- 新功能 = 实现接口 → 注册到 PluginRegistry
- 核心代码不写 if/else 区分功能
- 通过配置文件或 FeatureFlag 控制启用

### 4.3 显式契约（Contracts First）
- API → OpenAPI 3.0 规范
- 事件 → CloudEvents JSON Schema
- 数据库 → 显式 migration，禁止自动迁移

### 4.4 不可变核心（Immutable Core）
- EventBus、PluginRegistry、ComplianceGuard、FeatureFlag **不允许改业务逻辑**
- 改动核心需全员 Review + ADR 编号

### 4.5 可观测性默认（Observable by Default）
- 所有 Agent 决策记录到 Audit 模块
- 所有跨模块调用记录 TraceID
- 默认开启 Metrics、Health Check

### 4.6 人工回环（Human-in-the-Loop）
- 高风险操作（转账、卡片变更、跨场景联动、理财申购）**必须有人工确认**
- 通过 Guardrails Agent + Compliance 模块双重拦截

### 4.7 数据合规（Compliance by Design）
- 数据本地化、敏感信息脱敏、合规审计日志
- 详见 [`05-security-compliance/02-compliance-matrix.md`](../05-security-compliance/02-compliance-matrix.md)

### 4.8 渐进演进（Evolutionary Architecture）
- 当前模块化单体 → 必要时可平滑拆分为微服务
- 演进路径在 [`00-architecture/04-architecture-decisions.md`](04-architecture-decisions.md) ADR-0002

---

## 5. 目标系统组成（Target 鸟瞰）

```
┌────────────────────────────────────────────────────────────────┐
│                        用户渠道层                                │
│   ┌─────────┐   ┌─────────┐   ┌─────────┐   ┌─────────┐         │
│   │  APP    │   │   IM    │   │   H5    │   │  Voice  │         │
│   └─────────┘   └─────────┘   └─────────┘   └─────────┘         │
└──────────────────────────┬─────────────────────────────────────┘
                           ▼ HTTPS / WSS
┌────────────────────────────────────────────────────────────────┐
│                     API Gateway + BFF                            │
│   · JWT 鉴权、限流、路由                                         │
│   · 与 IM 集成的 Webhook 适配                                    │
└──────────────────────────┬─────────────────────────────────────┘
                           ▼
┌────────────────────────────────────────────────────────────────┐
│                 Core（不可变核心层）                              │
│   ┌────────────────────────────────────────────────────────┐   │
│   │  EventBus  │  PluginRegistry  │  FeatureFlag  │  Guard │   │
│   └────────────────────────────────────────────────────────┘   │
└──────────────────────────┬──────────────────────────────────┘
                           ▼
┌────────────────────────────────────────────────────────────────┐
│              Bounded Contexts（领域模块）                       │
│                                                                │
│   AgentOrchestration  ──┐                                     │
│                          ├──> Conversation                    │
│   Memory&Profile  ──────┘                                     │
│                                                                │
│   Cross-Scenario Coordinator ──┬──> Transfer                   │
│                                ├──> BillAnalysis               │
│                                ├──> Wealth                     │
│                                ├──> CardManagement             │
│                                └──> Subscription               │
│                                                                │
│   Audit（横切，所有模块调用）                                   │
└──────────────────────────┬──────────────────────────────────┘
                           ▼
┌────────────────────────────────────────────────────────────────┐
│                       基础设施层                                │
│   · PostgreSQL（主数据） · Redis（主数据处理）                │
│   · Vector DB（pgvector）· Kafka（事件总线后端，可选）         │
│   · LLM Providers（Qwen/DeepSeek/OpenAI等）                     │
└────────────────────────────────────────────────────────────────┘
```

---

## 6. 技术边界

### 6.1 Current

| 层级 | 当前选型 |
|---|---|
| 宿主/API | C#、.NET 8、ASP.NET Core Minimal API |
| UI | `BankingAgent.Host/wwwroot` 静态文件，无独立构建 |
| 持久化 | EF Core；默认 SQLite + `EnsureCreated` |
| 事件 | 自研 `IEventPublisher` + `InMemoryEventBus`，不是 MediatR |
| AI | OpenAI-compatible HTTP 客户端可选；默认规则意图识别 |
| 外部业务 | `ICoreBankClient` 调用 MockBank.Api |

### 6.2 Target（尚未实现）

| 层级 | 选型 | 备注 |
|------|------|------|
| **后端语言** | C# .NET 8 + Python 3.11 双栈 | .NET 强类型业务核心，Python 灵活 AI/ML |
| **后端框架** | .NET Aspire / FastAPI | 各自生态最优 |
| **前端** | Next.js 14 + TypeScript | App Router + Server Components |
| **数据库** | PostgreSQL 16 + pgvector | 关系 + 向量混合 |
| **缓存** | Redis 7 | 会话 + 限流 |
| **事件总线** | 可考虑持久化消息总线/Kafka | 当前不是 MediatR，详见 ADR-0003 |
| **LLM** | Qwen3 / DeepSeek-V3（国内）/ GPT-4o（备选） | 主用国内模型，海外作为兜底 |
| **Agent 框架** | Microsoft Semantic Kernel + LangGraph（Python） | 编排 + 工作流 |
| **监控** | OpenTelemetry + Prometheus + Grafana | 全链路追踪 |
| **CI/CD** | GitHub Actions + ArgoCD | GitOps |
| **部署** | K8s (生产) / Docker Compose (本地) | 详见 [`04-operations/01-deployment-architecture.md`](../04-operations/01-deployment-architecture.md) |

---

## 7. 与论文综述的对应（Target 设计来源）

本系统的设计理念融合了下载的 53 篇论文：

- **A01 Ryt Bank**：4 个 Agent（Guardrails/Intent/Payment/FAQ）→ 我们的 Guardrails + Conversation + Transfer + Audit
- **F05 TradingAgents**：6 角色多 Agent 辩论 → 我们的 Cross-Scenario Coordinator
- **F28 FinCon**：Manager-Analyst 层次 + 言语强化 → 我们的 AgentOrchestration + Memory Agent
- **F36 FinRobot**：分层 + Financial Chain-of-Thought → 我们的 Bounded Context 设计
- **G22-G25 RegTech**：审计 + 合规矩阵 → 我们的 Compliance 模块
- **I32 自主链上 Agent**：4 类架构 → 我们的 Plug-in Registry

详见 [`papers/papers_summary.md`](../../papers/papers_summary.md)

---

## 8. 演进路径（Target）

### 当前基线
- .NET 8 模块化单体
- 默认 SQLite + `EnsureCreated`
- 自研进程内 `InMemoryEventBus`
- 四个业务插件 + MockBank.Api

### 短期
- 明确插件边界并补齐自动化测试
- 生产环境按需切换 PostgreSQL + EF Core Migrate
- 为现有事件契约补充版本与持久化策略

### 中期（3-12 个月）
- 按需拆分高负载模块为微服务（先 WeChat/Cross-Scenario）
- 事件总线切换 Kafka
- Vector DB 用于 Memory Agent 检索

### 长期（12+ 个月）
- 完整微服务化
- 多区域部署
- 联邦学习 + Agent Marketplace

详见 ADR-0002 / ADR-0003。

---

## 9. 关联文档

- **架构决策**：[`04-architecture-decisions.md`](04-architecture-decisions.md)
- **C4 上下文**：[`01-c4-context.md`](01-c4-context.md)
- **模块边界**：[`05-module-boundaries.md`](05-module-boundaries.md)
- **插件扩展**：[`06-extension-points.md`](06-extension-points.md)
- **事件契约**：[`07-event-driven-contract.md`](07-event-driven-contract.md)