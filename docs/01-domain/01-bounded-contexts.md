# 限界上下文（Bounded Contexts）

> **状态**：与当前实现对齐 · **所有者**：架构师 + 业务开发 · **版本**：v1.1
> **最后更新**：2026-10-01
>
> **边界**：Current 描述实际插件能力边界；后文十个 Context 是 Target 领域分解，不代表已有十个项目、聚合或服务。

---

## 1. 什么是 Bounded Context？

**限界上下文**（Bounded Context）是 DDD 中的核心概念。每个 Context 内部：
- 有自己的领域模型、术语、规则
- 与外部 Context 通过**防腐层（Anti-Corruption Layer）** 通信
- 独立演进、独立部署（未来）

---

## 2. 当前能力边界（Current）

当前代码以 **Base + 插件** 划分能力，而不是以独立 DDD Context 项目划分：

| 当前边界 | 已实现职责 | 主要模型/接口 |
|---|---|---|
| Transfer 插件 | 意图解析、账户/收款人解析、合规、人工确认、幂等、模拟转账、记录落库、发布事件 | `TransferAgent`、`TransferRecord`、`ITransferIntentParser` |
| BillAnalysis 插件 | 月账单查询、分类汇总展示、脱敏；监听转账完成事件 | `BillAnalysisAgent`、`TransferEventListener` |
| CardManagement 插件 | 卡列表查询、挂失/冻结/解挂、合规检查与审计 | `CardManagementAgent` |
| Wealth 插件 | 余额、理财产品查询和只读推荐 | `理财Agent`；当前不含申购/赎回 |
| Base Agent 能力 | 规则/可选 LLM 意图分类、单跳路由、多步编排、轨迹 | `IIntentClassifier`、`AgentRouter`、`AgentOrchestrator` |
| Base 横切能力 | 合规、审计、数据库、事件、插件加载、核心银行客户端 | `ComplianceGuard`、`IAuditLogger`、`BankingDbContext`、`IEventPublisher` |
| MockBank.Api | 模拟客户、账户、交易、卡片和理财产品；不是 Host 内的领域插件 | `MockBankStore` 及 `MockBank.Api.Domain.*` |

Current 没有独立 Conversation、Subscription、CrossScenario、Memory&Profile、Audit Context 项目。Audit 是 Base 的横切服务；AgentOrchestration 也是 Base 组件。四个插件共享 Host 进程、Base 基础设施与核心银行契约，因此不能声称已经实现完全自治的限界上下文。

## 3. 目标 Context 划分（Target，共 10 个）

### 3.1 总览

```
                  ┌─────────────────────────┐
                  │   AgentOrchestration   │  ← 编排入口
                  └──────────┬──────────────┘
                             │
        ┌────────────────────┼────────────────────┐
        ▼                    ▼                    ▼
   Conversation        Memory&Profile         CrossScenario
   对话与渠道           用户记忆画像           跨场景编排
        │                    │                    │
        └────────────────────┼────────────────────┘
                             │
        ┌────────┬───────┬───┴────┬────────┬────────┐
        ▼        ▼       ▼        ▼        ▼        ▼
    Transfer  BillAnal  Wealth   Card    Subscription
                                        │
        ┌──────────────────────────────┘
        ▼
      Audit（横切）
```

---

## 4. 目标 Context 详细定义（Target 领域设计）

### 4.1 AgentOrchestration Context

**职责**：接收用户请求、调度执行计划、管理对话状态

**核心实体**：
- `AgentRequest`（聚合根）：用户的一次完整对话请求
- `ExecutionPlan`：由 LLM 生成、人类可读的计划
- `DialogState`：多轮对话状态

**核心服务**：
- `IOrchestrationService`
- `IPlanResolver`

**关键事件**：
- `PlanGenerated.v1`
- `PlanExecuted.v1`

---

### 4.2 Conversation Context

**职责**：与用户对话、生成自然语言回复

**核心实体**：
- `Message`（用户/Agent 消息）
- `DialogContext`（对话历史）

**核心服务**：
- `IConversationService`
- `IResponseGenerator`
- `IIntentClassifier`

**关键事件**：
- `MessageReceived.v1`
- `ResponseGenerated.v1`

---

### 4.3 Transfer Context

**核心实体**：
- `TransferOrder`（聚合根）：一次转账订单
- `Payee`：收款人信息
- `TransferSchedule`：定时转账

**核心服务**：
- `ITransferExecutionService`
- `IPayeeResolver`
- `ISplitterService`（AA 拆分）
- `IScheduledTransferService`
- `IAMLChecker`

**关键事件**：
- `TransferInitiated.v1`
- `TransferCompleted.v1`
- `TransferFailed.v1`

---

### 4.4 BillAnalysis Context

**核心实体**：
- `Bill`：账单
- `TransactionCategory`：交易分类
- `AnomalyReport`：异常检测报告

**核心服务**：
- `IBillAnalysisService`
- `ITransactionCategorizer`
- `IAnomalyDetector`
- `IInsightGenerator`

**关键事件**：
- `BillAnalyzed.v1`
- `AnomalyDetected.v1`
- `ReportGenerated.v1`

---

### 4.5 Wealth Context

**核心实体**：
- `FinancialProduct`（聚合根）：理财产品
- `InvestmentOrder`：申购/赎回订单
- `RiskProfile`：用户风险画像

**核心服务**：
- `IWealthService`
- `IProductRecommendationService`
- `IRiskAssessmentService`
- `IOrderExecutionService`

**关键事件**：
- `ProductRecommended.v1`
- `ProductSubscribed.v1`
- `ProductRedeemed.v1`

---

### 4.6 CardManagement Context

**核心实体**：
- `Card`（聚合根）：银行卡
- `CardApplication`：申请记录
- `LossReport`：挂失记录

**核心服务**：
- `ICardService`
- `ICardApplicationService`
- `ILimitService`
- `ILossReportService`

**关键事件**：
- `CardApplied.v1`
- `CardLimitAdjusted.v1`
- `CardReportedLost.v1`

---

### 4.7 Subscription Context

**核心实体**：
- `Subscription`（聚合根）：订阅记录
- `SubscriptionPattern`：周期扣费模式
- `MerchantKnowledge`：商家知识库（用于识别 + 退订）

**核心服务**：
- `ISubscriptionService`
- `IPatternDetector`
- `IReminderService`
- `ICancellationService`

**关键事件**：
- `SubscriptionDetected.v1`
- `SubscriptionCancelled.v1`

---

### 4.8 CrossScenario Context

**核心实体**：
- `Scenario`（聚合根）：一个跨场景任务（如"订生日蛋糕"）
- `ScenarioTask`：场景内的子任务
- `MerchantOrder`：商家订单

**核心服务**：
- `IScenarioCoordinator`
- `ITaskPlanner`
- `IMerchantAdapter`

**关键事件**：
- `ScenarioTriggered.v1`
- `TaskScheduled.v1`
- `TaskCompleted.v1`

---

### 4.9 Memory&Profile Context

**核心实体**：
- `UserProfile`（聚合根）：用户画像
- `UserRelationship`：用户关系网络
- `MemoryEmbedding`：向量化的记忆

**核心服务**：
- `IUserProfileService`
- `IRelationshipService`
- `IMemoryRetrievalService`

**关键事件**：
- `UserPreferenceUpdated.v1`
- `RelationshipDiscovered.v1`

---

### 4.10 Audit Context（横切）

**核心实体**：
- `AuditLog`：审计日志
- `DecisionTrace`：决策追溯

**核心服务**：
- `IAuditLogger`
- `IDecisionTracer`
- `IComplianceReporter`

**关键事件**：
- `AuditLogged.v1`（终点事件）

---

## 5. 目标 Context 依赖矩阵（Target）

| From → To | 调用方式 |
|-----------|---------|
| AgentOrchestration → Conversation | 接口 |
| AgentOrchestration → Memory&Profile | 接口 |
| AgentOrchestration → CrossScenario | 接口 |
| Conversation → Transfer / BillAnalysis / Wealth / Card / Subscription | **通过 CrossScenario 或直接事件** |
| CrossScenario → Transfer / Wealth / Card / Subscription | 接口 + 事件 |
| Memory&Profile → (none) | 终点 |
| Audit → (none) | 终点（订阅所有事件） |

**关键约束**：
- **业务 Context 之间禁止直接相互调用** → 必须经过 Orchestration 或 CrossScenario
- **Audit 订阅所有事件**（横切）
- **Memory 可被任意 Context 查询**

---

## 6. 防腐层（Target 示例）

当 Context A 调用 Context B 时，A 必须通过 ACL 转换：

```csharp
// AgentOrchestration/Infrastructure/ACL/TransferACL.cs
public class TransferACL : ITransferExecutionService
{
    private readonly Transfer.Infrastructure.ITransferService _transferService;
    private readonly TransferMapper _mapper;

    public async Task<TransferResultDTO> ExecuteAsync(TransferRequestDTO request)
    {
        // 1. 校验请求（防腐：拒绝非法输入）
        ValidateRequest(request);

        // 2. 转换为内部模型
        var internalRequest = _mapper.ToInternal(request);

        // 3. 调用
        var result = await _transferService.ExecuteAsync(internalRequest);

        // 4. 转换返回
        return _mapper.ToExternal(result);
    }
}
```

详见 [`../00-architecture/05-module-boundaries.md`](../00-architecture/05-module-boundaries.md)

---

## 7. 共享内核（Target）

只有真正跨 Context 的**枚举、错误码、值对象**可以共享：

```
Shared/
├── Enums/
│   ├── Currency.cs (CNY, USD, ...)
│   └── TransferStatus.cs (Pending, Completed, Failed)
├── Errors/
│   └── ErrorCodes.cs
└── ValueObjects/
    ├── Money.cs (decimal value + currency)
    ├── UserId.cs
    └── AccountId.cs
```

**禁止共享 Entity、Service、Repository**

---

## 8. Current 防腐边界补充

Current 的外部系统防腐边界是 `BankingAgent.PluginSdk.ICoreBankClient` 及其 DTO，由 Base 的 `CoreBankClient` 通过 HTTP 适配 `MockBank.Api`。插件不直接引用 `MockBank.Api.Domain` 类型。

## 9. 关联文档

- **领域模型详细**：[`02-domain-model.md`](02-domain-model.md)
- **术语表**：[`03-glossary.md`](03-glossary.md)
- **架构决策**：[`../00-architecture/04-architecture-decisions.md`](../00-architecture/04-architecture-decisions.md)
- **模块边界**：[`../00-architecture/05-module-boundaries.md`](../00-architecture/05-module-boundaries.md)