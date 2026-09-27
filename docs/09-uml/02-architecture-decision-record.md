# 架构决策记录（补充）— 实现阶段新增 ADR-011 ~ ADR-021

> **范围**：本文只记录 **编码实现过程中新产生** 的架构决策。
> 顶层设计决策（模块化单体、微服务演进、事件总线方向、双语言栈、LLM 选型、数据库选型、网关、HITL、Agent 注册、FeatureFlag）见
> [`../00-architecture/04-architecture-decisions.md`](../00-architecture/04-architecture-decisions.md) 的 ADR-001 ~ ADR-010，**本文不重复**。
>
> **日期**：2026-09-27
> **状态**：已采纳（全部已落地到代码）
> **格式**：Michael Nygard ADR 模板的精简工程版

---

## 0. 索引

| 编号 | 标题 | 状态 | 日期 | 一句话 |
|------|------|------|------|--------|
| [ADR-011](#adr-011) | 自研 SimpleDbContextFactory 替代 EF 内置工厂 | ✅ 已采纳 | 2026-09-27 | EF 工厂只认单参构造，与多参 DbContext 冲突 |
| [ADR-012](#adr-012) | PluginContributorRegistry 静态注册表 | ✅ 已采纳 | 2026-09-27 | 弥补工厂无法注入集合的缺口 |
| [ADR-013](#adr-013) | SlotReader 统一处理 JsonElement 槽位 | ✅ 已采纳 | 2026-09-27 | 消除 `InvalidCastException` 与已释放文档异常 |
| [ADR-014](#adr-014) | AgentRouter 最长前缀优先路由 | ✅ 已采纳 | 2026-09-27 | `transfer` 与 `transfer.execute` 不可互相抢占 |
| [ADR-015](#adr-015) | 审计链 SignAndAdvance 读算写原子化 | ✅ 已采纳 | 2026-09-27 | 拆开会导致并发写链断裂 |
| [ADR-016](#adr-016) | 审计文件 SemaphoreSlim 串行化 | ✅ 已采纳 | 2026-09-27 | 并发追加会撕裂行 |
| [ADR-017](#adr-017) | JWT HS256 对称签名 + 服务端决定角色 | ✅ 已采纳 | 2026-09-27 | 客户端可自助提权是致命越权 |
| [ADR-018](#adr-018) | 自定义中间件鉴权，不用 ASP.NET Core Authentication | ✅ 已采纳 | 2026-09-27 | `Forbid()` 需要鉴权方案配合 |
| [ADR-019](#adr-019) | Host 双容器装配（bootstrap + 正式容器） | ✅ 已采纳 | 2026-09-27 | 插件服务必须在 Build 之前并入主容器 |
| [ADR-020](#adr-020) | 内存事件总线，不引入 Kafka / MediatR | ✅ 已采纳 | 2026-09-27 | 迁移路径已明确写死 |
| [ADR-021](#adr-021) | 幂等键复用 SessionId | ⚠️ 已采纳（有已知缺陷） | 2026-09-27 | 取舍分析 + 缺陷与改造方向 |

### 与 ADR-001 ~ ADR-010 的关系

| 本文 ADR | 上游 ADR | 关系 |
|----------|----------|------|
| ADR-019 双容器装配 | [ADR-0009](../00-architecture/04-architecture-decisions.md#adr-0009可插拔-agent-注册机制) | 落地"新增 Agent 不改核心代码"的技术前提 |
| ADR-020 内存事件总线 | [ADR-0003](../00-architecture/04-architecture-decisions.md#adr-0003事件总线进程内-mediatr--未来-kafka) | 上游决策定了"进程内 → Kafka"方向，本文只定**进程内用什么实现**以及**如何保证可迁移** |
| ADR-017 + ADR-018 | [ADR-0008](../00-architecture/04-architecture-decisions.md#adr-0008强制人工回环human-in-the-loop) | HITL 要可信，前提是"确认人身份不可伪造" |
| ADR-011 ~ ADR-016 | [ADR-0002](../00-architecture/04-architecture-decisions.md#adr-0002未来可演进为微服务) | 这些是"数据访问必须经接口""通信必须经事件"的技术兑现 |
| ADR-021 | [ADR-0006](../00-architecture/04-architecture-decisions.md#adr-0006数据库选型-postgresql-16--pgvector) | 幂等最终要落在数据库唯一约束上 |

---

## ADR-011：自研 SimpleDbContextFactory 替代 EF 内置工厂

- **状态**：✅ 已采纳
- **日期**：2026-09-27
- **背景**

  项目的 `BankingDbContext` 需要注入 4 个依赖：插件贡献器集合 `IEnumerable<IEntitySetContributor>`、
  当前操作者 `ICurrentUserAccessor`、时钟 `DateTimeOffsetProvider`，以及标准的 `DbContextOptions`。

  而 EF Core 内置的 `AddDbContextFactory<TContext>()` / `IDbContextFactory<TContext>` 有两个硬约束：

  1. 工厂创建实例时**只传一个参数**：`DbContextOptions<TContext>`。它通过 `ActivatorUtilities` 之外的
     专用激活器调用构造函数，无法传入 `IEnumerable<T>`。
  2. 当类型有**多个构造函数**时，EF 内置工厂要求存在"接受且仅接受 `DbContextOptions` 的构造函数"，
     存在歧义时抛 `InvalidOperationException`。

  如果为了迁就 EF 而放弃注入贡献器与当前用户，会直接破坏两件事：
  插件的数据分区隔离（贡献器决定 Schema 与实体映射）、审计字段的真实操作者（当前用户决定 `CreatedBy`）。

- **决策**

  保留 `BankingDbContext` 的多参主构造函数（架构完整性优先），
  **手写** `SimpleDbContextFactory : IDbContextFactory<BankingDbContext>`，
  由它从根容器补齐除 `options` 以外的依赖。

  同时保留一个最小构造函数供工厂路径使用，它从静态注册表回填贡献器：

  ```csharp
  // SimpleDbContextFactory.cs:20-28
  public BankingDbContext CreateDbContext()
  {
      var contributors = rootProvider.GetServices<IEntitySetContributor>();
      PluginContributorRegistry.Register(contributors);
      return new BankingDbContext(options, contributors,
          rootProvider.GetRequiredService<ICurrentUserAccessor>(),
          rootProvider.GetRequiredService<DateTimeOffsetProvider>());
  }
  ```

  同时在 DI 层**绕开** `AddDbContextFactory` 扩展，手工把 `DbContextOptions<BankingDbContext>`
  注册为单例。原因是 `AddDbContext` 会额外注册一份 scoped 的 options，导致单例工厂消费 scoped 服务而抛异常。

- **备选方案**

  | 方案 | 放弃原因 |
  |------|----------|
  | A. 把 `BankingDbContext` 改成只有单参构造，依赖全走静态访问 | 架构被测试倒逼：贡献器与当前用户变成全局可变状态，可测性与多租户隔离同时受损 |
  | B. 放弃 `IDbContextFactory`，Agent 里直接注入 `BankingDbContext` | Agent 是**单例**（`TransferPlugin.cs:40`），无法消费 scoped DbContext；且失去"每次操作独立上下文"的隔离 |
  | C. 引入第三方工厂库（`EFCore.Factories` 等） | 引入未审计的依赖，且它同样解决不了 `IEnumerable<T>` 注入问题 |
  | D. 用 `IDbContextFactory` 但把贡献器合并进 `DbContextOptions` 自定义扩展 | 污染 EF 的扩展点，模型缓存与贡献器变更难以失效重建 |

- **影响**

  | 维度 | 内容 |
  |------|------|
  | ✅ 正面 | 插件数据分区与审计身份注入保持强类型、可测；`IDbContextFactory` 接口保持不变，未来换池化实现（`AddPooledDbContextFactory`）只需改 DI 注册 |
  | ⚖️ 代价 | 多写了 20 行工厂代码；`BankingDbContext` 双构造函数对阅读者有认知成本（已在 `DbContext.cs:49-52` 注释说明） |
  | ⚠️ 风险 | 单元测试必须复用同一工厂才能构造出正确的 DbContext，已在 `IdempotencyTests.cs:66-67` 注释提醒 |

- **关联代码**：`src/src/BankingAgent.Base/Data/SimpleDbContextFactory.cs`、`src/src/BankingAgent.Base/Data/BankingDbContext.cs:38-58`、`src/src/BankingAgent.Base/BankingCoreServiceCollectionExtensions.cs:98-114`、`src/UnitTests/IdempotencyTests.cs:64-86`

---

## ADR-012：PluginContributorRegistry 静态注册表

- **状态**：✅ 已采纳
- **日期**：2026-09-27
- **背景**

  ADR-011 解决了"工厂只传 options"的问题，但留下一个次生问题：
  插件的 `IEntitySetContributor` 实例**只有插件被加载后才存在**（它们在 `IPluginEntryPoint.ConfigureServices` 里注册），
  而 `BankingDbContext` 的 `OnModelCreating` 必须在构造时就知道有哪些贡献器。

  时序上这是死结：

  ```
  Build 容器 → 加载插件（ConfigureServices 写入贡献器）→ Build 容器（真正 Build）
       ↑                                                   ↓
       └────────────── EF 模型构建发生在 Build 之后 ─────────┘
  ```

  EF 的模型缓存意味着**模型只构建一次**，如果贡献者列表在构建时为空，后续再补充也不会生效。

- **决策**

  引入进程级静态注册表 `PluginContributorRegistry`，在 `builder.Build()` 之后、
  `DatabaseInitializer.InitializeAsync()` 之前写入：

  ```csharp
  // Program.cs:65
  PluginContributorRegistry.Register(app.Services.GetServices<IEntitySetContributor>());
  ```

  `BankingDbContext` 的最小构造函数调用 `PluginContributorRegistry.Resolve()` 回填（`DbContext.cs:55`），
  `SimpleDbContextFactory.CreateDbContext` 每次也先 `Register` 再构造（`SimpleDbContextFactory.cs:23`）。

  注册表内部用 `lock` 保证线程安全，`Register` 先 `Items.Clear()` 再 `AddRange`（幂等全量覆盖）。

- **备选方案**

  | 方案 | 放弃原因 |
  |------|----------|
  | A. 插件加载放到 `builder.Build()` 之后，用子容器承载插件服务 | Agent 已是主容器单例，插件贡献器要进 `BankingDbContext` 构造参数，必须与 DbContext 同容器；子容器无法满足 |
  | B. 约定所有插件实体继承 `BaseEntity` 并由 `Base` 统一扫描 | Base 不知道有哪些插件程序集；需要额外维护类型白名单，等于回到硬编码 |
  | C. 用 EF 的 `IModelCacheKeyFactory` 动态失效模型 | 只能解决缓存失效，解决不了"构造 DbContext 时贡献者列表为空" |
  | D. 每次请求动态重建 DbContext 模型 | 性能不可接受，且模型频繁重建会锁竞争 |

- **影响**

  | 维度 | 内容 |
  |------|------|
  | ✅ 正面 | 插件的实体映射真正做到了"零改动接入"；新增插件只要实现 `IEntitySetContributor` 即可自动建表 |
  | ⚖️ 代价 | 进程级可变全局状态；单元测试之间有串扰风险（`Register` 是全量覆盖，测试需自行保证隔离） |
  | ⚠️ 风险 | `SimpleDbContextFactory.CreateDbContext` **每次调用都重写静态表**。当前是单例 Agent + 每次一个工厂，集合内容恒定，因此无实际竞态；但若未来有多租户动态插件集合，这个设计会失效 |

- **关联代码**：`src/src/BankingAgent.Base/Data/BankingDbContext.cs:172-198`、`src/src/BankingAgent.Base/Data/SimpleDbContextFactory.cs:22-23`、`src/src/BankingAgent.Host/Program.cs:65`

---

## ADR-013：SlotReader 统一处理 JsonElement 槽位

- **状态**：✅ 已采纳
- **日期**：2026-09-27
- **背景**

  `AgentRequest.Slots` 的类型是 `IReadOnlyDictionary<string, object?>`（`SDK-Agent.cs:39-40`），
  它来自 Minimal API 对请求体 JSON 的反序列化。`System.Text.Json` 在反序列化成 `object` 时，
  **不会**产出 `string` / `int`，而是产出 `System.Text.Json.JsonElement`。

  后果有两层：

  1. 插件 Agent 里写 `(string)request.Slots["to_account"]` 会抛 `InvalidCastException`。
  2. 更隐蔽的是 **`JsonElement` 生命周期问题**：如果它来自一个已被 `Dispose()` 的 `JsonDocument`，
     访问 `GetString()` / `GetDecimal()` 会抛 `ObjectDisposedException`。
     Minimal API 在某些配置下会释放文档，此时请求会直接 500。

  如果每个插件各自写一遍转换逻辑，5 个插件就是 5 份不一致的实现，且新人极易写错。

- **决策**

  在 Base 层提供 `SlotReader` 静态工具类，要求**所有插件 Agent 优先使用它，禁止自行强转**：

  | 方法 | 支持类型 | 位置 |
  |------|----------|------|
  | `String(request, key)` | `string`、`JsonElement`（String/其他 ValueKind）、`ToString()` 兜底 | `SlotReader.cs:14-25` |
  | `Decimal(request, key)` | `decimal`、`int`、`long`、`double`、数字字符串、`JsonElement` 数字 | `SlotReader.cs:45-59` |
  | `Int(request, key)` | `int`、`long`、`decimal`、字符串、`JsonElement` | `SlotReader.cs:81-94` |
  | `Bool(request, key)` | `bool`、字符串、`JsonElement` 的 True/False | `SlotReader.cs:115-126` |

  每个 `JsonElement` 分支都用 `try/catch (ObjectDisposedException)` 包住，
  文档已释放时**降级返回 `null`**，让 Agent 走"槽位缺失"路径（例如触发正则兜底解析），而不是让整个请求失败。

- **备选方案**

  | 方案 | 放弃原因 |
  |------|----------|
  | A. 在 `Program.cs` 里把 `Slots` 递归转换成原生 .NET 类型后再进 Agent | 转换逻辑要写一遍，与 `SlotReader` 等量；且把"防御性降级"责任推给调用方，反而更容易漏 |
  | B. 约定槽位值必须是字符串，所有数值也用字符串传 | 需要 Agent 到处 `decimal.Parse`，且丢失类型信息 |
  | C. 用强类型 record 定义每个场景的槽位（如 `TransferSlots`） | 意图是插件自己产出的，上游分类器尚未定型；强类型会让新增意图的成本变高 |
  | D. 在 `AgentRequest` 上加 `GetString(key)` 扩展方法 | 逻辑要访问 `Slots`，必须挂在 `AgentRequest` 上，会污染 SDK 契约程序集 |

- **影响**

  | 维度 | 内容 |
  |------|------|
  | ✅ 正面 | 三个插件的槽位读取全部统一（`TransferAgent.cs:63-66`、`BillPlugin.cs:68-70`、`CardPlugin.cs:102`）；已有 4 个单元测试专门覆盖（`SlotReaderTests.cs`） |
  | ⚖️ 代价 | 每次读槽位多一层 switch；Base 承担了一点"便利性"职责 |
  | ⚠️ 风险 | 兜底分支 `value.ToString()` 对未知类型返回 `object.ToString()` 结果（如 `System.Collections.Hashtable` 的类型名），属于**静默错误**。约定：Agent 对关键槽位必须用 `!= null` 显式判断 |

- **关联代码**：`src/src/BankingAgent.Base/Agents/SlotReader.cs`、`src/UnitTests/SlotReaderTests.cs`、`src/plugins/BankingAgent.Plugin.Transfer/TransferAgent.cs:63-66`

---

## ADR-014：AgentRouter 最长前缀优先路由

- **状态**：✅ 已采纳
- **日期**：2026-09-27
- **背景**

  `IBankingAgent.SupportedIntents` 是一个**意图前缀列表**，而实际路由键是更细的意图字符串。
  当前三个 Agent 的声明存在天然重叠：

  | Agent | 声明的前缀 |
  |-------|-----------|
  | TransferAgent | `transfer`、`transfer.execute` |
  | BillAnalysisAgent | `bill`、`bill.summary` |
  | CardManagementAgent | `card`、`card.status` |

  `transfer` 是 `transfer.execute` 的前缀。如果按"第一个命中就返回"或"最短前缀"，
  `transfer.execute` 会被笼统的 `transfer` 抢走，后续如果再有一个 `transfer.execute.retry` 场景，
  就会路由到错误的 Agent。而插件新增 Agent 时**不允许修改路由表**（[ADR-0009](../00-architecture/04-architecture-decisions.md#adr-0009)）。

- **决策**

  `Resolve` 收集所有前缀命中的 Agent，按**命中前缀的最大长度倒序**排序，取第一个：

  ```csharp
  // AgentRouter.cs:31-38
  return _agents
      .Where(a => a.SupportedIntents.Any(p => intent.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
      .OrderByDescending(a => a.SupportedIntents
          .Where(p => intent.StartsWith(p, StringComparison.OrdinalIgnoreCase))
          .Max(p => p.Length))
      .FirstOrDefault();
  ```

  配套约定：
  - 匹配使用 `StringComparison.OrdinalIgnoreCase`（意图来自 LLM，大小写不可控）
  - 无匹配时返回 `null`，`RouteAsync` 转为 `AgentResult.Fail("NO_AGENT", ...)` 而非抛异常——
    **未识别意图是正常业务结果，不是系统故障**
  - 路由表在 `AgentRouter` 构造时从 `IEnumerable<IBankingAgent>` 一次性收集（`AgentRouter.cs:17-21`），
    插件的 Agent 都是单例，集合在进程生命周期内不变

- **备选方案**

  | 方案 | 放弃原因 |
  |------|----------|
  | A. 精确匹配优先，匹配不到再退化为前缀 | 两段逻辑，且退化的兜底规则难解释；单次排序即可表达同一语义 |
  | B. 显式路由表（Dictionary）由宿主维护 | 违反"新增 Agent 不改核心代码"，插件必须改宿主才能生效 |
  | C. 最短前缀优先 | 语义反了，笼统 Agent 会抢占具体 Agent |
  | D. 引入规则引擎（Drools / NRules） | 5 人团队运维负担，且规则化路由失去编译期类型检查 |

- **影响**

  | 维度 | 内容 |
  |------|------|
  | ✅ 正面 | 意图细分零成本；`UnitTests/IdempotencyTests.cs:159-165` 有专门的 `Resolve_LongestPrefixWins` 用例 |
  | ⚖️ 代价 | 每次路由是 O(Agent 数 × 前缀数) 的字符串比较。当前 3 个 Agent，可忽略 |
  | ⚠️ 风险 | **前缀冲突无法在编译期发现**。若两个 Agent 都声明了 `transfer`，且长度相同，`OrderByDescending` 是稳定排序，结果取决于 DI 注册顺序，**不可预测**。约定：插件应声明尽可能具体的前缀；后续应引入启动期冲突检测（见第 7 节遗留项） |

- **关联代码**：`src/src/BankingAgent.Base/Agents/AgentRouter.cs:27-61`、`src/UnitTests/IdempotencyTests.cs:131-202`

---

## ADR-015：审计链 SignAndAdvance 读算写原子化

- **状态**：✅ 已采纳
- **日期**：2026-09-27
- **背景**

  审计链的核心机制是：每条记录的 HMAC-SHA256 签名输入里包含**前一条记录的签名**（`GENESIS` 表示链首），
  任何对历史记录的篡改都会导致后续所有签名对不上。

  早期实现把"计算签名"和"推进链尾"拆成了两个方法，并且在锁外先读 `_lastSignature`：
  在并发场景下，线程 A 与 B 同时读到同一个 `prev`，算出两个签名后分别写回 `_lastSignature`，
  结果是**两条记录引用同一个 `PreviousSignature`**，链从此断裂——而 `VerifyChain` 会把它报成篡改。

  这是审计系统里最危险的失败模式：不是"漏记"，而是"自己制造了一条看起来像被攻击的链"。

- **决策**

  合并为 `SignAndAdvance`，把**读链尾 → 规范化 → 算 HMAC → 写链尾**四步放进同一个 `lock (_gate)` 临界区：

  ```csharp
  // AuditLogger.cs:98-114
  public AuditEvent SignAndAdvance(AuditEvent evt)
  {
      if (!_options.EnableChainSignature) return evt;
      lock (_gate)
      {
          var previous = _lastSignature ?? "GENESIS";
          var payload = Canonicalize(evt, previous);
          var signature = ComputeHmac(payload);
          _lastSignature = signature;
          return evt with { PreviousSignature = previous == "GENESIS" ? null : previous, Signature = signature };
      }
  }
  ```

  签名输入的规范化（`Canonicalize`，`AuditLogger.cs:160-174`）刻意**只取 10 个字段**并用 `|` 分隔，
  字段顺序固定，保证同一条事件在任何机器上重算出相同的 payload。
  金额用 `ToString("F2")` 固定两位小数，避免 `1000` 与 `1000.00` 造成签名不一致。

  另外保留 `Sign(evt)` 方法（`AuditLogger.cs:117-131`）：**只算不推进**，仅供离线校验与单元测试使用，
  绝不能出现在生产写路径上。

  `VerifyChain` 使用 `CryptographicOperations.FixedTimeEquals` 做签名比对（`AuditLogger.cs:147-149`），
  避免时序侧信道。

- **备选方案**

  | 方案 | 放弃原因 |
  |------|----------|
  | A. 用数据库序列做链尾（`SELECT MAX(sig) FOR UPDATE`） | 每次审计多一次数据库往返；且进程内锁已足够，写库反而成为串行瓶颈 |
  | B. 用 `Interlocked` + 自旋 | 临界区包含 HMAC 计算（微秒级），自旋会浪费 CPU |
  | C. 不做链式签名，只做单条 HMAC | 失去"整体不可篡改"能力，无法证明中间没有插入或删除记录 |
  | D. 链尾用 `DateTime.UtcNow.Ticks` | 只能保证顺序不能保证内容绑定，仍可整条替换 |

- **影响**

  | 维度 | 内容 |
  |------|------|
  | ✅ 正面 | 链的完整性有了确定的并发语义；`AuditLoggerTests.cs` + E2E 测试 `E2ETest/Program.cs:341-342` 双重验证 |
  | ⚖️ 代价 | 所有审计写入被串行化。HMAC-SHA256 对几百字节输入耗时在微秒级，实测无压力 |
  | ⚠️ 风险 | `_lastSignature` 只存在于**进程内存**（`AuditLogger.cs:73`），进程重启后链从 `GENESIS` 重开，<br>而审计文件是追加的、不清空。结果：`VerifyChain` 跨重启会报告断链。生产化方案见第 7 节 |

- **关联代码**：`src/src/BankingAgent.Base/Security/Audit/AuditLogger.cs:93-157`、`src/UnitTests/AuditLoggerTests.cs`

---

## ADR-016：审计文件 SemaphoreSlim 串行化

- **状态**：✅ 已采纳
- **日期**：2026-09-27
- **背景**

  审计除了写内存链，还要追加一份**独立的离线证据文件** `logs/audit-chain.log`
  （`AuditOptions.FilePath`，`AuditLogger.cs:23`）。设计意图是：数据库可能被攻破，但文件在另一个介质上，
  离线校验时能把文件内容重新串成链。

  关键约束是"每一行必须是一个完整、可解析的 JSON 记录"。
  `File.AppendAllTextAsync` **不保证并发下的原子性**：多个 async 写入会各自持有文件句柄位置，
  极端情况下产生**半行交错**（torn line），导致离线校验工具无法解析该文件。

  这类失败是**静默的**：审计链路本身没有报错，但证据文件已经不可用了。

- **决策**

  用 `SemaphoreSlim(1, 1)` 把文件追加完全串行化，并规定"追加失败必须显式告警，不允许静默丢弃"：

  ```csharp
  // AuditLogger.cs:209-224
  // 文件追加本身不是线程安全的，并发写会撕裂行，审计证据不可接受
  await _fileGate.WaitAsync(ct);
  try { await File.AppendAllTextAsync(_options.FilePath, line, ct); }
  finally { _fileGate.Release(); }
  // ...
  catch (Exception ex)
  {
      logger.LogCritical(ex, "审计文件写入失败，审计链存在断裂风险: {Path}", _options.FilePath);
  }
  ```

  三个细节：

  1. **`LogCritical` 而非 `LogError`**：审计断链是需要立即介入的事件，不是普通错误。
  2. **`finally` 释放信号量**：即使写入抛异常也不会死锁后续审计。
  3. **不向上抛异常**：审计写失败不应让用户的转账请求失败（此时副作用已产生），
     但必须留下 `LogCritical`。这是"可用性优先 + 强告警"的取舍。

  此外，文件中的 `ActorId` 经 `HashActor` 处理，取 SHA-256 前 12 个十六进制字符
  （`AuditLogger.cs:228-232`），符合最小化原则：审计要能证明"谁做的"，但不需要在文件里存明文用户标识。
  E2E 测试 `E2ETest/Program.cs:336` 断言了"文件里不出现明文 `u_demo01`"。

- **备选方案**

  | 方案 | 放弃原因 |
  |------|----------|
  | A. 用 `lock` 同步块包住异步写 | 同步块内不能 `await`；强行 `.Wait()` 有死锁与线程池饥饿风险 |
  | B. 换 `FileStream` + `FileShare.Read` + 手动 flush | 仍不解决多线程同时写同一 FileStream 的定位问题，且要自己管句柄生命周期 |
  | C. 引入 Serilog 文件 Sink | 引入依赖；且 Serilog 的按行写入同样不做跨线程串行化，需要自己写同步 |
  | D. 上消息队列异步落盘 | 引入外部依赖，且"审计事件已入队"不等于"已落盘"，反而弱化了证据强度 |

- **影响**

  | 维度 | 内容 |
  |------|------|
  | ✅ 正面 | 文件行原子性有保证；E2E 测试验证了哈希化与链式字段 |
  | ⚖️ 代价 | 审计写入成为全局串行点，文件 IO 抖动会传导到请求延迟 |
  | ⚠️ 风险 | 磁盘满时只 `LogCritical` 不重试也不落库。生产化应补：写失败计数 + 告警对接 + 本地环形缓冲兜底 |

- **关联代码**：`src/src/BankingAgent.Base/Security/Audit/AuditLogger.cs:70-72`、`src/src/BankingAgent.Base/Security/Audit/AuditLogger.cs:183-232`

---

## ADR-017：JWT HS256 对称签名 + 角色由服务端决定

- **状态**：✅ 已采纳
- **日期**：2026-09-27
- **背景**

  项目早期版本的 `/api/chat` 直接信任请求体里的 `userId` 字段。
  这是一个**致命的水平越权漏洞**：任何人只要把 body 里的 `userId` 改成别人的 ID，
  就能查别人的卡、看别人的账单、甚至向别人转账。`Program.cs:83-84` 的注释记录了这个修复背景。

  修复需要同时解决两件事：
  1. **身份可信** —— 请求方声明的 userId 不能直接用。
  2. **权限可信** —— 角色不能由客户端声明，否则用户可以在登录时直接传 `role: "admin"` 自助提权。

- **决策**

  ### 决策 A：HS256 对称签名

  令牌自包含，服务端与客户端共享同一把密钥，无需 JWKS 端点，适合单体架构快速落地。

  密钥强度在**构造时**校验，长度不足 32 字节直接抛 `InvalidOperationException`
  （`TokenService.cs:27-31`）——把配置错误拦在启动期。
  使用开发默认密钥时打 Warning（`TokenService.cs:33-37`）。
  `Validate` 完整校验 issuer / audience / 签名 / 生命周期，`ClockSkew` 容忍 30 秒（`TokenService.cs:96`）。

  ### 决策 B：角色一律以服务端账号表为准

  `TokenRequest` 记录里有 `Role` 字段（`Program.cs:524`），但签发时被**完全忽略**：

  ```csharp
  // Program.cs:141-143
  // 角色一律以服务端账号表为准，绝不信任客户端传入的角色（否则可自助提权）
  var role = DemoCredentials.GetRole(req.UserId) ?? JwtRoles.User;
  var token = tokenService.IssueToken(req.UserId, role, displayName);
  ```

  同时，密码校验失败统一返回 401，不区分"用户不存在"与"密码错误"（`Program.cs:137-139`），
  避免账号枚举。

  ### 决策 C：userId 一律从令牌提取

  `/api/chat` 里 `effectiveUserId = principal.UserId`（`Program.cs:354`），
  请求体的 `userId` **只用于越权比对**（不一致且无代客权限 → 403）。
  `/api/chat/confirm` 同样比对（`Program.cs:427-432`）——**确认动作也不能代客**，
  除非持有 `CanImpersonateSupport`。

  ### 决策 D：代客操作必须留痕

  客服/审计/管理员代客时必须提供 `impersonationReason`，缺失返回 400
  （`Program.cs:361-368`），并写入一条 `user.impersonation` 审计（`Program.cs:382-392`），
  `ActorType = "STAFF"`、`ActorId` 是**客服本人**而非被代理用户。

- **备选方案**

  | 方案 | 放弃原因 |
  |------|----------|
  | A. RS256 非对称 + JWKS 端点 | 多实例或第三方接入时更合适，但当前是单实例部署；非对称的密钥分发与轮换成本在当前阶段不划算。**已记录为生产化路径** |
  | B. ASP.NET Core 内置认证（`AddAuthentication` + JwtBearer） | 需要 NuGet 包 + 认证方案配置；且项目只需要"一个 Bearer 令牌"的最小能力，自实现 60 行更可控 |
  | C. Session Cookie | 前后端分离 + 移动端场景不适用；且 CSRF 防护成本更高 |
  | D. 接受客户端传入的角色 | 自助提权，P0 级漏洞 |
  | E. 只用数据库 Session | 每个请求一次查库，且丢失 JWT 的无状态优势 |

- **影响**

  | 维度 | 内容 |
  |------|------|
  | ✅ 正面 | 越权漏洞闭环；E2E 测试覆盖了 4 个场景：无令牌 401、伪造令牌 401、越权 401/403、正常 200（`Program.cs:145-177`） |
  | ⚖️ 代价 | 令牌无主动吊销能力（用户改密码/登出后旧令牌在 30 分钟内仍有效） |
  | ⚠️ 风险 | 令牌有效期 30 分钟且无刷新机制；演示账号密码明文写在 `DemoCredentials`（`Program.cs:542-550`），<br>生产必须替换为统一身份认证，代码注释已明确标注 |

- **关联代码**：`src/src/BankingAgent.Base/Security/Auth/TokenService.cs`、`src/src/BankingAgent.Base/Security/Auth/JwtOptions.cs`、`src/src/BankingAgent.Host/Program.cs:87-117`、`src/src/BankingAgent.Host/Program.cs:127-154`

---

## ADR-018：自定义中间件鉴权而非 ASP.NET Core Authentication

- **状态**：✅ 已采纳
- **日期**：2026-09-27
- **背景**

  引入 JWT 之后，403 的返回方式上踩了一个具体的技术坑：
  **在自定义鉴权中间件下调用 `Results.Forbid()` 会抛 `InvalidOperationException`**，运行时错误信息是
  "No authenticationScheme was specified, and there was no DefaultForbidScheme found."

  根因是 ASP.NET Core 的 `AuthorizationMiddlewareResultHandler` 在处理 `ForbidResult` 时，
  需要从请求的 `IAuthenticationSchemeProvider` 里解析出一个鉴权方案；
  而本项目没有注册任何 `AuthenticationScheme`（因为用的是自定义中间件），于是解析失败抛异常。

  这不是可以绕开的"配置问题"，而是两个机制的**结构性冲突**：
  走内置认证体系就得配 scheme，不配 scheme 就不能 `Forbid()`。

- **决策**

  **完全不引入 ASP.NET Core Authentication/Authorization 体系**，改为：

  1. 在 `app.Use(...)` 里写一个显式的鉴权中间件（`Program.cs:87-117`），手工完成：
     取 `Authorization` 头 → `ITokenService.Validate` → 成功则 `CurrentUserAccessor.Enter` 并放进 `ctx.Items["Principal"]`。
  2. 免认证白名单用**路径前缀匹配**实现（`Program.cs:486-489`），只放行 `/health` 与 `/api/auth/token`。
  3. 401 直接写响应体：
     ```csharp
     // Program.cs:111-116
     ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
     await ctx.Response.WriteAsJsonAsync(new { code = "UNAUTHORIZED", message = "..." });
     ```
  4. 403 用**显式构造的 `IResult`**，绕开 `Forbid()`：
     ```csharp
     // Program.cs:478-484
     /// 返回 403。自定义中间件下 Results.Forbid() 需要 ASP.NET Core
     /// 鉴权方案配合，否则会抛 InvalidOperationException，因此显式构造响应。
     static IResult Forbidden(string message) => Results.Json(
         new { code = "FORBIDDEN", message },
         statusCode: StatusCodes.Status403Forbidden);
     ```
  5. 权限判断走 `CurrentPrincipal` 的能力属性（`CanAudit` / `CanAdminister` / `CanImpersonateSupport`），
     而非 `[Authorize(Policy=...)]` 特性或 `IAuthorizationService`。

  身份通过 `AsyncLocal` 传递给数据层（`CurrentUserAccessor`，`DbContext.cs:210-239`），
  使 `CreatedBy` / `UpdatedBy` 拿到真实操作者。

- **备选方案**

  | 方案 | 放弃原因 |
  |------|----------|
  | A. `AddAuthentication().AddJwtBearer()` + `[Authorize]` | 标准方案，但需要 NuGet 包与 scheme 配置；且本项目的权限模型是"能力布尔值"而非 Policy，Policy 反而更绕。**若未来需要 OAuth2/OIDC 外部登录，应重新评估并迁移过来** |
  | B. 只用 `AddAuthentication` 不用 `[Authorize]` | 仍然要配 scheme 才能用 `Forbid()`，复杂度与方案 A 相同却没有标准化的收益 |
  | C. 在端点里各自手工鉴权 | 10 个端点就要写 10 遍，且极易漏。集中式中间件是唯一可审计的做法 |
  | D. 保留 `Forbid()` 并 catch 异常 | 把框架异常当业务逻辑用，脆弱且难测试 |

- **影响**

  | 维度 | 内容 |
  |------|------|
  | ✅ 正面 | 零外部依赖、行为完全可控；错误响应体格式统一（`{code, message}`）；能力模型简单可测 |
  | ⚖️ 代价 | **放弃了 ASP.NET Core 生态的一整套能力**：Swagger 的 Authorize 按钮、`[Authorize]` 特性、基于 Policy 的细粒度授权、Blazor/Identity 的集成 |
  | ⚠️ 风险 | 白名单用 `StartsWith` 匹配（`Program.cs:487-489`），若将来新增 `/healthz/detail` 之类端点会被误放行。约定：白名单项必须是**完整端点**；后续可改为 `PathString` 精确集合 |

- **关联代码**：`src/src/BankingAgent.Host/Program.cs:82-117`、`src/src/BankingAgent.Host/Program.cs:478-499`、`src/src/BankingAgent.Base/Data/BankingDbContext.cs:200-239`

---

## ADR-019：Host 双容器装配（bootstrap 容器 + 正式容器）

- **状态**：✅ 已采纳
- **日期**：2026-09-27
- **背景**

  [ADR-0009](../00-architecture/04-architecture-decisions.md#adr-0009) 要求"新增 Agent/功能不改核心代码"。
  落地到 ASP.NET Core 有一个硬性时序约束：

  `builder.Build()` 之后容器就是**冻结**的，无法再注册新服务。
  而插件的 `IPluginEntryPoint.ConfigureServices(IServiceCollection, IPluginContext)`
  天然需要在一个 `IServiceCollection` 上执行。

  同时 `PluginRegistry` 的构造函数需要一个 `IServiceProvider` 作为 `IPluginContext.Services`
  传给插件，插件据此解析宿主能力。

  三个需求撞在一起：
  1. 插件要往主容器注册服务 → 必须在 `Build()` 之前
  2. `PluginRegistry` 要一个 `IServiceProvider` → 只能来自已构建的容器
  3. 插件的 Agent 必须是**主容器**的单例（`AgentRouter` 从根容器收集 `IEnumerable<IBankingAgent>`）

- **决策**

  **提前构建一个临时容器（bootstrap）供插件加载期使用，插件服务再合并回主容器，正式 Build 后立刻释放临时容器**：

  ```csharp
  // Program.cs:33-62
  var bootstrap = builder.Services.BuildServiceProvider();          // 临时容器
  var registry = new PluginRegistry(bootstrap, ...);
  var loaded = await registry.LoadFromDirectoryAsync(pluginDir);    // 插件 ConfigureServices 写入 registry.Services
  foreach (var svc in registry.Services) builder.Services.Add(svc); // 合并回主容器
  builder.Services.AddSingleton<AgentRouter>();                     // 必须在所有插件服务就位后注册
  var app = builder.Build();                                       // 正式容器
  registry.AttachProvider(app.Services);                            // 启停钩子此时才有 provider
  await bootstrap.DisposeAsync();                                   // 立即释放临时容器
  ```

  `PluginRegistry` 内部持有一个自己的 `IServiceCollection _serviceCollection`（`PluginRegistry.cs:80`），
  插件先注册到它，宿主再逐条 `Add` 进主容器——这样插件加载逻辑与宿主装配解耦，
  `PluginRegistry` 也可以脱离 ASP.NET Core 单独测试。

  依赖拓扑排序在合并之前完成（`PluginRegistry.cs:135-139`），保证依赖方在其依赖方之后启动。

- **备选方案**

  | 方案 | 放弃原因 |
  |------|----------|
  | A. 构建后用子容器承载插件服务 | `AgentRouter` 需要在主容器里解析全部 Agent；插件的 `IEntitySetContributor` 还要参与主容器的 `BankingDbContext` 构造。子容器满足不了 |
  | B. 让插件直接改 `builder.Services` | 插件加载器就绑死 ASP.NET Core，`PluginRegistry` 无法独立测试，也无法给非 Web 宿主复用 |
| C. 要求插件在编译期被宿主引用 | 退化为"每个插件改一次 Host 项目"，直接违反 [ADR-0009](../00-architecture/04-architecture-decisions.md#adr-0009) |
  | D. 用 `IServiceCollection` 的动态代理（运行时可变容器） | 引入第三方动态容器（如 `Lamar`），复杂度远超收益 |

- **影响**

  | 维度 | 内容 |
  |------|------|
  | ✅ 正面 | 插件目录即插即用，宿主不重编译；`PluginRegistry` 与 Web 框架解耦 |
  | ⚖️ 代价 | 手动 `BuildServiceProvider()` 属于"早期介入"反模式，官方文档并不推荐；本项目接受这个代价，因为它是唯一能同时满足三个约束的方案 |
  | ⚠️ 风险 1 | 临时容器若忘记 `DisposeAsync`（`Program.cs:62`）会泄漏单例（如 `SqliteConnection`）。当前代码已正确释放 |
  | ⚠️ 风险 2 | 临时容器里解析出的服务实例与正式容器里的**不是同一个对象**。当前 `PluginRegistry` 只用它当"服务查询入口"传给插件（插件不在 `ConfigureServices` 阶段解析服务），所以安全。**约定：插件不得在 `ConfigureServices` 内解析服务** |

- **关联代码**：`src/src/BankingAgent.Host/Program.cs:27-62`、`src/src/BankingAgent.Base/Plugins/PluginRegistry.cs:72-197`

---

## ADR-020：内存事件总线（`InMemoryEventBus`），不引入 Kafka / MediatR

- **状态**：✅ 已采纳
- **日期**：2026-09-27
- **背景**

  上游 [ADR-0003](../00-architecture/04-architecture-decisions.md#adr-0003事件总线进程内-mediatr--未来-kafka) 已经定了
  "进程内 → 未来 Kafka"的方向，本文只决定**进程内具体用什么**。

  候选：
  - **MediatR**：最流行的 .NET 进程内 mediator/通知机制
  - **自研 `InMemoryEventBus`**：约 87 行，直接实现 SDK 里的 `IEventPublisher`
  - **Channel<T> + BackgroundService**：.NET 原生队列
  - **MassTransit / NServiceBus**：重量级消息框架

  需求很具体：
  1. 发布者不知道订阅者，订阅者不必知道发布者
  2. 支持按事件类型订阅，**也要支持通配订阅 `*`**
  3. 处理器异常不能影响发布者，必须进**死信队列**且可查询
  4. 需要保留最近 N 条事件历史，供演示与调试端点展示
  5. 事件契约必须在 **SDK 程序集**里，插件只依赖 SDK

- **决策**

  自研 `InMemoryEventBus : IEventPublisher`，理由逐条对应需求：

  | 需求 | 实现 |
  |------|------|
  | 解耦 | 订阅索引 `ConcurrentDictionary<string, List<IDomainEventHandler>>`（`EventBus.cs:14`） |
  | 精确 + 通配 | 发布时同时查 `evt.EventType` 与 `"*"` 两个键（`EventBus.cs:64-71`） |
  | 死信 | 单个处理器异常被 try/catch 捕获，事件入 `ConcurrentQueue<DomainEvent> _deadLetters`（`EventBus.cs:79-84`） |
  | 历史 | `_history` 保留最近 500 条，超出丢最旧（`EventBus.cs:57`） |
  | 计数 | `Interlocked.Increment` 保证并发下不丢计数（`EventBus.cs:53`） |
  | 可观测 | `Program.cs:307-319` 暴露 `GET /api/plugins/events` |

  **不用 MediatR 的原因**（具体，不是泛泛而谈）：

  1. **MediatR 的 `INotificationHandler<T>` 是泛型强绑定的**，而本项目的 `DomainEvent` 是**单个信封类型**
     携带 `EventType` 字符串（`EventContracts.cs:7-17`）。用 MediatR 就得为每种事件定义一个类型，
     或者加一层 marker 泛型包装——都比重写订阅索引更麻烦。
  2. MediatR v12 起对 `IPublisher` 的许可与版本策略有变化，5 人团队维护一个 MIT 许可切换的依赖不划算。
  3. 死信队列 MediatR 不提供，需要自己加 `IPipelineBehavior`。
  4. 87 行代码，换来 100% 可控的语义，这笔账很划算。

  **迁移路径（必须写死，避免将来大改）**：

  | 步骤 | 动作 | 影响面 |
  |------|------|------|
  | 1 | 新增 `KafkaEventPublisher : IEventPublisher`，实现同样的 `PublishAsync` | **插件零改动**（它们只依赖 `IEventPublisher`，`EventContracts.cs:28-31`） |
  | 2 | 在 `AddBankingCore` 里根据配置选择实现 | 只改 `DIExtensions.cs:43-49` 一处 |
  | 3 | 事件契约 `DomainEvent` 已与 CloudEvents 字段命名对齐（`EventId`/`Source`/`OccurredAt`/`CorrelationId`） | 可直接映射到 Kafka message headers，无需改契约 |
  | 4 | 处理器侧引入 outbox，保证"落库"与"发消息"的原子性 | ⚠️ **当前实现缺少 outbox**：`TransferAgent` 先 `SaveChangesAsync` 再 `PublishAsync`（`TransferAgent.cs:209` → `:212`），进程崩溃会丢事件。这是迁移到 Kafka 之前**必须先补**的 |

- **备选方案**

  | 方案 | 放弃原因 |
  |------|----------|
  | A. MediatR | 见上，泛型强绑定与本项目的信封式事件模型不匹配 |
  | B. `Channel<T>` + `BackgroundService` 异步消费 | 引入"发布成功 ≠ 处理成功"的语义模糊；且失去 `/api/plugins/events` 的即时可观测性 |
  | C. MassTransit | 依赖体积大，为一个只有 1 个订阅者的场景引入完整消息框架不划算 |
  | D. 直接上 Kafka | 单实例部署时引入分布式中间件是纯成本；[ADR-0001](../00-architecture/04-architecture-decisions.md#adr-0001) 已定模块化单体 |
  | E. 插件间直接方法调用 | 直接违反 [ADR-0002](../00-architecture/04-architecture-decisions.md#adr-0002) 的"事件通信强制"约束 |

- **影响**

  | 维度 | 内容 |
  |------|------|
  | ✅ 正面 | 零依赖；插件间零耦合（账单插件订阅转账插件的事件，两个插件互不引用，见 `BillPlugin.cs:120`）；死信与历史可观测 |
  | ⚖️ 代价 | 事件**不跨进程**、**不持久化**；进程重启丢失历史 |
  | ⚠️ 风险 1 | 发布是 `await` 同步串行的（`EventBus.cs:77`），慢处理器会拖慢转账请求的响应时间 |
  | ⚠️ 风险 2 | 合规规则短路导致 `AmlThresholdRule` 在大额场景下**不会被执行**（`TransferAmountRule` 先返回，见 `ComplianceGuard.cs:77-85`）。当前 `RiskScore` 仍写审计，合规视角可接受；若未来要求"所有规则都跑一遍再汇总"，需要把 `Evaluate` 改为全量收集模式 |

- **关联代码**：`src/src/BankingAgent.Base/Events/InMemoryEventBus.cs`、`src/src/BankingAgent.Plugin.Sdk/EventContracts.cs`、`src/src/BankingAgent.Base/BankingCoreServiceCollectionExtensions.cs:44-50`、`src/plugins/BankingAgent.Plugin.BillAnalysis/BillAnalysisPlugin.cs:112-133`

---

## ADR-021：幂等键复用 SessionId

- **状态**：⚠️ 已采纳（存在已知缺陷，改造方向见下）
- **日期**：2026-09-27
- **背景**

  资金操作的第一底线是：**重复请求不得重复扣款**。

  当前系统的幂等实现在 `TransferAgent`：

  ```csharp
  // TransferAgent.cs:140-163
  var idempotencyKey = request.SessionId ?? Guid.NewGuid().ToString("N");
  await using var db = await _dbFactory.CreateDbContextAsync(ct);
  var existing = await db.Set<TransferRecord>().FirstOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, ct);
  if (existing is not null) { /* 返回原结果，不重复扣款 */ }
  ```

  配套还有数据库唯一索引 `entity.HasIndex(e => e.IdempotencyKey).IsUnique()`（`TransferPlugin.cs:83`）。

  问题在于：**`sessionId` 是什么？** 它是客户端在 `/api/chat` 里自带的会话标识
  （`ChatRequest.SessionId`，`Program.cs:520`），客户端可以传、可以复用、可以随便填。
  用它当幂等键是三个候选里最省事但最不安全的一个。

- **决策**

  选用 `SessionId` 作为幂等键，理由：

  | 候选 | 取舍 |
  |------|------|
  | A. `SessionId` ✅ | 客户端无需改造；人工确认链路天然复用（`/api/chat/confirm` 传同一个 `sessionId`，`Program.cs:456`）。代价是语义错位——一个会话可以有 N 笔转账，但只有一个键 |
  | B. `Idempotency-Key` HTTP 头（业界标准做法） | 需要客户端配合改造；且 `/api/chat` 是自然语言入口，"这次请求"与"这次会话"边界模糊，前端要额外维护键的生命周期 |
  | C. 服务端按 `userId + 金额 + 收款方 + 时间窗` 哈希 | 无需客户端传参，但**极其危险**：用户合法地转两笔相同金额给同一人会被误判为重复，静默丢单 |

  选定 A 之后补三条约束：

  1. **`sessionId` 为空时降级为随机 GUID**（`TransferAgent.cs:141`），保证键永远非空。
  2. **人工确认必须复用原 `sessionId`**，`/api/chat/confirm` 的 `SessionId` 是必填语义字段。
  3. **命中重复时返回 `idempotent_replay = true` 并携带原 `transaction_no`**（`TransferAgent.cs:156-161`），
     而不是报错——因为对客户端而言这是成功。

- **备选方案**

  | 方案 | 放弃原因 |
  |------|----------|
  | A. 强制 `Idempotency-Key` 头 | 演示与 E2E 都要改；自然语言入口下"请求边界"确实模糊 |
  | B. 服务端内容哈希 | 会静默吞掉合法重复转账，不可接受 |
  | C. 只依赖 CBS 幂等 | MockBank 目前不支持；且不能防御"第一次请求超时但实际成功"的场景 |
  | D. 分布式锁（Redis） | 单实例部署引入 Redis 不划算；锁只能防并发，不能防"超时重试" |

- **影响与已知缺陷（必须记录）**

  | # | 缺陷 | 位置 | 严重度 |
  |---|------|------|:------:|
  | 1 | **同一会话内的两笔不同转账会被误判为重复**。第二笔不执行，且返回"该转账已处理过，流水号 XXX"——用户看到的是**错误的成功信息** | `TransferAgent.cs:144-163` | 🔴 高 |
  | 2 | **并发下可能重复扣款**。检查（`:144`）与写入（`:208`）之间无事务无锁，中间还调了核心系统（`:166`）。两个同 `sessionId` 的并发请求可能都通过检查 | `TransferAgent.cs:144` / `:166` / `:208` | 🔴 高 |
  | 3 | **唯一索引拦不住缺陷 2**。唯一索引只保证不会出现两条相同键的记录；但两次扣款发生在索引冲突之前 | `TransferPlugin.cs:83` | 🔴 高 |
  | 4 | **`CoreBankClient` 没把 `IdempotencyKey` 发给核心系统**，CBS 侧无第二道防线 | `CoreBankClient.cs:63-71` | 🟠 中 |
  | 5 | `UnitOfWork.TryRegisterIdempotencyKey` 注册为 Scoped，其字典生命周期只有单请求，实际无防护作用 | `DIExtensions.cs:115`、`UnitOfWork.cs:13` | 🟢 低（冗余代码） |

  **已验证的部分**：E2E 测试 `E2ETest/Program.cs:305` 断言"重复请求后余额未重复扣款"——
  串行重复场景是**通过**的。风险集中在并发与同会话多笔。

  **改造方向（分三步，建议按序推进）**：

  1. **立即（不改契约）**：把幂等键从 `SessionId` 改为
     `SHA256(userId + "|" + sessionId + "|" + hash(槽位金额与收款方))`。
     同一会话的**不同**转账得到不同键（修缺陷 1），同一笔转账的**重试**（槽位相同）仍得到相同键。
  2. **短期**：把 `IdempotencyKey` 加进 `CoreBankClient` 的 POST 请求体（修缺陷 4），
     MockBank 侧增加同键去重，形成两道防线。
  3. **中期**：引入**持久化状态机**——`TransferRecord.Status` 落库全部中间态
     （`PendingApproval` / `Confirmed` / `Executing`），
     插入时用 `INSERT ... ON CONFLICT DO NOTHING` 抢占，天然解决并发双写（修缺陷 2、3）。
     这一步同时会让 [转账生命周期状态图](01-uml-diagrams.md#10-状态图转账生命周期) 变得名副其实。

- **关联代码**：`src/plugins/BankingAgent.Plugin.Transfer/TransferAgent.cs:140-163`、`src/plugins/BankingAgent.Plugin.Transfer/TransferPlugin.cs:83`、`src/src/BankingAgent.Base/CoreBank/CoreBankClient.cs:59-108`、`src/src/BankingAgent.Base/Data/UnitOfWork.cs:43-48`、`src/UnitTests/IdempotencyTests.cs`

---

## 7. 遗留项与后续决策入口

以下问题在实现中被发现但**本次未决策**，留给后续 ADR：

| # | 遗留项 | 建议 ADR 方向 | 优先级 |
|---|--------|---------------|:------:|
| 1 | 插件启动钩子永不触发（`PluginLoadResult.IsActive` 默认 `true`） | 在 `LoadAsync` 中显式设 `IsActive = false`，并补一个"插件启动顺序"的启动测试 | 高 |
| 2 | `TransferRecord.Status` 只落 `Completed`，中间态全丢 | 持久化状态机（同 ADR-021 改造方向第 3 步） | 高 |
| 3 | 软删除未落地（无全局查询过滤器，`ApplyAuditFields` 不处理 `Deleted`） | 定义 `SaveChanges` 中 `Deleted → IsDeleted = true` 的转换 + 全局过滤器 | 中 |
| 4 | `RowVersion` 无值生成器，乐观并发实际不生效 | 改用 PostgreSQL 的 `xmin` 或应用层生成 `Guid` 版本号 | 中 |
| 5 | `OnModelCreating` 中 `HasDefaultSchema` 被循环覆盖，PostgreSQL 多插件不会独立 Schema | 每个贡献器用 `modelBuilder.HasDefaultSchema` + `entity.ToTable(name, schema)` 组合，或改用表前缀 | 中 |
| 6 | 审计链跨进程重启断裂（`_lastSignature` 只在内存） | 启动时从审计存储读取最后一条签名作为链尾；或改用分段链 + 段间锚定 | 中 |
| 7 | 意图前缀冲突无启动期检测 | 在 `PluginRegistry` 启动时对全部 `SupportedIntents` 做两两冲突检查并告警 | 中 |
| 8 | `ComplianceOptions` 两个阈值无规则消费（单日限额、频率风控） | 补 `DailyLimitRule` / `HighFrequencyRule`，数据源需接 `TransferRecord` | 中 |
| 9 | `FeatureFlags` 只声明不强制（[ADR-0010](../00-architecture/04-architecture-decisions.md#adr-0010) 仍"评审中"） | 待 ADR-0010 结论落地后补 | 待定 |
| 10 | 事件无 outbox，进程崩溃会丢事件 | 引入 `OutboxMessage` 实体 + 后台投递器 | 中 |

---

## 8. 关联文档

- 图集（含时序图、状态图、架构不一致清单）：[01-uml-diagrams.md](01-uml-diagrams.md)
- 接口契约：[03-api-contract.md](03-api-contract.md)
- 数据模型：[04-data-model.md](04-data-model.md)
- 顶层 ADR-001 ~ ADR-010：[../00-architecture/04-architecture-decisions.md](../00-architecture/04-architecture-decisions.md)
- 事件驱动契约：[../00-architecture/07-event-driven-contract.md](../00-architecture/07-event-driven-contract.md)
- 合规矩阵：[../05-security-compliance/02-compliance-matrix.md](../05-security-compliance/02-compliance-matrix.md)
- 审计日志：[../05-security-compliance/04-audit-logging.md](../05-security-compliance/04-audit-logging.md)
