# 数据库迁移与 CI 协作

> 本文以当前仓库实现为准，区分已经落地的 **Current** 与后续建议的 **Target**。
> 本地命令以 Windows PowerShell 为主；CI 片段保持 GitHub Actions 的 Linux shell 语法。

## 1. Current：当前基线

### 1.1 初始化模式与默认配置

`DatabaseInitializer` 已支持三种 `DatabaseInitMode`：

- `EnsureCreated`：直接按当前模型建空库，适合本地开发和演示；不会写入 `__EFMigrationsHistory`。
- `Migrate`：先确保 PostgreSQL 插件 Schema 存在，再执行 `Database.MigrateAsync()`。
- `None`：跳过初始化，由外部部署流程负责。

宿主 `appsettings.json` 的当前默认值是：

```json
{
  "Database": {
    "Provider": "Sqlite",
    "ConnectionString": "Data Source=bankingagent.db",
    "InitMode": "EnsureCreated"
  }
}
```

因此必须分清两件事：

1. **默认开发启动路径**是 SQLite + `EnsureCreated`；
2. **迁移路径已经存在**，用于 PostgreSQL/生产化场景，并非“项目没有迁移”。

`EnsureCreated` 与 Migration 不应对同一个长期环境混用。用 `EnsureCreated` 建出的数据库没有迁移历史；需要切到 `Migrate` 时，应新建数据库或先完成受控的基线接管。

### 1.2 迁移位置与设计时模型

迁移位于 Base 项目，而不是独立的 Migrations 项目：

```text
src/src/BankingAgent.Base/Data/Migrations/
├── 20260928074506_InitialSchema.cs
├── 20260928074506_InitialSchema.Designer.cs
└── BankingDbContextModelSnapshot.cs
```

`20260928074506_InitialSchema` 当前创建 PostgreSQL Schema `plugin_transfer`、表 `transfer_records` 及其索引。

`DesignTimeFactory` 已实现 PostgreSQL 设计时支持：

- 默认以 PostgreSQL 生成迁移，也可通过 `--provider` 或环境变量覆盖；
- 从已构建的插件目录发现 `IEntitySetContributor`；
- 将插件贡献的实体纳入设计时 EF 模型；
- 使用迁移专用加密服务保留 `[Encrypted]` 字段的字符串列映射；
- 将迁移程序集指向 `BankingAgent.Base`。

因此生成迁移前必须先用 Release 配置构建，确保设计时发现到的是最新插件 DLL。

### 1.3 PostgreSQL Schema 初始化

`BankingDbContext` 在 PostgreSQL 下把每个贡献器新增的实体映射到该贡献器的 `PartitionName`。`DatabaseInitializer` 的 `Migrate` 路径会在应用迁移前：

1. 校验 Schema 名只含合法标识符字符；
2. 执行 `CREATE SCHEMA IF NOT EXISTS`；
3. 调用 `Database.MigrateAsync()`。

这意味着插件发现、模型 Schema 映射和运行时 Schema 初始化均已实现。迁移文件仍应显式审阅 `schema`、列类型和索引，不能只看迁移名称。

### 1.4 审计库

审计不是“仅规划”。当前已经有独立的 `AuditDbContext` 和 `IAuditRepository`：

- 表名：`audit_events`；
- PostgreSQL Schema：`audit`；
- SQLite 下使用独立审计数据库；
- `AuditLogger` 将同一审计事件写入 JSONL 文件和数据库；
- 数据库仓储只暴露追加、查询、计数和链校验，不提供更新/删除接口。

业务库的 Migration 只管理 `BankingDbContext`。审计库由 `DatabaseInitializer` 单独初始化；当前使用 `AuditDbContext.Database.EnsureCreatedAsync()`，不属于 `BankingDbContext` 的迁移历史。

## 2. 本地迁移命令（Windows PowerShell）

以下命令从仓库根目录执行，不依赖个人绝对路径。

### 2.1 还原工具并构建

```powershell
Set-Location .\src
dotnet tool restore
dotnet restore .\BankingAgent.slnx
dotnet build .\BankingAgent.slnx --no-restore --configuration Release
```

仓库本地工具清单固定 `dotnet-ef` 为 `8.0.10`，优先使用 `dotnet tool restore`，不要要求每位开发者全局安装。

### 2.2 检查模型是否有待生成迁移

```powershell
dotnet ef migrations has-pending-model-changes `
  --project .\src\BankingAgent.Base\BankingAgent.Base.csproj `
  --startup-project .\src\BankingAgent.Host\BankingAgent.Host.csproj `
  --context BankingDbContext `
  --configuration Release `
  --no-build

if ($LASTEXITCODE -ne 0) {
    throw "模型与迁移快照不一致，请生成并审阅迁移。"
}
```

`--configuration Release` 不可省略：此前一步只构建 Release，`--no-build` 若按默认 Debug 查找输出，会找不到宿主的 `.deps.json`。

### 2.3 生成迁移

```powershell
dotnet ef migrations add AddMeaningfulChange `
  --project .\src\BankingAgent.Base\BankingAgent.Base.csproj `
  --startup-project .\src\BankingAgent.Host\BankingAgent.Host.csproj `
  --context BankingDbContext `
  --output-dir Data\Migrations `
  --configuration Release `
  --no-build `
  -- --provider PostgreSql
```

迁移名称应描述结构变化，例如 `AddTransferStatusIndex`。不要使用 `FixStuff`、`Temp` 或 `Initial2`。

生成后至少审阅：

- 新迁移的 `Up` / `Down`；
- `BankingDbContextModelSnapshot`；
- 表是否进入正确插件 Schema；
- `[Encrypted]` 字段是否仍映射为预期字符串列；
- 是否出现意外的删表、改列类型或索引重建。

### 2.4 列出迁移

```powershell
dotnet ef migrations list `
  --project .\src\BankingAgent.Base\BankingAgent.Base.csproj `
  --startup-project .\src\BankingAgent.Host\BankingAgent.Host.csproj `
  --context BankingDbContext `
  --configuration Release `
  --no-build `
  -- --provider PostgreSql
```

### 2.5 在 PostgreSQL 测试库应用迁移

```powershell
$env:BANKING_DB_PROVIDER = "PostgreSql"
$env:BANKING_DB_POSTGRES = "Host=localhost;Database=banking_migration_test;Username=postgres;Password=postgres"

dotnet ef database update `
  --project .\src\BankingAgent.Base\BankingAgent.Base.csproj `
  --startup-project .\src\BankingAgent.Host\BankingAgent.Host.csproj `
  --context BankingDbContext `
  --configuration Release `
  --no-build `
  -- --provider PostgreSql
```

测试完成后可用 `Remove-Item Env:BANKING_DB_PROVIDER, Env:BANKING_DB_POSTGRES` 清理本次 PowerShell 会话中的变量。不要把真实凭据写入文档、脚本或仓库配置。

## 3. Current：CI 门禁

当前 `.github/workflows/ci.yml` 在 **build job** 中执行：

1. 安装 .NET 8 与 .NET 9 SDK；
2. `dotnet restore`；
3. `dotnet build ... -c Release /warnaserror`；
4. 在 `src` 目录执行 `dotnet tool restore`，恢复 `dotnet-ef 8.0.10`；
5. 执行以下阻断式检查：

```bash
dotnet ef migrations has-pending-model-changes \
  --project src/BankingAgent.Base/BankingAgent.Base.csproj \
  --startup-project src/BankingAgent.Host/BankingAgent.Host.csproj \
  --context BankingDbContext \
  --configuration Release \
  --no-build
```

该命令检查的是“当前设计时模型是否超前于迁移快照”。它不连接数据库，也不等价于“数据库是否存在未应用迁移”。失败时，开发者应在本地生成并审阅迁移，而不是修改 CI 绕过门禁。

## 4. 迁移协作规则

1. **模型与迁移同一 PR**：实体、映射、索引或插件贡献器改变时，同步提交迁移和快照。
2. **迁移存放在 Base 项目**：当前路径为 `BankingAgent.Base/Data/Migrations`；不要另建 Migrations 项目。
3. **禁止改写已合并且已部署的迁移**：新增修正迁移，避免不同环境的迁移历史分叉。
4. **先构建，再生成/检查**：设计时插件发现依赖最新 Release 插件产物。
5. **一个迁移聚焦一个主题**：减少冲突并便于回滚审阅；一个 PR 可以因同一功能包含多个有序迁移。
6. **合并冲突后重新生成判断**：不要手工拼接 Snapshot 后直接提交，必须再次运行 `has-pending-model-changes`。
7. **破坏性变更分阶段**：先新增兼容列并完成回填/双写，再切换读取，最后在后续版本删除旧列。
8. **生产优先回滚应用**：数据库 Down 可能丢数据；只有确认数据影响并完成备份后才回退数据库迁移。

### 并行开发发生迁移冲突时

```powershell
Set-Location .\src
git rebase origin/main
dotnet build .\BankingAgent.slnx --configuration Release
dotnet ef migrations has-pending-model-changes `
  --project .\src\BankingAgent.Base\BankingAgent.Base.csproj `
  --startup-project .\src\BankingAgent.Host\BankingAgent.Host.csproj `
  --context BankingDbContext `
  --configuration Release `
  --no-build
```

若本分支迁移尚未共享且需要重做，可先使用 `dotnet ef migrations remove` 删除本分支最后一个迁移，再基于最新快照重新生成。不得删除或重写已进入共享分支的迁移。

## 5. Target：后续增强

以下均是目标，不应写成当前已完成：

- 在 CI 增加临时 PostgreSQL 服务，从空库执行全部 Migration；
- 对每个迁移验证关键 `Down`/重放路径，或采用前向修复策略并自动验证；
- 为 `AuditDbContext` 建立独立迁移历史，替代当前审计库的 `EnsureCreated`；
- 在部署流水线中把迁移作为显式阶段，并在多实例启动前只执行一次；
- 保存迁移 SQL、已应用迁移列表和审批记录作为发布证据。

建议的生产顺序：

```text
备份/快照 → 审阅迁移 SQL → 单点应用迁移 → 验证健康与关键查询 → 部署应用 → 观察
```

默认 SQLite + `EnsureCreated` 仍可服务本地演示，但它不是长期生产数据库升级方案。

## 6. 关联资料

- 数据模型：[`../09-uml/04-data-model.md`](../09-uml/04-data-model.md)
- CI：[`../../.github/workflows/ci.yml`](../../.github/workflows/ci.yml)
- 初始迁移：[`../../src/src/BankingAgent.Base/Data/Migrations/20260928074506_InitialSchema.cs`](../../src/src/BankingAgent.Base/Data/Migrations/20260928074506_InitialSchema.cs)
- EF Core Migration：https://learn.microsoft.com/ef/core/managing-schemas/migrations/
