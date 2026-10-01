# 💼 业务开发 / Backend Developer 分册

> **角色定位**：业务核心代码的主要作者
> **主战场**：`src/plugins/` `src/mock-bank/MockBank.Api/` `src/UnitTests/`
> **更新时间**：2026-10-01
> **口径**：当前实现为四个 C# 插件 + Base/Sdk/Host；下文分层、PostgreSQL 和 Testcontainers 要求标为 Target。

---

## 1. 你是什么

你是 5 人团队里**写业务代码最多**的人。每个 Bounded Context 的应用服务、聚合根、领域事件，绝大多数代码出自你手。

你的代码直接关系**用户资金安全 + 业务正确性**，所以你必须：
- 严守模块边界（架构师定的规则）
- 严守领域模型（不写贫血模型）
- 为行为变更补测试；80% 是目标阈值，不冒充当前 CI 门禁
- 严守审计埋点（合规要求）

---

## 2. 你的文档清单（按重要性排序）

### 2.1 必须精通 ⭐⭐⭐

| # | 文档 | 用途 |
|---|------|------|
| 1 | [`01-domain/01-bounded-contexts.md`](../../01-domain/01-bounded-contexts.md) | 限界上下文（**你工作的边界**） |
| 2 | [`01-domain/02-domain-model.md`](../../01-domain/02-domain-model.md) | **领域模型（C# 代码参考）** |
| 3 | [`01-domain/03-glossary.md`](../../01-domain/03-glossary.md) | 术语表（中英对照，避免歧义）|
| 4 | [`02-api/01-rest-api-spec.md`](../../02-api/01-rest-api-spec.md) | REST API 设计规范 |
| 5 | [`02-api/02-event-schema.md`](../../02-api/02-event-schema.md) | 事件 Schema（CloudEvents） |
| 6 | [`02-api/03-feature-flags.md`](../../02-api/03-feature-flags.md) | 功能开关规范 |
| 7 | [`03-development/01-coding-standards.md`](../../03-development/01-coding-standards.md) | **代码规范（必读）** |
| 8 | [`03-development/02-git-workflow.md`](../../03-development/02-git-workflow.md) | Git Flow |
| 9 | [`03-development/03-pr-review-checklist.md`](../../03-development/03-pr-review-checklist.md) | PR Review Checklist |
| 10 | [`03-development/04-testing-strategy.md`](../../03-development/04-testing-strategy.md) | **测试策略** |

### 2.2 必须了解 ⭐⭐

| # | 文档 | 用途 |
|---|------|------|
| 11 | [`00-architecture/05-module-boundaries.md`](../../00-architecture/05-module-boundaries.md) | 模块边界（写代码时遵守） |
| 12 | [`00-architecture/06-extension-points.md`](../../00-architecture/06-extension-points.md) | 扩展机制（新增功能看这里） |
| 13 | [`00-architecture/07-event-driven-contract.md`](../../00-architecture/07-event-driven-contract.md) | 事件契约（发事件前必看） |
| 14 | [`05-security-compliance/04-audit-logging.md`](../../05-security-compliance/04-audit-logging.md) | 审计日志（关键操作必埋点） |
| 15 | [`06-product/02-feature-lifecycle.md`](../../06-product/02-feature-lifecycle.md) | 功能生命周期 |

### 2.3 偶读 ⭐

| # | 文档 | 用途 |
|---|------|------|
| 16 | [`05-security-compliance/02-compliance-matrix.md`](../../05-security-compliance/02-compliance-matrix.md) | 合规义务 |
| 17 | [`05-security-compliance/03-data-classification.md`](../../05-security-compliance/03-data-classification.md) | 数据分级 |
| 18 | [`06-product/03-release-process.md`](../../06-product/03-release-process.md) | 发布流程 |
| 19 | [`03-development/06-environment-setup.md`](../../03-development/06-environment-setup.md) | 本地环境搭建 |

---

## 3. 你负责的代码模块

```
src/
├── plugins/
│   ├── BankingAgent.Plugin.Transfer/
│   ├── BankingAgent.Plugin.BillAnalysis/
│   ├── BankingAgent.Plugin.CardManagement/
│   └── BankingAgent.Plugin.Wealth/
├── mock-bank/MockBank.Api/                # 模拟核心银行端点与服务
├── src/BankingAgent.Base/                 # 需谨慎修改的共享能力
├── src/BankingAgent.Plugin.Sdk/           # 公共插件契约
└── UnitTests/                             # 当前 xUnit 测试
```

**Target**：复杂插件可逐步采用 Domain/Application/Infrastructure 分层；不要把尚不存在的 `agent-core/Modules`、Subscription 或 CrossScenario 目录当作当前主战场。

---

## 4. 你的工作流程（开发一个功能）

```
1. 接到 Issue
   ↓
2. 读 PRD + ADR（影响占位的话）
   ↓
3. 设计
   ├─ 新模块？→ 走模块边界规则（00-architecture/05）
   ├─ 现有模块？→ 直接进入
   └─ 涉及 PII？→ 走 PIA（05-security-compliance/02 §9）
   ↓
4. 创建分支
   git checkout -b feature/<ticket>-<short-name>
   ↓
5. 编码（遵循 coding-standards.md）
   ├─ 对应插件中的 Agent/服务/规则/数据贡献器
   ├─ 必要的 Sdk 契约或 Base 共享能力（谨慎评审）
   ├─ Host/MockBank API（仅在职责属于入口或模拟系统时）
   └─ 数据库模型变化；生产化时使用 EF Core Migration 类
   ↓
6. 在 src/UnitTests 增补单元测试；覆盖率目标不冒充当前 CI 门禁
   ↓
7. 集成测试（API + DB + EventBus）
   ↓
8. 审计埋点（关键操作）
   ↓
9. 若功能需灰度，先实现运行时 FeatureFlag；当前 manifest 字段仅是元数据
   ↓
10. 自测通过 → Push → 创建 PR
   ↓
11. 自填 PR 模板 + Checklist
   ↓
12. 请架构师 Review 核心模块
   ↓
13. 处理 Review 意见
   ↓
14. CI 全绿 → 合入
   ↓
15. 灰度发布（参见 06-product/03）
```

---

## 5. 你的关键产出

### 6.1 每周

- ≥ **3 个业务 PR**（含单元测试）
- 新行为有对应测试；覆盖率逐步向 **80% Target** 靠拢
- API 文档同步更新（OpenAPI 自动生成）
- 关键路径审计埋点完整

### 6.2 每月

- ≥ 1 次代码 refactor（消除技术债）
- 至少 1 个 bug 修复
- 配合架构师做模块边界 Review

---

## 6. 你的协作接口

| 你对接 | 对接什么 | 频率 |
|--------|---------|------|
| **架构师** | 模块边界、领域模型、核心代码 Review | 每日 |
| **AI/数据** | Agent 编排、事件 Schema、LLM 工具 | 每周 2-3 次 |
| **平台** | 部署、CI、本地环境 | 按需 |
| **安全合规** | PIA、审计埋点、合规 Review | 每次涉及合规时 |
| **前端** | API 契约、OpenAPI、字段含义 | 每次有 API 变更 |

---

## 7. 你必须遵守的"硬规则"

1. **遵守当前模块边界** — Host 负责入口，Base 提供共享能力，Sdk 定义契约，插件承载场景
2. **跨模块通过已落地契约** — Sdk 接口、Agent、事件或贡献器
3. **不能把 manifest FeatureFlags 当作运行时开关** — 灰度发布前需先落地真正的拦截服务
4. **关键操作必须审计埋点** — 按当前已实现的场景逐项验证，不引用尚未实现的订阅模块
5. **数据访问集中管理** — 当前插件可通过 `BankingDbContext`/贡献器持久化；复杂化后再引入 Repository
6. **业务规则放在明确的领域或场景组件中** — 不机械套用尚未落地的聚合结构
7. **不绕过 ComplianceGuard** — 所有写操作必须经过它
8. **不泄露 PII** — 日志中永不出现明文 PII
9. **不硬编码密钥或敏感 Prompt 数据** — 当前 LLM 配置走 Host 配置/环境变量；独立 AI Service 为 Target
10. **必须有单元测试** — 80% 是目标，是否达标以实际覆盖率报告为准

---

## 8. 关键技术栈

| 技术 | 用途 | 学习资源 |
|------|------|---------|
| **C# .NET 8** | 主语言 | 微软官方文档 |
| **EF Core 8** | ORM | 微软官方文档 |
| **当前：Base/Sdk 插件契约** | 插件注册、Agent、事件、贡献器 | 仓库源码 |
| **当前：ASP.NET Core 日志** | 结构化日志 | 微软文档 |
| **当前：xUnit** | 单元测试 | xUnit.net |
| **Target：Testcontainers** | PostgreSQL 集成测试 | testcontainers.com |
| **SpecFlow** | BDD（可选） | specflow.org |

---

## 9. 你的"严"（测试要求）

每个 PR 的当前检查基线（仅把仓库真实 CI 作业称为强制门禁）：

- [ ] 编译成功（dotnet build）
- [ ] 单元测试通过（dotnet test）
- [ ] 新行为有对应测试，测试数量引用运行输出
- [ ] 无硬编码 secret 或明文敏感数据
- [ ] OpenAPI 文档同步
- [ ] PR 模板全部勾选
- [ ] ≥ 1 个 Reviewer；Base/Sdk/Host 边界变更请架构角色参与

---

## 10. 你的代码模板

### 10.1 聚合根示例（参见 01-domain/02）

```csharp
public class Transfer : AggregateRoot<TransferId>
{
    public AccountId From { get; }
    public AccountId To { get; }
    public Money Amount { get; }
    public TransferStatus Status { get; private set; }
    
    public static Transfer Create(AccountId from, AccountId to, Money amount)
    {
        // 业务规则校验
        if (amount.IsNegative) throw new InvalidAmountException();
        
        var transfer = new Transfer(from, to, amount, TransferStatus.Pending);
        transfer.AddDomainEvent(new TransferCreatedEvent(transfer.Id, ...));
        return transfer;
    }
    
    public void Approve(string approvedBy)
    {
        if (Status != TransferStatus.PendingApproval)
            throw new InvalidStateException();
        Status = TransferStatus.Approved;
        AddDomainEvent(new TransferApprovedEvent(Id, approvedBy));
    }
}
```

### 10.2 API 端点示例（参见 02-api/01）

```csharp
[ApiController]
[Route("api/v1/transfers")]
public class TransferController : ControllerBase
{
    private readonly IMediator _mediator;
    
    [HttpPost]
    [ProducesResponseType(typeof(TransferResponse), 201)]
    [ProducesResponseType(typeof(ErrorResponse), 400)]
    public async Task<IActionResult> Create([FromBody] CreateTransferRequest req)
    {
        var cmd = new CreateTransferCommand(req.FromAccount, req.ToAccount, req.Amount);
        var result = await _mediator.Send(cmd);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }
}
```

### 10.3 单元测试示例

```csharp
public class TransferTests
{
    [Fact]
    public void Create_WithNegativeAmount_ShouldThrow()
    {
        var act = () => Transfer.Create(
            AccountId.New(),
            AccountId.New(),
            Money.CNY(-100));
        act.Should().Throw<InvalidAmountException>();
    }
}
```

---

## 11. 学习资源

### 11.1 必读

- Vaughn Vernon《实现领域驱动设计》第 5-9 章
- Jon Smith《Entity Framework Core 实战》
- 微软《ASP.NET Core 最佳实践》

### 11.2 推荐

- Andrew Lock《ASP.NET Core 深入浅出》
- Vladimir Khorikov《单元测试的艺术》

### 11.3 论文

- [Ryt Bank (EMNLP 2025)](https://arxiv.org/abs/2510.07645) — 真实银行多 Agent 工具调用

---

## 12. FAQ

### Q1：模块之间需要共享数据怎么办？

**禁止**直接共享。通过：
1. **领域事件**（异步，最终一致）
2. **API 调用**（同步，通过 API Gateway）
3. **共享只读视图**（独立 DB Schema）

### Q2：领域逻辑放在哪？Service 还是 Entity？

**Entity 优先**。业务规则是实体的责任（如 `Transfer.create` 校验金额）。Service 只做编排（事务、跨实体协调）。

### Q3：HITL 怎么做？

调用 `_complianceGuard.RequireHumanApproval(scenario, context)`，由 `ComplianceGuard` 决定是否路由到人工工单（参见 `00-architecture/05`）。

### Q4：审计埋点漏了怎么办？

**视为 P0 Bug**。补埋点 + 重新审计 → 走事故响应流程（Postmortem）。

---

## 13. 联系

- Slack / 飞书：@backend
- Mentor：架构师（资深业务开发）