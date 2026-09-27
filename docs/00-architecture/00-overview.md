# 系统总览（Architecture Overview）

> **状态**：评审中 · **所有者**：架构师 · **版本**：v1.0
> **最后更新**：2026-09-21

---

## 1. 一句话定义

**AI Banking Agent System** 是一个支持自然语言对话、自主规划、跨场景联动的银行业务 AI 系统。用户通过 APP/IM 与 Agent 对话，Agent 自主调度多个领域服务完成转账、账单分析、理财、卡片管理、订阅管理等操作。

---

## 2. 设计目标（可衡量）

| 目标 | 度量 | 优先级 |
|------|------|--------|
| **可扩展性**（功能随时增加） | 新功能上线 ≤ 3 人日，零改动核心代码 | P0 |
| **合规性** | 100% 关键操作可审计 + 人工回环可执行 | P0 |
| **高可用** | SLA ≥ 99.9%，核心交易 P99 < 500ms | P0 |
| **可观测** | 100% Agent 决策可追溯、跨模块调用全链路 | P1 |
| **5 人协同** | 模块边界清晰，并行无冲突 | P0 |

---

## 3. 架构原则（8 条铁律）

### 3.1 模块化优先（Modular First）
- 每个 Bounded Context 独立、可单独演进、可单独测试
- 模块间禁止**直接调用**、**共享数据库**、**共享类型**
- 通信**只能**通过 EventBus 或显式接口

### 3.2 插件化扩展（Plugin over Hardcode）
- 新功能 = 实现接口 → 注册到 PluginRegistry
- 核心代码不写 if/else 区分功能
- 通过配置文件或 FeatureFlag 控制启用

### 3.3 显式契约（Contracts First）
- API → OpenAPI 3.0 规范
- 事件 → CloudEvents JSON Schema
- 数据库 → 显式 migration，禁止自动迁移

### 3.4 不可变核心（Immutable Core）
- EventBus、PluginRegistry、ComplianceGuard、FeatureFlag **不允许改业务逻辑**
- 改动核心需全员 Review + ADR 编号

### 3.5 可观测性默认（Observable by Default）
- 所有 Agent 决策记录到 Audit 模块
- 所有跨模块调用记录 TraceID
- 默认开启 Metrics、Health Check

### 3.6 人工回环（Human-in-the-Loop）
- 高风险操作（转账、卡片变更、跨场景联动、理财申购）**必须有人工确认**
- 通过 Guardrails Agent + Compliance 模块双重拦截

### 3.7 数据合规（Compliance by Design）
- 数据本地化、敏感信息脱敏、合规审计日志
- 详见 [`05-security-compliance/02-compliance-matrix.md`](../05-security-compliance/02-compliance-matrix.md)

### 3.8 渐进演进（Evolutionary Architecture）
- 当前模块化单体 → 必要时可平滑拆分为微服务
- 演进路径在 [`00-architecture/04-architecture-decisions.md`](04-architecture-decisions.md) ADR-0002

---

## 4. 系统组成（鸟瞰）

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

## 5. 关键技术选型

| 层级 | 选型 | 备注 |
|------|------|------|
| **后端语言** | C# .NET 8 + Python 3.11 双栈 | .NET 强类型业务核心，Python 灵活 AI/ML |
| **后端框架** | .NET Aspire / FastAPI | 各自生态最优 |
| **前端** | Next.js 14 + TypeScript | App Router + Server Components |
| **数据库** | PostgreSQL 16 + pgvector | 关系 + 向量混合 |
| **缓存** | Redis 7 | 会话 + 限流 |
| **事件总线** | MediatR（进程内）→ Kafka（演化目标） | 详见 ADR-0003 |
| **LLM** | Qwen3 / DeepSeek-V3（国内）/ GPT-4o（备选） | 主用国内模型，海外作为兜底 |
| **Agent 框架** | Microsoft Semantic Kernel + LangGraph（Python） | 编排 + 工作流 |
| **监控** | OpenTelemetry + Prometheus + Grafana | 全链路追踪 |
| **CI/CD** | GitHub Actions + ArgoCD | GitOps |
| **部署** | K8s (生产) / Docker Compose (本地) | 详见 [`04-operations/01-deployment-architecture.md`](../04-operations/01-deployment-architecture.md) |

---

## 6. 与论文综述的对应

本系统的设计理念融合了下载的 53 篇论文：

- **A01 Ryt Bank**：4 个 Agent（Guardrails/Intent/Payment/FAQ）→ 我们的 Guardrails + Conversation + Transfer + Audit
- **F05 TradingAgents**：6 角色多 Agent 辩论 → 我们的 Cross-Scenario Coordinator
- **F28 FinCon**：Manager-Analyst 层次 + 言语强化 → 我们的 AgentOrchestration + Memory Agent
- **F36 FinRobot**：分层 + Financial Chain-of-Thought → 我们的 Bounded Context 设计
- **G22-G25 RegTech**：审计 + 合规矩阵 → 我们的 Compliance 模块
- **I32 自主链上 Agent**：4 类架构 → 我们的 Plug-in Registry

详见 [`../papers/papers_summary.md`](../papers/papers_summary.md)

---

## 7. 演进路径

### 短期（0-3 个月）
- 模块化单体 + 单库 PostgreSQL
- 进程内 EventBus (MediatR)
- 5 个 Bounded Context 上线 MVP

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

## 8. 关联文档

- **架构决策**：[`04-architecture-decisions.md`](04-architecture-decisions.md)
- **C4 上下文**：[`01-c4-context.md`](01-c4-context.md)
- **模块边界**：[`05-module-boundaries.md`](05-module-boundaries.md)
- **插件扩展**：[`06-extension-points.md`](06-extension-points.md)
- **事件契约**：[`07-event-driven-contract.md`](07-event-driven-contract.md)