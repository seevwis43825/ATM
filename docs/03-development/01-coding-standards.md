# 代码规范（Coding Standards）

> **状态**：评审中 · **所有者**：架构师 · **版本**：v1.0
> **最后更新**：2026-09-21

---

## 0. 总体原则

| 原则 | 说明 |
|------|------|
| **可读性优先** | 代码被读的时间远多于被写的时间 |
| **显式优于隐式** | 不要"魔法"，每个值都有来源 |
| **小而专注** | 函数 < 50 行，文件 < 500 行，类 < 300 行 |
| **测试金字塔** | 70% 单元测试 + 20% 集成测试 + 10% E2E |

---

## 1. C# (.NET 8) 规范

### 1.1 命名规范

| 类型 | 规则 | 示例 |
|------|------|------|
| 类 | PascalCase，名词 | `TransferOrderService` |
| 接口 | I + PascalCase | `ITransferExecutionService` |
| 方法 | PascalCase，动词 | `ExecuteAsync`, `ValidateRequest` |
| 公共属性 | PascalCase | `Amount`, `Status` |
| 私有字段 | _camelCase | `_userId`, `_eventBus` |
| 局部变量 | camelCase | `orderId`, `amount` |
| 常量 | UPPER_SNAKE | `MAX_TRANSFER_AMOUNT` |
| 枚举值 | PascalCase | `PendingHumanConfirmation` |
| 异步方法 | 后缀 Async | `ExecuteAsync()` |

### 1.2 项目布局

```
ProjectName/
├── ProjectName.Domain/        # 实体、值对象、聚合、领域事件
│   ├── Aggregates/
│   ├── Entities/
│   ├── ValueObjects/
│   ├── Events/
│   ├── Exceptions/
│   └── Interfaces/             # 仓储接口（实现位于 Infrastructure）
├── ProjectName.Application/   # 用例、命令查询、DTO
│   ├── Commands/
│   ├── Queries/
│   ├── Services/               # 业务用例编排
│   ├── DTOs/
│   └── Interfaces/             # 应用层依赖的接口
├── ProjectName.Infrastructure/  # 实现
│   ├── Persistence/            # 仓储实现
│   ├── ExternalServices/       # 外部服务适配
│   └── Configuration/
└── ProjectName.Api/            # 控制器、DTO 映射
    ├── Controllers/
    ├── Filters/
    └── Middleware/
```

### 1.3 异步编程

```csharp
// ✅ 正确：async/await 全链路使用
public async Task<TransferResult> ExecuteAsync(TransferOrder order, CancellationToken ct)
{
    var result = await _coreBank.ExecuteAsync(order, ct);
    return result;
}

// ✅ 正确：CancellationToken 传递
public async Task HandleAsync(SomeEvent @event, CancellationToken ct)
{
    await _audit.LogAsync(@event, ct);
}

// ❌ 错误：async void（仅 EventHandler 可用）
public async void DoSomething() { }

// ❌ 错误：.Result / .GetAwaiter().GetResult()（死锁风险）
var result = _service.ExecuteAsync().Result;
```

### 1.4 异常处理

```csharp
// ✅ 正确：领域异常
public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
}

public class TransferAmountInvalidException : DomainException
{
    public TransferAmountInvalidException(Money amount)
        : base($"Transfer amount must be positive, got {amount.Value}") { }
}

// ✅ 正确：领域方法抛出异常
public static TransferOrder Create(UserId from, Payee to, Money amount, ...)
{
    if (!amount.IsPositive())
        throw new TransferAmountInvalidException(amount);
    // ...
}

// ✅ 正确：Application 层捕获并转换为用户友好错误
try {
    return await _service.ExecuteAsync(...);
} catch (DomainException ex) {
    throw new UserFacingException("操作失败：" + ex.Message);
}

// ❌ 错误：catch (Exception) 后吞掉
try { ... } catch (Exception) { }  // 静默失败
```

### 1.5 日志规范

使用结构化日志：

```csharp
// ✅ 正确
_logger.LogInformation("Transfer completed for user {UserId} with amount {Amount}",
    userId, amount);

// ❌ 错误
_logger.LogInformation($"Transfer completed for user {userId}");  // 非结构化
```

**日志级别**：
- `Trace` / `Debug`：开发调试
- `Information`：业务事件（转账完成、用户登录）
- `Warning`：可恢复异常（重试成功、规则降级）
- `Error`：业务异常（转账失败）
- `Critical`：系统级故障（数据库不可用）

### 1.6 LINQ 与集合

```csharp
// ✅ 正确：使用 FirstOrDefault / SingleOrDefault 处理可能的空
var order = await _repo.GetAllAsync()
    .FirstOrDefaultAsync(o => o.Id == orderId, ct);
if (order == null) throw new OrderNotFoundException(orderId);

// ❌ 错误：直接 First() 会抛异常
var order = await _repo.GetAllAsync().FirstAsync(o => o.Id == orderId);

// ✅ 正确：避免 N+1 查询
var orders = await _repo.GetOrdersByUserAsync(userId, ct);  // 一次查询
foreach (var order in orders) {
    var details = order.Items;  // 已加载
}

// ❌ 错误：循环内查询
foreach (var orderId in orderIds) {
    var order = await _repo.GetByIdAsync(orderId);  // N 次查询！
}
```

### 1.7 依赖注入

```csharp
// ✅ 正确：注入接口
public class TransferExecutionService
{
    private readonly ICoreBankAdapter _coreBank;
    private readonly IEventBus _eventBus;
    private readonly IAuditLogger _audit;

    public TransferExecutionService(
        ICoreBankAdapter coreBank,
        IEventBus eventBus,
        IAuditLogger audit) { ... }
}

// ❌ 错误：依赖具体实现
public class TransferExecutionService
{
    private readonly CoreBankAdapter _coreBank;  // 不可 mock、不可替换
}
```

### 1.8 单元测试

```csharp
// 使用 xUnit + FluentAssertions + NSubstitute
public class TransferOrderTests
{
    [Fact]
    public void Create_WithNegativeAmount_ThrowsDomainException()
    {
        // Arrange
        var fromUser = UserId.From("U001");
        var payee = new Payee { Name = "小李" };
        var amount = new Money(-100, Currency.CNY);  // 负数

        // Act & Assert
        var act = () => TransferOrder.Create(fromUser, payee, amount, TransferChannel.Agent);
        act.Should().Throw<TransferAmountInvalidException>();
    }
}
```

---

## 2. Python (3.11) 规范

### 2.1 命名规范

| 类型 | 规则 | 示例 |
|------|------|------|
| 模块 | snake_case | `transfer_agent.py` |
| 类 | PascalCase | `TransferAgent` |
| 函数 | snake_case | `execute_transfer()` |
| 变量 | snake_case | `order_id`, `amount` |
| 常量 | UPPER_SNAKE | `MAX_AMOUNT` |
| 私有 | _前缀 | `_internal_state` |

### 2.2 类型注解（强制使用）

```python
# ✅ 正确：使用 type hints
from typing import Optional
from decimal import Decimal

def execute_transfer(
    order_id: str,
    amount: Decimal,
    user_id: str,
    memo: Optional[str] = None
) -> TransferResult:
    ...

# ❌ 错误：无类型
def execute_transfer(order_id, amount, user_id, memo=None):
    ...
```

### 2.3 数据类

```python
from dataclasses import dataclass
from decimal import Decimal

@dataclass(frozen=True)  # 不可变值对象
class Money:
    value: Decimal
    currency: str

    def __post_init__(self):
        if self.currency not in {"CNY", "USD", "EUR"}:
            raise ValueError(f"Unsupported currency: {self.currency}")

@dataclass(frozen=True)
class Payee:
    name: Optional[str] = None
    phone_number: Optional[str] = None
    account_id: Optional[str] = None
    memo: Optional[str] = None
```

### 2.4 异步编程

```python
import asyncio

# ✅ 正确
async def execute_transfer(order: TransferOrder) -> TransferResult:
    result = await core_bank.execute(order)
    return result

# ✅ 正确：并行调用
async def gather_user_data(user_id: str):
    profile, balance, transactions = await asyncio.gather(
        get_profile(user_id),
        get_balance(user_id),
        get_transactions(user_id)
    )
    return profile, balance, transactions

# ❌ 错误：阻塞调用在异步函数中
async def bad():
    time.sleep(1)  # 阻塞整个事件循环！
```

### 2.5 AI Service 代码规范

```python
# ✅ 正确：LLM 调用统一包装
from functools import lru_cache

class LLMRouter:
    """统一的 LLM 调用接口"""

    async def chat(
        self,
        messages: list[dict],
        model: Optional[str] = None,
        temperature: float = 0.7,
        max_tokens: int = 2000,
        **kwargs
    ) -> LLMResponse:
        # 1. 选择模型
        model = model or self.default_model

        # 2. 限流检查
        await self._rate_limiter.acquire()

        # 3. 调用
        try:
            return await self._call_provider(model, messages, temperature, max_tokens, **kwargs)
        except RateLimitError:
            # 降级到备用模型
            return await self._call_provider(self.fallback_model, messages, temperature, max_tokens, **kwargs)

    @lru_cache(maxsize=1000)
    def _select_model(self, task_type: str) -> str:
        # 根据任务类型选择模型
        ...
```

### 2.6 测试

```python
import pytest
from decimal import Decimal

def test_transfer_with_positive_amount():
    order = TransferOrder(
        from_user="U001",
        payee=Payee(name="小李"),
        amount=Money(Decimal("500"), "CNY")
    )
    assert order.status == TransferStatus.PENDING_HUMAN_CONFIRM

def test_transfer_with_negative_amount_raises():
    with pytest.raises(TransferAmountInvalidError):
        TransferOrder(
            from_user="U001",
            payee=Payee(name="小李"),
            amount=Money(Decimal("-100"), "CNY")  # 负数
        )
```

---

## 3. TypeScript / Next.js 规范

### 3.1 命名规范

| 类型 | 规则 |
|------|------|
| 组件文件 | PascalCase：`TransferCard.tsx` |
| Hook 文件 | camelCase：`useTransfer.ts` |
| 工具函数 | camelCase：`formatMoney.ts` |
| 类型定义 | PascalCase |
| 常量 | UPPER_SNAKE |

### 3.2 React 组件

```typescript
// ✅ 正确：函数组件 + TypeScript
interface TransferCardProps {
  orderId: string;
  amount: Money;
  onConfirm: () => Promise<void>;
  onCancel: () => void;
}

export function TransferCard({ orderId, amount, onConfirm, onCancel }: TransferCardProps) {
  const [isLoading, setIsLoading] = useState(false);

  return (
    <div className="card">
      <p>金额：{formatMoney(amount)}</p>
      <Button onClick={onConfirm} disabled={isLoading}>确认</Button>
      <Button onClick={onCancel}>取消</Button>
    </div>
  );
}

// ❌ 错误：class 组件、any 类型
export class TransferCard extends React.Component<any, any> {
  render() { return ... }
}
```

### 3.3 Hooks

```typescript
// ✅ 正确
export function useTransfer(orderId: string) {
  return useQuery({
    queryKey: ['transfer', orderId],
    queryFn: () => api.getTransfer(orderId),
    staleTime: 30_000,
  });
}
```

---

## 4. 数据库（PostgreSQL）

### 4.1 命名

| 类型 | 规则 | 示例 |
|------|------|------|
| 表 | snake_case，复数 | `transfer_orders`, `user_profiles` |
| 列 | snake_case | `user_id`, `created_at` |
| 主键 | `{table}_id` | `transfer_order_id` |
| 外键 | `{ref_table}_id` | `user_id` |
| 索引 | `idx_{table}_{column(s)}` | `idx_orders_user_id` |
| 唯一约束 | `uq_{table}_{column(s)}` | `uq_users_phone` |

### 4.2 Schema 隔离

每个 Context 一个 Schema：

```sql
CREATE SCHEMA transfer;
CREATE TABLE transfer.orders (...);
CREATE TABLE transfer.payees (...);
```

**禁止跨 Schema JOIN**。

### 4.3 Migration 规范

- 用 **Roundhouse** / **DbUp** / **Flyway** / **Entity Framework Migrations**
- 文件名：`V001__create_transfer_orders.sql`
- 永远**只向前**，不删除历史 migration
- 上线失败的 migration 立即修复或回滚整个版本

---

## 5. Git 提交规范

### 5.1 提交信息格式

```
<type>(<scope>): <subject>

<body>

<footer>
```

**Type**：
- `feat`：新功能
- `fix`：bug 修复
- `refactor`：重构
- `docs`：仅文档
- `test`：仅测试
- `chore`：构建/CI/依赖

**示例**：
```
feat(transfer): add AA split transfer feature

支持多人聚餐 AA 自动拆分，自动计算每人金额并生成子订单。

- 添加 AA transfer aggregate
- 添加 SplitterService
- 添加单元测试
- 添加 API 端点 POST /v1/transfers/{id}/split

Closes #123
```

### 5.2 提交原子性

- 一个 commit 只做一件事
- 不要在 feature commit 中混入格式调整
- 不要在 fix commit 中混入新功能

---

## 6. 性能与资源

| 限制 | 值 |
|------|-----|
| HTTP 请求响应时间 P95 | < 500ms |
| HTTP 请求响应时间 P99 | < 1s |
| LLM 调用响应时间 P95 | < 3s |
| 数据库查询 P95 | < 100ms |
| 同步链路调用数 | ≤ 5 |
| 单个函数圈复杂度 | ≤ 10 |
| 单元测试覆盖率（核心） | ≥ 80% |
| 单元测试覆盖率（接口） | ≥ 70% |

---

## 7. 关联文档

- **Git 工作流**：[`02-git-workflow.md`](02-git-workflow.md)
- **测试策略**：[`04-testing-strategy.md`](04-testing-strategy.md)
- **CI/CD**：[`05-ci-cd-pipeline.md`](05-ci-cd-pipeline.md)
- **环境搭建**：[`06-environment-setup.md`](06-environment-setup.md)