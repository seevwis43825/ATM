# 数据库与 CI 协同方案

> **当前状态**：🔴 严重不足 —— 用 `EnsureCreated()`，**无迁移历史、无版本控制**
> **风险**：表结构演进会失控，多人协作时互相覆盖
> **目标**：EF Core Migration + CI 强制校验

---

## 0. 问题陈述

当前 `DatabaseInitializer` 用的是：

```csharp
await db.Database.EnsureCreatedAsync(ct);
```

`EnsureCreated` 的问题：

| 问题 | 后果 |
|------|------|
| **无迁移历史** | 不知道表结构怎么演进到现在的 |
| **不能改已有表** | 加列、改类型、拆表全都不支持 |
| **不能回滚** | 出问题只能手动改 SQL |
| **不感知并发** | 两人同时改模型，后编译的覆盖前面的 |
| **生产不可用** | 生产环境会直接改表结构，无审计 |

**多人协作下这是致命的** —— 5 人各自改模型，A 加一列、B 改一列，合并后 `EnsureCreated` 谁都发现不了冲突。

---

## 1. 目标架构

```
模型变更 → 生成 Migration → 提交到 git → CI 校验 → 部署时按序应用
   ↓              ↓              ↓            ↓
EF Core       *.cs + Snapshot  版本控制     dotnet ef database update
```

---

## 2. 实施步骤

### 2.1 安装 EF Core 工具

```powershell
dotnet tool install --global dotnet-ef --version 8.0.10
```

### 2.2 首次生成初始迁移

```powershell
cd G:\cunchu\大学\poject\ATM\src\src\BankingAgent.Base
dotnet ef migrations add InitialSchema `
  --context BankingDbContext `
  --output-dir Data/Migrations
```

生成物：

```
Data/Migrations/
├── 20260928000000_InitialSchema.cs
├── 20260928000000_InitialSchema.Designer.cs
└── BankingDbContextModelSnapshot.cs      ← 关键：模型快照
```

### 2.3 改造 DatabaseInitializer

```csharp
public class DatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    ILogger<DatabaseInitializer> logger,
    IOptions<DatabaseInitOptions> options)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (options.Value.Mode == DatabaseInitMode.EnsureCreated)
        {
            // 仅用于本地开发与单元测试
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BankingDbContext>();
            await db.Database.EnsureCreatedAsync(ct);
            logger.LogInformation("数据库已创建（EnsureCreated 模式，无迁移历史）");
            return;
        }

        // 生产/CI：应用迁移
        logger.LogInformation("开始应用数据库迁移...");
        var pending = await GetPendingMigrationsAsync(ct);

        if (pending.Any())
        {
            logger.LogWarning("待应用迁移: {Migrations}", string.Join(", ", pending));
        }

        await using var migrationScope = scopeFactory.CreateAsyncScope();
        var migrator = migrationScope.ServiceProvider
            .GetRequiredService<IMigrator>();
        await migrator.MigrateAsync(ct);

        logger.LogInformation("数据库迁移完成");
    }

    private async Task<List<string>> GetPendingMigrationsAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BankingDbContext>();
        return (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
    }
}
```

### 2.4 配置区分环境

```json
{
  "Database": {
    "Provider": "PostgreSql",
    "ConnectionString": "Host=...;Database=banking",
    "InitMode": "Migrate"          // 开发用 EnsureCreated，生产用 Migrate
  }
}
```

```json
{
  "Database": {
    "InitMode": "EnsureCreated"    // appsettings.Development.json
  }
}
```

---

## 3. CI 集成

### 3.1 迁移一致性校验（阻断式）

**这是最重要的一条**：确保模型与迁移历史一致，防止「有人改了模型但忘了生成迁移」。

```yaml
# .github/workflows/ci.yml（追加到 build 作业）
- name: 校验迁移与模型一致性
  run: |
    dotnet tool install --global dotnet-ef --version 8.0.10

    # 生成一个临时迁移，若有差异说明模型改了但没生成迁移
    dotnet ef migrations add MigrationConsistencyCheck \
      --context BankingDbContext \
      --output-dir /tmp/consistency-check \
      --project src/src/BankingAgent.Base \
      --startup-project src/src/BankingAgent.Host \
      --no-build

    if [ -n "$(find /tmp/consistency-check -name '*.cs' 2>/dev/null)" ]; then
      echo "::error::模型已变更但未生成迁移。请运行: dotnet ef migrations add <Name>"
      exit 1
    fi
    echo "迁移与模型一致"
```

### 3.2 迁移可应用性验证

```yaml
- name: 验证迁移可从零应用
  run: |
    docker run -d --name test-pg \
      -e POSTGRES_PASSWORD=test \
      -e POSTGRES_DB=banking_test \
      -p 5432:5432 postgres:16-alpine

    sleep 10

    dotnet ef database update \
      --context BankingDbContext \
      --project src/src/BankingAgent.Base \
      --startup-project src/src/BankingAgent.Host \
      --connection "Host=localhost;Database=banking_test;Username=postgres;Password=test"

    dotnet ef migrations has-pending-model-changes \
      --context BankingDbContext \
      --project src/src/BankingAgent.Base \
      --startup-project src/src/BankingAgent.Host \
      && echo "✅ 迁移完整" || (echo "::error::存在未应用的迁移" && exit 1)
```

### 3.3 迁移回滚验证

```yaml
- name: 验证迁移可回滚
  run: |
    # 回滚到上一个迁移
    dotnet ef database update 0 \
      --context BankingDbContext \
      --project src/src/BankingAgent.Base \
      --startup-project src/src/BankingAgent.Host

    # 再正向应用，验证 Down 逻辑正确
    dotnet ef database update \
      --context BankingDbContext \
      --project src/src/BankingAgent.Base \
      --startup-project src/src/BankingAgent.Host

    echo "✅ 回滚与重放均成功"
```

### 3.4 完整 CI 片段

```yaml
  database:
    name: 数据库迁移校验
    runs-on: ubuntu-latest
    needs: build
    services:
      postgres:
        image: postgres:16-alpine
        env:
          POSTGRES_PASSWORD: test
          POSTGRES_DB: banking_test
        ports:
          - 5432:5432
        options: >-
          --health-cmd pg_isready
          --health-interval 10s
          --health-timeout 5s
          --health-retries 5
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 8.0.x

      - name: 安装 EF 工具
        run: dotnet tool install --global dotnet-ef --version 8.0.10

      - name: 迁移一致性检查
        env:
          ConnectionStrings__Default: "Host=localhost;Database=banking_test;Username=postgres;Password=test"
        run: |
          dotnet ef migrations add _ConsistencyCheck \
            --context BankingDbContext --output-dir /tmp/check \
            --project src/src/BankingAgent.Base \
            --startup-project src/src/BankingAgent.Host
          if [ -n "$(find /tmp/check -name '*.cs' 2>/dev/null)" ]; then
            echo "::error::模型变更未生成迁移"
            exit 1
          fi

      - name: 从零应用全部迁移
        env:
          ConnectionStrings__Default: "Host=localhost;Database=banking_test;Username=postgres;Password=test"
        run: |
          dotnet ef database update \
            --context BankingDbContext \
            --project src/src/BankingAgent.Base \
            --startup-project src/src/BankingAgent.Host

      - name: 回滚与重放
        env:
          ConnectionStrings__Default: "Host=localhost;Database=banking_test;Username=postgres;Password=test"
        run: |
          dotnet ef database update 0 \
            --context BankingDbContext \
            --project src/src/BankingAgent.Base \
            --startup-project src/src/BankingAgent.Host
          dotnet ef database update \
            --context BankingDbContext \
            --project src/src/BankingAgent.Base \
            --startup-project src/src/BankingAgent.Host
          echo "回滚与重放验证通过"
```

---

## 4. 多插件的迁移协调（关键难点）

### 4.1 问题

每个插件有独立的 `IEntitySetContributor`，实体分散在不同插件程序集。迁移时如何收集全部插件的实体？

**方案：迁移工厂 + 独立迁移项目**

```
src/
├── src/
│   ├── BankingAgent.Migrations/          ← 【新建】集中管理全部迁移
│   │   ├── BankingAgent.Migrations.csproj
│   │   ├── Migrations/                   # 所有迁移文件集中在这里
│   │   └── DesignTimeDbContextFactory.cs
│   ├── BankingAgent.Base/
│   └── plugins/
│       ├── Plugin.Transfer/              # 只保留实体与 contributor
│       └── ...
```

`DesignTimeDbContextFactory` 负责在迁移时加载全部插件贡献器：

```csharp
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BankingDbContext>
{
    public BankingDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<BankingDbContext>()
            .UseNpgsql("Host=localhost;Database=banking;Username=postgres;Password=dev")
            .Options;

        // 手工收集全部插件贡献器
        var contributors = new List<IEntitySetContributor>
        {
            new TransferPersistenceContributor(),
            new BillPersistenceContributor(),
            new CardPersistenceContributor()
        };

        PluginContributorRegistry.Register(contributors);

        return new BankingDbContext(options, contributors,
            new CurrentUserAccessor(), new DateTimeOffsetProvider());
    }
}
```

### 4.2 迁移命名规范

```
AddInitialSchema                 初始建表
AddTransferRecords               转账表
AddTransferAmountIndex          转账表加索引
AddCardManagement                卡片管理表
AddAuditLogPartition            审计表分区
AddRiskScoringColumns           风控字段
```

**禁止** `InitialCreate2`、`FixStuff`、`TempChange` 这类无意义命名。

### 4.3 多人协作规则

| 规则 | 说明 |
|------|------|
| **一个 PR 一个迁移** | 便于 review 与回滚 |
| **迁移文件必须进 git** | 否则 CI 校验失败 |
| **禁止改已合并的迁移** | 会导致环境间历史不一致 |
| **破坏性变更分两步** | 见下 |
| **大表加列分批** | 避免锁表 |

### 4.4 破坏性变更的两步走

```csharp
// 步骤 1：新增列（向后兼容）
migrationBuilder.AddColumn<string>(
    name: "new_column",
    table: "transfer_records",
    maxLength: 64,
    nullable: true);   // 必须可空
// 同时代码双写：写新旧两列，读旧列

// 步骤 2（下一个版本）：删除旧列
migrationBuilder.DropColumn(
    name: "old_column",
    table: "transfer_records");
// 同时代码切到只读写新列
```

**绝不允许**在一次迁移里既删又加又改类型。

---

## 5. 生产部署流程

```yaml
  deploy:
    needs: [build, unit-test, database, integration]
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: 应用数据库迁移
        env:
          ConnectionStrings__Default: ${{ secrets.PROD_DB_CONNECTION }}
        run: |
          dotnet tool install --global dotnet-ef --version 8.0.10
          dotnet ef database update \
            --context BankingDbContext \
            --project src/src/BankingAgent.Migrations \
            --startup-project src/src/BankingAgent.Host \
            --no-build

      - name: 记录已应用迁移
        run: |
          dotnet ef migrations list \
            --context BankingDbContext \
            --project src/src/BankingAgent.Migrations \
            --connection "${{ secrets.PROD_DB_CONNECTION }}" \
            | tee migration-log.txt

      - name: 部署应用
        run: ./deploy.sh

      - name: 健康检查
        run: |
          for i in $(seq 1 30); do
            if curl -sf "${{ secrets.PROD_URL }}/health" > /dev/null; then
              echo "部署成功"; exit 0
            fi
            sleep 5
          done
          echo "::error::健康检查失败，启动回滚"
          exit 1
```

**关键顺序**：**先迁移，后部署应用**。因为迁移是向后兼容的（旧代码能跑在新 schema 上），反过来则不行。

---

## 6. 回滚策略

| 场景 | 处理 |
|------|------|
| 应用发布失败 | 回滚应用镜像（数据库不动） |
| 迁移应用失败 | EF 自动回滚到上一个迁移（单次迁移内是事务） |
| 迁移已应用但应用有问题 | 保留 schema，回滚应用；确认无数据影响后再 `database update <prev>` |
| 数据错误 | `database update <prev>` + 数据修复脚本 |

**重要**：生产回滚**优先回滚应用而非数据库**。因为数据库回滚可能丢数据。

---

## 7. 落地清单

| # | 事项 | 优先级 | 预估 |
|---|------|-------|------|
| 1 | 安装 dotnet-ef 工具 | P0 | 1 分钟 |
| 2 | 创建 `BankingAgent.Migrations` 项目 | **P0** | 30 分钟 |
| 3 | 实现 `DesignTimeDbContextFactory` | **P0** | 30 分钟 |
| 4 | 生成初始迁移并提交 | **P0** | 20 分钟 |
| 5 | 改造 `DatabaseInitializer` 支持 Migrate 模式 | **P0** | 20 分钟 |
| 6 | CI 加迁移一致性校验 | **P0** | 30 分钟 |
| 7 | CI 加迁移可应用性验证 | P1 | 30 分钟 |
| 8 | CI 加回滚验证 | P1 | 20 分钟 |
| 9 | 生产部署接入 `database update` | **P0** | 30 分钟 |
| 10 | 编写迁移规范文档 | P1 | 30 分钟 |

**总计约 4.5 小时**，是当前 P0 清单里性价比最高的一项。

---

## 8. 相关文档

- 数据模型：[`09-uml/04-data-model.md`](../09-uml/04-data-model.md)
- CI 流水线：[`../../.github/workflows/ci.yml`](../../.github/workflows/ci.yml)
- 实现状态：[`plugin/03-implementation-status.md`](../plugin/03-implementation-status.md)
- EF Core 官方文档：https://learn.microsoft.com/ef/core/managing-schemas/migrations/
