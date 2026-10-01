# 插件化扩展机制（Extension Points）

> **状态**：与当前实现对齐 · **所有者**：架构师 · **版本**：v1.1
> **最后更新**：2026-10-01

> **边界**：先按 Current 插件契约扩展。后文 Context/FeatureFlag 方案是 Target，不可照抄为现有 API。

---

## 1. 当前扩展机制（Current）

当前唯一完整落地的业务扩展点是 **.NET 插件程序集**：

1. 新建引用 `BankingAgent.Base` 与 `BankingAgent.Plugin.Sdk` 的 .NET 8 项目（可从 `src/templates/banking-plugin` 开始）。
2. 实现 `IPluginEntryPoint`，通过 `PluginManifest` 声明 ID、版本、场景、依赖和敏感级别。
3. 在 `ConfigureServices(IServiceCollection, IPluginContext)` 中注册一个或多个 `IBankingAgent`。
4. Agent 声明 `SupportedIntents` 与 `TriggerKeywords`；默认规则分类和可选 LLM 分类都会从这些声明生成候选。
5. 需要事件时注入 `IEventPublisher`，处理方实现 `IDomainEventHandler`；插件不得引用另一插件程序集。
6. 需要持久化时实现并注册 `IEntitySetContributor`，然后生成/审查 EF Core migration。
7. 将 DLL 及依赖放入 Host 输出的 `plugins/` 目录。宿主在 Build 前扫描并把插件服务并入主容器。

当前四个实现可作为样例：Transfer（持久化/事件/人工确认）、BillAnalysis（只读/事件订阅）、CardManagement（manifest 依赖/写操作）、Wealth（只读）。

### Current 限制

- `PluginManifest.FeatureFlags` 只是元数据，尚无运行时开关服务或灰度拦截。
- `/api/plugins/{id}/start|stop` 管理的是生命周期状态；已注入的 Agent 不会因此自动从 `AgentRouter` 移除。
- “可回收 `AssemblyLoadContext`”不等于可以安全在线替换所有已注册 DI 服务。
- 插件服务必须在主容器 `Build` 前完成注册。

---

## 2. 目标机制 A：增加 Bounded Context（Target）

### 场景举例
增加"保险"领域（Insurance Context），支持：
- 保单查询
- 理赔申请
- 续保提醒

### 步骤（5 步走，约 3-5 人天）

#### 步骤 1：创建项目结构（半天）
```bash
mkdir -p src/Contexts/Insurance/{Insurance.Domain,Insurance.Application,Insurance.Infrastructure,Insurance.Api}
```

#### 步骤 2：定义领域模型（半天）
```csharp
// Insurance.Domain/InsurancePolicy.cs
public class InsurancePolicy : AggregateRoot
{
    public PolicyId Id { get; private set; }
    public UserId Holder { get; private set; }
    public Money Coverage { get; private set; }
    public DateTime EffectiveFrom { get; private set; }
    public DateTime EffectiveTo { get; private set; }
    public PolicyStatus Status { get; private set; }

    public Claim FileClaim(Money amount, string reason) { ... }
}
```

#### 步骤 3：定义公开接口（半天）
```csharp
// Insurance.Application/IInsuranceService.cs
public interface IInsuranceService
{
    Task<InsurancePolicy> GetPolicyAsync(PolicyId id);
    Task<Claim> FileClaimAsync(PolicyId id, Money amount, string reason);
    Task<IReadOnlyList<InsurancePolicy>> ListUserPoliciesAsync(UserId userId);
}
```

#### 步骤 4：在 PluginRegistry 注册（半天）
```csharp
// Bootstrap/Startup.cs
services.AddBoundedContext<InsuranceModule>(config =>
{
    config.ModuleName = "Insurance";
    config.Description = "保单与理赔";
    config.Version = "1.0.0";
    config.EnabledByDefault = false;  // 默认关闭
    config.FeatureFlagKey = "feature.insurance.enabled";
});
```

#### 步骤 5：在 AgentOrchestration 添加 Skill（半天）
```json
// AgentOrchestration/Skills/insurance.json
{
  "name": "insurance",
  "description": "保险业务",
  "triggers": ["保单", "理赔", "续保"],
  "inputSchema": { ... },
  "handler": "IInsuranceService"
}
```

**总计：3-5 人天**

---

## 3. 目标机制 B：Context 内轻量 Plugin（Target 示例，接口未实现）

### 场景举例
在 Transfer Context 中增加"扫码转账"功能。

### 步骤（约 0.5-1 人天）

#### 步骤 1：实现 ITransferPlugin 接口
```csharp
// Transfer.Infrastructure/Plugins/QrCodeTransferPlugin.cs
public class QrCodeTransferPlugin : ITransferPlugin
{
    public string Name => "QrCodeTransfer";
    public string Version => "1.0.0";

    public bool CanHandle(TransferRequest request) =>
        request.Source == TransferSource.QrCode;

    public async Task<TransferResult> ExecuteAsync(TransferRequest request)
    {
        // 二维码解析逻辑
        var account = await ParseQrCodeAsync(request.QrCodeData);
        return await _executor.ExecuteAsync(account, request.Amount);
    }
}
```

#### 步骤 2：注册插件
```csharp
services.AddTransferPlugin<QrCodeTransferPlugin>(config =>
{
    config.Enabled = false;  // 默认关闭
    config.FeatureFlagKey = "feature.transfer.qrcode";
});
```

**总计：0.5-1 人天**

---

## 4. 目标机制 C：FeatureFlag（Target，未实现）

### 场景
已有功能但默认关闭（如跨场景联动、保险），需要快速启用给内部用户测试。

### 步骤（约 0.5 小时）

#### 选项 1：通过配置文件（推荐开发用）
```json
// appsettings.Development.json
{
  "FeatureFlags": {
    "feature.cross_scenario.enabled": true,
    "feature.cross_scenario.rollout_percentage": 5,
    "feature.insurance.enabled": true,
    "feature.insurance.rollout_percentage": 100
  }
}
```

#### 选项 2：通过管理后台（推荐生产用）
访问 Admin Console → 功能开关 → 选择功能 → 调整灰度。

#### 选项 3：通过 SQL（紧急回滚）
```sql
UPDATE core.feature_flags
SET enabled = false
WHERE flag_key = 'feature.cross_scenario.enabled';
```

10 秒内生效（Redis 缓存过期）。

---

## 5. FeatureFlag 设计原则（Target）

### 5.1 命名规范
- `feature.<module>.<feature>.enabled` — 总开关
- `feature.<module>.<feature>.rollout_percentage` — 灰度百分比
- `feature.<module>.<feature>.user_whitelist` — 白名单用户

### 5.2 灰度发布流程

| 阶段 | 比例 | 持续时间 |
|------|------|---------|
| 内部灰度 | 内部账号 100% | 1-3 天 |
| 小流量 | 1% | 1-3 天 |
| 中流量 | 10% | 3-7 天 |
| 大流量 | 50% | 3-7 天 |
| 全量 | 100% | 持续 |

### 5.3 紧急回滚
- 控制台一键关闭 → 10 秒生效
- 数据库紧急停机开关 → 1 秒生效
- 配合 ADR-0008 强制人工回环 → 即使出错也只影响回环用户

---

## 6. 目标实现草案（Target，不是当前源码）

### 6.1 插件注册中心

```csharp
// Core.Infrastructure/PluginRegistry/PluginRegistry.cs
public class PluginRegistry : IPluginRegistry
{
    private readonly Dictionary<string, IAgentPlugin> _plugins = new();
    private readonly IFeatureFlagService _flags;

    public void Register<T>(string name) where T : IAgentPlugin
    {
        var plugin = ActivatorUtilities.CreateInstance<T>(_serviceProvider);
        _plugins[name] = plugin;
    }

    public async Task<PluginResponse> ExecuteAsync(string name, PluginContext context)
    {
        // 1. 检查功能开关
        if (!await _flags.IsEnabledAsync($"feature.plugin.{name}.enabled"))
            return PluginResponse.Skipped("功能未启用");

        // 2. 检查灰度
        if (!await _flags.IsInRolloutAsync($"feature.plugin.{name}.rollout_percentage", context.UserId))
            return PluginResponse.Skipped("用户不在灰度范围内");

        // 3. 执行
        if (!_plugins.TryGetValue(name, out var plugin))
            return PluginResponse.NotFound();

        return await plugin.ExecuteAsync(context);
    }
}
```

### 6.2 功能开关服务

```csharp
// Core.Infrastructure/FeatureFlag/FeatureFlagService.cs
public class FeatureFlagService : IFeatureFlagService
{
    private readonly IDbConnection _db;
    private readonly IMemoryCache _cache;

    public async Task<bool> IsEnabledAsync(string flagKey)
    {
        var flag = await GetFlagAsync(flagKey);
        return flag.Enabled;
    }

    public async Task<bool> IsInRolloutAsync(string flagKey, string userId)
    {
        var flag = await GetFlagAsync(flagKey);
        if (flag.RolloutPercentage >= 100) return true;
        if (flag.RolloutPercentage <= 0) return false;

        // 基于 userId 哈希的一致性灰度
        var hash = Hash(flagKey + userId);
        return (hash % 100) < flag.RolloutPercentage;
    }

    private async Task<FeatureFlag> GetFlagAsync(string flagKey)
    {
        // 缓存优先
        if (_cache.TryGetValue(flagKey, out FeatureFlag flag))
            return flag;

        // 数据库
        flag = await _db.QueryFirstOrDefaultAsync<FeatureFlag>(
            "SELECT * FROM core.feature_flags WHERE flag_key = @key",
            new { key = flagKey });

        _cache.Set(flagKey, flag, TimeSpan.FromSeconds(10));
        return flag;
    }
}
```

---

## 7. Current 新插件 Checklist

发布新功能时检查以下项：

- [ ] **入口**：是否只有一个 `IPluginEntryPoint`，manifest ID/版本/依赖是否正确？
- [ ] **Agent 注册**：是否在 `ConfigureServices` 注册 `IBankingAgent`？
- [ ] **意图声明**：`SupportedIntents` 与 `TriggerKeywords` 是否足以被默认规则路径识别？
- [ ] **跨插件边界**：是否只用 Sdk 事件契约/manifest 依赖，未引用其他插件实现？
- [ ] **持久化**：需要落库时是否注册 `IEntitySetContributor` 并更新 migration？
- [ ] **FeatureFlag**：若仅写入 manifest，是否明确它当前不具备强制开关效果？
- [ ] **审计日志**：是否调用了 `IAuditLogger`？
- [ ] **合规检查**：是否经过 `IComplianceGuard`？
- [ ] **人工回环**：高风险操作是否有人工确认？
- [ ] **测试**：单元测试 + 集成测试？
- [ ] **文档**：更新相关 ADR 和 README？
- [ ] **配置**：是否在 appsettings.json 添加配置？
- [ ] **监控**：是否有 Metrics/Tracing？

---

## 8. 关联文档

- **架构总览**：[`00-overview.md`](00-overview.md)
- **模块边界**：[`05-module-boundaries.md`](05-module-boundaries.md)
- **架构决策**：[`04-architecture-decisions.md`](04-architecture-decisions.md)
- **FeatureFlag 详细**：[`../02-api/03-feature-flags.md`](../02-api/03-feature-flags.md)