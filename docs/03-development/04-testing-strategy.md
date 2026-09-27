# 测试策略（Testing Strategy）

> **状态**：评审中 · **所有者**：架构师 · **版本**：v1.0
> **最后更新**：2026-09-21

---

## 1. 测试金字塔

```
                 ╱╲
                ╱  ╲          E2E Tests (5%)
             ╱  端到端 ╲      - Playwright
            ╱──────────╲     - 5 ~ 20 分钟
           ╱            ╲
          ╱  集成测试    ╲   Integration Tests (20%)
         ╱   - DB集成   ╲    - xUnit + Testcontainers
        ╱   - API集成    ╲   - 30 秒 ~ 5 分钟
       ╱────────────────╲
      ╱                  ╲
     ╱   单元测试          ╲  Unit Tests (75%)
    ╱     - Domain      ╲   - xUnit (C#) / pytest (Python)
   ╱       - Application ╲   - 几秒 ~ 1 分钟
  ╱────────────────────────╲
```

**覆盖率目标**：
- 核心代码（Domain、聚合根）：≥ 80%
- 接口/适配器：≥ 70%
- AI 服务 / LLM 包装：≥ 60%
- 配置/脚手架：不要求

---

## 2. 单元测试

### 2.1 范围
- 聚合根、实体、值对象（不依赖 DB/网络）
- 领域服务（mock 仓储）
- 工具类、转换器

### 2.2 命名规范
```
{ClassName}_{MethodName}_{Scenario}_{ExpectedResult}
```

**示例**：
- `TransferOrder_Create_WithNegativeAmount_ThrowsDomainException`
- `Money_Add_DifferentCurrencies_ThrowsException`
- `SplitterService_Divide_FourParticipants_ReturnsEqualAmounts`

### 2.3 AAA 模式

```csharp
[Fact]
public void TransferOrder_ConfirmHuman_FromPending_Status_Succeeds()
{
    // Arrange
    var order = TransferOrder.Create(
        UserId.From("U001"),
        new Payee { Name = "小李" },
        new Money(500, Currency.CNY),
        TransferChannel.Agent
    );

    // Act
    order.ConfirmHuman("tok-abc123");

    // Assert
    order.Status.Should().Be(TransferStatus.Confirmed);
    order.DomainEvents.Should().ContainSingle(e => e is TransferInitiatedEvent);
}
```

### 2.4 测试数据构建器（Builder Pattern）

```csharp
public class TransferOrderBuilder
{
    private UserId _fromUser = UserId.From("U001");
    private Money _amount = new(500, Currency.CNY);
    private Payee _payee = new() { Name = "小李" };

    public TransferOrderBuilder FromUser(UserId userId) { _fromUser = userId; return this; }
    public TransferOrderBuilder WithAmount(Money amount) { _amount = amount; return this; }
    public TransferOrderBuilder WithPayee(Payee payee) { _payee = payee; return this; }

    public TransferOrder Build() => TransferOrder.Create(_fromUser, _payee, _amount, TransferChannel.Agent);
}

// 使用
var order = new TransferOrderBuilder()
    .WithAmount(new Money(-100, Currency.CNY))
    .Build();
```

---

## 3. 集成测试

### 3.1 范围
- 数据库访问（真实 PostgreSQL）
- API 端到端
- 事件总线
- 外部依赖（mock HTTP 服务器）

### 3.2 Testcontainers

```csharp
public class TransferIntegrationTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public TransferIntegrationTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task CreateTransfer_PersistsToDb()
    {
        // Arrange
        await using var conn = new NpgsqlConnection(_postgres.ConnectionString);

        // Act
        var service = new TransferExecutionService(conn, ...);
        var result = await service.ExecuteAsync(...);

        // Assert
        var saved = await conn.QueryFirstOrDefaultAsync<TransferOrder>(
            "SELECT * FROM transfer.orders WHERE id = @id", new { id = result.OrderId });
        saved.Should().NotBeNull();
    }
}
```

### 3.3 容器化测试环境

使用 docker-compose-test.yml 启动：
- PostgreSQL
- Redis
- Kafka（如需）
- Mock 外部 API（CoreBank mock、LLM mock）

---

## 4. 端到端（E2E）测试

### 4.1 范围
- 用户场景（转账、账单、理财）
- IM 集成
- 跨场景联动

### 4.2 工具：Playwright

```typescript
// e2e/transfer.spec.ts
import { test, expect } from '@playwright/test';

test('user can transfer money via IM', async ({ page }) => {
  // 1. 登录
  await page.goto('https://im.bankagent.com');
  await page.fill('#userId', 'U001');
  await page.fill('#password', 'test-password');

  // 2. 发起转账对话
  await page.fill('#message', '转 500 元给小李');
  await page.click('#send');

  // 3. 等待 Agent 回复
  await expect(page.locator('.agent-message')).toContainText('确认转账');

  // 4. 点击确认
  await page.click('#confirm-button');

  // 5. 验证成功
  await expect(page.locator('.transfer-success')).toContainText('转账成功');
});
```

---

## 5. AI/LLM 相关测试

### 5.1 单元测试（无需真实 LLM）

```python
def test_intent_classifier_with_positive_amount():
    intent = classify_intent("转500给小李")
    assert intent.intent == Intent.TRANSFER
    assert intent.amount.value == 500

def test_payee_resolver_with_name():
    payee = resolve_payee(name="小李", user_phonebook=mock_phonebook)
    assert payee.account_id == "AC987654"
```

### 5.2 集成测试（mock LLM）

```python
@pytest.fixture
def mock_llm():
    """Mock LLM 返回固定响应"""
    with patch('ai_service.llm_router.LLMRouter.chat') as mock:
        mock.return_value = LLMResponse(
            content='{"intent": "transfer", "amount": 500, "payee": "小李"}',
            usage=Usage(total_tokens=150)
        )
        yield mock

async def test_conversation_with_transfer_intent(mock_llm):
    agent = ConversationAgent(llm_router=mock_llm)
    response = await agent.handle_message("转500给小李")
    assert response.intent == Intent.TRANSFER
    assert response.requires_human_confirm == True
```

### 5.3 评估测试（Eval Suite）

```python
def eval_intent_accuracy():
    """在测试集上评估意图识别准确率"""
    test_cases = load_eval_set("intent_classification_v1.jsonl")
    correct = 0
    for case in test_cases:
        result = classify_intent(case.input)
        if result.intent == case.expected_intent:
            correct += 1
    accuracy = correct / len(test_cases)
    assert accuracy >= 0.90, f"意图识别准确率仅 {accuracy:.2%}，要求 ≥ 90%"
```

### 5.4 LLM 输出验证

```python
def test_llm_output_is_valid_json():
    response = await llm.chat([...])
    try:
        parsed = json.loads(response.content)
        assert "intent" in parsed
        assert "amount" in parsed
    except json.JSONDecodeError:
        pytest.fail(f"LLM 输出不是合法 JSON: {response.content}")
```

---

## 6. 性能测试（可选）

### 6.1 工具：k6

```javascript
// tests/load/transfer.js
import http from 'k6/http';
import { check } from 'k6';

export const options = {
  vus: 100,        // 100 个虚拟用户
  duration: '30s',  // 持续 30 秒
};

export default function() {
  const res = http.post('https://api.bankagent.com/v1/transfers',
    JSON.stringify({...}),
    { headers: { 'Content-Type': 'application/json', 'Authorization': 'Bearer xxx' } }
  );
  check(res, {
    'status is 200': (r) => r.status === 200,
    'response time < 500ms': (r) => r.timings.duration < 500,
  });
}
```

### 6.2 性能 SLA

| 指标 | SLA |
|------|-----|
| HTTP P95 响应时间 | < 500ms |
| HTTP P99 响应时间 | < 1s |
| 错误率 | < 0.1% |
| 并发用户 | ≥ 10,000 |

---

## 7. 测试覆盖率

### 7.1 工具
- C#：`coverlet` + `reportgenerator`
- Python：`pytest-cov`
- TypeScript：`c8` / `istanbul`

### 7.2 报告

CI 流水线自动生成覆盖率报告：

```
Code Coverage Report:
  Core/                   95.2%  ✅ (目标 ≥ 80%)
  AgentOrchestration/    88.7%  ✅
  Conversation/          85.4%  ✅
  Transfer/              90.1%  ✅
  BillAnalysis/          78.3%  ❌ (需提升)
  Wealth/                82.6%  ✅
  CardManagement/        80.4%  ✅
  Subscription/          76.9%  ❌ (需提升)
  CrossScenario/         71.5%  ❌ (需提升)
  MemoryProfile/         84.1%  ✅
  Audit/                 91.3%  ✅

总体覆盖率: 84.9%  ✅ (目标 ≥ 80%)
```

覆盖率 < 70% 的 PR 拒绝合并。

---

## 8. 测试数据管理

### 8.1 单元测试
- 完全隔离、无共享
- 使用 Builder 模式创建
- 不依赖任何数据库/网络

### 8.2 集成测试
- 每个测试用独立的 Schema
- 测试结束后自动清理（TRUNCATE）

### 8.3 E2E 测试
- 使用专用测试用户（mock 数据）
- 测试账户与生产严格隔离

### 8.4 测试数据原则
- 永不修改生产数据库
- 永不 commit 真实用户数据
- 测试数据库定期清空重建

---

## 9. 关联文档

- **代码规范**：[`01-coding-standards.md`](01-coding-standards.md)
- **PR Review**：[`03-pr-review-checklist.md`](03-pr-review-checklist.md)
- **CI/CD**：[`05-ci-cd-pipeline.md`](05-ci-cd-pipeline.md)