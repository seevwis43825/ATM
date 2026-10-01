# 模块边界与依赖规则（Module Boundaries）

> **状态**：与当前实现对齐 · **所有者**：架构师 · **版本**：v1.1
> **最后更新**：2026-10-01
>
> **边界**：本文件的 Current 规则描述现有项目引用与运行期装配；Clean Architecture 多项目拆分、独立 Context Schema 和 Python 服务均为 Target。

---

## 1. 边界目标

- **禁止耦合**：模块之间不允许直接服务调用
- **强制解耦**：所有跨模块通信走 **事件总线** 或 **显式接口**
- **可独立测试**：每个模块可单独构建、单独测试
- **可演进**：未来可单独部署为微服务

---

## 2. 当前项目结构（Current）

```
src/
├── src/
│   ├── BankingAgent.Host/             # ASP.NET Core 宿主、Minimal API、wwwroot
│   ├── BankingAgent.Base/             # Agent、AI、数据、安全、事件、插件加载、核心银行客户端
│   └── BankingAgent.Plugin.Sdk/       # 插件/Agent/事件/核心银行共享契约
├── plugins/
│   ├── BankingAgent.Plugin.Transfer/
│   ├── BankingAgent.Plugin.BillAnalysis/
│   ├── BankingAgent.Plugin.CardManagement/
│   └── BankingAgent.Plugin.Wealth/
├── mock-bank/MockBank.Api/            # 独立模拟核心银行进程
├── templates/banking-plugin/          # 插件模板
└── UnitTests/、E2ETest/、LoadTest/、StressTest/ 等测试/工具项目
```

当前命名空间分别为 `BankingAgent.Base.*`、`BankingAgent.PluginSdk`、`BankingAgent.Plugin.<Name>`、`BankingAgent.Host.*` 和 `MockBank.Api.*`。不存在 `Core.*`、`Contexts/*`、`Bootstrap`、`Shared.Contracts` 或 `AIService` 目录。

## 3. 目标代码结构（Target，尚未落地）

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

## 4. 当前依赖规则（Current）

| From | To | 当前方式 |
|---|---|---|
| Host | Base、Plugin.Sdk | 编译期 `ProjectReference` |
| Host | 四个插件 | 仅 MSBuild 构建顺序；运行期扫描 `plugins/` DLL，不编译依赖插件类型 |
| Base | Plugin.Sdk | 编译期 `ProjectReference` |
| Plugin | Base、Plugin.Sdk | 编译期引用基础能力与契约 |
| Plugin A | Plugin B | 禁止项目引用；通过 `DomainEvent` 或 manifest 依赖声明 |
| Base/Plugin | MockBank.Api | 通过 `ICoreBankClient` HTTP 契约，不引用其实现项目 |

Sdk 是最内层契约程序集。Base 不应引用具体插件；插件之间也不应共享具体实现类型。当前插件会共享 Base 的 `BankingDbContext`、合规、审计和核心银行客户端，这与“每个 Context 完全独立基础设施”的 Target 仍有差距。

## 5. 目标依赖规则（Target）

### 5.1 允许的依赖

| From | To | 方式 |
|------|-----|------|
| Bounded Context | Core.Abstractions | ✅ 通过接口 |
| Bounded Context | 自己的 Infrastructure / Application | ✅ |
| Bounded Context | Shared.Contracts（仅枚举/错误码） | ✅ |
| Context A | Context B（公开的 `IContextBOrderService` 接口） | ✅ 通过接口 |
| Agent Core | AIService | ✅ gRPC（类型契约） |
| Agent Core | PostgreSQL | ✅（但每个 Context 用自己的 Schema） |

### 5.2 禁止的依赖

| From | To | 禁止原因 |
|------|-----|---------|
| Core | Bounded Context | Core 是被调用方 |
| Context A | Context B 的具体类 | 强耦合，必须走接口 |
| Context A | Context B 的 Entity / DbContext | 数据隔离必须 |
| Bounded Context | Shared.Entities（共享实体） | 必须各自定义 |
| 前端 | PostgreSQL 直接 | 跨域违规 |
| Bounded Context | 另一个 Bounded Context 的 Controller | 跨进程边界未设计 |

### 5.3 强制规则

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

## 6. 数据边界

### 6.1 Current

- 默认 Provider 是 SQLite，连接串为 `Data Source=bankingagent.db`，初始化为 `EnsureCreated`。
- 审计使用独立 `AuditDbContext`；SQLite 默认派生 `bankingagent.audit.db`。
- 当前只有 Transfer 插件实现 `IEntitySetContributor`，分区名 `plugin_transfer`，实体表 `transfer_records`。
- PostgreSQL 模式下贡献器分区映射为 Schema；SQLite 不支持 Schema，只有逻辑分区。
- BillAnalysis、CardManagement、Wealth 当前不拥有独立插件表；它们主要通过 MockBank.Api 查询/更新进程内模拟数据。

### 6.2 Target：每个 Context 独立 Schema

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

## 7. 命名规范（Target；Current 以实际命名空间为准）

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

## 8. 跨模块通信示例（Target 示例，不是当前类路径）

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

## 9. 拆分原则（Target）

| 信号 | 阈值 |
|------|------|
| 模块代码量 > 5000 行 | 考虑拆 |
| 模块 SLA 需求明显不同 | 拆 |
| 模块部署频率明显不同 | 拆 |
| 模块负载差异 > 10× | 拆 |
| 团队需要独立扩展该模块 | 拆 |

**当前基线**：四个业务插件与 Base 在 Host 同一进程，MockBank.Api 单独运行
**Q4+**：CrossScenario / Conversation 优先拆出
**1 年后**：完整微服务化

---

## 10. 关联文档

- **架构总览**：[`00-overview.md`](00-overview.md)
- **架构决策**：[`04-architecture-decisions.md`](04-architecture-decisions.md)
- **扩展点（新模块接入指南）**：[`06-extension-points.md`](06-extension-points.md)
- **事件契约**：[`07-event-driven-contract.md`](07-event-driven-contract.md)