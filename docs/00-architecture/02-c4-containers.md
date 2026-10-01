# C4 架构模型 - Level 2：容器视图（Containers）

> **状态**：与当前实现对齐 · **所有者**：架构师 · **版本**：v1.1
> **最后更新**：2026-10-01
>
> **边界**：C4 的 Container 指可运行/部署单元。Current 只有两个 Web 进程及其本地依赖；K8s、网关、Next.js、Python、PostgreSQL、Redis、Kafka 均为 Target。

---

## 1. 当前容器视图（Current）

```
┌──────────────────────────────────────────────────────────┐
│ BankingAgent.Host（ASP.NET Core/.NET 8，:5243）          │
│ ├─ Minimal API + wwwroot 静态 UI                         │
│ ├─ BankingAgent.Base                                     │
│ ├─ BankingAgent.Plugin.Sdk                               │
│ └─ 四个运行期加载插件                                    │
└───────────────┬──────────────────┬───────────────────────┘
                │ HTTP             │ EF Core
                ▼                  ▼
┌───────────────────────────┐   SQLite（默认）
│ MockBank.Api（.NET 8，    │   bankingagent.db +
│ :5200，进程内 MockBankStore）│   bankingagent.audit.db
└───────────────────────────┘

可选：Host 通过 HTTPS 调用 OpenAI-compatible LLM，仅增强意图分类。
```

`BankingAgent.Base`、`BankingAgent.Plugin.Sdk` 和四个插件是项目/程序集边界，但随 Host 在同一进程运行，不是独立网络容器。

## 2. 当前容器清单（Current）

| 容器 | 技术 | 职责 | 默认端口/存储 |
|---|---|---|---|
| `BankingAgent.Host` | ASP.NET Core Minimal API, .NET 8 | API、静态 UI、认证、插件装配、Agent 路由/编排 | `5243` |
| `MockBank.Api` | ASP.NET Core Minimal API, .NET 8 | 模拟账户、转账、账单、卡片、理财核心接口 | `5200`，进程内数据 |
| SQLite | EF Core SQLite | Host 业务与审计持久化 | 默认两个本地文件 |

## 3. 目标容器视图（Target）

```
┌────────────────────────────────────────────────────────────────────────────┐
│                                                                            │
│                       AI Banking Agent System                              │
│                                                                            │
│  ┌──────────────┐   ┌──────────────┐   ┌──────────────┐   ┌──────────────┐│
│  │              │   │              │   │              │   │              ││
│  │  Web/Mobile  │   │  IM Bot      │   │  Admin       │   │  Auditor     ││
│  │  (Next.js)   │   │  Adapter     │   │  Console     │   │  Console     ││
│  │              │   │              │   │              │   │              ││
│  └──────┬───────┘   └──────┬───────┘   └──────┬───────┘   └──────┬───────┘│
│         │                  │                  │                  │       │
│         └──────────────────┼──────────────────┼──────────────────┘       │
│                            │   HTTPS / WSS                              │
│                            ▼                                            │
│  ┌────────────────────────────────────────────────────────────────────┐  │
│  │                       API Gateway (Kong/Traefik)                     │  │
│  │         · JWT 鉴权    · 限流    · 路由    · 协议转换                  │  │
│  └─────────────────────────────┬──────────────────────────────────────┘  │
│                                ▼                                        │
│  ┌────────────────────────────────────────────────────────────────────┐  │
│  │                                                                       │  │
│  │                   Agent Core (.NET 8 + Python 3.11)                  │  │
│  │                                                                       │  │
│  │  ┌──────────────────────────────────────────────────────────────┐  │  │
│  │  │  Core Layer (.NET)                                            │  │  │
│  │  │  · EventBus · PluginRegistry · FeatureFlag · ComplianceGuard │  │  │
│  │  └──────────────────────────────────────────────────────────────┘  │  │
│  │                                                                       │  │
│  │  ┌──────────────────────────────────────────────────────────────┐  │  │
│  │  │  Bounded Contexts (.NET)                                       │  │  │
│  │  │  AgentOrchestration · Conversation · Transfer · BillAnalysis    │  │  │
│  │  │  Wealth · CardManagement · Subscription · CrossScenario         │  │  │
│  │  │  Memory&Profile · Audit                                         │  │  │
│  │  └──────────────────────────────────────────────────────────────┘  │  │
│  │                                                                       │  │
│  │  ┌──────────────────────────────────────────────────────────────┐  │  │
│  │  │  AI Service (Python)                                           │  │  │
│  │  │  · Prompt Management · LLM Gateway · Agent Skills · Embedding  │  │  │
│  │  └──────────────────────────────────────────────────────────────┘  │  │
│  │                                                                       │  │
│  └─────┬─────────────┬─────────────┬─────────────┬─────────────┬─────────┘  │
│        │             │             │             │             │            │
└────────┼─────────────┼─────────────┼─────────────┼─────────────┼────────────┘
         ▼             ▼             ▼             ▼             ▼
   ┌─────────┐   ┌─────────┐   ┌─────────┐   ┌─────────┐   ┌─────────┐
   │PostgreSQL│   │  Redis   │   │ VectorDB │   │ Kafka   │   │  LLM    │
   │(主库)  │   │(缓存)   │   │(pgvector)│   │(事件)  │   │Providers│
   └─────────┘   └─────────┘   └─────────┘   └─────────┘   └─────────┘
   [数据层]       [缓存]         [向量检索]    [消息]        [AI推理]
```

---

## 4. 目标容器清单（Target）

### 2.1 前端容器

| 容器 | 技术栈 | 职责 | 部署 |
|------|--------|------|------|
| **Web/Mobile** | Next.js 14 (App Router) + TypeScript + Tailwind | 客户面向：APP/H5 | Vercel / 阿里云 ESA |
| **IM Bot Adapter** | Node.js + WebSocket | 飞书/钉钉/企微消息桥接 | K8s |
| **Admin Console** | Next.js 14 + Ant Design Pro | 内部运营管理 | K8s（内网） |
| **Auditor Console** | Next.js 14 + TanStack Table | 合规审计工作台 | K8s（隔离环境） |

### 2.2 中间层

| 容器 | 技术 | 职责 |
|------|------|------|
| **API Gateway** | Kong / APISIX | 统一入口、限流、鉴权、协议转换 |

### 2.3 核心服务

| 容器 | 技术 | 职责 |
|------|------|------|
| **Agent Core (.NET)** | .NET 8 + Aspire | 业务核心（Clean Architecture） |
| **AI Service (Python)** | Python 3.11 + FastAPI + Semantic Kernel | LLM 网关、Skill 执行、嵌入 |

### 2.4 数据层

| 容器 | 技术 | 用途 |
|------|------|------|
| **PostgreSQL 16** | 含 pgvector | 业务数据 + 向量检索 |
| **Redis 7** | | 会话、限流、缓存 |
| **Kafka** | （可选） | 事件总线后端（生产环境） |
| **LLM Providers** | Qwen3 / DeepSeek / GPT-4o | 模型推理 |

---

## 5. 目标容器间通信矩阵（Target）

| From → To | 协议 | 鉴权 | 备注 |
|-----------|------|------|------|
| Web/Mobile → API Gateway | HTTPS / WSS | JWT | 长连接用 WSS |
| IM Bot → API Gateway | HTTPS | Bot Token | Webhook |
| Admin → API Gateway | HTTPS | RBAC + 2FA | 内网 + MFA |
| API Gateway → Agent Core | gRPC / REST | mTLS | 内网 |
| Agent Core → AI Service | gRPC | mTLS | 高频低延迟 |
| Agent Core → PostgreSQL | TCP | 密码 | 持久连接池 |
| Agent Core → Redis | TCP | 密码 | 短连接 |
| Agent Core → Kafka | TCP | SASL | 异步事件 |
| Agent Core → LLM Providers | HTTPS | API Key | 通过 AI Service 代理 |
| Admin → Audit 模块 | gRPC | mTLS | 只读 |

---

## 6. 目标数据流向详解（Target）

### 4.1 用户对话请求

```
[Client] → HTTPS → [Gateway] → REST → [Agent Core]
                                       │
                                       ├── (1) 调用 [AI Service] 解析意图
                                       │       └── HTTPS → [LLM Provider]
                                       │
                                       ├── (2) 通过 [EventBus] 路由到目标 Context
                                       │
                                       ├── (3) 调用外部 [CBS] 执行交易
                                       │
                                       └── (4) 写 [Audit] 日志
                                               └── Kafka → [Audit Store]
```

### 4.2 反洗钱报告

```
[Transfer Context] 
       │ (业务执行后)
       ▼
[ComplianceGuard] (拦截，检查规则)
       │
       ▼
[Audit Context] (写日志)
       │
       ▼
[Kafka Topic: aml-events] 
       │
       ▼
[AML Report Service] (异步生成报告)
       │
       ▼
[监管报送系统] (HTTPS, mTLS)
```

---

## 7. 目标部署拓扑（Target，尚未落地）

```
┌──────────────────────────────────────────────────────────────────┐
│                    生产 K8s 集群 (阿里云 ACK)                       │
│                                                                  │
│  Namespace: prod-agent                                            │
│  ┌────────────────────────┐  ┌────────────────────────┐            │
│  │  agent-core (.NET)      │  │  ai-service (Python)   │            │
│  │  Deployment: 3 副本    │  │  Deployment: 3 副本    │            │
│  │  HPA: 50% CPU/内存      │  │  HPA: 70% CPU           │            │
│  └────────────────────────┘  └────────────────────────┘            │
│                                                                  │
│  ┌────────────────────────┐  ┌────────────────────────┐            │
│  │  im-bot                │  │  admin / auditor       │            │
│  │  Deployment: 2 副本    │  │  Deployment: 2 副本    │            │
│  └────────────────────────┘  └────────────────────────┘            │
│                                                                  │
│  Namespace: prod-data                                            │
│  ┌────────────────────────┐  ┌────────────────────────┐            │
│  │  PostgreSQL (主)        │  │  PostgreSQL (备)          │            │
│  │  主库 RDS 高可用         │  │  跨可用区只读            │            │
│  └────────────────────────┘  └────────────────────────┘            │
│  ┌────────────────────────┐  ┌────────────────────────┐            │
│  │  Redis Cluster         │  │  Kafka Cluster         │            │
│  └────────────────────────┘  └────────────────────────┘            │
└──────────────────────────────────────────────────────────────────┘
```

详见 [`../04-operations/01-deployment-architecture.md`](../04-operations/01-deployment-architecture.md)

---

## 8. Current 通信补充

| From → To | 协议/机制 | 说明 |
|---|---|---|
| 静态 UI → Host | HTTP JSON | 同一 Host 提供页面和 API |
| Host → MockBank.Api | HTTP | `ICoreBankClient`，默认 BaseUrl `http://localhost:5200` |
| 插件 → 插件 | `IEventPublisher` | 进程内 `InMemoryEventBus`；当前仅明确发布 `transfer.completed` |
| Host/Base → SQLite | EF Core | 默认 `EnsureCreated`；生产可配置 Migrate |
| Host → LLM | OpenAI-compatible HTTPS | 可选；未配置密钥时不调用 |

## 9. 关联文档

- **Level 1 上下文**：[`01-c4-context.md`](01-c4-context.md)
- **Level 3 组件视图**：[`03-c4-components.md`](03-c4-components.md)
- **系统总览**：[`00-overview.md`](00-overview.md)
- **事件契约**：[`07-event-driven-contract.md`](07-event-driven-contract.md)