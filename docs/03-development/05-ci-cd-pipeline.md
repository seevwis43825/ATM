# CI/CD 流水线（CI/CD Pipeline）

> **状态**：与当前仓库对齐 · **所有者**：平台 · **版本**：v1.1
> **最后更新**：2026-10-01
>
> **边界**：`.github/workflows/ci.yml` 是当前唯一落地的流水线；仓库没有 CD、Dockerfile、镜像仓库、staging/production 环境或部署脚本。本文后半部分保留为 Target，不能当作已上线能力。

---

## 1. 当前流水线（Current）

当前 GitHub Actions 在 `main`、`master`、`dev` 的 push/PR 上触发。分支触发范围是工作流现状，不改变团队当前以 `main` 为主线的协作规则。

| Job | 当前行为 | 门禁口径 |
|---|---|---|
| `build` | SDK 8/9、restore、Release `/warnaserror` build、EF pending-model gate、插件契约校验 | 阻断 |
| `unit-test` | xUnit + TRX + Cobertura 上传 | 测试失败阻断；未配置覆盖率百分比阈值 |
| `static-analysis` | `dotnet format`、Gitleaks、依赖漏洞检查 | 当前命令允许 warning/continue-on-error，不是安全阻断门 |
| `integration` | 启动 MockBank `:5200` 与 Host `:5243`，运行 E2E 和 StressTest | 阻断 |
| `summary` | 汇总 build/unit/static/integration | build、unit、integration 非 success 时失败 |

仓库当前不在 CI 中运行 LoadTest，也不构建/推送镜像或自动部署。

### 1.1 本地复现

```bash
dotnet restore src/BankingAgent.slnx
dotnet build src/BankingAgent.slnx -c Release /warnaserror
dotnet test src/UnitTests/UnitTests.csproj -c Release
dotnet run --project src/PluginValidator --configuration Release -- \
  src/src/BankingAgent.Host/bin/Release/net8.0/plugins
```

迁移一致性命令见 [`../13-database/01-migration-and-ci.md`](../13-database/01-migration-and-ci.md)；E2E/Stress 需先启动两个服务。

---

## 2. 目标流水线总览（Target）

```
┌──────────────┐    ┌──────────────┐    ┌──────────────┐    ┌──────────────┐
│  Push / PR   │ →  │  CI (Build)  │ →  │  CD (Deploy) │ →  │  Production │
└──────────────┘    └──────────────┘    └──────────────┘    └──────────────┘
      │                    │                    │                    │
      │                    ▼                    ▼                    ▼
      │              ┌──────────────┐    ┌──────────────┐    ┌──────────────┐
      │              │  Unit Tests  │    │   Staging    │    │  Monitoring │
      │              │  Integration │    │  Smoke Tests │    │  Alerting   │
      │              └──────────────┘    └──────────────┘    └──────────────┘
      │
      ▼
┌──────────────────────────────────────────────────────────────────┐
│                          PR Checks                                │
│  · Lint          · Format          · Type Check                  │
│  · Unit Tests    · Coverage ≥ 80%  · Security Scan              │
│  · Build         · Docker Build    · Image Push                │
└──────────────────────────────────────────────────────────────────┘
```

---

## 3. 目标环境（Target，尚未提供）

| 环境 | 用途 | URL | 数据 |
|------|------|-----|------|
| **local** | 个人开发 | `localhost:5243` | mock |
| **dev** | 团队集成测试 | `dev.api.bankagent.com` | 共享 dev DB（每日重置） |
| **staging** | 预发 / QA / 性能测试 | `staging.api.bankagent.com` | 脱敏数据 |
| **production** | 生产 | `api.bankagent.com` | 真实数据（生产 + 强审计） |

---

## 4. 目标 CI 示例（Target，不是当前工作流）

### 4.1 PR Checks 示例

> 当前真实文件是 `.github/workflows/ci.yml`。以下 Python、Node、Docker 作业只有在对应项目和构建资产落地后才可采用。

```yaml
name: PR Checks
on:
  pull_request:
    branches: [main]

jobs:
  backend-checks:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 8.0.x

      - name: Restore
        run: dotnet restore

      - name: Format check
        run: dotnet format --verify-no-changes src/

      - name: Build
        run: dotnet build --no-restore -c Release

      - name: Unit tests
        run: dotnet test --no-build -c Release --logger "trx;LogFileName=test-results.trx"

      - name: Coverage
        run: |
          dotnet test --collect:"XPlat Code Coverage"
          reportgenerator -reports:"**/coverage.cobertura.xml" -targetdir:"coverage" -reporttypes:Html

      - name: Upload coverage
        uses: codecov/codecov-action@v4

      - name: Security scan
        run: |
          dotnet tool install -g security-scan
          security-scan --project src/

  ai-service-checks:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Setup Python
        uses: actions/setup-python@v5
        with:
          python-version: '3.11'

      - name: Install
        run: |
          cd ai-service
          pip install -r requirements.txt

      - name: Lint (ruff)
        run: ruff check .

      - name: Type check (mypy)
        run: mypy .

      - name: Unit tests
        run: pytest --cov=ai_service --cov-report=xml

  frontend-checks:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Setup Node
        uses: actions/setup-node@v4
        with:
          node-version: 20

      - name: Install
        run: npm ci

      - name: Lint (eslint)
        run: npm run lint

      - name: Type check
        run: npm run type-check

      - name: Unit tests
        run: npm test -- --coverage

      - name: Build
        run: npm run build

  docker-build:
    runs-on: ubuntu-latest
    needs: [backend-checks, ai-service-checks, frontend-checks]
    steps:
      - name: Build agent-core image
        run: docker build -t agent-core:${{ github.sha }} -f deploy/docker/Dockerfile.agent .

      - name: Build ai-service image
        run: docker build -t ai-service:${{ github.sha }} -f deploy/docker/Dockerfile.ai .

      - name: Push to registry (staging only)
        if: github.ref == 'refs/heads/main'
        run: |
          docker push agent-core:${{ github.sha }}
```

### 4.2 目标必过 Check

| Check | 阻塞 | 通过条件 |
|-------|------|---------|
| Format | ✅ | dotnet format 无变更 |
| Build | ✅ | 编译成功 |
| Lint | ✅ | 0 个 error |
| Type Check | ✅ | 0 个 error |
| Unit Tests | ✅ | 100% 通过 |
| Coverage | ✅ | ≥ 80%（核心模块） |
| Security Scan | ✅ | 0 个 HIGH/CRITICAL |
| Docker Build | ✅ | 镜像构建成功 |

---

## 5. CD 流水线（Target，未实现）

### 4.1 Staging 部署（自动）

合并到 `main` → 自动部署到 staging：

```yaml
name: Deploy to Staging
on:
  push:
    branches: [main]

jobs:
  deploy-staging:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - name: Deploy
        run: ./scripts/deploy.sh staging
```

### 4.2 Production 部署（手动审批 + 灰度）

```yaml
name: Deploy to Production
on:
  workflow_dispatch:
    inputs:
      version:
        description: 'Release version (e.g., v1.2.3)'
        required: true
      rollout_strategy:
        description: 'Rollout strategy'
        required: true
        default: 'canary'
        type: choice
        options:
          - canary
          - blue-green
          - all-at-once

jobs:
  deploy:
    runs-on: ubuntu-latest
    environment: production
    steps:
      - name: Approve
        uses: trstringer/manual-approval@v1
        with:
          secret-name: PRODUCTION_DEPLOY_TOKEN
          # 需要 2 人审批：架构师 + 平台

      - name: Deploy
        run: ./scripts/deploy.sh production ${{ inputs.version }} ${{ inputs.rollout_strategy }}
```

### 4.3 灰度发布策略

详见 [`../06-product/03-release-process.md`](../06-product/03-release-process.md)。

---

## 6. 数据库 Migration（Current + Target）

### 5.1 流程

```
开发 PR 中包含 EF Core Migration 类、Designer 与 ModelSnapshot
    ↓
CI 使用 `has-pending-model-changes` 检查模型一致性
    ↓
PR 合并 → 自动在 staging 跑 migration
    ↓
手动部署到生产时 → 运行 migration
    ↓
如失败 → 立即停止部署、保留旧版本
```

### 6.2 工具

- 当前统一使用 EF Core Migrations；不使用 Flyway `V*.sql`。
- 当前 CI 已执行模型/迁移一致性门禁，但没有 staging/production 自动迁移。
- 生产化后应在部署前执行备份、`dotnet ef database update`、冒烟和回滚演练。

```bash
dotnet ef database update \
  --project src/src/BankingAgent.Base/BankingAgent.Base.csproj \
  --startup-project src/src/BankingAgent.Host/BankingAgent.Host.csproj \
  --context BankingDbContext \
  --configuration Release
```

---

## 7. 镜像构建（Target 示例，当前仓库无 Dockerfile）

### 6.1 Agent Core Dockerfile

```dockerfile
# deploy/docker/Dockerfile.agent
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 5000

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Core ./Core
COPY src/Contexts ./Contexts
COPY src/Bootstrap ./Bootstrap
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish

FROM base AS final
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Bootstrap.dll"]
```

### 6.2 AI Service Dockerfile

```dockerfile
FROM python:3.11-slim
WORKDIR /app
COPY ai-service/requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY ai-service/ .
EXPOSE 8000
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
```

---

## 8. 部署策略（Target）

### 7.1 蓝绿部署（推荐）

```
Production_BLUE（v1.2.0）
Production_GREEN（v1.2.3）  ← 新版本先部署到这里
```

- **切换**：通过负载均衡器切换 100% 流量
- **回滚**：5 秒内切回 BLUE

### 7.2 灰度（Canary）

```
Production v1.2.0 接收 95% 流量
Production v1.2.3 接收 5% 流量
   ↓ 监控 1 小时无异常
Production v1.2.3 接收 25% 流量
   ↓ 监控 1 小时
Production v1.2.3 接收 50% 流量
   ↓ 监控 1 小时
Production v1.2.3 接收 100% 流量
```

详见 [`../06-product/03-release-process.md`](../06-product/03-release-process.md)。

---

## 9. 关联文档

- **测试策略**：[`04-testing-strategy.md`](04-testing-strategy.md)
- **部署架构**：[`../04-operations/01-deployment-architecture.md`](../04-operations/01-deployment-architecture.md)
- **事故响应**：[`../04-operations/03-incident-response.md`](../04-operations/03-incident-response.md)
- **发布流程**：[`../06-product/03-release-process.md`](../06-product/03-release-process.md)