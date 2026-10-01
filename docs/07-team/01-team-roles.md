# 团队角色定义（Team Roles）

> **状态**：评审中 · **所有者**：架构师 · **版本**：v1.0
> **最后更新**：2026-10-01
>
> **口径**：角色职责是团队协作目标；代码主战场以当前仓库真实路径为准。K8s、独立 Python AI 服务、Next.js 管理台等标为 **Target**，不代表已经落地。

---

## 1. 目的

明确 5 人小团队的**角色边界**、**核心职责**、**能力要求**和**成长路径**，让每个人：

1. 知道自己的「主场」在哪里
2. 知道其他人的「主场」在哪里，跨边界协作有据可循
3. 知道每个角色未来的成长方向

---

## 2. 总览：5 个核心角色

### 2.1 当前仓库责任面（Current）

| 角色 | 当前代码主战场 |
|------|----------------|
| 架构 | `src/src/BankingAgent.Base`、`BankingAgent.Plugin.Sdk`、`BankingAgent.Host`、`docs/00-architecture` |
| 业务 | `src/plugins/BankingAgent.Plugin.{Transfer|BillAnalysis|CardManagement|Wealth}`、`src/mock-bank/MockBank.Api` |
| AI/数据 | `BankingAgent.Base/Ai`、`BankingAgent.Base/Agents`、插件 Agent；当前没有 `src/ai-service` |
| 平台 | `src/BankingAgent.slnx`、仓库现有 CI/脚本、运行配置；当前默认 SQLite + EnsureCreated |
| 安全/合规 | `BankingAgent.Base/Security`、Host 安全中间件、插件合规规则、`docs/05-security-compliance` |
| 前端（可兼任） | `src/src/BankingAgent.Host/wwwroot`；`docs/08-frontend/prototype` 仅为设计资产 |

```
                       ┌─────────────────┐
                       │   架构师 (A)     │ ← 跨领域串联
                       │   Architect     │
                       └────────┬────────┘
                                │
        ┌───────────────────────┼───────────────────────┐
        │                       │                       │
┌───────▼────────┐    ┌─────────▼─────────┐    ┌────────▼────────┐
│ 业务开发 (B)    │    │  AI/数据开发 (D)  │    │   平台 (P)       │
│ Backend Dev    │    │  AI/Data Dev      │    │  Platform       │
└────────────────┘    └───────────────────┘    └─────────────────┘
                                                        │
                                                        │
                              ┌─────────────────────────┘
                              │
                       ┌──────▼─────────┐
                       │  安全/合规 (S)  │
                       │  Sec/Compliance│
                       └────────────────┘
                              ▲
                              │
                       ┌──────┴─────────┐
                       │  前端全栈 (F)   │
                       │  Frontend      │
                       └────────────────┘
```

> 注：**第 5 人** 可选为安全合规 或 前端全栈，根据团队实际能力。本文档以**安全/合规 + 前端全栈** 双角色描述，实际可合并为 1 人。

---

## 3. 角色详解

### 3.1 架构师 / Architect

**核心定位**：技术决策者，跨领域串联者

**核心职责**：

| 维度 | 内容 |
|------|------|
| **架构设计** | 系统总体架构、技术选型、ADR 撰写、模块边界 |
| **跨域协调** | 串联业务、AI、平台、合规四方，处理架构冲突 |
| **代码 Review** | 核心模块的代码必须 Architect 签字 |
| **事故 IC** | P0 事故默认 IC（事故指挥官） |
| **合规对齐** | 与安全/合规对接架构层面合规设计 |
| **文档维护** | 架构文档、ADR、文档体系维护 |

**能力要求**：
- ✅ 5+ 年后端 / 架构经验
- ✅ .NET 8、模块化单体、插件边界、事件与安全设计
- 🎯 Target：Clean Architecture、微服务、Python、多语言
- 🎯 Target：Kubernetes / PostgreSQL / Redis / Kafka
- ✅ 安全与合规基础知识
- ⚠️ 不要求精通 AI / 前端 / 移动端

**文档所有权**：
- 主：`00-architecture/`、`01-domain/`、`07-team/`
- 辅：所有文档 Review

**关键产出（每周）**：
- ≥ 1 次架构决策（ADR 或 RFC）
- 至少 Review 5 个 PR
- ≥ 1 次跨域 sync

---

### 3.2 业务开发 / Backend Developer

**核心定位**：业务核心代码的主要作者

**核心职责**：

| 维度 | 内容 |
|------|------|
| **业务实现** | 当前四插件及 MockBank 的场景服务、Agent、规则和数据贡献器 |
| **API 设计** | REST API、OpenAPI 文档 |
| **数据库** | Schema 设计、Migration、查询优化 |
| **单元/集成测试** | 业务代码覆盖率 ≥ 80% |
| **业务对接** | 与核心银行 CBS / 外部系统对接 |
| **On-call** | 业务相关事故的 SME |

**能力要求**：
- ✅ 3+ 年 C# .NET 经验
- ✅ EF Core / SQLite；🎯 Target：PostgreSQL
- ✅ REST / OpenAPI / 中间件
- ✅ DDD 基础
- ⚠️ 不要求精通架构选型、K8s

**文档所有权**：
- 主：`01-domain/`（领域模型 + 限界上下文）、`02-api/`（API 规范）
- 辅：`03-development/`（编码规范、测试）

**关键产出（每周）**：
- ≥ 3 个业务 PR
- API 文档同步更新
- 关键路径的单元测试覆盖

---

### 3.3 AI/数据开发 / AI & Data Developer

**核心定位**：智能体、LLM、数据层的主要作者

**核心职责**：

| 维度 | 内容 |
|------|------|
| **当前 AI 能力** | Base 中的可选 LLM 意图识别、规则降级、AgentRouter/AgentOrchestrator 与插件 Agent |
| **Target AI Service** | 独立 Python/FastAPI、Prompt 工程、RAG、Function Call |
| **数据** | 数据 Schema（事件 Schema、用户记忆）、向量库 |
| **FeatureFlag** | AI 相关功能开关（产品决策落地） |
| **模型评估** | 黄金集构建、A/B 测试、效果监控 |
| **产品协作** | 与产品紧密对接，对业务结果负责 |

**能力要求**：
- ✅ 当前需能维护 C# AI/Agent 组件；🎯 Target：Python/FastAPI
- ✅ LLM / Prompt 工程 / RAG / Vector DB
- ✅ 多 Agent 框架（LangChain / AutoGen / 自研）
- ✅ PostgreSQL + pgvector
- ⚠️ 不要求精通 .NET 业务代码

**文档所有权**：
- 主：`02-api/02-event-schema.md`、`02-api/03-feature-flags.md`、`06-product/`
- 辅：`01-domain/02-domain-model.md`（AI 相关部分）

**关键产出（每周）**：
- ≥ 2 个 AI Service PR
- LLM 效果监控数据
- 黄金集准确率维护

---

### 3.4 平台 / Platform Engineer

**核心定位**：基础设施、CI/CD、环境的守护者

**核心职责**：

| 维度 | 内容 |
|------|------|
| **当前工程基线** | .NET 8 构建、测试、插件校验、Host/MockBank 启动配置 |
| **Target 平台** | K8s 集群、镜像、灰度、完整可观测性与高可用数据库 |
| **可观测性** | Prometheus / Grafana / ELK / Jaeger 维护 |
| **数据库运维** | PG 高可用、Redis、备份、监控 |
| **On-call** | 基础设施相关事故的 SME |
| **开发体验** | 开发环境、本地 Compose、Mock 服务 |

**能力要求**：
- ✅ 3+ 年 SRE / 平台经验
- ✅ Kubernetes / Docker / Helm / ArgoCD
- ✅ Linux / 网络 / 存储
- ✅ PostgreSQL DBA 级 / Redis / Kafka
- ✅ Prometheus / Grafana / ELK

**文档所有权**：
- 主：`04-operations/`、`03-development/05-ci-cd-pipeline.md`、`03-development/06-environment-setup.md`
- 辅：事故 Runbook、灾备演练

**关键产出（每周）**：
- 监控大盘维护
- 季度 DR 演练
- 优化 1 项基础设施

---

### 3.5 安全/合规 / Security & Compliance

**核心定位**：合规底线 + 安全防线的守护者

**核心职责**：

| 维度 | 内容 |
|------|------|
| **合规** | 法规跟踪、PIA、合规矩阵维护 |
| **安全** | 威胁建模、安全扫描、渗透测试协调 |
| **数据保护** | 数据分级、脱敏、加密方案 |
| **审计** | 审计日志规范、监管报送 |
| **应急** | 数据泄露应急、合规事故响应 |
| **培训** | 团队合规培训、宣导 |

**能力要求**：
- ✅ 2+ 年金融合规 / 安全经验
- ✅ 熟悉《个保法》《网络安全法》《数据安全法》《商业银行法》
- ✅ 熟悉 AI / LLM 合规要求
- ✅ 威胁建模（STRIDE）
- ⚠️ 不要求写业务代码

**文档所有权**：
- 主：`05-security-compliance/`（全部）
- 辅：合规检查 Review

**关键产出（每周）**：
- 法规跟踪报告
- ≥ 1 次合规 Review
- 季度合规审计

---

### 3.6 前端全栈 / Frontend & Full-Stack（可选第 5 人）

**核心定位**：IM 通道 / H5 / 后台的所有界面 + 用户体验

**核心职责**：

| 维度 | 内容 |
|------|------|
| **IM Bot** | 微信 / 支付宝 / 飞书 机器人 SDK 接入 |
| **当前 Web** | 维护 Host `wwwroot` 静态控制台 |
| **Target H5/Admin** | Next.js/Tailwind 对话界面、客服/审计后台与组件库 |
| **API 对接** | 调用 REST API、SSE 流式对话 |

**能力要求**：
- ✅ 3+ 年前端经验
- ✅ TypeScript / React / Next.js / 移动 H5
- ✅ IM 平台生态（公众号/小程序）
- ⚠️ 不要求精通架构

**文档所有权**：
- 主：`08-frontend/`（Current/Target 口径）
- 辅：所有 API 文档 Review、UI 规范

---

## 4. 角色 × 文档所有权矩阵

| 文档 | 架构师 | 业务开发 | AI 数据 | 平台 | 安全合规 |
|------|--------|---------|---------|------|---------|
| `00-architecture/` | **主** | 辅 | 辅 | 辅 | 辅 |
| `01-domain/` | **主** | **主** | 辅 | — | 辅 |
| `02-api/` | 辅 | **主** | **主** | — | 辅 |
| `03-development/` | 辅 | 辅 | 辅 | **主** | 辅 |
| `04-operations/` | 辅 | 辅 | 辅 | **主** | 辅 |
| `05-security-compliance/` | 辅 | 辅 | 辅 | 辅 | **主** |
| `06-product/` | 辅 | 辅 | **主** | 辅 | 辅 |
| `07-team/` | **主** | 辅 | 辅 | 辅 | 辅 |

---

## 5. 协作矩阵（RACI）

R = Responsible（执行）、A = Accountable（最终负责）、C = Consulted（咨询）、I = Informed（知情）

| 工作项 | 架构 | 业务 | AI | 平台 | 合规 |
|--------|------|------|-----|------|------|
| **架构决策** | A/R | C | C | C | C |
| **新功能设计** | A | R | C | I | C |
| **业务代码** | A | R | I | I | I |
| **AI 实现** | C | I | A/R | I | C |
| **部署发布** | A | C | C | R | I |
| **监控告警** | C | I | I | A/R | I |
| **合规审查** | A | I | C | I | R |
| **事故响应（业务）** | A | R | C | C | I |
| **事故响应（AI）** | A | C | R | C | I |
| **事故响应（基础设施）** | A | C | C | R | I |
| **事故响应（合规）** | A/R | I | I | I | R |
| **法规跟踪** | C | I | I | I | A/R |

---

## 6. 兼职与代岗

**原则**：5 人小团队必须能相互备份关键能力。

| 主岗 | 备份岗 | 备份能力要求 |
|------|--------|------------|
| 架构师 | 业务开发（最资深）| 能做架构决策 + 写核心代码 |
| 业务开发 | 架构师 + AI | 业务熟悉即可 |
| AI/数据 | 业务开发 | Python + LLM 基础 |
| 平台 | 架构师 | K8s + 脚本能力 |
| 安全合规 | 架构师 + 全员 | 合规意识 + 应急流程 |

---

## 7. 角色成长路径

| 职级 | 架构 | 业务 | AI | 平台 | 合规 |
|------|------|------|-----|------|------|
| **P5** | 独立负责子域 | 独立模块 | 独立 Agent | 独立子系统 | 独立合规域 |
| **P6** | 跨域协调 | 主要模块 + 部分架构 | 复杂 Agent 编排 | 全栈平台 | 法规深度 |
| **P7** | 系统总架构师 | 业务总负责人 | AI 架构师 | 平台总架构 | 合规总监 |

---

## 8. 跨域协作机制

| 场景 | 协作方式 |
|------|---------|
| 架构变更 | RFC → 全员 Review → ADR |
| AI 模型变更 | AI 起草 → 业务 Review（业务影响）→ 安全合规 Review（合规）|
| 合规新增义务 | 合规 Review → 架构 影响分析 → 业务 + AI 实施 |
| 故障 | IC 指挥（详见 `04-operations/03-incident-response.md`）|
| 新人加入 | Onboarding（详见 `07-team/03-onboarding.md`）|

---

## 9. 5 人团队的特殊挑战

| 挑战 | 缓解 |
|------|------|
| **单点故障**（某人请假/离职）| 文档化 + 知识共享 + 代岗机制 |
| **并发能力有限** | 模块边界清晰 + 可并行开发 |
| **轮值 on-call** | 5 人轮值 + 备份机制 |
| **新功能 vs 维护** | 留 ≥ 20% 时间给技术债清理 |
| **合规压力** | 合规角色不兼职（独立视角）|

---

## 10. 招聘画像（如扩招）

| 角色 | 关键画像 |
|------|---------|
| 业务开发 | 3-5 年 C# / Java 经验，做过金融或高并发 |
| AI/数据 | 2+ 年 LLM 实际项目，了解 RAG / Function Call |
| 安全合规 | 金融合规 2+ 年经验，了解 AI 法规 |
| 前端 | 3+ 年 React/Next.js，做过 IM 或 H5 |
| 平台 | 3+ 年 SRE，有金融系统 K8s 经验 |

---

## 11. 参考

- 协作机制 — `07-team/02-collaboration.md`
- 新人入职 — `07-team/03-onboarding.md`
- 架构总览 — `00-architecture/00-overview.md`
- 模块边界 — `00-architecture/05-module-boundaries.md`