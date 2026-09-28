# 📚 项目文档总索引（Documentation Hub）

> **项目**：AI Banking Agent System — 智能银行对话式 AI 系统
> **架构**：模块化单体（Modular Monolith） + DDD + Clean Architecture + 事件驱动 + 插件化
> **团队**：5 人
> **最后更新**：2026-09-21

---

## 🚀 一分钟导航

| 我想知道什么 | 看哪份文档 |
|-------------|------------|
| 整个系统长什么样？ | [`00-architecture/01-c4-context.md`](00-architecture/01-c4-context.md) |
| 怎么分工？ | [`07-team/01-team-roles.md`](07-team/01-team-roles.md) |
| **我是某个角色，我该看哪些文档？** | **`07-team/by-role/`（按角色分册）** |
| 我该开始做什么？ | [`07-team/03-onboarding.md`](07-team/03-onboarding.md) |
| 如何增加一个新功能？ | [`00-architecture/06-extension-points.md`](00-architecture/06-extension-points.md) |
| 代码规范是什么？ | [`03-development/01-coding-standards.md`](03-development/01-coding-standards.md) |
| API 怎么写？ | [`02-api/01-rest-api-spec.md`](02-api/01-rest-api-spec.md) |
| **用户看到的页面长啥样？** | **[`08-frontend/prototype/chat.html`](08-frontend/prototype/chat.html)** |
| **运营/合规后台长啥样？** | **[`08-frontend/prototype/admin.html`](08-frontend/prototype/admin.html)** |
| **代码怎么跑起来？** | **[`plugin/00-quick-start.md`](plugin/00-quick-start.md)** |
| **怎么加一个新功能？** | **[`plugin/01-plugin-onboarding-guide.md`](plugin/01-plugin-onboarding-guide.md)** |
| **接口有哪些、怎么调？** | **[`plugin/02-api-reference.md`](plugin/02-api-reference.md)** |
| **哪些做了、哪些没做？** | **[`plugin/03-implementation-status.md`](plugin/03-implementation-status.md)** |
| **密码能抗量子吗？安全吗？** | **[`10-security/01-cryptography-and-hardening.md`](10-security/01-cryptography-and-hardening.md)** |
| **抗量子怎么落地的？** | **[`10-security/02-hardening-report.md`](10-security/02-hardening-report.md)** |
| **系统能扛多少 QPS？** | **[`11-performance/01-capacity-test-report.md`](11-performance/01-capacity-test-report.md)** |
| **多 Agent 怎么协同？** | **[`12-multi-agent/01-orchestration-design.md`](12-multi-agent/01-orchestration-design.md)** |
| **看架构图 / UML / 数据模型** | **[`09-uml/`](09-uml/)** |
| **数据库怎么演进？** | **[`13-database/01-migration-and-ci.md`](13-database/01-migration-and-ci.md)** |
| 出了事故怎么办？ | [`04-operations/03-incident-response.md`](04-operations/03-incident-response.md) |

---

## 📁 文档目录结构

```
docs/
├── README.md                              ← 你正在看（项目文档总索引）
│
├── 00-architecture/                       ← 架构决策
│   ├── 00-overview.md                       系统总览 + 设计原则
│   ├── 01-c4-context.md                     C4 Level 1：系统上下文
│   ├── 02-c4-containers.md                  C4 Level 2：容器视图
│   ├── 03-c4-components.md                  C4 Level 3：组件视图
│   ├── 04-architecture-decisions.md         架构决策记录（ADR）
│   ├── 05-module-boundaries.md              模块边界 + 依赖规则
│   ├── 06-extension-points.md               插件化扩展机制（新功能接入指南）
│   ├── 07-event-driven-contract.md          事件契约 + Event Bus 设计
│
├── 01-domain/                              ← 领域模型
│   ├── 01-bounded-contexts.md               限界上下文划分
│   ├── 02-domain-model.md                   核心实体 + 值对象 + 聚合
│   ├── 03-glossary.md                       统一术语表（中英对照）
│
├── 02-api/                                 ← 接口规范
│   ├── 01-rest-api-spec.md                   REST API 设计规范（OpenAPI）
│   ├── 02-event-schema.md                   事件 Schema（CloudEvents 标准）
│   ├── 03-feature-flags.md                  功能开关系统（动态上下线）
│
├── 03-development/                         ← 工程实践
│   ├── 01-coding-standards.md               代码规范（C# / Python / TypeScript）
│   ├── 02-git-workflow.md                   Git Flow + 分支策略
│   ├── 03-pr-review-checklist.md            PR Review 清单
│   ├── 04-testing-strategy.md               测试策略（单元/集成/E2E）
│   ├── 05-ci-cd-pipeline.md                 CI/CD 流水线
│   ├── 06-environment-setup.md              开发环境搭建
│
├── 04-operations/                          ← 运维
│   ├── 01-deployment-architecture.md        部署架构（K8s + 多环境）
│   ├── 02-monitoring-observability.md        监控与可观测性
│   ├── 03-incident-response.md              事故响应
│   ├── 04-disaster-recovery.md              容灾与备份
│
├── 05-security-compliance/                 ← 安全合规
│   ├── 01-threat-model.md                   STRIDE 威胁建模
│   ├── 02-compliance-matrix.md              法律合规矩阵（与法规清单联动）
│   ├── 03-data-classification.md            数据分级与脱敏
│   ├── 04-audit-logging.md                  审计日志规范
│
├── 06-product/                             ← 产品
│   ├── 01-roadmap.md                        路线图
│   ├── 02-feature-lifecycle.md              功能全生命周期管理
│   ├── 03-release-process.md                发布流程（含灰度）
│
├── 07-team/                                ← 团队协作
│   ├── 01-team-roles.md                     5 人角色定义
│   ├── 02-collaboration.md                  协作机制（会议、工具）
│   ├── 03-onboarding.md                     新人入职指南
│   ├── sprint-example-v0.4.md               ← 5 人 10 天协同开发作战示例（v0.4 智能转账）
│   ├── by-role/                             ← 按角色分册（每个角色单独看自己）
│   │   ├── architect.md                       架构师分册
│   │   ├── backend.md                         业务开发分册
│   │   ├── ai.md                              AI/数据开发分册
│   │   ├── platform.md                        平台分册
│   │   └── compliance.md                      安全/合规分册
│
├── 08-frontend/                            ← 前端原型与设计规范
│   ├── README.md                            设计令牌 + 设计原则 + 信息架构
│   └── prototype/
│       ├── chat.html                        IM 对话 H5 原型（含转账/账单/理财等卡片）
│       └── admin.html                       Admin 后台原型（含 Dashboard/审计/LLM 监控等）
│
└── plugin/                                 ← 可运行代码的配套文档
    ├── 00-quick-start.md                    一键启动 + API 速查 + 故障排查
    ├── 01-plugin-onboarding-guide.md        插件接入指南（5 分钟上手）
    ├── 02-api-reference.md                  实测 API 参考（宿主 + 模拟银行）
    └── 03-implementation-status.md          实现状态与设计对照（含缺口清单）
│
├── 09-uml/                                 ← UML 与结构化架构文档
│   ├── 01-uml-diagrams.md                  10 张 Mermaid 图（上下文/容器/组件/类图/时序/状态）
│   ├── 02-architecture-decision-record.md  ADR-011~021（实现过程中的新决策）
│   ├── 03-api-contract.md                  接口契约（Schema/错误码/权限/幂等）
│   └── 04-data-model.md                    数据模型（ER 图/字段/索引/迁移）
│
├── 10-security/                            ← 安全与密码学
│   ├── 01-cryptography-and-hardening.md    抗量子分析 + 安全能力盘点 + 风险矩阵
│   └── 02-hardening-report.md              **安全加固实施报告（PQC 落地 + 实测证据）**
│
├── 11-performance/                         ← 性能
│   └── 01-capacity-test-report.md          容量压测（296 QPS 实测 + 瓶颈分析）
│
├── 12-multi-agent/                         ← 多 Agent 协同
│   └── 01-orchestration-design.md          Supervisor 编排器 + 轨迹日志（对齐 Harness）
│
└── 13-database/                            ← 数据库
    └── 01-migration-and-ci.md               EF Migration + CI 协同方案
```

> **代码位置**：`../src/`（54 个文件 / 6753 行）
> **代码索引**：[`../src/README.md`](../src/README.md)

---

## 🎯 文档使用约定

### 文档状态标识

每份文档头部会带：
- **状态**：`草案` / `评审中` / `已通过` / `已废弃`
- **所有者**：责任人
- **最后更新**：日期 + 版本号

### 文档变更流程

1. 任何对架构、API、流程的变更 → **先开 Issue 讨论**
2. 变更获批后 → 修改文档 → **ADR 必须同步更新**
3. 重大变更 → 拉所有 5 人 Review

### 5 人协作文档分工

| 角色 | 主要文档所有者 |
|------|--------------|
| 架构师 | `00-architecture/`、`01-domain/`、`07-team/` |
| 业务开发 | `02-api/`、`01-domain/` |
| 平台/基础设施 | `04-operations/`、`03-development/05,06` |
| AI/数据开发 | `01-domain/02`、`02-api/03`、`06-product/` |
| 安全/合规 | `05-security-compliance/` |

详见 [`07-team/01-team-roles.md`](07-team/01-team-roles.md)

---

## 🏛️ 架构一句话总结

**模块化单体 + 插件化扩展**：

```
Banking Agent System
├── Core（不可变核心）
│   ├── EventBus          ← 模块间唯一通信方式
│   ├── PluginRegistry    ← 新功能 = 新插件
│   ├── FeatureFlag       ← 动态上下线
│   └── ComplianceGuard   ← 全局合规拦截
│
└── Bounded Contexts（模块）
    ├── AgentOrchestration（编排）  ← IAgentOrchestrator
    ├── Conversation       ← IConversationService
    ├── Transfer           ← ITransferService
    ├── BillAnalysis       ← IBillAnalysisService
    ├── Wealth             ← IWealthService
    ├── CardManagement     ← ICardService
    ├── Subscription       ← ISubscriptionService
    ├── CrossScenario      ← ICrossScenarioCoordinator
    ├── Memory&Profile     ← IUserMemoryService
    └── Audit              ← IAuditLogger
```

详见 [`00-architecture/05-module-boundaries.md`](00-architecture/05-module-boundaries.md) 与 [`06-extension-points.md`](00-architecture/06-extension-points.md)

---

## 📜 相关引用文档

- 法律合规清单：`../papers/laws\国内金融业AI_Banking_Agent_现行法律法规清单.md`
- 多智能体论文综述：`../papers/papers_summary.md` + `papers_summary_part2.md`
- AI Banking Agent 系统分析（任务书分析）：会话上下文