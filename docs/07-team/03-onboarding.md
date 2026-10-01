# 新人入职指南（Onboarding）

> **状态**：评审中 · **所有者**：架构师 · **版本**：v1.0
> **最后更新**：2026-10-01
> **预计入职上手时间**：2 周（基本独立）+ 1 个月（完全独立）

---

## 1. 欢迎 🎉

欢迎加入 AI Banking Agent 团队！

我们做的是一个**面向银行业的多智能体（Multi-Agent）对话系统**。当前仓库使用 MockBank 和演示数据；真实资金、真实用户隐私与生产监管要求属于上线目标。

这意味着：
- 生产化后的代码可能直接关系到**用户的真金白银**
- 你的代码必须**可审计、可解释、可回滚**
- 你的代码关乎**监管合规**

这份文档会带你从 0 到独立上手。如果有任何不清楚，找 **架构师**（默认 mentor）或任何团队成员。

---

## 2. 第一天（Day 1）

### 2.1 上午：账号开通（HR + IT）

- [ ] 企业邮箱
- [ ] 代码仓库权限（GitHub / GitLab）
- [ ] 文档平台权限（飞书 / Notion / 语雀）
- [ ] IM 群组（团队群 + 各模块群）
- [ ] 云账号（生产只读权限）
- [ ] Jira / 飞书项目账号

### 2.2 下午：环境搭建

先安装 .NET SDK 9.0.200+（用于读取 `.slnx`），并确保具备 .NET 8 Runtime/Targeting Pack（项目目标为 `net8.0`），再在仓库根目录执行：

```bash
# 1. 克隆代码
git clone <repo-url>
cd ATM

# 2. 恢复、构建和单元测试
dotnet restore src/BankingAgent.slnx
dotnet build src/BankingAgent.slnx -c Release
dotnet test src/UnitTests/UnitTests.csproj -c Release

# 3. 终端 A：启动 MockBank
dotnet run --project src/mock-bank/MockBank.Api/MockBank.Api.csproj

# 4. 终端 B：启动 Host
dotnet run --project src/src/BankingAgent.Host/BankingAgent.Host.csproj
```

访问：

- Host 静态控制台：`http://localhost:5243/`
- Host 健康检查：`http://localhost:5243/health`
- MockBank OpenAPI：`http://localhost:5200/openapi/v1.json`

默认使用 SQLite + `EnsureCreated`，无需 Docker/PostgreSQL 即可启动。当前仓库没有 `src/agent-core`、`src/ai-service` 或 Python/FastAPI 服务。

### 2.3 第一天目标

- [ ] 能跑起来本地环境
- [ ] 能访问所有文档
- [ ] 认识所有团队成员
- [ ] 知道自己的 mentor 是谁
- [ ] 加入 `#general`、`#dev` 群

### 2.4 第一天 README

由 mentor 提供一份当周重点文档清单（建议）：
- [`00-architecture/00-overview.md`](../00-architecture/00-overview.md)
- [`01-domain/01-bounded-contexts.md`](../01-domain/01-bounded-contexts.md)
- [`07-team/01-team-roles.md`](01-team-roles.md)
- [`03-development/01-coding-standards.md`](../03-development/01-coding-standards.md)

---

## 3. 第一周：认知周

### 3.1 阅读清单（按顺序）

| 顺序 | 文档 | 目标 |
|------|------|------|
| 1 | [00-architecture/00-overview.md](../00-architecture/00-overview.md) | 了解系统总览 |
| 2 | [00-architecture/01-c4-context.md](../00-architecture/01-c4-context.md) | 了解系统上下文 |
| 3 | [00-architecture/02-c4-containers.md](../00-architecture/02-c4-containers.md) | 了解容器视图 |
| 4 | [00-architecture/05-module-boundaries.md](../00-architecture/05-module-boundaries.md) | 了解模块边界 |
| 5 | [01-domain/01-bounded-contexts.md](../01-domain/01-bounded-contexts.md) | 了解限界上下文 |
| 6 | [01-domain/03-glossary.md](../01-domain/03-glossary.md) | 了解术语 |
| 7 | [02-api/01-rest-api-spec.md](../02-api/01-rest-api-spec.md) | 了解 API 规范 |
| 8 | [03-development/01-coding-standards.md](../03-development/01-coding-standards.md) | 了解代码规范 |
| 9 | [03-development/02-git-workflow.md](../03-development/02-git-workflow.md) | 了解 Git 流程 |
| 10 | [05-security-compliance/02-compliance-matrix.md](../05-security-compliance/02-compliance-matrix.md) | 了解合规 |
| 11 | [05-security-compliance/04-audit-logging.md](../05-security-compliance/04-audit-logging.md) | 了解审计 |

### 3.2 跑通本地完整链路

```bash
# 先按 Day 1 启动 MockBank(:5200) 与 Host(:5243)，再运行独立 E2E 程序
dotnet run --project src/E2ETest/E2ETest.csproj
```

通过 Host `wwwroot` 控制台验证登录、对话、人工确认和四插件场景。Grafana、Kibana、PostgreSQL 是 Target 平台能力，不是当前本地验收前提。

### 3.3 第一个 PR（修复小问题 / 完善文档）

- mentor 指派一个简单任务（如"修复 README 错别字"或"补充某文档缺失章节"）
- 体验完整 PR 流程：分支 → 提交 → Push → 创建 PR → Review → 合入

### 3.4 旁听会议

- 旁听 Daily Standup（不说，只听）
- 旁听迭代规划 / 复盘

### 3.5 第一周目标

- [ ] 读完 11 篇核心文档
- [ ] 本地环境能跑通核心场景
- [ ] 完成第 1 个 PR
- [ ] 熟悉团队成员与职责
- [ ] 理解核心业务流程

---

## 4. 第二周：实战周

### 4.1 接受第一个真实任务

由 mentor 分配一个**简单但真实**的任务，例如：

- 加一个 API 接口（参见 `02-api/01-rest-api-spec.md`）
- 加一个领域事件（参见 `02-api/02-event-schema.md`）
- 加一个 Feature Flag（参见 `02-api/03-feature-flags.md`）
- 修一个简单 bug

### 4.2 走完完整流程

```
1. 创建 Issue（参见 03-development/02-git-workflow.md）
2. 从最新 main 拉短分支：feature/<ticket-id>-<name>
3. 写代码（遵循 coding-standards.md）
4. 写单元测试
5. 本地测试通过
6. 提交（Conventional Commits）
7. Push + 创建 PR
8. 自评（PR 模板）
9. 请 mentor Review
10. 处理 Review 意见
11. CI 全绿
12. PR 合入 main
```

### 4.3 参与设计评审（如有）

- 旁听 1 次 RFC / 设计评审
- 看完 RFC 后能提出 1-2 个问题

### 4.4 第二周目标

- [ ] 完成 1 个真实任务（独立完成）
- [ ] 熟悉完整开发流程
- [ ] 知道如何找 mentor 求助
- [ ] 开始参加 Daily Standup（发言）

---

## 5. 第三 - 四周：独立周

### 5.1 接受复杂任务

由 mentor 分配一个**跨模块**的任务，例如：

- 加一个新场景的 Bounded Context（参见 `00-architecture/06-extension-points.md`）
- 实现一个 LLM Agent 的工具（Function Call）
- 加一个监控指标 + 告警

### 5.2 走完完整流程 + 灰度发布

```
1. 任务 PIA（参见 05-security-compliance/02-compliance-matrix.md §9）
2. 设计 + 评审
3. 开发 + 测试
4. FeatureFlag 上线
5. 灰度发布（参见 06-product/03-release-process.md）
6. 监控 + 复盘
```

### 5.3 一次完整 On-call

- 加入 On-call 轮值（与 mentor 一起值班）
- 处理 1 次真实告警（或演练）

### 5.4 一个月目标

- [ ] 能独立完成中等复杂度任务
- [ ] 知道系统每个模块的作用
- [ ] 知道如何定位 bug / 调优性能
- [ ] 知道如何发布功能（灰度）
- [ ] 知道如何响应告警（on-call）
- [ ] 读过 ≥ 5 篇核心论文（参见 `../papers/`）

---

## 6. 学习资源（推荐路径）

### 6.1 必读（入职第 1 周）

- 本项目所有架构文档（`docs/00-architecture/`）
- 本项目所有合规文档（`docs/05-security-compliance/`）
- DDD 速成：Vaughn Vernon《DDD 精粹》（前 5 章）
- 论文阅读：从 [F05 TradingAgents](https://arxiv.org/abs/2412.20138) 开始

### 6.2 推荐（入职 1 个月）

- Vaughn Vernon《实现领域驱动设计》
- Robert C. Martin《架构整洁之道》
- Microsoft《Azure 架构良好实践》
- LangChain / LlamaIndex 官方文档
- OWASP Top 10 + LLM Top 10

### 6.3 进阶（入职 3 个月+）

- Eric Evans《领域驱动设计》（软件工程界"圣经"）
- Sam Newman《微服务设计》
- 央行《AI 类工具指导意见》原文
- 《个人信息保护法》《数据安全法》《网络安全法》原文

### 6.4 论文清单

参考 `papers/papers_summary.md` + `papers_summary_part2.md` 推荐的论文。

建议从以下论文入手：
1. [Ryt Bank (EMNLP 2025)](https://arxiv.org/abs/2510.07645) — 真实银行场景
2. [TradingAgents (AAAI 2025)](https://arxiv.org/abs/2412.20138) — 多 Agent 协同
3. [FinCon (NeurIPS 2024)](https://arxiv.org/abs/2407.06567) — LLM 金融决策
4. [FinAgent (KDD 2024)](https://arxiv.org/abs/2410.08061) — 金融 Agent 工具调用
5. [DeepFund (NeurIPS 2025)](https://arxiv.org/abs/2505.11065) — 实时基金投资

---

## 7. 常见问题（FAQ）

### Q1：本地环境跑不起来怎么办？

按顺序排查：
1. .NET 8 SDK 是否安装，`dotnet --info` 是否正常？
2. `5200` / `5243` 端口是否被占用？
3. 是否先构建整个 `src/BankingAgent.slnx`，让四个插件复制到 Host 输出目录？
4. 查看两个进程的控制台日志，再搜索 Issue 或求助 mentor

### Q2：不知道某个功能怎么实现？

1. 查文档（搜索关键字）
2. 看已有类似功能的代码（grep）
3. 看 Issue 历史
4. 问 mentor（**不要** 自己瞎猜然后提交）

### Q3：测试挂了怎么排查？

1. 看 CI 日志
2. 本地复现：`dotnet test src/UnitTests/UnitTests.csproj -c Release`
3. 看代码改动是否影响测试
4. 实在不行请 mentor 帮忙看

### Q4：PR Review 被打了 NIT（琐碎意见）怎么办？

**谦虚接受**。NIT 是为了团队代码一致性，不是针对个人。

### Q5：合规问题不知道怎么处理？

**立刻**找安全合规角色（如 HR 告知） + 架构师。合规问题一律 P0，宁可错杀。

### Q6：犯了错怎么办？

**立即报告，不要掩盖**。Blameless 文化下，承认错误是最好的处理方式。

### Q7：感觉工作太多 / 太累怎么办？

**主动沟通**。可以：
- 与 mentor 1-on-1
- 在 Daily Standup 上说"我现在 OOO，需要帮助"
- 团队会议讨论优先级调整

---

## 8. mentor 制度

### 8.1 分配原则

- 每个新人指定 **1 名主 mentor** + 1 名副 mentor
- mentor 优先级：架构师 > 业务开发 > 平台 > 合规
- mentor 任期：入职后 1 个月

### 8.2 mentor 职责

- 每日 15 分钟 check-in（入职第 1 周）
- 每周 1 小时 1-on-1（入职第 2-4 周）
- 任务分配 + Code Review 主导
- 答疑 + 心理支持

### 8.3 1-on-1 议程（mentor 主导）

```
1. 这周做得怎么样？（整体感受）
2. 技术问题 / 困惑
3. 工作量是否合适？
4. 团队融入情况
5. 下周计划
```

---

## 9. 入职检查清单（HR / IT）

### 9.1 入职前 1 天

- [ ] 办公设备（电脑、显示器、键鼠）
- [ ] VPN 账号（如需）
- [ ] 企业邮箱
- [ ] 临时门禁卡

### 9.2 入职当天

- [ ] 团队介绍（HR 主导）
- [ ] 账号全部开通（IT 主导）
- [ ] 入职材料签字
- [ ] 第一次与 mentor 见面对接

### 9.3 入职 1 周

- [ ] 完成第一周目标
- [ ] 入职面谈（HR 回访）

### 9.4 入职 1 个月

- [ ] 转正评估（如适用）
- [ ] 1-on-1（HR + mentor）
- [ ] 完整 On-call 1 次

### 9.5 入职 3 个月

- [ ] 转正答辩（如适用）
- [ ] 完整任务交付
- [ ] 团队反馈

---

## 10. 团队价值观（请融入日常）

我们 5 人小团队的**核心价值观**：

1. **用户资金安全第一**：永远在合规与资金安全前提下做技术决策
2. **可审计可解释**：任何关键决策必须可追溯、可解释、可申诉
3. **简单优先**：5 人团队做不了复杂架构，能简单就简单
4. **文档即代码**：文档与代码同等重要，必须同步更新
5. **Blameless 文化**：事故复盘追究系统问题，不追究个人
6. **持续学习**：AI / 合规领域日新月异，每周都有新东西

---

## 11. 参考

- 团队角色 — `07-team/01-team-roles.md`
- 协作机制 — `07-team/02-collaboration.md`
- 环境搭建 — `03-development/06-environment-setup.md`
- 编码规范 — `03-development/01-coding-standards.md`
- Git 工作流 — `03-development/02-git-workflow.md`
- 合规矩阵 — `05-security-compliance/02-compliance-matrix.md`