# 开发环境搭建（Environment Setup）

> **状态**：已通过 · **所有者**：平台 · **版本**：v1.0
> **最后更新**：2026-09-21

---

## 1. 环境要求

### 1.1 硬件（推荐）
- CPU：4 核+
- 内存：16GB+
- 磁盘：50GB+ SSD
- 网络：稳定（拉镜像、调用 LLM）

### 1.2 软件

| 软件 | 版本 | 用途 |
|------|------|------|
| Docker Desktop | 4.x | 本地容器 |
| Git | 2.30+ | 版本控制 |
| VSCode / Rider / Visual Studio | 最新 | IDE |
| Node.js | 20 LTS | 前端 |
| Python | 3.11 | AI Service |
| .NET SDK | 8.0 | 后端 |
| PostgreSQL Client（psql） | 16 | 数据库调试 |
| Redis Client（redis-cli） | 7 | 缓存调试 |

### 1.3 IDE 推荐

| 角色 | IDE |
|------|-----|
| 后端（C#） | JetBrains Rider / Visual Studio 2022 |
| AI Service（Python） | VSCode + Python 扩展 / Cursor |
| 前端（TypeScript） | VSCode / Cursor |
| 通用 | JetBrains Fleet |

---

## 2. 快速开始（5 步）

### 2.1 克隆仓库

```bash
git clone https://github.com/bank-agent/bank-agent.git
cd bank-agent
```

### 2.2 启动本地基础设施

```bash
# 启动 PostgreSQL + Redis + Kafka（如需）
cd deploy
docker compose -f docker-compose.dev.yml up -d

# 验证
docker compose -f docker-compose.dev.yml ps
```

### 2.3 启动 Agent Core

```bash
cd src/Bootstrap
dotnet restore
dotnet run --project Bootstrap.csproj --launch-profile "Development"
```

访问：
- API: `http://localhost:5000`
- Swagger: `http://localhost:5000/swagger`
- Health: `http://localhost:5000/health`

### 2.4 启动 AI Service

```bash
cd ai-service
python -m venv venv
source venv/bin/activate  # Windows: venv\Scripts\activate
pip install -r requirements.txt

# 配置环境变量
cp .env.example .env
# 编辑 .env，设置 LLM API Key

# 启动
uvicorn app.main:app --reload --port 8000
```

访问：`http://localhost:8000/docs`

### 2.5 启动前端

```bash
cd web
npm install
npm run dev
```

访问：`http://localhost:3000`

---

## 3. docker-compose.dev.yml

```yaml
version: '3.8'

services:
  postgres:
    image: postgres:16
    container_name: bank-agent-postgres
    environment:
      POSTGRES_USER: bank_agent
      POSTGRES_PASSWORD: dev_password
      POSTGRES_DB: bank_agent
    ports:
      - "5432:5432"
    volumes:
      - postgres-data:/var/lib/postgresql/data
      - ./init-scripts:/docker-entrypoint-initdb.d

  pgvector:
    image: pgvector/pgvector:pg16
    # 与 postgres 共用数据卷

  redis:
    image: redis:7-alpine
    container_name: bank-agent-redis
    ports:
      - "6379:6379"

  kafka:
    image: confluentinc/cp-kafka:7.5.0
    container_name: bank-agent-kafka
    environment:
      KAFKA_NODE_ID: 1
      KAFKA_PROCESS_ROLES: broker,controller
      KAFKA_LISTENERS: PLAINTEXT://0.0.0.0:9092
      KAFKA_ADVERTISED_LISTENERS: PLAINTEXT://localhost:9092
      KAFKA_CONTROLLER_LISTENER_NAMES: CONTROLLER
      KAFKA_LISTENER_SECURITY_PROTOCOL_MAP: CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT
    ports:
      - "9092:9092"

  # 用于 LLM mock（如未配置真实 API）
  ollama:
    image: ollama/ollama:latest
    container_name: bank-agent-ollama
    ports:
      - "11434:11434"

volumes:
  postgres-data:
```

---

## 4. 环境变量

### 4.1 Agent Core (`src/Bootstrap/appsettings.Development.json`)

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Port=5432;Database=bank_agent;Username=bank_agent;Password=dev_password",
    "Redis": "localhost:6379"
  },
  "AIService": {
    "Url": "http://localhost:8000",
    "Timeout": 30000
  },
  "FeatureFlags": {
    "feature.cross_scenario.enabled": false,
    "feature.wealth.auto_recommend.enabled": true,
    "feature.transfer.qrcode.enabled": false
  },
  "Jwt": {
    "Issuer": "https://dev.bankagent.com",
    "Audience": "bank-agent-dev",
    "Key": "dev-only-secret-key-please-change-in-production"
  }
}
```

### 4.2 AI Service (`.env`)

```bash
# LLM Provider
OPENAI_API_KEY=sk-xxx
DASHSCOPE_API_KEY=sk-xxx       # 通义千问
DEFAULT_LLM_MODEL=qwen3-max
FALLBACK_LLM_MODEL=gpt-4o

# Local Ollama（如使用）
OLLAMA_BASE_URL=http://localhost:11434

# Vector DB
PGVECTOR_URL=postgresql://bank_agent:dev_password@localhost:5432/bank_agent

# Cache
REDIS_URL=redis://localhost:6379

# Tracing (Jaeger / Tempo)
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317
```

---

## 5. IDE 配置

### 5.1 VSCode `.vscode/settings.json`

```json
{
  "python.defaultInterpreterPath": "./ai-service/venv/bin/python",
  "python.testing.pytestEnabled": true,
  "[python]": {
    "editor.defaultFormatter": "charliermarsh.ruff",
    "editor.formatOnSave": true
  },
  "[typescript]": {
    "editor.defaultFormatter": "esbenp.prettier-vscode",
    "editor.formatOnSave": true
  },
  "[csharp]": {
    "editor.defaultFormatter": "ms-dotnettools.csharp",
    "editor.formatOnSave": true,
    "editor.codeActionsOnSave": {
      "source.organizeImports": true
    }
  },
  "files.exclude": {
    "**/bin": true,
    "**/obj": true,
    "**/.next": true,
    "**/node_modules": true
  },
  "csharp.test.testRunSettings": {
    "TestingPlatformCaptureOutput": true
  }
}
```

### 5.2 JetBrains Rider

启用：
- Editor → Inspections → C# → Roslyn Analyzers
- Plugins → .NET Core CLI、Database
- Settings → Version Control → Commit Template

---

## 6. 数据库初始化

### 6.1 自动执行

启动时自动：
1. 连接 PostgreSQL
2. 检查 Schema 是否创建
3. 执行 pending migrations

### 6.2 手动执行（如需）

```bash
# 初始化 Schema
psql -h localhost -U bank_agent -d bank_agent -f init-scripts/01-init-schemas.sql

# 执行 migration
psql -h localhost -U bank_agent -d bank_agent -f src/Contexts/Transfer/Infrastructure/Migrations/V001__create_orders.sql
```

---

## 7. 测试运行

### 7.1 后端单元测试

```bash
cd src
dotnet test --logger "console;verbosity=normal"
```

### 7.2 后端集成测试

```bash
cd tests/IntegrationTests
dotnet test
```

### 7.3 AI Service 测试

```bash
cd ai-service
pytest
```

### 7.4 前端测试

```bash
cd web
npm test
```

### 7.5 E2E 测试

```bash
cd tests/E2E
npx playwright test
```

---

## 8. 常见问题

### 8.1 数据库连接失败

```bash
# 检查容器状态
docker ps | grep postgres

# 测试连接
psql -h localhost -p 5432 -U bank_agent -d bank_agent

# 检查 docker 网络
docker network ls
```

### 8.2 LLM 调用超时

```bash
# 检查 API Key
echo $OPENAI_API_KEY

# 测试 LLM（独立）
curl -X POST https://api.openai.com/v1/chat/completions \
  -H "Authorization: Bearer $OPENAI_API_KEY" \
  -d '{"model": "gpt-4o-mini", "messages": [{"role": "user", "content": "hi"}]}'

# 如超时，检查网络代理
export https_proxy=http://proxy.example.com:8080
```

### 8.3 端口冲突

修改 `docker-compose.dev.yml` 中对应端口：
```yaml
ports:
  - "5433:5432"  # 改为 5433 避免冲突
```

### 8.4 数据库 migration 失败

```bash
# 查看已应用 migrations
psql -c "SELECT * FROM core.migrations ORDER BY applied_at"

# 回滚
psql -c "DELETE FROM core.migrations WHERE version = 'V005'"
```

---

## 9. 关联文档

- **Git 工作流**：[`02-git-workflow.md`](02-git-workflow.md)
- **测试策略**：[`04-testing-strategy.md`](04-testing-strategy.md)
- **CI/CD**：[`05-ci-cd-pipeline.md`](05-ci-cd-pipeline.md)
- **部署架构**：[`../04-operations/01-deployment-architecture.md`](../04-operations/01-deployment-architecture.md)