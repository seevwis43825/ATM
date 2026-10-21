# 第四轮独立复核报告（只读审查）

- **审查对象**：`G:\cunchu\大学\poject\ATM`（.NET 8 AI 银行 Agent）
- **审查基线**：`6215aba`（含本轮 6 笔提交 + 上游在审查期间追加的 `d4b778f` / `3274f92` / `6215aba`）
- **上一轮报告**：`artifacts/doc-verify3/report.md`（已入库，作为上下文）
- **角色**：Verifier（只读；唯一写入为本报告。变异测试改动均已 `git checkout --` 还原，收尾时 `git status --porcelain` 为空、`git diff HEAD` 为空）
- **端口纪律**：只用 MockBank `5210` / 宿主 `5250`；启动前确认端口空闲；只停止自己启动的进程（监听端口 PID + 记录的 `dotnet run` 父 PID）
- **审查窗口**：2026-10-21 07:35 ~ 09:00 (UTC+8)

---

## 0. 结论

**VERDICT: PASS_WITH_ISSUES**

一句话结论：**P0（审计链空密钥）已真正堵死** —— 空串在任何非开发环境下都会让宿主拒绝启动、开发环境下会换成公开的派生密钥，且离线重算证明归档不再由空密钥签名；但**新增的并发回归闸门 `MockBankResetTests` 在它声称保护的那条不变量上有假阳性**（我用独立变异证明：把 `ResetAll` 的加锁顺序改成目录原始顺序后 5 项测试全绿，而该改动在真实 HTTP 并发下会造成死锁），另有 5 处文档漂移（含新提交 `6215aba` 里一条**根本跑不通**的排障命令）。

| 严重度 | 条数 | 说明 |
|--------|------|------|
| 🔴 P0 | **0** | 上一轮的 P0 已确认修复，无残留 |
| 🟠 P1 | **0** | 无阻断性问题 |
| 🟡 P2 | **6** | 闸门假阳性 ×1、环境判定分裂 ×1、`IsProduction` 只认字面 Production ×1、单测数残留 200 ×1、6215aba 的无效命令 ×1、容量报告残留过期指令 ×1 |
| 🟢 P3 | **4** | 顺序性/冗余/可维护性建议 ×4 |

---

## 1. A 组：P0 修复（`c404043`）是否真的把洞堵住了？

### A-01 ✅ 已确认修好 —— 空密钥无法再流到 `AuditLogger`

**结论**：穷举所有装配路径后，**不存在**能让 `AuditOptions.SigningKey` 为空串而流到 `AuditLogger` 的路径。

| 可能的绕过路径 | 结论 | 证据 |
|----------------|------|------|
| `Configure<AuditOptions>` 绑定（`BankingCoreServiceCollectionExtensions.cs:156`） | ✅ 绑定在前，策略在后 | 策略 `Configure` 注册在 `:204`，`IOptions` 物化时按注册顺序执行 |
| 其它 `Configure<AuditOptions>` / `PostConfigure<AuditOptions>` | ✅ 全仓 0 处 | `grep 'Configure<AuditOptions>\|PostConfigure<AuditOptions>'` 仅命中 `:156` 与 `:204` |
| `AuditOptions` 默认值 | ✅ 默认即 `DevelopmentDefaultKey`，非空 | `AuditLogger.cs:26` |
| 直接 `new AuditOptions()` | ✅ 仅测试/压测用，且显式给键 | `StressTest/Program.cs:102` 用 `SigningKey="stress-test-key"`；`AuditLoggerTests.cs:22`、`AuditPersistenceTests.cs:93/138` 同理 |
| 反序列化 | ✅ 不适用 | `AuditOptions` 无 `JsonConstructor`、无 `System.Text.Json` 反序列化入口 |
| 其它 DI 注册覆盖 `IAuditLogger` | ✅ 全仓仅 1 处注册 | `grep 'AddSingleton<IAuditLogger'` → `BankingCoreServiceCollectionExtensions.cs:211` |
| `IOptionsMonitor` / `IOptionsSnapshot` | ✅ 全仓 0 处；即便有也会重跑策略 | `grep` 0 命中 |
| `AuditLogger` 构造方式 | ✅ 取 `IOptions<AuditOptions>` → 必过策略 | `AuditLogger.cs:149,153` |

**运行期实证**：
- `ASPNETCORE_ENVIRONMENT=Production` + 仅注入 `Jwt__SigningKey` / `Crypto__DataEncryptionKey`、**不设** `Audit__SigningKey` ⇒ **启动被阻止**（`SecurityGateViolationException`，违规项含「未配置 Audit:SigningKey」）。
- `ASPNETCORE_ENVIRONMENT=Staging` + 同样不设 ⇒ 安全门被 `Enabled=false` 短路（只打一条 warn），但 **Program.cs 的启动期硬校验仍然抛 `InvalidOperationException`（`AuditSigningKeyPolicy.Resolve`，AuditLogger.cs:99）⇒ 启动被阻止**。
- `ASPNETCORE_ENVIRONMENT=Development` + `Audit__SigningKey=" "`（单个空格）⇒ 启动成功并打印「未配置 Audit:SigningKey，已启用开发环境默认密钥…」，产出 1 条审计记录；离线重算：**dev 默认密钥完全一致（True）**，**空密钥不一致（False）** ⇒ 开发路径也不会再签出空密钥链。

### A-02 🟡P2 仍存在 —— DI 的环境判定与宿主的环境判定**可以分裂**（方向相反的优先级）

- **现象**：`BankingCoreServiceCollectionExtensions.cs:290-297` 的 `IsDevelopmentEnvironment` 读 `ASPNETCORE_ENVIRONMENT ?? DOTNET_ENVIRONMENT`；而 ASP.NET Core 宿主取的是 **`DOTNET_ENVIRONMENT` 优先**。两个变量都被设置且不同时，两处判定会给出**相反**的答案。
- **实测**（`%TEMP%\verify4\envcheck`，`WebApplication.CreateBuilder` 探针）：

| 场景 | `builder.Environment.EnvironmentName` | DI 策略判定 | 分裂 |
|------|--------------------------------------|-----------|------|
| 都不设 | Production | 非开发 | 否 |
| `ASPNETCORE=Development` | Development | 开发 | 否 |
| `DOTNET=Development` | Development | 开发 | 否 |
| `ASPNETCORE=Development` + `DOTNET=Production` | **Production** | **开发** | ✅ 分裂（DI 放行、Program.cs 抛） |
| `ASPNETCORE=Production` + `DOTNET=Development` | **Development** | **非开发** | ✅ 分裂（Program.cs 放行、DI 抛） |

- **安全影响**：**两个方向都是 fail-closed**，没有可利用的洞 —— S4 里 `Program.cs:76` 抛异常直接阻止启动；S5 里 DI 策略在 `AuditOptions` 物化时抛异常（宿主起不来或首个审计写入即失败）。但它会制造**难以定位的失败模式**（“Program.cs 说不是开发、DI 说不是开发，两个都说不一致”），且**单元测试完全看不到它**（测试全部靠往配置里塞 `["ASPNETCORE_ENVIRONMENT"]` 模拟环境，从不经过宿主的环境名解析）。
- **建议**：把策略改成注入宿主环境，而不是自己再解析一遍配置：
  `services.AddOptions<AuditOptions>().Configure<IHostEnvironment>((o, env) => o.SigningKey = AuditSigningKeyPolicy.Resolve(o.SigningKey, env.IsDevelopment()));`
  （`IOptions` 物化发生在容器构建之后，`IHostEnvironment` 此时可解析；这样 DI 与 `Program.cs` 必然同源，永不分裂。）

### A-03 🟢P3 —— 启动期顺序：`SecurityGate.Enforce()` 确实先于审计密钥检查，但**不构成漏洞**

- **现象**：`Program.cs:55` 的 `securityGate.Enforce()` 在 `Program.cs:76` 的 `AuditSigningKeyPolicy.Resolve(...)` **之前**。生产环境缺审计密钥时，安全门先抛 `SecurityGateViolationException` ⇒ **`Program.cs` 那道检查永远不会执行**（实测栈顶就是 `SecurityGate.cs:258`）。
- **结论**：**不是洞** —— 安全门自己的第 3 项检查（`SecurityGate.cs:164-177`）覆盖了同一个条件，生产环境照样启动失败。只有当安全门被短路时（`Enabled=false` 且非 Production，典型如 Staging），`Program.cs` 那道检查才成为唯一的守门人 —— 而它**确实拦住了**（实测 Staging 场景：安全门 warn 后 `InvalidOperationException` 阻止启动）。
- **建议**（非必须）：把 `Program.cs` 的检查挪到 `Enforce()` 之前，或直接删掉安全门里的审计密钥检查只留一处，避免“两道门顺序不同、其中一道是死代码”的理解成本。

### A-04 🟡P2 仍存在 —— `IsProduction` 只认字面 `"Production"`，Staging 类环境仍可关掉安全门

- **现象**：`SecurityGate.cs:88-94` 的 `IsProduction` 只在**恰好等于** `Production`（OrdinalIgnoreCase）时为真。随仓库入库的 `appsettings.json` 里 `SecurityGate:Enabled=false`、`SecurityGate:Environment=Development`，于是：
  - `ASPNETCORE_ENVIRONMENT=Production` ⇒ `IsProduction=true` ⇒ 安全门**不可关闭**（`:121` 的 `!IsProduction` 为假）⇒ 任务书问的「环境变量是 Production 但配置是 Development 时是否仍被短路」**答案：不会**，已实测（生产场景确实被安全门拦下）。
  - 但 `ASPNETCORE_ENVIRONMENT=Staging`（或 `Prod`、`production ` 带尾空格）⇒ `IsProduction=false` ⇒ 安全门被 `Enabled=false` **短路**，JWT 密钥、加密密钥、HTTPS、限流这些硬性检查**全部不执行**。审计密钥仍安全（由 A-01 的 `Program.cs` 检查兜住），但其它安全门能力在 Staging 环境形同虚设。
- **建议**：`IsProduction` 改为「**非 Development 即视为生产**」（`!name.Equals("Development")`），或至少把 `Staging`/`Prod` 一并纳入；并在文档里写清 Staging 的期望姿态。

### A-05 🟢P3 —— 开发默认密钥的连带影响：三处一致；但**上一轮报告里「CI 用空密钥」的说法现在已不成立**

- **一致性核对**（三处都对得上）：
  - `scripts/read-audit-chain.ps1`：`param([string]$SigningKey = '')`，**没有默认值**，HMAC 重算必须显式传键（实测传 dev 键 → 1/1 一致；不传 → 只做结构校验）。
  - `src/src/BankingAgent.Host/appsettings.Development.json.example`：`"Audit": { "SigningKey": "" }` + 注释「留空 = 未配置：Development 下沿用公开的开发默认密钥；非 Development 下启动即失败」⇒ 与 `AuditSigningKeyPolicy` 一致。
  - 本机未入库的 `appsettings.Development.json` 显式写了 `"SigningKey": "dev-only-key-change-in-production"` ⇒ 与 `AuditSigningKeyPolicy.DevelopmentDefaultKey`（`AuditLogger.cs:79`）**逐字相同** ⇒ 既有归档仍可离线重算（实测：开发链用 dev 键 1/1 一致、用空键不一致）。
- **「CI 不生成 appsettings.Development.json ⇒ CI 用开发默认密钥签名审计链」是否属实**：**属实**，且比上一轮的说法要好。`ci.yml` 集成作业只 `checkout`，不生成该文件（它被 `.gitignore` 覆盖），因此 CI 下 `Audit:SigningKey` 取仓库内 `appsettings.json` 的空串 → 走策略 → **用开发默认密钥**签名（不再是上一轮报告所说的空密钥；**上一轮那条结论现已过期/不成立**）。CI 作业环境是 `ASPNETCORE_ENVIRONMENT=Development`，符合策略预期。
- **附带发现（P3）**：`ci.yml` 的集成作业**完全没有跑审计链校验**（无 `read-audit-chain.ps1` 步骤）⇒ CI 产出的链**从未被验证过**（链结构、HMAC、GENESIS 唯一性都没有门禁）。建议加一步 `scripts\read-audit-chain.ps1 -VerifyChain -VerifyHmac -SigningKey dev-only-key-change-in-production`。

### A-06 ✅ 新增测试**基本**有效（有 1 个盲点）—— 见 §5 变异测试

- 4 项测试能抓住 2 类真实回归（变异 A/B 均如预期 FAIL）。
- **盲点 1（P2）**：`EmptySigningKey_Development_ProducesUsableLogger` 在「策略被摘掉」的变异下**仍然通过** ⇒ 说明**空密钥会造出一个完全可用的 `AuditLogger`（签名照算、全程不报错）**，底层没有任何第二道防线；能抓住这一点的只有另两条「断言键值」的用例。
- **盲点 2（P2）**：4 项测试全部用 `BuildProvider(environmentName, …)` 手工塞 `["ASPNETCORE_ENVIRONMENT"]` 进配置来模拟环境，**从不经过宿主的环境名解析** ⇒ A-02 的分裂在测试里不可见。
- **建议**：补一条以 `IHostEnvironment` 驱动的用例（并把 A-02 的修复一并钉住），以及一条「`AuditOptions.SigningKey` 在任何装配路径下都非空」的断言式用例。

---
