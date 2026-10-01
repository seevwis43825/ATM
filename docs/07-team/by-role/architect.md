# 🏛️ 架构师 / Architect 分册

> **角色定位**：技术决策者 · 跨领域串联者
> **主战场**：`src/src/BankingAgent.Base/` `src/src/BankingAgent.Plugin.Sdk/` `src/src/BankingAgent.Host/` `docs/00-architecture/`
> **更新时间**：2026-10-01
> **口径**：先维护当前 .NET 8 模块化单体；独立服务、K8s 与生产平台职责均为 Target。

---

## 1. 你是什么

你是 5 人团队的**技术大脑**。其他 4 个人都依赖你做技术选型、模块边界、跨域协调。

你不一定写最多代码，但你要**看得最远**（5 年后再回看是否合理）。
你也不一定救最多的火，但你要**站在最前**（P0 事故默认 IC）。

---

## 2. 你的文档清单（按重要性排序）

### 2.1 必须精通 ⭐⭐⭐

| # | 文档 | 用途 |
|---|------|------|
| 1 | [`00-architecture/00-overview.md`](../../00-architecture/00-overview.md) | 系统总览，新人必读第一篇 |
| 2 | [`00-architecture/01-c4-context.md`](../../00-architecture/01-c4-context.md) | 系统上下文，与外部边界 |
| 3 | [`00-architecture/02-c4-containers.md`](../../00-architecture/02-c4-containers.md) | 容器视图，技术栈定型 |
| 4 | [`00-architecture/03-c4-components.md`](../../00-architecture/03-c4-components.md) | 组件视图，模块依赖 |
| 5 | [`00-architecture/05-module-boundaries.md`](../../00-architecture/05-module-boundaries.md) | **模块边界（你守的门）** |
| 6 | [`00-architecture/06-extension-points.md`](../../00-architecture/06-extension-points.md) | 插件化扩展（功能随时增加） |
| 7 | [`01-domain/01-bounded-contexts.md`](../../01-domain/01-bounded-contexts.md) | 限界上下文划分 |
| 8 | [`01-domain/02-domain-model.md`](../../01-domain/02-domain-model.md) | 领域模型核心实体 |
| 9 | [`00-architecture/04-architecture-decisions.md`](../../00-architecture/04-architecture-decisions.md) | **ADR 总集，你写的决策记录** |
| 10 | [`00-architecture/07-event-driven-contract.md`](../../00-architecture/07-event-driven-contract.md) | 事件驱动契约 |

### 2.2 必须了解 ⭐⭐

| # | 文档 | 用途 |
|---|------|------|
| 11 | [`07-team/01-team-roles.md`](../01-team-roles.md) | 团队角色全图 |
| 12 | [`07-team/02-collaboration.md`](../02-collaboration.md) | 协作机制、RFC、On-call |
| 13 | [`05-security-compliance/02-compliance-matrix.md`](../../05-security-compliance/02-compliance-matrix.md) | 合规义务矩阵 |
| 14 | [`06-product/01-roadmap.md`](../../06-product/01-roadmap.md) | 路线图，架构演进路线 |
| 15 | [`04-operations/01-deployment-architecture.md`](../../04-operations/01-deployment-architecture.md) | 部署架构 |
| 16 | [`03-development/04-testing-strategy.md`](../../03-development/04-testing-strategy.md) | 测试策略（保证架构可演进的护栏）|

### 2.3 偶读 ⭐

| # | 文档 | 用途 |
|---|------|------|
| 17 | [`02-api/01-rest-api-spec.md`](../../02-api/01-rest-api-spec.md) | API 设计规范 |
| 18 | [`04-operations/03-incident-response.md`](../../04-operations/03-incident-response.md) | 事故响应（你做 IC）|
| 19 | [`04-operations/04-disaster-recovery.md`](../../04-operations/04-disaster-recovery.md) | 容灾（架构层面） |

---

## 3. 你负责的代码模块

```
src/
├── src/BankingAgent.Base/           # 共享运行时、数据、安全、Agent 编排
├── src/BankingAgent.Plugin.Sdk/     # 插件契约与扩展边界
├── src/BankingAgent.Host/           # 组合根、API、静态控制台
├── plugins/                         # 四个业务插件
├── mock-bank/MockBank.Api/          # 独立模拟银行
└── UnitTests/                       # 当前 xUnit 测试
```

**Target**：如果未来通过 ADR 引入独立 AI 服务、Next.js 管理台、K8s 或更细的 Clean Architecture 分层，再为其建立真实目录和所有权；当前不存在 `agent-core`、`ai-service`、`Bootstrap`。

---

## 4. 你的关键产出

### 4.1 每周

- ≥ **1 个 ADR / RFC**（架构决策必须有据可查）
- ≥ **5 个 PR Review**（核心模块必须 Architect 签字）
- ≥ **1 次跨域 sync**（与每个角色分别 1-on-1）

### 4.2 每月

- 架构文档 Review + 更新
- 路线图 Review + 调整
- 团队技术分享（每月 1 次）

### 4.3 关键节点

- 新 Bounded Context 上线前 → 必须 Architect Review
- 重大依赖变更 → 必须 ADR
- 重大事故 → 必须 Postmortem 签字

---

## 5. 你的协作接口

| 你对接 | 对接什么 | 频率 |
|--------|---------|------|
| **业务开发** | 模块边界、领域模型、设计 Review | 每日 |
| **AI/数据** | Agent 架构、事件 Schema、LLM 集成 | 每周 2-3 次 |
| **平台** | 部署架构、可观测性、事故响应 | 每周 1 次 |
| **安全合规** | 合规架构、审计设计、数据保护 | 每周 1 次 |

---

## 6. 你的工作模式

### 6.1 一天典型时间分配

| 时间 | 活动 |
|------|------|
| 上午 10:00 | Daily Standup |
| 10:15 - 12:00 | 深度设计 / 编码（核心模块）|
| 13:00 - 14:00 | PR Review |
| 14:00 - 15:00 | 跨域 1-on-1 / RFC 评审 |
| 15:00 - 17:00 | 架构文档 / ADR 撰写 |
| 17:00 - 17:30 | 当日收尾 + 次日计划 |

### 6.2 深度工作原则

- 上午大块时间 = 设计与编码（不被打扰）
- 下午小块时间 = 沟通、Review、会议
- **永远不在上午安排会议**（除非紧急）

---

## 7. 你的关键决策清单（启动期）

下列是历史/目标决策主题；是否“已定”只以 [`../../00-architecture/04-architecture-decisions.md`](../../00-architecture/04-architecture-decisions.md) 的 Current/Target/Historical 标记为准：

- [x] ADR-0001：模块化单体（Current）
- [ ] ADR-0002：未来微服务演进（Target）
- [x] ADR-0003：当前进程内事件总线；持久化总线为 Target
- [ ] ADR-0004：C# + Python 双语言栈（Target）
- [ ] ADR-0005：国内 LLM 可选接入（Target / 可选）
- [ ] ADR-0006：PostgreSQL + pgvector（Target；当前默认 SQLite）
- [ ] ADR-0007：Kong / APISIX 网关（Target / 待决策）
- [ ] ADR-0008：人工回环（Partial，已落基础能力但仍需补齐场景）
- [x] ADR-0009：可插拔 Agent 注册（Current）
- [ ] ADR-0010：FeatureFlag 体系（Target / 待决策；当前只有 manifest 元数据）

后续需补的：
- [ ] ADR-011：何时拆分为微服务
- [ ] ADR-012：CDN 与边缘计算
- [ ] ADR-013：API 网关迁移（Kong → APISIX？）
- [ ] ADR-014：灾备 Region 选择

---

## 8. 你负责的"严"（必须为的）

1. **模块依赖只能向下** — UI → Application → Infrastructure，绝不反向
2. **跨模块依赖遵守已落地契约** — 优先 Sdk 接口、Agent、事件和贡献器，不虚构不存在的层
3. **核心代码加强 Review** — Base、Sdk、Host 组合根与安全边界
4. **新功能必须插件化** — 严禁改 Core
5. **关键操作必须 HITL** — 转账/卡片/理财/跨场景
6. **ADR 优先于口口相传** — 所有决策必须有据

---

## 9. 学习资源

### 9.1 必读

- Vaughn Vernon《实现领域驱动设计》（前 10 章）
- Robert C. Martin《架构整洁之道》
- Microsoft《eShopOnContainers》源码（C# 微服务参考）

### 9.2 推荐

- Eric Evans《领域驱动设计》
- Sam Newman《微服务设计》《构建可演化系统》
- 国内：郑烨《代码精进之路》

### 9.3 论文（与本系统相关）

- [Ryt Bank (EMNLP 2025)](https://arxiv.org/abs/2510.07645) — 真实银行多 Agent
- [TradingAgents (AAAI 2025)](https://arxiv.org/abs/2412.20138) — 多 Agent 决策
- [FinAgent (KDD 2024)](https://arxiv.org/abs/2410.08061) — 金融 Agent 工具调用

---

## 10. FAQ

### Q1：我代码写得不多，会不会掉队？

**不会**。架构师的核心价值是**看得远 + 把控方向**。但你需要保持代码手感（每周至少写 1-2 个核心 PR）。

### Q2：业务开发和我意见冲突怎么办？

倾听 → 理解 → 决策。架构师有最终决定权，但**必须给理由**（ADR 记录）。

### Q3：ADR 太多怎么办？

归档：合并、过期、废弃。保留最近 20 条活跃 ADR，历史归档到 `docs/adr-archive/`。

### Q4：我不熟悉 AI，怎么 Review AI 代码？

重点 Review **架构层面**（与 Core 的接口、事件 Schema、数据流），具体算法不需要精通。

---

## 11. 联系

- Slack / 飞书：@architect
- On-call：值班 1 周/月（详见 [`../07-team/02-collaboration.md`](../02-collaboration.md)）
- Mentor 给：架构师（资深成员）