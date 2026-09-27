# 模块边界与依赖规则（Module Boundaries）

> **状态**：评审中 · **所有者**：架构师 · **版本**：v1.0
> **最后更新**：2026-09-21

---

## 1. 目标

- **禁止耦合**：模块之间不允许直接服务调用
- **强制解耦**：所有跨模块通信走 **事件总线** 或 **显式接口**
- **可独立测试**：每个模块可单独构建、单独测试
- **可演进**：未来可单独部署为微服务

---

## 2. 项目代码结构

```
src/
├── Core/                              # 不可变核心（任何人不许改业务逻辑）
│   ├── Core.Abstractions/
│   │   ├── IEventBus.cs
│   │   ├── IPluginRegistry.cs
│   │   ├── IFeatureFlagService.cs
│   │   └── IComplianceGuard.cs
│   ├── Core.Infrastructure/
│   │   ├── EventBus/                  # MediatR 实现
│   │   ├── PluginRegistry/            # DI 容器扩展
│   │   ├── FeatureFlag/                # DB + Cache 实现
│   │   └── Guards/                     # 合规规则
│   └── Core.Tests/
│
├── Contexts/                          # Bounded Contexts（可扩展领域）
│   ├── AgentOrchestration/
│   │   ├── AgentOrchestration.Domain/   # 实体、聚合、领域服务
│   │   ├── AgentOrchestration.Application/  # 用例
│   │   ├── AgentOrchestration.Infrastructure/  # 仓储、外部集成
│   │   └── AgentOrchestration.Api/       # 控制器、DTO
│   ├── Conversation/
│   ├── Transfer/
│   ├── BillAnalysis/
│   ├── Wealth/
│   ├── CardManagement/
│   ├── Subscription/
│   ├── CrossScenario/
│   ├── MemoryProfile/
│   └── Audit/
│
├── AIService/                         # Python AI 微服务（FastAPI）
│   ├── app/
│   │   ├── api/
│   │   ├── services/
│   │   │   ├── llm_router.py
│   │   │   ├── skill_runner.py
│   │   │   └── embedding.py
│   │   └── core/
│   └── tests/
│
├── Bootstrap/                         # 启动 + 装配
│   ├── Program.cs
│   └── appsettings.json
│
└── Shared/
    └── Shared.Contracts/               # 跨 Context 共享契约（仅枚举、错误码）
```

---

## 3. 依赖规则

### 3.1 允许的依赖

| From | To | 方式 |
|------|-----|------|
| Bounded Context | Core.Abstractions | ✅ 通过接口 |
| Bounded Context | 自己的 Infrastructure / Application | ✅ |
| Bounded Context | Shared.Contracts（仅枚举/错误码） | ✅ |
| Context A | Context B（公开的 `IContextBOrderService` 接口） | ✅ 通过接口 |
| Agent Core | AIService | ✅ gRPC（类型契约） |
| Agent Core | PostgreSQL | ✅（但每个 Context 用自己的 Schema） |

### 3.2 禁止的依赖

| From | To | 禁止原因 |
|------|-----|---------|
| Core | Bounded Context | Core 是被调用方 |
| Context A | Context B 的具体类 | 强耦合，必须走接口 |
| Context A | Context B 的 Entity / DbContext | 数据隔离必须 |
| Bounded Context | Shared.Entities（共享实体） | 必须各自定义 |
| 前端 | PostgreSQL 直接 | 跨域违规 |
| Bounded Context | 另一个 Bounded Context 的 Controller | 跨进程边界未设计 |

### 3.3 强制规则

```csharp
// ✅ 正确：通过接口调用
public class TransferApplicationService
{
    private readonly ITransferExecutionService _executor;  // 接口

    public async Task ExecuteAsync(...) => await _executor.RunAsync(...);
}

// ❌ 错误：直接引用实现
public class TransferApplicationService
{
    private readonly TransferExecutionService _executor;  // 具体类！
}
```

---

## 4. 数据库 Schema 隔离

每个 Bounded Context 在 PostgreSQL 中有独立的 Schema：

```sql
CREATE SCHEMA transfer;          -- Transfer 银行转账
CREATE SCHEMA bill_analysis;     -- 账单分析
CREATE SCHEMA wealth;            -- 理财
CREATE SCHEMA card;              -- 卡片
CREATE SCHEMA subscription;      -- 订阅
CREATE SCHEMA memory;            -- 记忆画像
CREATE SCHEMA audit;             -- 审计
CREATE SCHEMA core;              -- 核心（FeatureFlag 等）
```

**禁止跨 Schema JOIN**。跨 Context 数据访问通过：
1. **REST/gRPC API**（同步）
2. **事件总线**（异步，最终一致性）

---

## 5. 命名规范

| 类型 | 规则 | 示例 |
|------|------|------|
| Bounded Context | PascalCase 名词 | `Transfer` |
| 聚合根 | PascalCase 名词 | `TransferOrder` |
| 实体 | PascalCase 名词 | `Account` |
| 值对象 | PascalCase 名词 | `Money`、`AccountId` |
| 领域事件 | PascalCase + Event 后缀 | `TransferCompletedEvent` |
| 集成事件 | PascalCase + IntegrationEvent 后缀 | `TransferCompletedIntegrationEvent` |
| 接口 | I + PascalCase | `ITransferExecutionService` |
| 实现 | PascalCase 名词 | `TransferExecutionService` |
| 仓储 | PascalCase + Repository 后缀 | `TransferOrderRepository` |

详见 [`../03-development/01-coding-standards.md`](../03-development/01-coding-standards.md)

---

## 6. 跨模块通信示例

### 6.1 通过事件总线（推荐）

```csharp
// 1. 发布方（Transfer Context）
public class TransferExecutionService
{
    private readonly IEventBus _eventBus;

    public async Task ExecuteAsync(TransferOrder order)
    {
        // 业务执行
        await _coreBank.ExecuteAsync(order);

        // 发布事件
        await _eventBus.PublishAsync(
            new TransferCompletedEvent(order.Id, order.Amount, DateTime.UtcNow)
        );
    }
}

// 2. 订阅方（Audit Context）
public class TransferCompletedEventHandler : IEventHandler<TransferCompletedEvent>
{
    private readonly IAuditLogger _audit;

    public async Task HandleAsync(TransferCompletedEvent @event, CancellationToken ct)
    {
        await _audit.LogTransferAsync(@event);
    }
}
```

### 6.2 通过公开接口（同步场景）

```csharp
// 1. 通知 Context 提供接口
public interface INotificationService
{
    Task NotifyUserAsync(UserId userId, NotificationContent content);
}

// 2. Transfer Context 通过接口调用
public class TransferExecutionService
{
    private readonly INotificationService _notifier;
    public async Task ExecuteAsync(TransferOrder order)
    {
        await _coreBank.ExecuteAsync(order);
        await _notifier.NotifyUserAsync(order.FromUser, new NotificationContent(
            Title: "转账成功",
            Body: $"已转 {order.Amount}"
        ));
    }
}
```

---

## 7. 拆分原则（何时考虑拆分 Context）

| 信号 | 阈值 |
|------|------|
| 模块代码量 > 5000 行 | 考虑拆 |
| 模块 SLA 需求明显不同 | 拆 |
| 模块部署频率明显不同 | 拆 |
| 模块负载差异 > 10× | 拆 |
| 团队需要独立扩展该模块 | 拆 |

**当前 MVP 阶段**：所有模块在同一进程
**Q4+**：CrossScenario / Conversation 优先拆出
**1 年后**：完整微服务化

---

## 8. 关联文档

- **架构总览**：[`00-overview.md`](00-overview.md)
- **架构决策**：[`04-architecture-decisions.md`](04-architecture-decisions.md)
- **扩展点（新模块接入指南）**：[`06-extension-points.md`](06-extension-points.md)
- **事件契约**：[`07-event-driven-contract.md`](07-event-driven-contract.md)