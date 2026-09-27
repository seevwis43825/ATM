# CI/CD 流水线（CI/CD Pipeline）

> **状态**：已通过 · **所有者**：平台 · **版本**：v1.0
> **最后更新**：2026-09-21

---

## 1. 总览

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

## 2. 环境

| 环境 | 用途 | URL | 数据 |
|------|------|-----|------|
| **local** | 个人开发 | `localhost:5000` | mock |
| **dev** | 团队集成测试 | `dev.api.bankagent.com` | 共享 dev DB（每日重置） |
| **staging** | 预发 / QA / 性能测试 | `staging.api.bankagent.com` | 脱敏数据 |
| **production** | 生产 | `api.bankagent.com` | 真实数据（生产 + 强审计） |

---

## 3. CI 流水线（GitHub Actions）

### 3.1 PR Checks（`.github/workflows/pr.yml`）

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

### 3.2 必过的 Check

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

## 4. CD 流水线

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

## 5. 数据库 Migration

### 5.1 流程

```
开发 PR 中包含 migration SQL 文件
    ↓
CI 检查 migration 语法（启动 PostgreSQL 容器测试）
    ↓
PR 合并 → 自动在 staging 跑 migration
    ↓
手动部署到生产时 → 运行 migration
    ↓
如失败 → 立即停止部署、保留旧版本
```

### 5.2 工具

- 开发期：Entity Framework Core Migrations / Flyway
- 生产期：Flyway

```bash
# 生产部署前
flyway -url=jdbc:postgresql://prod-db migrate

# 如需回滚
flyway -url=jdbc:postgresql://prod-db undo
```

---

## 6. 镜像构建

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

## 7. 部署策略（生产）

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

## 8. 关联文档

- **测试策略**：[`04-testing-strategy.md`](04-testing-strategy.md)
- **部署架构**：[`../04-operations/01-deployment-architecture.md`](../04-operations/01-deployment-architecture.md)
- **事故响应**：[`../04-operations/03-incident-response.md`](../04-operations/03-incident-response.md)
- **发布流程**：[`../06-product/03-release-process.md`](../06-product/03-release-process.md)