# 插件接入指南（5 分钟上手）

> **适用对象**：需要在 AI Banking Agent 上新增业务功能的开发者
> **前置条件**：.NET SDK 9.0.200+（解决方案是 `.slnx` 格式；各项目本身目标 `net8.0`），会写 C#
> **上手方式**：用 `dotnet new banking-plugin` 模板生成骨架 → 补齐第 1 节第 5 步的三处手工接线 → 过契约校验门禁
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
│  BankingAgent.Plugin.Wealth                     │
└─────────────────────────────────────────────────┘
              ⬡ HTTP
┌─────────────────────────────────────────────────┐
│  MockBank.Api（模拟银行核心系统，端口 5200）       │
└─────────────────────────────────────────────────┘
```

**核心设计**：新功能 = 新插件。宿主代码零改动。

---

## 1. 六步接入流程

### 第 1 步：用模板生成插件项目

插件骨架已经模板化（模板位置 [`src/templates/banking-plugin/`](../../src/templates/banking-plugin/)，短名 `banking-plugin`）。先装模板，再生成项目：

```bash
cd src

# 只需安装一次
dotnet new install ./templates/banking-plugin

# 生成插件。-o 相对 src/，因此项目落在 src/plugins/ 下
dotnet new banking-plugin -n BankingAgent.Plugin.Wealth \
  -o plugins/BankingAgent.Plugin.Wealth \
  --pluginId banking.wealth --pluginName 理财 --scenario wealth \
  --intentPrefix wealth --withPersistence true
```

模板参数：

| 参数 | 说明 | 默认值 |
|------|------|--------|
| `--pluginId` | 插件唯一标识，反向域名风格，例如 `banking.wealth` | `banking.myfeature` |
| `--pluginName` | 插件显示名，**会直接进入 C# 类型名**（如 `理财Agent`、`理财PluginEntryPoint`） | `我的场景` |
| `--scenario` | 业务场景标识，同时用作数据分区名后缀 | `myfeature` |
| `--intentPrefix` | 该 Agent 处理的意图前缀 —— 决定它能否被路由到 | `myfeature` |
| `--withPersistence` | 是否生成独立数据分区（`true` 时才会生成 `Persistence.cs`） | `false` |

生成的文件：

| 文件 | 内容 |
|------|------|
| `BankingAgent.Plugin.<Name>.csproj` | 已引用 SDK + Base，无需手改 |
| `PluginEntryPoint.cs` | 插件清单声明 + DI 注册 |
| `Agent.cs` | `BankingAgentBase` 子类，含槽位读取与脱敏示例 |
| `Persistence.cs` | 仅 `--withPersistence true` 时生成：实体 + `IEntitySetContributor` |

> ⚠️ **不要**再用 `dotnet new classlib` 手搭项目：模板里的 csproj、命名空间、槽位读取与脱敏写法都是踩过坑的版本。
> ⚠️ 生成位置要保持在 `src/plugins/<项目名>`（相对 `src/` 深度两层）：模板 csproj 里的 `..\..\src\...` 项目引用是按这个深度写死的，生成到别处会解析不到 SDK/Base。

### 第 2 步：确认项目引用（模板已配好）

模板生成的 csproj 只引用契约层：

```xml
  <ItemGroup>
    <!-- 只引用这两个，不要引用 Host -->
    <ProjectReference Include="..\..\src\BankingAgent.Plugin.Sdk\BankingAgent.Plugin.Sdk.csproj" />
    <ProjectReference Include="..\..\src\BankingAgent.Base\BankingAgent.Base.csproj" />
  </ItemGroup>
```

> ⚠️ **绝对不要** 引用 `BankingAgent.Host`。插件依赖宿主会造成循环引用，破坏热插拔 —— 契约校验器会把这条判为 ERROR。

### 第 3 步：补齐插件入口的清单信息

入口类由模板生成（`PluginEntryPoint.cs`），你只需要补齐 `Description` / `Author` 这类占位信息：

```csharp
using BankingAgent.Base.Agents;
using BankingAgent.Base.Security.Audit;
using BankingAgent.PluginSdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Plugin.YourFeature;

/// <summary>你的插件入口。宿主扫描程序集时首先发现本类。</summary>
public sealed class YourFeaturePluginEntryPoint : IPluginEntryPoint
{
    // ① 声明身份：ID 全局唯一，版本用语义化版本
    public PluginManifest GetManifest() => new()
    {
        Id = new PluginId("banking.yourfeature"),
        Name = "你的功能名",
        Description = "一句话说清这个插件做什么",   // ← 模板占位，必填
        Version = PluginVersion.Parse("1.0.0"),
        Author = "你的组名",                        // ← 模板占位，必填
        Scenarios = ["yourfeature"],           // 业务场景标识
        FeatureFlags = ["yourfeature.enabled"],
        MaxDataClassification = DataClassification.L2
    };

    // ② 注册服务：Agent 用单例（无状态）
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton<IBankingAgent, YourFeatureAgent>();

        // 需要落库时把这一行取消注释（模板故意留成注释，见第 5 步 ③；
        // 取消注释后还需补 using BankingAgent.Base.Data;）
        // services.AddSingleton<IEntitySetContributor, YourFeaturePersistenceContributor>();

        context.Logger.LogInformation("插件初始化，数据分区: {Partition}", context.PartitionName);
    }
}
```

> ⚠️ `FeatureFlags` **当前仅作声明，运行时并未强制**，不要依赖它做安全控制（见 [`03-implementation-status.md`](03-implementation-status.md)）。
> ⚠️ 一个程序集**只能有一个** `IPluginEntryPoint`；多写一个，契约校验器直接判 ERROR。

### 第 4 步：实现你的 Agent

模板已经生成 Agent 骨架（`Agent.cs`），照它补业务逻辑即可：

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
    // 同时：宿主规则表认不出意图时，会用这里声明的前缀在用户消息里做包含匹配来兜底，
    // 所以「没写进这里的前缀」等于「永远收不到请求」。
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

### 第 5 步：三处手工接线（生成后必做）

模板**不会**帮你改下面这三处。漏掉任何一处，插件都会表现为「装好了但什么都不发生」，而且大多数情况下**没有任何报错**：

- [ ] **① 加入解决方案**：在 `src/BankingAgent.slnx` 的 `/plugins/` 段补一行，否则插件不参与本地与 CI 构建：

```xml
<Project Path="plugins/BankingAgent.Plugin.YourFeature/BankingAgent.Plugin.YourFeature.csproj" />
```

- [ ] **② 声明构建产物**：在 `src/src/BankingAgent.Host/BankingAgent.Host.csproj` 的 `PluginArtifact` 列表里补一行，否则编译出的 DLL **永远不会**被复制进宿主的 `plugins/` 输出目录（该列表目前显式列了 Transfer / BillAnalysis / CardManagement / Wealth 四项）：

```xml
<PluginArtifact Include="..\..\plugins\BankingAgent.Plugin.YourFeature\bin\$(Configuration)\net8.0\BankingAgent.Plugin.YourFeature.dll" />
```

- [ ] **③ 注册数据分区贡献器**：`--withPersistence true` 时，只实现 `IEntitySetContributor` **不够**。模板故意把注册行留成注释，必须你自己在 `PluginEntryPoint.ConfigureServices` 里打开：

```csharp
services.AddSingleton<IEntitySetContributor, YourFeaturePersistenceContributor>();
```

漏掉 ③ 的后果：不报错、不告警，只是**你的表不会被创建**。

### 第 6 步：编译 → 契约校验 → 重启宿主

```bash
# 从仓库根目录编译解决方案（.slnx 需要 .NET SDK 9.0.200+）
dotnet build src/BankingAgent.slnx -c Release

# 契约校验（与 CI 门禁同一条命令，见下）
dotnet run --project src/PluginValidator -- src/src/BankingAgent.Host/bin/Release/net8.0/plugins

# 用同一配置启动宿主（默认 http://localhost:5243），启动后按第 2 节验证
cd src/src/BankingAgent.Host
dotnet run -c Release
```

> ⚠️ 构建配置要和启动配置一致：`PluginArtifact` 只在**该配置下插件 DLL 已存在**时才复制。用 `-c Release` 编译却用默认 `dotnet run`（Debug）启动，宿主的 `plugins/` 输出目录会是空的 —— 不报错，只是什么插件都没加载。

`PluginValidator` 是 **CI 的阻断式门禁**：退出码 `0` = 通过、`1` = 发现违规、`2` = 用法错误；加 `--json` 输出机器可读结果。它用 `MetadataLoadContext` **只做反射检查，不执行插件代码**。强制规则：

| 规则 | 级别 |
|------|------|
| 每个程序集恰好一个 `IPluginEntryPoint` | ERROR |
| 文件名必须以 `BankingAgent.Plugin.` 开头 | ERROR |
| 不得引用 `BankingAgent.Host` | ERROR |
| 不得直接引用其他插件程序集 | ERROR |
| `PartitionName` 必须是 public `string` 属性 | ERROR |
| 至少实现一个 `IBankingAgent` | WARN |

> ⚠️ 传参要传**宿主输出目录下的 `plugins/` 子目录**（`.../bin/<配置>/net8.0/plugins`）：校验器会自己向上找到同级的 `BankingAgent.Plugin.Sdk` / `BankingAgent.Base`。
> **不要把插件目录拷到别处单独校验** —— 解析不到契约程序集时它会明确报错退出（`无法解析契约程序集`）。这是刻意设计：一个跳过接口检查还「全绿」的校验器，比没有校验器更危险。
> 也不要只传 `.../net8.0`（宿主输出目录本身）：扫描不递归，会报「未发现实现了 IPluginEntryPoint 的程序集」。

---

## 2. 快速验证清单

宿主默认监听 **5243**（见 `src/src/BankingAgent.Host/Properties/launchSettings.json`）。除 `/health` 与 `/api/auth/token` 外，**所有接口都要 Bearer 令牌**：

```powershell
# ⓪ 取令牌：演示账号 u_demo01 / demo1234。该端点限流 5 次/分钟，不要连环调用
$token = (Invoke-RestMethod -Method Post http://localhost:5243/api/auth/token `
  -ContentType 'application/json' `
  -Body '{"userId":"u_demo01","password":"demo1234"}').token
$h = @{ Authorization = "Bearer $token" }

# ① 插件已加载
Invoke-RestMethod http://localhost:5243/api/plugins -Headers $h | Format-Table id, name, version, isActive

# ② Agent 已注册，并确认 intents 里有你的前缀
Invoke-RestMethod http://localhost:5243/api/plugins/agents -Headers $h | Format-Table id, name, role, intents

# ③ 合规规则已加载
Invoke-RestMethod http://localhost:5243/api/plugins/compliance -Headers $h | Format-Table ruleId, version

# ④ 事件总线工作正常
Invoke-RestMethod http://localhost:5243/api/plugins/events -Headers $h
```

> ⚠️ ①–④ 全绿只证明「注册成功」，**不等于「能被访问到」**。「已注册但收不到请求」是这套插件系统最常见的静默故障，务必再走一步：

```powershell
# ⑤ 用一句含你意图前缀的话发真实请求，确认落到你的 Agent
Invoke-RestMethod -Method Post http://localhost:5243/api/chat -Headers $h `
  -ContentType 'application/json' `
  -Body '{"message":"wealth 推荐","userId":"u_demo01"}'
```

curl 版：

```bash
curl -H "Authorization: Bearer $TOKEN" http://localhost:5243/api/plugins
curl -H "Authorization: Bearer $TOKEN" http://localhost:5243/api/plugins/agents
```

---

## 3. 示例插件对照

| 插件 | 演示了什么 | 代码位置 |
|------|-----------|---------|
| **Transfer** | 资金操作全链路：槽位抽取 → 合规守卫 → 人工回环 → 调用核心银行 → 落库 → 发事件 | [`plugins/BankingAgent.Plugin.Transfer/`](../../src/plugins/BankingAgent.Plugin.Transfer/) |
| **BillAnalysis** | 只读场景 + 订阅他人事件实现跨插件联动（零耦合） | [`plugins/BankingAgent.Plugin.BillAnalysis/`](../../src/plugins/BankingAgent.Plugin.BillAnalysis/) |
| **CardManagement** | 写操作但非资金 + 插件依赖声明 + L3 数据强制脱敏 | [`plugins/BankingAgent.Plugin.CardManagement/`](../../src/plugins/BankingAgent.Plugin.CardManagement/) |
| **Wealth** | 模板直接生成的最小可运行插件（`--withPersistence true`，含独立数据分区实体） | [`plugins/BankingAgent.Plugin.Wealth/`](../../src/plugins/BankingAgent.Plugin.Wealth/) |

**建议**：照着 Transfer 写你的第一个功能，它覆盖了资金操作的全部合规要求；想先看看「模板生成出来长什么样」，直接对照 Wealth。

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

> ⚠️ 写完贡献器**还要注册**，否则表不会被创建（第 1 节第 5 步 ③）：
> ```csharp
> services.AddSingleton<IEntitySetContributor, MyPersistence>();
> ```
> 用模板生成时（`--withPersistence true`），实体与贡献器已写好，只差这一行注册。

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
| 插件没出现在 `/api/plugins` | DLL 没被复制进 `plugins/` 输出目录 | 检查 `BankingAgent.Host.csproj` 里的 `<PluginArtifact Include="..." />` 是否加了你的 DLL（第 1 节第 5 步 ②） |
| 插件没出现在 `/api/plugins` | 文件名不符合扫描约定 | 文件名必须以 `BankingAgent.Plugin.` 开头，否则 `PluginRegistry` 扫不到 |
| 插件出现在 `/api/plugins/agents`，却永远收不到请求 | `SupportedIntents` 没覆盖用户消息里出现的字面前缀（规则表只认转账/账单/卡片，其余靠 Agent 前缀兜底） | 在 `SupportedIntents` 里补上该前缀，然后对照 `GET /api/plugins/agents` 的 `intents` 字段确认 |
| 数据表没被创建 | 实现了 `IEntitySetContributor` 但没注册 | 在 `ConfigureServices` 里 `services.AddSingleton<IEntitySetContributor, XxxContributor>()`（第 1 节第 5 步 ③） |
| CI 构建里根本没有我的插件 | 项目没进 `src/BankingAgent.slnx` | 补 `<Project Path="plugins/..." />`（第 1 节第 5 步 ①） |
| 契约校验报 `无法解析契约程序集 ...` | 传入了脱离宿主输出目录的 `plugins/` 目录 | 传 `src/src/BankingAgent.Host/bin/<配置>/net8.0/plugins` |
| 契约校验报 `未发现实现了 IPluginEntryPoint 的程序集` | 传了宿主输出目录本身（扫描不递归） | 传它的 `plugins/` 子目录 |
| 接口全返回 401 | 缺 Bearer 令牌 | 先 `POST /api/auth/token` 取令牌，再带 `Authorization: Bearer <token>` |
| `依赖的 xxx 未找到` | 依赖插件没加载 | 先加载依赖插件 |
| `版本不兼容` | 依赖版本声明过高 | 调整 `PluginVersion.Parse("x.y.z")` |
| `检测到循环依赖` | A 依赖 B，B 依赖 A | 拆掉环，通常用事件解耦 |
| 路由不到我的 Agent | `SupportedIntents` 没覆盖实际意图 | 检查意图前缀；最长前缀优先匹配 |
| `未实现 IPluginEntryPoint` | 缺入口类或未实现接口 | 检查 `IPluginEntryPoint` 实现（一个程序集恰好一个） |
| 类型找不到（跨插件） | 引用了其他插件的类型 | 改用事件通信；契约校验器会把直接引用判为 ERROR |
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

编译 → 过契约校验 → 重启，插件就能上线；但这之前**别忘了第 1 节第 5 步的三处手工接线**（slnx / PluginArtifact / `IEntitySetContributor` 注册），否则上面的代码写得再对，宿主也不会加载或路由它。

> 上面的代码是手写示意；仓库里真实的 `BankingAgent.Plugin.Wealth` 是 `dotnet new banking-plugin` 生成的版本（入口类名为 `理财PluginEntryPoint`，因为 `--pluginName` 会进入类型名）。两者对照着看即可。

---

## 12. 参考

- 插件模板：[`src/templates/banking-plugin/`](../../src/templates/banking-plugin/)（`dotnet new banking-plugin`）
- 插件契约校验器：[`src/PluginValidator/`](../../src/PluginValidator/)（CI 阻断式门禁）
- 实现状态（哪些能力只是声明、哪些真的生效）：[`03-implementation-status.md`](03-implementation-status.md)
- 扩展点设计：[`docs/00-architecture/06-extension-points.md`](../00-architecture/06-extension-points.md)
- 模块边界：[`docs/00-architecture/05-module-boundaries.md`](../00-architecture/05-module-boundaries.md)
- 合规矩阵：[`docs/05-security-compliance/02-compliance-matrix.md`](../05-security-compliance/02-compliance-matrix.md)
- 数据分级：[`docs/05-security-compliance/03-data-classification.md`](../05-security-compliance/03-data-classification.md)
- 审计规范：[`docs/05-security-compliance/04-audit-logging.md`](../05-security-compliance/04-audit-logging.md)
- 业务开发分册：[`docs/07-team/by-role/backend.md`](../07-team/by-role/backend.md)
