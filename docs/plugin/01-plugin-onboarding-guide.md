# 插件接入指南（5 分钟上手）

> **适用对象**：需要在 AI Banking Agent 上新增业务功能的开发者
> **前置条件**：.NET 8 SDK，会写 C#
> **对应规范**：[`docs/00-architecture/06-extension-points.md`](../00-architecture/06-extension-points.md)

---

## 0. 三分钟理解架构

```
┌─────────────────────────────────────────────────┐
│  BankingAgent.Host（宿主，零业务逻辑）            │
│  ├── /api/plugins      插件管理                  │
│  ├── /api/chat         对话入口                  │
│  └── PluginRegistry    扫描并加载 plugins/ 目录   │
├─────────────────────────────────────────────────┤
│  BankingAgent.Base（框架，不含业务）              │
│  ├── 插件系统   热插拔 / 依赖拓扑 / 生命周期      │
│  ├── Agent 路由 意图 → 处理者                    │
│  ├── 事件总线   插件间零耦合通信                  │
│  ├── 数据库     DbContext / 审计字段 / 分区隔离   │
│  └── 安全合规   审计链 / 脱敏 / 合规守卫          │
├─────────────────────────────────────────────────┤
│  BankingAgent.Plugin.Sdk（唯一需要引用的契约层）   │
└─────────────────────────────────────────────────┘
              ⬇ plugins/ 目录
┌─────────────────────────────────────────────────┐
│  BankingAgent.Plugin.Transfer   ← 你要写的这类     │
│  BankingAgent.Plugin.BillAnalysis                │
│  BankingAgent.Plugin.CardManagement              │
└─────────────────────────────────────────────────┘
              ⬡ HTTP
┌─────────────────────────────────────────────────┐
│  MockBank.Api（模拟银行核心系统，端口 5200）       │
└─────────────────────────────────────────────────┘
```

**核心设计**：新功能 = 新插件。宿主代码零改动。

---

## 1. 五步接入流程

### 第 1 步：创建插件项目

```bash
cd src/plugins
dotnet new classlib -n BankingAgent.Plugin.YourFeature -f net8.0
```

### 第 2 步：配置项目引用

编辑 `BankingAgent.Plugin.YourFeature.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <!-- 只引用这两个，不要引用 Host -->
    <ProjectReference Include="..\..\src\BankingAgent.Plugin.Sdk\BankingAgent.Plugin.Sdk.csproj" />
    <ProjectReference Include="..\..\src\BankingAgent.Base\BankingAgent.Base.csproj" />
  </ItemGroup>
</Project>
```

> ⚠️ **绝对不要** 引用 `BankingAgent.Host`。插件依赖宿主会造成循环引用，破坏热插拔。

删除模板生成的 `Class1.cs`。

### 第 3 步：实现插件入口

新建 `YourFeaturePlugin.cs`：

```csharp
using BankingAgent.Base.Agents;
using BankingAgent.Base.Data;
using BankingAgent.PluginSdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BankingAgent.Plugin.YourFeature;

/// <summary>你的插件入口。宿主扫描程序集时首先发现本类。</summary>
public sealed class YourFeaturePluginEntryPoint : IPluginEntryPoint
{
    // ① 声明身份：ID 全局唯一，版本用语义化版本
    public PluginManifest GetManifest() => new()
    {
        Id = new PluginId("banking.yourfeature"),
        Name = "你的功能名",
        Description = "一句话说清这个插件做什么",
        Version = PluginVersion.Parse("1.0.0"),
        Author = "你的组名",
        Scenarios = ["yourfeature"],           // 业务场景标识
        FeatureFlags = ["yourfeature.enabled"], // 新功能默认关闭
        MaxDataClassification = DataClassification.L2
    };

    // ② 注册服务：Agent 用单例（无状态）
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton<IBankingAgent, YourFeatureAgent>();

        // 需要落库时再加上自己的分区
        // services.AddSingleton<IEntitySetContributor, YourFeaturePersistence>();

        context.Logger.LogInformation("插件初始化，数据分区: {Partition}", context.PartitionName);
    }
}
```

### 第 4 步：实现你的 Agent

```csharp
using BankingAgent.Base.Agents;
using BankingAgent.Base.Security.Audit;
using BankingAgent.PluginSdk;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Plugin.YourFeature;

public sealed class YourFeatureAgent : BankingAgentBase
{
    private readonly ICoreBankClient _coreBank;

    public YourFeatureAgent(ICoreBankClient coreBank, IAuditLogger audit, ILogger<YourFeatureAgent> logger)
        : base(audit, logger)   // 基类自动处理计时、审计、异常兜底
    {
        _coreBank = coreBank;
    }

    public override AgentId Id => new("yourfeature.agent");
    public override string Name => "你的功能 Agent";
    public override AgentRole Role => AgentRole.DomainExpert;

    // 关键：这个 Agent 响应哪些意图。路由器按最长前缀匹配。
    public override IReadOnlyList<string> SupportedIntents => ["yourfeature", "yourfeature.query"];

    protected override async Task<AgentResult> HandleAsync(AgentRequest request, CancellationToken ct)
    {
        // 从上游拿槽位
        var id = request.Slots.GetValueOrDefault("id")?.ToString();

        // 调核心银行
        var accounts = await _coreBank.ListAccountsAsync(request.UserId, ct);

        // 返回结果
        return AgentResult.Ok(
            content: "处理完成",
            intent: "yourfeature.completed",
            data: new Dictionary<string, object?> { ["count"] = accounts.Count });
    }
}
```

### 第 5 步：放入 plugins 目录并重启

```bash
# 编译
dotnet build plugins/BankingAgent.Plugin.YourFeature

# 复制到宿主的 plugins 目录
cp plugins/BankingAgent.Plugin.YourFeature/bin/Debug/net8.0/BankingAgent.Plugin.YourFeature.dll \
   src/BankingAgent.Host/bin/Debug/net8.0/plugins/

# 重启宿主，扫描加载
```

验证：

```bash
curl http://localhost:5100/api/plugins
```

---

## 2. 快速验证清单

启动宿主后，依次确认：

```powershell
# ① 插件已加载
Invoke-RestMethod http://localhost:5100/api/plugins | Format-Table id, name, version, isActive

# ② Agent 已注册
Invoke-RestMethod http://localhost:5100/api/plugins/agents | Format-Table id, name, role

# ③ 合规规则已加载
Invoke-RestMethod http://localhost:5100/api/plugins/compliance | Format-Table ruleId, version

# ④ 事件总线工作正常
Invoke-RestMethod http://localhost:5100/api/plugins/events
```

---

## 3. 三个示例插件对照

| 插件 | 演示了什么 | 代码位置 |
|------|-----------|---------|
| **Transfer** | 资金操作全链路：槽位抽取 → 合规守卫 → 人工回环 → 调用核心银行 → 落库 → 发事件 | [`plugins/BankingAgent.Plugin.Transfer/`](../../src/plugins/BankingAgent.Plugin.Transfer/) |
| **BillAnalysis** | 只读场景 + 订阅他人事件实现跨插件联动（零耦合） | [`plugins/BankingAgent.Plugin.BillAnalysis/`](../../src/plugins/BankingAgent.Plugin.BillAnalysis/) |
| **CardManagement** | 写操作但非资金 + 插件依赖声明 + L3 数据强制脱敏 | [`plugins/BankingAgent.Plugin.CardManagement/`](../../src/plugins/BankingAgent.Plugin.CardManagement/) |

**建议**：照着 Transfer 写你的第一个功能，它覆盖了资金操作的全部合规要求。

---

## 4. 资金操作的标准写法（必须遵守）

如果你的插件会动钱，**必须**按以下顺序，缺一不可：

```csharp
protected override async Task<AgentResult> HandleAsync(AgentRequest request, CancellationToken ct)
{
    // ① 槽位抽取
    var amount = ExtractAmount(request);

    // ② 合规守卫：超限直接拒绝
    var compliance = _compliance.Evaluate(new ComplianceContext
    {
        UserId = request.UserId,
        Scenario = "yourfeature",
        Amount = amount,
        SourceAccount = source,
        TargetAccount = target
    });
    if (!compliance.Allowed)
        return AgentResult.Fail("COMPLIANCE_REJECTED", compliance.Reason);

    // ③ 人工回环：大额/可疑时阻断，等用户确认
    if (compliance.RequiresHumanApproval && !confirmed)
        return AgentResult.PendingApproval(compliance.Reason, slots);

    // ④ 幂等检查：防止重复扣款
    if (await AlreadyProcessedAsync(idempotencyKey, ct))
        return AgentResult.Ok("该操作已处理过");

    // ⑤ 调核心银行
    var result = await _coreBank.ExecuteTransferAsync(cmd, ct);
    if (!result.Success)
        return AgentResult.Fail(result.ErrorCode!, result.ErrorMessage!);

    // ⑥ 落库
    _db.Add(new MyRecord { ... });
    await _db.SaveChangesAsync(ct);

    // ⑦ 发事件，解耦下游
    await _events.PublishAsync(new DomainEvent { EventType = "yourfeature.completed", ... }, ct);

    // ⑧ 标记副作用已提交
    return new AgentResult { Success = true, SideEffectCommitted = true, ... };
}
```

**跳过任何一步都是 P0 缺陷**，对应文档 [`docs/05-security-compliance/02-compliance-matrix.md`](../05-security-compliance/02-compliance-matrix.md)。

---

## 5. 插件间通信：只用事件

**禁止**直接引用其他插件的类型（会导致加载顺序耦合）。

```csharp
// ✅ 正确：订阅事件
public sealed class MyListener : IDomainEventHandler
{
    public IReadOnlyCollection<string> SubscribedEventTypes => ["transfer.completed"];

    public Task HandleAsync(DomainEvent evt, CancellationToken ct)
    {
        // 转账完成后刷新我的视图
        return Task.CompletedTask;
    }
}

// 在 ConfigureServices 里注册
services.AddSingleton<IDomainEventHandler, MyListener>();
```

内置事件：

| 事件类型 | 发布者 | 含义 |
|---------|-------|------|
| `transfer.completed` | 转账插件 | 转账成功 |
| `transfer.failed` | 转账插件 | 转账失败 |
| `card.status.updated` | 卡片插件 | 卡状态变更 |

命名约定：`{领域}.{动作}`，如 `wealth.subscribed`。

---

## 6. 插件依赖

```csharp
Dependencies =
[
    new PluginDependency(new PluginId("banking.transfer"), PluginVersion.Parse("1.0.0")),
    new PluginDependency(new PluginId("banking.optional"), PluginVersion.Parse("1.2.0"), IsOptional: true)
]
```

宿主会在加载时：
1. 检查依赖是否存在 → 缺失则拒绝加载
2. 检查版本是否兼容 → 不兼容则拒绝加载
3. 按拓扑顺序启动 → 被依赖的先启动
4. 检测循环依赖 → 检测到则报错

---

## 7. 自己的数据分区

```csharp
public class MyRecord : BaseEntity   // 基类自带审计字段与乐观并发
{
    public required string UserId { get; set; }
    public decimal Amount { get; set; }
}

public sealed class MyPersistence : IEntitySetContributor
{
    public string PartitionName => "plugin_yourfeature";
    public DataClassification MaxClassification => DataClassification.L2;

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MyRecord>(entity =>
        {
            entity.ToTable("my_records");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.RowVersion).IsConcurrencyToken();
            entity.HasIndex(e => new { e.UserId, e.CreatedAt });
        });
    }
}
```

`BaseEntity` 自动获得：`CreatedAt` / `CreatedBy` / `UpdatedAt` / `RowVersion`（乐观并发）/ `IsDeleted`（软删除）。

---

## 8. 数据脱敏（强制）

**L3 及以上数据出库必须脱敏**：

```csharp
using BankingAgent.Base.Security;

// 账号
DataMasker.MaskAccount(accountNo);      // **************3456
// 卡号
DataMasker.MaskBankCard(cardNo);        // **** **** **** 6789
// 手机
DataMasker.MaskPhone("13800138000");    // 138****8000
// 身份证
DataMasker.MaskIdCard("110101...");     // 110101********1234
// 姓名
DataMasker.MaskName("张三");            // 张*
// 通用（按级别）
DataMasker.Apply(value, DataClassification.L3, "phone");
```

**L4 数据永不返回明文**，`Apply` 会直接返回 `********`。

---

## 9. 审计（强制）

关键操作必须留痕。基类已自动记录 Agent 执行，你在关键节点补充业务语义：

```csharp
await Audit.WriteAsync(new AuditEvent
{
    AuditId = Guid.NewGuid().ToString("N")[..12],
    Timestamp = DateTimeOffset.UtcNow,
    ActorType = "AGENT",
    ActorId = Id.Value,
    Operation = "yourfeature.execute",
    Scenario = "yourfeature",
    Decision = "SUCCESS",        // SUCCESS / FAILED / DENY / REQUIRE_APPROVAL
    DecisionReason = "...",
    RuleId = compliance.RuleId,
    Amount = amount,
    RiskScore = compliance.RiskScore
}, ct);
```

审计采用 **HMAC 链式签名**，任何篡改都会导致链断裂。日志同时写入 `logs/audit-chain.log`。

---

## 10. 故障排查

| 现象 | 原因 | 解决 |
|------|------|------|
| 插件没出现在 `/api/plugins` | DLL 没放到 `plugins/` 目录 | 确认路径；文件名必须以 `BankingAgent.Plugin.` 开头 |
| `依赖的 xxx 未找到` | 依赖插件没加载 | 先加载依赖插件 |
| `版本不兼容` | 依赖版本声明过高 | 调整 `PluginVersion.Parse("x.y.z")` |
| `检测到循环依赖` | A 依赖 B，B 依赖 A | 拆掉环，通常用事件解耦 |
| 路由不到我的 Agent | `SupportedIntents` 没覆盖实际意图 | 检查意图前缀；最长前缀优先匹配 |
| `未实现 IPluginEntryPoint` | 缺入口类或未实现接口 | 检查 `IPluginEntryPoint` 实现 |
| 类型找不到（跨插件） | 引用了其他插件的类型 | 改用事件通信 |
| 提交后不生效 | 插件未激活 | `POST /api/plugins/{id}/start` |

---

## 11. 完整示例：30 行写一个新插件

```csharp
// 文件：BankingAgent.Plugin.Wealth/WealthPlugin.cs
using BankingAgent.Base.Agents;
using BankingAgent.Base.Security;
using BankingAgent.PluginSdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Plugin.Wealth;

public sealed class WealthEntryPoint : IPluginEntryPoint
{
    public PluginManifest GetManifest() => new()
    {
        Id = new PluginId("banking.wealth"),
        Name = "理财推荐",
        Description = "按风险等级推荐理财产品",
        Version = PluginVersion.Parse("1.0.0"),
        Scenarios = ["wealth"],
        FeatureFlags = ["wealth.enabled"],
        MaxDataClassification = DataClassification.L3
    };

    public void ConfigureServices(IServiceCollection services, IPluginContext ctx)
        => services.AddSingleton<IBankingAgent, WealthAgent>();
}

public sealed class WealthAgent : BankingAgentBase
{
    private readonly ICoreBankClient _bank;

    public WealthAgent(ICoreBankClient bank, IAuditLogger audit, ILogger<WealthAgent> log)
        : base(audit, log) => _bank = bank;

    public override AgentId Id => new("wealth.agent");
    public override string Name => "理财推荐 Agent";
    public override AgentRole Role => AgentRole.DomainExpert;
    public override IReadOnlyList<string> SupportedIntents => ["wealth", "wealth.recommend"];

    protected override async Task<AgentResult> HandleAsync(AgentRequest r, CancellationToken ct)
    {
        var products = await _bank.ListProductsAsync(null, ct);
        var top = products.OrderByDescending(p => p.AnnualRate).FirstOrDefault();

        return AgentResult.Ok(
            content: $"为您推荐 {top?.Name}，年化 {top?.AnnualRate}%",
            intent: "wealth.recommended",
            data: new Dictionary<string, object?> { ["count"] = products.Count });
    }
}
```

编译、复制、重启，三步上线。

---

## 12. 参考

- 扩展点设计：[`docs/00-architecture/06-extension-points.md`](../00-architecture/06-extension-points.md)
- 模块边界：[`docs/00-architecture/05-module-boundaries.md`](../00-architecture/05-module-boundaries.md)
- 合规矩阵：[`docs/05-security-compliance/02-compliance-matrix.md`](../05-security-compliance/02-compliance-matrix.md)
- 数据分级：[`docs/05-security-compliance/03-data-classification.md`](../05-security-compliance/03-data-classification.md)
- 审计规范：[`docs/05-security-compliance/04-audit-logging.md`](../05-security-compliance/04-audit-logging.md)
- 业务开发分册：[`docs/07-team/by-role/backend.md`](../07-team/by-role/backend.md)
