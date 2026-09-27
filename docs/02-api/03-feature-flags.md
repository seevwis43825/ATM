# 功能开关系统（Feature Flags）

> **状态**：评审中 · **所有者**：业务开发 · **版本**：v1.0
> **最后更新**：2026-09-21

---

## 1. 为什么需要 FeatureFlag？

- **安全上线**：新功能默认关闭，按需启用
- **灰度发布**：按用户/比例逐步放量
- **快速回滚**：10 秒内一键关闭
- **A/B 实验**：同一功能多版本并存
- **业务隔离**：VIP 与普通用户不同能力

详见 ADR-0010。

---

## 2. 命名规范

```
feature.<module>.<feature>.enabled              # 总开关
feature.<module>.<feature>.rollout_percentage    # 灰度百分比
feature.<module>.<feature>.user_whitelist        # 白名单
feature.<module>.<feature>.user_blacklist        # 黑名单
feature.<module>.<feature>.version               # 实验版本
```

**示例**：
- `feature.cross_scenario.enabled`
- `feature.cross_scenario.rollout_percentage`
- `feature.cross_scenario.user_whitelist`
- `feature.wealth.subscribed.enabled`

---

## 3. 数据结构

### 3.1 数据库表

```sql
-- Core Schema
CREATE TABLE core.feature_flags (
    flag_key           VARCHAR(200) PRIMARY KEY,
    enabled            BOOLEAN NOT NULL DEFAULT FALSE,
    rollout_percentage INT NOT NULL DEFAULT 0,  -- 0-100
    user_whitelist     JSONB DEFAULT '[]'::jsonb,
    user_blacklist     JSONB DEFAULT '[]'::jsonb,
    default_variant    VARCHAR(50),
    variants           JSONB DEFAULT '{}'::jsonb,  -- A/B 实验
    description        TEXT,
    owner              VARCHAR(100),
    expires_at         TIMESTAMP,
    created_at         TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_at         TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_by         VARCHAR(100)
);

CREATE INDEX idx_feature_flags_enabled ON core.feature_flags(enabled) WHERE enabled = true;
```

### 3.2 Redis 缓存

```redis
# Key: feature_flag:{flag_key}
# TTL: 10 秒
# Value:
{
  "flagKey": "feature.cross_scenario.enabled",
  "enabled": true,
  "rolloutPercentage": 50,
  "userWhitelist": ["U001", "U002"],
  "userBlacklist": [],
  "defaultVariant": "v1",
  "variants": {
    "v1": { "weight": 50 },
    "v2": { "weight": 50 }
  },
  "expiresAt": null
}
```

---

## 4. API 接口

### 4.1 服务接口（IFeatureFlagService）

```csharp
public interface IFeatureFlagService
{
    Task<bool> IsEnabledAsync(string flagKey);
    Task<bool> IsEnabledAsync(string flagKey, string userId);
    Task<string?> GetVariantAsync(string flagKey, string userId);
    Task<FeatureFlag> GetFlagAsync(string flagKey);
    Task UpdateFlagAsync(string flagKey, FeatureFlag flag);
    Task<IReadOnlyList<FeatureFlag>> ListFlagsAsync();
}
```

### 4.2 评估优先级（从高到低）：

1. **Kill Switch（管理后台紧急关闭）**：enabled=false → 直接 false
2. **User Blacklist**：当前用户在黑名单 → false
3. **User Whitelist**：当前用户在白名单 → true
4. **Percentage Rollout**：基于 userId 哈希的一致性灰度
5. **Default Enabled**：enabled 默认值

### 4.3 一致性灰度算法

```csharp
public bool IsInRollout(string flagKey, string userId, int percentage)
{
    // 基于用户 ID + 标志 key 的稳定哈希
    // 同一用户对同一 feature 总是落入相同桶
    var hash = SHA256(flagKey + ":" + userId);
    var bucket = (hash % 100) + 1;  // 1-100
    return bucket <= percentage;
}
```

**重要**：
- flagKey 和 userId 的拼接顺序固定
- 哈希算法不可变（保证一致性）
- 调整 percentage 时，已分到的用户不受影响

---

## 5. A/B 实验

### 5.1 多变体配置

```json
{
  "flagKey": "feature.transfer.execution_strategy",
  "enabled": true,
  "defaultVariant": "control",
  "variants": {
    "control": { "weight": 50 },
    "fast_track": { "weight": 50 }
  }
}
```

### 5.2 变体分配

```csharp
public string GetVariant(string flagKey, string userId)
{
    var hash = SHA256(flagKey + ":variant:" + userId);
    var bucket = hash % 100;

    var cumulative = 0;
    foreach (var (variantName, variantConfig) in flag.Variants)
    {
        cumulative += variantConfig.Weight;
        if (bucket < cumulative)
            return variantName;
    }
    return flag.DefaultVariant;
}
```

---

## 6. 接入示例

### 6.1 C# 中使用

```csharp
// 在业务代码中
public class TransferExecutionService
{
    private readonly IFeatureFlagService _flags;

    public async Task<TransferResult> ExecuteAsync(TransferOrder order)
    {
        // 检查功能开关
        if (!await _flags.IsEnabledAsync("feature.transfer.fast_track", order.FromUser))
        {
            // 走老路
            return await ExecuteLegacyPath(order);
        }

        // 走新路径
        return await ExecuteFastTrack(order);
    }
}
```

### 6.2 中间件形式

```csharp
// 通过特性标注
[FeatureFlag("feature.cross_scenario.enabled")]
public class CrossScenarioController {
    // 自动拦截：功能关闭时返回 404
}
```

### 6.3 在 Agent 中使用

```python
# Python AI Service
async def recommend_wealth_product(user_id: str, amount: Money) -> List[Product]:
    # 检查 A/B 实验变体
    variant = await feature_flag_client.get_variant(
        "feature.wealth.recommendation_strategy",
        user_id
    )

    if variant == "v2_with_llm_explanation":
        return await recommend_with_llm_explanation(user_id, amount)
    else:
        return await recommend_baseline(user_id, amount)
```

---

## 7. 管理后台（Admin Console）

### 7.1 功能列表页面

- 显示所有 FeatureFlag
- 支持搜索（按 key、模块名）
- 显示启用状态、灰度比例
- 显示最后修改人、修改时间
- 显示关联的 Agent/Context

### 7.2 编辑页面

- 切换启用开关（带二次确认）
- 调整灰度比例（10% 步进）
- 编辑白名单/黑名单（多选用户 ID）
- 设置变体权重
- 设置过期时间

### 7.3 审计日志

- 任何 FeatureFlag 变更必须记录到 Audit Log
- 包含：变更人、变更前/后值、变更时间、原因

### 7.4 紧急 Kill Switch

- **Kill All New Features**：一键关闭所有 `feature.*.enabled = true` 的标志
- 数据库 trigger，1 秒内生效
- Redis 自动过期，10 秒内所有节点同步

---

## 8. 与模块/Context 的关系

| Context | 使用的典型 Flag |
|--------|---------------|
| AgentOrchestration | `feature.agent.llm_provider`（A/B 国内/海外模型） |
| Conversation | `feature.conversation.suggestion`（是否主动建议） |
| Transfer | `feature.transfer.qrcode`（扫码转账） |
| CrossScenario | `feature.cross_scenario.enabled`（跨场景主开关） |
| Wealth | `feature.wealth.auto_recommend`（自动推荐） |

详见 [`../00-architecture/06-extension-points.md`](../00-architecture/06-extension-points.md)。

---

## 9. 关联文档

- **扩展机制**：[`../00-architecture/06-extension-points.md`](../00-architecture/06-extension-points.md)
- **ADR-0010**：[`../00-architecture/04-architecture-decisions.md`](../00-architecture/04-architecture-decisions.md#adr-0010)
- **事件 Schema**：[`02-event-schema.md`](02-event-schema.md)