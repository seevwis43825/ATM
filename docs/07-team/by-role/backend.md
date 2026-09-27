# 💼 业务开发 / Backend Developer 分册

> **角色定位**：业务核心代码的主要作者
> **主战场**：`01-domain/` `02-api/` `src/agent-core/Modules/`
> **更新时间**：2026-09-21

---

## 1. 你是什么

你是 5 人团队里**写业务代码最多**的人。每个 Bounded Context 的应用服务、聚合根、领域事件，绝大多数代码出自你手。

你的代码直接关系**用户资金安全 + 业务正确性**，所以你必须：
- 严守模块边界（架构师定的规则）
- 严守领域模型（不写贫血模型）
- 严守测试覆盖率（≥ 80%）
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
├── agent-core/                            ← 【你的主战场】
│   ├── Transfer/                          # 转账场景
│   │   ├── Transfer.API/                  # API 层
│   │   ├── Transfer.Application/          # 应用服务
│   │   ├── Transfer.Domain/               # 领域模型
│   │   ├── Transfer.Infrastructure/       # 基础设施
│   │   └── Transfer.Integration.Tests/    # 集成测试
│   ├── BillAnalysis/                      # 账单分析
│   ├── Wealth/                            # 理财
│   ├── CardManagement/                    # 卡片管理
│   ├── Subscription/                      # 订阅/代扣
│   ├── CrossScenario/                     # 跨场景联动
│   └── AgentOrchestration/                # 编排（与 AI 协作）
└── admin-web/                             # 【前端角色，但业务理解是你】
```

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
   ├─ 聚合根 + 值对象 + 领域事件
   ├─ 应用服务（CQRS）
   ├─ API 端点（OpenAPI）
   └─ 数据库 Migration
   ↓
6. 单元测试（覆盖率 ≥ 80%）
   ↓
7. 集成测试（API + DB + EventBus）
   ↓
8. 审计埋点（关键操作）
   ↓
9. Feature Flag 包裹（新功能默认关闭）
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
- ≥ **80%** 新代码覆盖率
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

1. **模块依赖只能向下** — UI → Application → Domain ← Infrastructure
2. **跨模块只通过 EventBus** — 严禁直接调用其他模块的服务
3. **新功能必须 FeatureFlag 关闭** — 默认 OFF，灰度打开
4. **关键操作必须审计埋点** — 转账、卡片、理财、订阅
5. **不直接调用 DB** — 必须通过 Repository / Aggregate
6. **不写贫血模型** — 业务逻辑在领域对象中，不在 Service 中
7. **不绕过 ComplianceGuard** — 所有写操作必须经过它
8. **不泄露 PII** — 日志中永不出现明文 PII
9. **不硬编码 LLM Prompt** — Prompt 在配置中心 / ai-service
10. **必须有单元测试** — 覆盖率 ≥ 80%

---

## 8. 关键技术栈

| 技术 | 用途 | 学习资源 |
|------|------|---------|
| **C# .NET 8** | 主语言 | 微软官方文档 |
| **EF Core 8** | ORM | 微软官方文档 |
| **MediatR** | 进程内事件总线 | GitHub Wiki |
| **Polly** | 熔断/重试 | GitHub Wiki |
| **Serilog** | 日志 | Serilog.net |
| **xUnit + FluentAssertions** | 单元测试 | xUnit.net |
| **Testcontainers** | 集成测试 | testcontainers.com |
| **SpecFlow** | BDD（可选） | specflow.org |

---

## 9. 你的"严"（测试要求）

每个 PR 必须通过的检查（CI 强制）：

- [ ] 编译成功（dotnet build）
- [ ] 单元测试通过（dotnet test）
- [ ] 代码覆盖率 ≥ 80%（新代码）
- [ ] SonarQube 无高危阈值
- [ ] 无 secrets（GitLeaks）
- [ ] OpenAPI 文档同步
- [ ] PR 模板全部勾选
- [ ] ≥ 1 个 Reviewer（核心模块要架构师）

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