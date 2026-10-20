# 第三轮文档审查报告（独立复核）

- **审查对象**：`G:\cunchu\大学\poject\ATM`（.NET 8 AI 银行 Agent）
- **基线提交**：`44fb549`（`feat(resilience): 审计落库失败补偿 + 下游熔断`）
- **审查窗口**：2026-10-21 04:18 ~ 05:05 (UTC+8)
- **角色**：Verifier（只读；除本报告外未改动仓库任何文件）
- **端口纪律**：全程只用 MockBank `5210` / 宿主 `5250`；启动前确认空闲；只停止自己启动的进程（按监听端口 PID + 记录的 `dotnet run` 父 PID）

---

## 0. 结论

| 严重度 | 条数 |
|--------|------|
| 🔴 P0 | **1** |
| 🟡 P1 | **6**（含 1 条由上游本轮改动新引入） |
| 🟢 建议 | **6** |

**VERDICT: FAIL**（存在 1 条 🔴P0）

🔴P0 一句话：**随仓库入库的 `appsettings.json` 把审计 HMAC 密钥置空，而代码对审计密钥没有任何开发兜底、且同一个文件把安全门显式关闭** —— 实测审计链就是用**空密钥**签名的（任何人都能重算与伪造），而 `README.md:285` 明确声称「缺失时 Development 会派生开发密钥并告警；**Production 会拒绝启动**」，两句都是假的。

---

## 1. 重要前提：审查期间上游在并发修复（影响结论归属）

审查开始时工作区干净（HEAD=`44fb549`）。审查期间上游陆续提交了四笔改动：

```
171eeda  fix(mockbank): 修复演示数据重置端点 500 + 重置实际不生效
5d12087  docs(audit): 修正审计补偿机制的过期描述 + 修 ADR 重复引用
59549f9  docs(audit): 修正退避档位等 5 处文档与代码不一致（第三轮审查发现）
4e4393d  test(e2e): 把「跑 E2E 前手工重置演示数据」变成自动第 0A 步
```

**当前工作区已干净**（`git status --short` 仅剩本报告目录 `?? artifacts/`）。我最后一轮验证（编译 / 单测 / E2E / 压测 / 审计链 / reset 端点）跑在包含 `4e4393d` 的构建上。

因此本报告把条目分为三类，并明确标注：

- **🔴/🟡 仍存在**（当前工作区实测仍成立）→ 需要上游处理；
- **✅ 已在上游修复**（我复核过修复后的行为）→ 不计入待办；
- **🟢 建议**。

我**在最新代码上重跑了全部验证**（编译 0 警告 / 单测 200 / E2E 71-0 / 压测 31-0 / 插件契约 0-0 / 锚点 / 端点矩阵 / 审计链 184 条 HMAC 全对），结论以**当前工作区**为准。

---

## 2. 仍存在的缺陷（待办）

| 编号 | 严重度 | 文件:行 | 现象 / 文档声称 | 实际代码 / 实测 | 证据 | 建议改法 |
|------|--------|---------|------------------|-----------------|------|----------|
| D-01 | 🔴P0 | `README.md:285`；`src/src/BankingAgent.Host/appsettings.Development.json.example:13,39-41`；`src/src/BankingAgent.Host/appsettings.json:55,105-107`；`src/src/BankingAgent.Base/BankingCoreServiceCollectionExtensions.cs:156,182,209,245`；`src/src/BankingAgent.Base/Security/Hardening/SecurityGate.cs:77-79,103-107,146-155` | README：「生产必须通过环境变量注入：`Jwt__SigningKey`、**`Audit__SigningKey`**、`Crypto__DataEncryptionKey`。缺失时 Development 会派生开发密钥并告警；**Production 会拒绝启动**」；.example:13：「留空时宿主在 Development 下会派生一个确定性的开发密钥并打印警告，保证 clone 下来即可运行」 | ①审计密钥**无任何派生兜底**：`DeriveDevelopmentKey` 全仓只有两个调用点 —— `crypto:`（:184）与 `jwt-hs256`（:209），**没有审计分支**；②随仓库入库的 `appsettings.json:55` 是 `"SigningKey": ""`，`Configure<AuditOptions>(Section("Audit"))`（:156）绑定成空串，覆盖 `AuditOptions.SigningKey` 的默认值；③同一文件 `appsettings.json:106` 把安全门设为 `"Enabled": false`，`SecurityGate.Validate()` 在 :103-107 直接返回 `Passed=true`（只打一条 warn），**生产也不拦**；④`IsProduction`（:77-79）只看 `SecurityGate:Environment`（该文件里是 `Development`），不看 `ASPNETCORE_ENVIRONMENT` | **实测**：以 `ASPNETCORE_ENVIRONMENT=Production` 启动，仅注入 `Crypto__DataEncryptionKey` / `Jwt__SigningKey`、**不设 `Audit__SigningKey`** ⇒ 宿主**启动成功**，`/health` 200，日志「安全门已关闭（SecurityGate:Enabled=false），仅允许在开发环境使用」；随后 1 次 `/api/chat` 产生 1 条审计记录，离线重算 HMAC：<br>· 记录 sig = `88b41d8555a78813e5b76e739675575ddce09753dce2e0e26bb769b16c03619a`<br>· **空密钥重算 = 完全一致（匹配=True）**<br>· dev-only 密钥重算 = `19e9c4143ac9a4ea1df709fdfe0cc0ae1f58df5e3cdd8f10fd7a03c83dfd4992`（匹配=False）<br>另：`HMACSHA256` 用零长度密钥在 .NET **可用**（实测可算出 `4cbc9609…`），不会抛异常<br>**CI 影响面**：`.github/workflows/ci.yml` 不生成 `appsettings.Development.json`（该文件被 gitignore）⇒ **CI 跑出来的审计链同样是空密钥** | ①给审计密钥补 Development 派生兜底（与 Crypto/Jwt 同款 `DeriveDevelopmentKey("audit-hmac")`），或至少在缺密钥时**拒绝启动**；②把"空审计密钥"作为安全门**硬性违规**（`SecurityGate.cs:146-155` 已有该分支，但被 `Enabled=false` 短路），并让安全门在 `ASPNETCORE_ENVIRONMENT=Production` 时强制生效；③修正 `README.md:285` 与 `.example:13` 两处不实表述；④同步核对 `docs/03-deploy/部署说明.md:187` |
| D-04 | 🟡P1 | `docs/05-security-compliance/04-audit-logging.md:119` | §3.1.1「JSONL 归档（权威记录）实际只有 11 个键」表中，`RuleId` 一行写「`evt.ComplianceRule ?? evt.RuleId`」 | JSONL 归档写的是 **`evt.RuleId`**（`AuditLogger.cs:390`）；`evt.ComplianceRule ?? evt.RuleId` 是**数据库**映射（`AuditLogger.cs:195`，仅 `ToEntity` 用）。真实数据里两者**不同**：`src/plugins/BankingAgent.Plugin.Transfer/TransferAgent.cs:164,167` 同时设置 `RuleId = compliance.RuleId`（如 `transfer.amount.threshold`）与 `ComplianceRule = compliance.RuleVersion`（版本号） | `AuditLogger.cs:390`（JSONL）与 `:195`（库表）取值不同；`TransferAgent.cs:164,167` 证明两字段并存且不等 | 表格改为 `RuleId` = `evt.RuleId`（JSONL）；并在"库表额外有的列"补一句：库表 `RuleId` 列实际存的是 `evt.ComplianceRule`（规则版本），与 JSONL 不同源。**这条直接影响离线重算** —— 按文档去重算会用错字段 |
| D-05 | 🟡P1 | `docs/05-security-compliance/04-audit-logging.md:312,563` | §4.6.1 表：「**HMAC 签名值重算** ✅ 能（归档自身包含重算所需的全部输入）」；§11 清单：「- [x] HMAC 签名值可离线重算」 | 只对**新算法写入的归档**成立。提交 `0d00d27` 把签名输入的操作者由 `evt.ActorId` 改为 `HashActor(evt.ActorId)`（`git show 0d00d27 -- .../AuditLogger.cs` 明确显示 `- .Append(evt.ActorId)` / `+ .Append(HashActor(evt.ActorId))`）。**变更前写入的历史归档**用旧算法签名，按现行算法重算必然全不一致，文档对此一字未提 | `git --no-pager show 0d00d27 -- src/src/BankingAgent.Base/Security/Audit/AuditLogger.cs` 的 Canonicalize diff | 在 §4.6.1 增加边界行：「⚠️ 本能力只对 2026-10-21（提交 `0d00d27`）之后写入的归档成立；此前归档的签名由旧算法（操作者为原始 ID）生成，重算会不一致 —— 升级时需重签或按旧算法留存归档」 |
| D-06 | 🟡P1 | `README.md:101`；`docs/plugin/03-implementation-status.md:340,368` | 「端到端场景测试（**17 个场景**）」「端到端（**17 个场景** / 70 断言）」「端到端场景覆盖（`E2ETest` 项目，**17 个场景**）」 | `src/E2ETest/Program.cs` 现有 **21 个 `Section(`**：场景 0（环境连通性）+ **0A（重置演示数据，上游本轮新增）** + 0B + 场景 1~17 + "测试汇总" ⇒ **20 个场景节**。文档自己的表格（03-implementation-status.md:372-384）列到 0B 与 17，也**没有 0A** | 静态统计：`Section(` = 21、`Check(` = 82；运行期输出：`通过: 71  失败: 0` | 统一改为「**20 个场景（0 / 0A / 0B / 1~17）**」，并在场景表补 **0A 行**（重置演示数据 → 回归护栏）。断言数见 D-13 |
| D-10 | 🟡P1 | `README.md:97`；`src/mock-bank/MockBank.Api/appsettings.json:35`；`src/mock-bank/MockBank.Api/Data/BankDbContext.cs:4` | README 目录树：「`mock-bank/MockBank.Api/` └ `data/` 各行独立数据库文件（运行后生成）」；MockBank 配置注释：「或删除 **data/** 下的 .db 文件」；代码注释：「数据库文件落在 MockBank.Api/**data/** 目录下」 | 实际目录是 **`App_Data/`**（`MockBankOptions.cs:20` 默认 `"App_Data"`；`mock-bank/MockBank.Api/appsettings.json:31` `"DataDirectory": "App_Data"`）。而 `docs/13-database/02-multi-bank-databases.md:200` 专门解释了**为什么不能叫 data**（Windows 大小写不敏感会命中源码目录 `Data/`）—— 三处 `data/` 与该结论直接冲突 | 实测库文件位于 `src/mock-bank/MockBank.Api/App_Data/mockbank_{icbc,ccb,cmb}.db`（另有 `.db-wal` / `.db-shm`）；`docs/13-database/02:200` 明写"默认 `App_Data`" | 三处 `data/` 统一改为 `App_Data/`。按 README 去删 `data/` 的人会找不到文件 |
| D-11 | 🟡P1 | `docs/plugin/03-implementation-status.md:290` | 「UML 10 张图 + **ADR 11 条** + API 契约 + 数据模型」 | ADR 登记册范围是 **ADR-011 ~ ADR-029**（`docs/09-uml/02-architecture-decision-record.md:1` 标题 + `:16-36` 的 19 行索引表）⇒ **19 条**（连上游 ADR-001~010 共 29 条）。"11 条"是 ADR-022~029 加入**之前**的旧数 | `02-architecture-decision-record.md:1`「实现阶段新增 ADR-011 ~ ADR-029」；索引表逐条列出 ADR-011…ADR-029（ADR-021 标注被 ADR-024 取代） | 改为「ADR 19 条（ADR-011~029，含上游共 29 条）」 |
| D-13 | 🟡P1 | `README.md:7`；`docs/plugin/03-implementation-status.md:340,472`；`docs/plugin/02-api-reference.md:6`；`docs/plugin/00-quick-start.md:4` | 上述 5 处仍写「E2E **70 断言**」并附「**E2E 需干净的 MockBank 库**，见 `docs/03-development/04-testing-strategy.md`」 | 上游新增 **0A 步（E2E 自动 `POST /_admin/reset`，失败即终止）** 后，断言数由 70 变 **71**，且**不再需要手工准备干净库**。实测：`通过: 71  失败: 0`（连跑两次均 71/0）。`docs/03-development/04-testing-strategy.md:43,57-58` **已经**改成「E2E 会自动重置 MockBank 演示数据（脚本第 0A 步），无需手工准备」，但上述 5 处**未同步** | 运行期输出 `通过: 71    失败: 0`（新构建，连续两次）；`03-development/04-testing-strategy.md:43` 与 `:57-58` 已更新为新口径 | 5 处统一改为「E2E **71 断言**（20 个场景，含自动重置的 0A 步）」并删掉"需干净的 MockBank 库"的免责 —— 该免责现在既过时又会误导（以为必须手工重置）。⚠️ 这条是**上游本轮改动新引入的漂移**，改 0A 步时应同步计数 |

---

## 3. 已在上游修复（我已复验，不计入待办）

| 编号 | 原严重度 | 文件:行（修复后） | 修复内容 | 我的复验结果 |
|------|----------|-----------------|----------|--------------|
| D-02 | 🟡P1 | `docs/05-security-compliance/04-audit-logging.md:199`；`docs/11-performance/01-capacity-test-report.md:42,264`；`docs/plugin/03-implementation-status.md:305`；`docs/09-uml/02-architecture-decision-record.md:425`；`src/src/BankingAgent.Base/Security/Audit/AuditDbWriter.cs:42,46,200` | 退避表述统一为「**含首次共 3 次尝试，即最多 2 次重试，退避 200ms→800ms，最坏等待约 1.0s**」；代码注释还明写「第三档 3200ms 永远不会执行（第三次失败即写死信，不再延迟）」 | ✅ 与代码一致：`AuditDbWriter.cs:215` 的 `catch … when (attempt < MaxCommitAttempts)` 只在 attempt=1、2 进入（→200/800ms），attempt=3 落到 :230 兜底直接写死信、不再延迟；单测 `AuditBatchedWriterTests.cs:238` `Assert.Equal(MaxCommitAttempts, repo.Attempts)` ⇒ Attempts=3 |
| D-03 | 🟡P1 | `docs/05-security-compliance/04-audit-logging.md:339,361-373` | §5.1 样例已保留 `_fileGate` 临界区并改用真实方法名 `AppendToFileCoreAsync`，且注明「裁剪的是字段与辅助方法；**`_fileGate` 临界区与那条日志行被刻意保留**」 | ✅ 与 `AuditLogger.cs:125-159` 一致 |
| D-07 | 🟡P1 | `docs/plugin/03-implementation-status.md:396` | 区间已与容量报告对齐为「逐条 142~358 条/秒、批量 2,989~6,092 条/秒，**快 17.0~25.7 倍**（7 轮）」 | ✅ 我按容量报告 §3.2 七行原始数据复算倍数 17.0/17.7/18.2/20.8/21.0/21.7/25.7，区间吻合 |
| D-08 | 🟡P1 | `docs/11-performance/01-capacity-test-report.md:275` | 原「峰值日处理（15% 峰值系数）1,539,648」已改为「**峰值时段承载量**（峰值系数 15% 时长 = 12,960s；118.8 QPS × 12,960s）= 1,539,648」 | ✅ 复算 118.8×12,960 = 1,539,648，自洽 |
| D-09 | 🟡P1 | `docs/11-performance/01-capacity-test-report.md:112-114,116,125-132` | §2 注改为「这张表**不是「常驻句柄」（§1.2）改造后的数字**」；§2.1 标题改为「两轮实测的量级对比」，并新增 :128-132 明说「本文不把这两轮的 3~6 倍归因于任何一项具体改造……两轮都在 §1.2 改造之前」 | ✅ 自相矛盾已消除（原先 :112 写「优化前（§1.1 之前）」而 :118 把同一次 20:49 标为「优化后」） |
| D-12 | 🟡P1 | `src/mock-bank/MockBank.Api/Data/BankLedgerFactory.cs`（`171eeda`）；`src/mock-bank/MockBank.Api/Data/BankLedger.cs`（新增 `ClearSnapshot()`） | `POST /_admin/reset` 原 500（`BankLedgerFactory.cs:73` 对本进程仍打开的 SQLite 文件直接 `File.Delete`，Microsoft.Data.Sqlite 默认连接池使 OS 层句柄仍在）已修复：原地重置 + `SqliteConnection.ClearPool` 释放句柄 + 按行代码字典序取写锁（与跨行转账同序，避免死锁）+ `ClearSnapshot()`/`LoadSnapshot()` | ✅ **直接 `POST http://localhost:5210/api/corebank/v1/_admin/reset` ⇒ 200** `{"resetAt":"…","message":"已重置全部银行数据库并重新写入种子数据。"}`；**连跑两次 E2E 均 71 通过 / 0 失败**（证明重置真的把余额还原，不只是返回 200）；新构建 0 警告 0 错误、单测 200 通过 |

---

## 4. 🟢 建议（非缺陷，建议改）

| 编号 | 文件:行 | 说明 | 建议 |
|------|---------|------|------|
| G-01 | `docs/05-security-compliance/04-audit-logging.md:10,177-178`；`docs/plugin/03-implementation-status.md:302,412` | 引用「312 → 989 QPS（3.17×）」单点端到端数字，而容量报告 §4.1:195-204 明确说本机 `/health` 定点测速离散 **4.3×**、`/api/chat` **2.2×**，并写「❌ 不要用单次端到端 QPS 作为容量结论」 | 引用处补一句「本机端到端数字离散 2~4.3 倍，此处仅作**相对**改进的 A/B 证据」（句柄 A/B 是 3 轮交错做的，引用其**比值**站得住） |
| G-02 | `docs/05-security-compliance/04-audit-logging.md:10` | §0「Current（源码可验证）」摘要仍写「文件或数据库写失败均记录 Critical」，未提**有界重试 + 死信文件**（§4.1 已补齐，摘要没跟上） | 摘要补一句「落库失败先有界重试（3 次尝试），仍失败写死信文件」 |
| G-03 | `docs/05-security-compliance/04-audit-logging.md:533-537` | §9 法律取证步骤编号跳号：1,2,3,**5**,6（缺 4） | 补回第 4 步或重排编号 |
| G-04 | `docs/09-uml/02-architecture-decision-record.md:8` | 「日期：2026-09-27（**ADR-001~021**）」—— 本文范围是 ADR-011~029，`001~` 应为 `011~` | 改为 ADR-011~021 |
| G-05 | `docs/04-operations/04-disaster-recovery.md:191-195` | §8.2（Target）给的熔断方案是「Polly，触发条件：错误率 > 50% 且样本 > 20」，而仓库**已实现**的是自研 `CoreBankCircuitBreaker`（连续 5 次**传输层**失败，**5xx 不计入**）。运维照 Target 章节排障会用错阈值 | 在 §8.2 标注「已实现的是自研传输层熔断器（`CoreBankCircuitHandler`，连续 5 次传输层失败，5xx 不计入），与本节 Target 的错误率判据不同」 |
| G-06 | `docs/05-security-compliance/04-audit-logging.md:443,448` | §5.3（Target 示意）用 `_audit.LogAsync(...)` —— 恰是 §5.1 对照表（:391）点名的**旧方法名** | 示意代码改用 `WriteAsync`，避免读者误以为当前接口叫 `LogAsync` |

---## 5. 已核对通过、未发现问题的断言（避免只报问题不报通过）

全部在**当前工作区 + 新构建**上重跑：

| 断言 | 验证方式 | 结果 |
|------|----------|------|
| 单元测试 **200 项**全绿 | `dotnet test src/UnitTests/UnitTests.csproj -c Release --nologo -v minimal` | ✅ **失败 0 / 通过 200 / 共 200** |
| 端到端（文档写 70，实测 **71**） | `dotnet run --project src/E2ETest -c Release --no-build -- http://localhost:5250 http://localhost:5210` | ✅ **通过 71 / 失败 0**（连跑两次均 71/0；含新增 0A 自动重置步）⇒ 断言数与文档差 1，见 D-13 |
| 压力测试 **31 项** | `dotnet run --project src/StressTest -c Release --no-build -- http://localhost:5250 http://localhost:5210` | ✅ **通过 31 / 失败 0**（"测试 7"实测 逐条 823.8ms/364 条每秒 → 批量 44.2ms/6789 条每秒 = **18.6×**，落在报告 17.0~25.7 区间内） |
| 插件契约 0 错 0 警 | `dotnet run --project src/PluginValidator -c Release --no-build -- src/src/BankingAgent.Host/bin/Release/net8.0/plugins` | ✅ **ERROR 0 项，WARN 0 项** |
| 编译 **0 警告** | `dotnet build src\BankingAgent.slnx -c Release /warnaserror --nologo -v minimal` | ✅ **0 个警告 / 0 个错误** |
| 文档锚点全可达 | `scripts\check-anchors.ps1` | ✅ `OK: all anchors reachable (70 files)`，exit 0 |
| 端点矩阵零缺失 | `scripts\check-endpoints.ps1` | ✅ Host 17 / MockBank 26，「代码有但文档未列」为空，exit 0 |
| 审计链 HMAC 可离线重算 | `scripts\read-audit-chain.ps1 -VerifyChain -VerifyHmac -SigningKey 'dev-only-key-change-in-production'` | ✅ **GENESIS 1 个 / 结构断点 0 / 184 条全部重算一致**，exit 0（跨 3 次宿主重启后仍成立） |
| reset 端点 | `POST http://localhost:5210/api/corebank/v1/_admin/reset` | ✅ **200**（修复前 500，见 D-12） |

---

## 6. 语义类逐条核对（任务书 §2.2）

| # | 核对项 | 结论 |
|---|--------|------|
| 1 | **常驻句柄的三个约束**是否与代码一致；文档有没有写成"无条件更好"或漏代价 | ✅ **一致，无缺陷**。代码 `AuditLogger.cs:496-498` 用 `FileShare.ReadWrite`（刻意不给 `Delete`）⇒（a）读者必须 `FileShare.ReadWrite`（读者自身的共享模式必须允许写句柄的既有写访问，否则 `FileShare.Read` 会被拒）；（b）进程运行期间文件**不能删除/改名**；（c）`KeepFileOpen=false` 可回退到 `File.AppendAllTextAsync`（:404）。文档 `04-audit-logging.md:178-183` 与 `01-capacity-test-report.md:59-62` **三条代价都写了**，也没写成"无条件更好"（还额外写了日志节流这一条） |
| 2 | **签名与落盘同一把锁** | ✅ 代码一致：`AuditLogger.cs:138-148` 在 `_fileGate` 内完成 `SignAndAdvance` + `AppendToFileCoreAsync`，finally 释放。文档 §4.6:291 散文正确、§5.1 样例曾缺失 → **已在上游修复**（D-03） |
| 3 | **链跨重启续接** + **旧归档算法变更是否诚实披露** | ✅ `EnsureChainSeeded`（:241-282）读归档最后一条 `sig` 作链尾，与文档 §4.6:287-290 一致；**实测**：宿主重启 3 次后归档 **184 条、GENESIS 仍只有 1 个、结构断点 0**。⚠️ 但文档**没有**披露"签名算法本轮改过 ⇒ 旧归档不可重算" → **D-05 仍存在** |
| 4 | **熔断判据：只算传输层失败、不算 5xx** | ✅ **一致，无缺陷**。`CoreBankCircuitHandler.IsTransportFailure`（:53-61）只认 `HttpRequestException` / `TaskCanceledException`（调用方未取消） / `IOException`；`CoreBankClient` 的 `EnsureSuccessStatusCode()`（:60,70,151,183,244,289）在**处理器之外**调用，5xx 响应不会抛进管道 ⇒ 不计入熔断。文档 `03-implementation-status.md:306`、`01-capacity-test-report.md:255` 均写"连续 5 次**传输层**失败"，**无一处**写成"5xx 也算"。单测 `CoreBankCircuitBreakerTests.cs:170-174` 专门钉住"503 不打开熔断" |
| 5 | **落库补偿：重试次数 / 退避时长 / 死信路径** | ⚠️ 死信路径与次数口径一致（`AuditOptions.DeadLetterPath` = `logs/audit-deadletter.jsonl` ✓）；**退避时长曾与代码不符**（3200ms 永不发生）→ **已在上游修复**（D-02） |
| 6 | **HMAC 签名输入与 `Canonicalize` 逐字一致**（字段顺序 / Amount F2 / GENESIS 还原） | ✅ **完全一致**。我按 `AuditLogger.Canonicalize`（:341-355）独立重算：<br>`AuditId \| ts(Unix ms) \| ActorType \| actor(哈希) \| Operation \| Scenario \| Decision \| RuleId \| Amount(F2, InvariantCulture) \| prev`<br>与 `scripts/read-audit-chain.ps1:119-122` 的拼接**逐字段一致**；链首用 `GENESIS` 还原（:116）✓；金额 `InvariantCulture` 的 `F2` ✓（归档 66 条样本中有 **19 条带金额**：800 / 30000，该分支被真实覆盖且全部重算一致）⇒ **184/184 重算一致、0 不匹配** |

---

## 7. 全库过期扫描结果

| 扫描项 | 结果 |
|--------|------|
| "只记 Critical / 不重试 / 不补偿"（落库失败） | ✅ **已清零**（HEAD 中 `01-capacity-test-report.md:42` 存在，现已改为"有界重试 + 死信"） |
| "无法重算 HMAC / 不能离线重算" | ✅ 未发现残留 |
| "178/183/187/188 项" | ✅ 未发现；全库统一 **200**（README、quick-start、api-reference、implementation-status、testing-strategy、roadmap、hardening-report、分工表一致，实测 200） |
| "296 QPS" | ✅ 仅作为**明确标注的历史基线**出现（`01-capacity-test-report.md:309,313`），并注明与本轮不构成可比 —— 属正确用法 |
| "无熔断降级" | ✅ 仅作为**已解决项的原始标签**出现在瓶颈清单（`01-capacity-test-report.md:255`，同行即写"✅ 已实现"） |
| "审计文件仍逐条 open+close" | ✅ 仅出现在"已改常驻句柄"的对照语境，无残留的现状描述 |
| ADR 范围 `ADR-011~021` | ✅ 标题已是 `ADR-011 ~ ADR-029`；残留问题是**条目数**"ADR 11 条"（D-11）与 `:8` 的 `ADR-001~021`（G-04） |
| 端口 5205/5248 的**复现命令** | ✅ 未发现。容量报告明确说明"本轮实测跑在 5248/5205，**下文复现命令统一用仓库默认端口 5243/5200**"，§3.3/§4.3 命令确为 5243/5200；CI 亦用 5200/5243 |
| "端到端 17 个场景 / 70 断言" | ⚠️ **两处都不对**：实际 **20 个场景 / 71 断言**（D-06、D-13） |
| 引用不存在的文件/类/方法 | ⚠️ `AppendToFileAsync` 曾被引用 → **已在上游修复**（D-03）。其余抽查均存在：`AuditDbWriter` ✓、`CoreBankCircuitHandler` ✓、`CoreBankCircuitBreaker` ✓、`scripts/read-audit-chain.ps1` ✓、`AuditOptions.DeadLetterPath` ✓、`AuditDbWriter.MaxCommitAttempts` ✓、`AuditDbWriter.CommitRetryBaseDelayMs` ✓、`BankLedger.LoadSnapshot()` ✓、`BankLedger.ClearSnapshot()` ✓（上游新增）、`DatabaseInitializer.EnableSqliteWriteAheadLogAsync` ✓、`EfAuditRepository.AppendBatchAsync` ✓ |
| 文档之间是否自相矛盾 | ⚠️ 发现 2 处（A/B 区间 D-07、容量报告优化前后标注 D-09）—— **均已在上游修复**。其余抽查（测试数 200 / 端到端断言 / 压力 31 / 端口 / 死信路径 / 熔断阈值 / 退避次数）跨文档一致 |

---

## 8. 文档与"现实"的一致性（容量报告专项）

| 检查 | 结论 |
|------|------|
| 报告内数字是否内部自洽 | ✅ 大部分自洽：988.8/312.0 = **3.169**（文档 3.17× ✓）；118.8×86400 = **10,264,320** ✓；198×86400 = **17,107,200** ✓；§3.2 七轮倍数我逐行复算 25.7/21.0/17.7/17.0/18.2/21.7/20.8 全部吻合；§6 峰值时段承载量 118.8×12,960 = **1,539,648** ✓。原先两处不自洽（§6 标签、§2/§2.1）**已在上游修复** |
| 是否把"某一次观察"写成"稳定结论" | ✅ 报告自身克制：§3.2 给区间+离散度、§3.3 标注"测于常驻句柄改造之前"、§2.1 标注"不是严格 A/B"、§4.1 列出 2.0×/2.2×/4.3× 离散度并写"❌ 不要用单次端到端 QPS 作容量结论"；§2.1 更新增 :128-132 明说"不把 3~6 倍归因于任何一项具体改造"。**注意**：§0 摘要与 §6 仍以 198 QPS（阶梯、常驻句柄改造前）作"安全水位 119 QPS"的基数，而同报告又给出改造后的 990 QPS —— 报告取了**保守**一侧，可接受但读者易困惑（§2 已注明两表口径不同） |
| "本机离散 2 倍"免责是否仍成立、有没有被其它文档的单点数字违反 | ✅ 免责成立（报告给出同构建同档位 290.0 vs 142.8、91.7 vs 198.0、1211 vs 282 req/s）。其它文档的单点引用只出现在 **A/B 比值**语境（3.17×，3 轮交错），属报告 §4.1 认可的用法，但未附免责措辞（G-01）。**未发现**把单次 QPS 写成容量承诺的地方（`03-implementation-status.md:473`、`10-security/02-hardening-report.md:352` 都带"离散达 2 倍，非容量承诺"） |

---

## 9. 我实际跑过的验证命令与结果摘要

| # | 命令 | 结果 |
|---|------|------|
| 1 | `dotnet build src\BankingAgent.slnx -c Release /warnaserror --nologo -v minimal` | ✅ 0 警告 / 0 错误（HEAD 一次 → 上游修复后一次；中途一次因我自己未释放的 MockBank 进程锁住 `MockBank.Api.exe` 而失败，释放后复跑通过 —— 该失败属我的进程残留，非仓库问题） |
| 2 | `dotnet test src/UnitTests/UnitTests.csproj -c Release --nologo -v minimal` | ✅ 200/0（HEAD、上游修复后各一次） |
| 3 | `dotnet run --project src/E2ETest -c Release --no-build -- http://localhost:5250 http://localhost:5210` | ✅ 70/0（修复前）→ **71/0**（新增 0A 后，连跑两次） |
| 4 | `dotnet run --project src/StressTest -c Release --no-build -- http://localhost:5250 http://localhost:5210` | ✅ 31/0（测试 7 = 18.6×） |
| 5 | `dotnet run --project src/PluginValidator -c Release --no-build -- src/src/BankingAgent.Host/bin/Release/net8.0/plugins` | ✅ ERROR 0 / WARN 0 |
| 6 | `scripts\check-anchors.ps1` | ✅ all anchors reachable (70 files) |
| 7 | `scripts\check-endpoints.ps1` | ✅ Host 17 / MockBank 26，无缺失 |
| 8 | `scripts\read-audit-chain.ps1 -VerifyChain -VerifyHmac -SigningKey 'dev-only-key-change-in-production'` | ✅ 26 → 66 → **184 条**：GENESIS=1 / 断点=0 / HMAC 184/184 一致 |
| 9 | 空密钥 vs dev 密钥离线重算（自写 PowerShell，按 `Canonicalize` 逐字段拼接） | ✅ **空密钥命中** ⇒ 证明默认配置下审计链用空密钥签名（D-01 的直接证据） |
| 10 | `POST http://localhost:5210/api/corebank/v1/_admin/reset` | ❌ HEAD 版本 **500**（`IOException … being used by another process`，栈在 `BankLedgerFactory.cs:73`）→ ✅ 上游修复后 **200** |
| 11 | 生产环境启动（`ASPNETCORE_ENVIRONMENT=Production` + 仅注入 Crypto/Jwt 密钥 + 不设 `Audit__SigningKey`） | ✅ 启动成功，日志「安全门已关闭（SecurityGate:Enabled=false）」⇒ D-01 的直接证据 |

---

## 10. 未能验证的边界（明确列出）

1. **GitHub CI run #22 的远程绿灯未复现**：本机无 CI 触发条件；我只验证了 `ci.yml` 的门禁定义与本机等价命令（编译 /warnaserror、单测、插件校验）。`dotnet ef migrations has-pending-model-changes`（CI 作业 1）本轮未复跑。
2. **CI 作业 4 的端到端/压力环境差异**：CI 用 `ASPNETCORE_ENVIRONMENT=Development` 且**不生成** `appsettings.Development.json`（被 gitignore）⇒ CI 下审计链用空密钥签名（D-01 的 CI 影响面）。此推论基于配置与代码路径分析，**未在 CI 上实测**。
3. **容量数字未复跑**：按任务书要求不靠复跑压测来"验证"容量；§2/§3/§4/§6 的 QPS 只做了**内部自洽性**与**跨文档一致性**核对，并只跑了进程内 A/B（StressTest 测试 7 = 18.6×）。
4. **上游 `BankLedgerFactory.ResetAll` 新实现的并发正确性未做深度审查**：我只验证了「编译 0 警告 + 200 单测 + E2E 71-0 + reset 200 + 连跑两次通过」，未在跨行转账并发下验证其锁顺序；建议补一条并发回归测试（上游注释已声明锁顺序与 `MockBankStore.AcquireLedgerLocksAsync` 一致，但缺自动化覆盖）。
5. **`docs/04-operations/04-disaster-recovery.md` §8.2 的 Target 熔断方案未做实现层评估**（仅给出一致性标注建议 G-05）。
6. **本次唯一新增的仓库内文件是本报告** `artifacts/doc-verify3/report.md`（任务书指定）；运行期产生的 `logs/`、`App_Data/`、`bankingagent.db` 均在 `.gitignore` 覆盖范围内。

---

## 11. 给上游的处置建议（按优先级）

1. **D-01（P0）**：补审计密钥的 Development 派生兜底或缺密钥即拒绝启动，并让安全门在 Production 强制生效；同步修正 `README.md:285`、`.example:13` 的两句不实表述。**这一条不修，审计链的"不可篡改"在默认与 CI 配置下都不成立。**
2. **D-13（新增漂移）**：改 0A 步时同步 5 处 E2E 计数（70→71、17→20）与已过期的"需干净 MockBank 库"免责。
3. **D-04 / D-05**：审计文档 §3.1.1 的 `RuleId` 来源更正 + §4.6.1 补"旧归档不可重算"边界（这两条直接影响离线重算的正确用法）。
4. **D-06 / D-10 / D-11**：场景数、MockBank 数据目录（`data/` → `App_Data/`）、ADR 条数（11 → 19）。
5. **G-01 ~ G-06**：低成本的一致性与措辞修正。