# 部署架构（Deployment Architecture）

> **状态**：评审中 · **所有者**：平台 · **版本**：v1.0
> **最后更新**：2026-09-21

---

## 0. 现状与目标边界

> **Current（源码可验证）**：当前可运行主线为 .NET 8 模块化单体 `BankingAgent.Host`（HTTP `localhost:5243`）调用 `MockBank.Api`（HTTP `localhost:5200`）。默认使用 SQLite 文件库、`EnsureCreated` 初始化和进程内事件总线；宿主内置静态控制台。仓库未提供已部署的 K8s、PostgreSQL HA、Redis、Kafka、Service Mesh 或云上生产环境证据。
>
> **Target / Runbook（规划）**：下文 K8s、ACK/TKE、PostgreSQL HA、Redis、Kafka、Kong、Istio、云资源规格、域名、备份与 RTO/RPO 均为生产目标或示例配置，不代表已经采购、部署或演练。执行前必须由实际环境负责人补齐地址、凭据、资源清单和验证记录。

### 0.1 当前运行拓扑

```text
客户端
  └─HTTP→ BankingAgent.Host :5243
             ├─HTTP→ MockBank.Api :5200
             ├─SQLite（业务表）
             ├─SQLite audit_events（独立 DbContext）
             └─HMAC 链 JSONL（logs/audit-chain.log）
```

## 1. 总体拓扑

> **Target**：本节为生产目标拓扑。

```
                              ┌────────────────────────┐
                              │    用户（IM/APP/H5）   │
                              └───────────┬────────────┘
                                          │ HTTPS / WSS
                                          ▼
              ┌─────────────────────────────────────────────────────┐
              │         CDN / WAF / API Gateway (Kong)             │
              │   · TLS 终止 · JWT 鉴权 · 限流 · 路由              │
              └────────────────────┬─────────────────────────────┘
                                   │
                                   ▼
              ┌─────────────────────────────────────────────────────┐
              │              Kubernetes Cluster (生产)               │
              │                                                     │
              │   Namespace: prod-agent                            │
              │   ┌─────────────────────────────────────────────────┐ │
              │   │  agent-core (.NET 8)              Replicas: 3  │ │
              │   │  ai-service (Python 3.11)          Replicas: 3  │ │
              │   │  im-bot (Node.js)                  Replicas: 2  │ │
              │   │  admin / auditor                    Replicas: 2  │ │
              │   └─────────────────────────────────────────────────┘ │
              │                                                     │
              │   Namespace: prod-data                              │
              │   ┌─────────────────────────────────────────────────┐ │
              │   │  PostgreSQL HA (主备)  Redis Cluster  Kafka   │ │
              │   └─────────────────────────────────────────────────┘ │
              └────────────────────┬─────────────────────────────┘
                                   │
                                   ▼
              ┌─────────────────────────────────────────────────────┐
              │            外部服务                                  │
              │  · 核心银行系统 CBS (内网 API)                      │
              │  · 银联 / 网联 / 央行清算                           │
              │  · 通义千问 / DeepSeek / GPT-4o (LLM API)         │
              │  · 第三方商家（美团/京东/支付宝）                     │
              │  · 监管报送系统（央行反洗钱中心）                    │
              └─────────────────────────────────────────────────────┘
```

---

## 2. 集群架构（Target）

### 2.1 生产环境（K8s on 阿里云 ACK）

| 组件 | 规格 | 数量 |
|------|------|------|
| Master 节点 | 4 vCPU + 8GB | 3 |
| Worker 节点（系统） | 8 vCPU + 16GB | 3 |
| Worker 节点（数据） | 16 vCPU + 32GB | 3 |
| PostgreSQL | RDS 高可用 | 1 主 2 备 |
| Redis | 阿里云 Redis Cluster | 3 主 3 从 |
| Kafka | 阿里云 Kafka | 3 broker |

### 2.2 Staging 环境（K8s on 腾讯云 TKE）

简化版（共享 PostgreSQL）：

| 组件 | 规格 |
|------|------|
| Worker 节点 | 4 vCPU + 8GB × 2 |
| PostgreSQL | 单实例 |
| Redis | 单实例 |
| Kafka | 单实例 |

### 2.3 Dev 环境（Docker Compose）

详见 [`../03-development/06-environment-setup.md`](../03-development/06-environment-setup.md)。

---

## 3. K8s 命名空间与资源（Target 示例）

### 3.1 命名空间

| Namespace | 用途 |
|-----------|------|
| `prod-agent` | 生产业务容器 |
| `prod-data` | 生产数据服务 |
| `prod-network` | 网络（Ingress、Service Mesh） |
| `staging-agent` | 预发业务 |
| `staging-data` | 预发数据 |
| `monitoring` | Prometheus、Grafana、Jaeger |

### 3.2 资源配额

```yaml
# agent-core Deployment
apiVersion: apps/v1
kind: Deployment
metadata:
  name: agent-core
  namespace: prod-agent
spec:
  replicas: 3
  selector:
    matchLabels:
      app: agent-core
  template:
    metadata:
      labels:
        app: agent-core
        version: v1.2.3
    spec:
      containers:
      - name: agent-core
        image: registry.bankagent.com/agent-core:v1.2.3
        ports:
        - containerPort: 5000
        env:
        - name: ASPNETCORE_ENVIRONMENT
          value: Production
        - name: ConnectionStrings__Postgres
          valueFrom:
            secretKeyRef:
              name: agent-core-secrets
              key: postgres-connection
        resources:
          requests:
            cpu: 500m
            memory: 1Gi
          limits:
            cpu: 2000m
            memory: 4Gi
        livenessProbe:
          httpGet:
            path: /health
            port: 5000
          initialDelaySeconds: 30
          periodSeconds: 10
        readinessProbe:
          httpGet:
            path: /health/ready
            port: 5000
          initialDelaySeconds: 10
          periodSeconds: 5
```

### 3.3 HPA

```yaml
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: agent-core-hpa
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: agent-core
  minReplicas: 3
  maxReplicas: 10
  metrics:
  - type: Resource
    resource:
      name: cpu
      target:
        type: Utilization
        averageUtilization: 70
  - type: Resource
    resource:
      name: memory
      target:
        type: Utilization
        averageUtilization: 80
  behavior:
    scaleUp:
      stabilizationWindowSeconds: 30
    scaleDown:
      stabilizationWindowSeconds: 300  # 慢速收缩避免抖动
```

---

## 4. 数据层（Target）

### 4.1 PostgreSQL

- **生产**：阿里云 RDS for PostgreSQL 16
  - 高可用（一主两备）
  - 跨可用区部署
  - 自动备份（每日全量 + 实时 binlog）
  - 性能监控

- **Schema 隔离**：每个 Context 一个 Schema

### 4.2 Redis

- **生产**：阿里云 Redis Cluster（3 节点）
- 用途：会话缓存、限流、FeatureFlag 缓存、Idempotency-Key 存储

### 4.3 Kafka

- **生产**：阿里云 Kafka 3.x
- 用途：事件总线后端（ADR-0003，未来迁移）
- Topic 命名：`bank-agent.<context>.<event>.v<version>`

### 4.4 向量数据库（pgvector）

- 复用 PostgreSQL
- Memory Agent 的用户关系、对话历史向量化

---

## 5. 网络（Target）

### 5.1 Ingress（Kong / APISIX）

```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: agent-ingress
  namespace: prod-network
  annotations:
    cert-manager.io/cluster-issuer: letsencrypt-prod
spec:
  tls:
  - hosts:
    - api.bankagent.com
    secretName: api-tls
  rules:
  - host: api.bankagent.com
    http:
      paths:
      - path: /
        pathType: Prefix
        backend:
          service:
            name: agent-core
            port:
              number: 5000
```

### 5.2 Service Mesh（Istio）

- 所有内部服务 mTLS 加密
- 流量管理（金丝雀、A/B 测试）
- 可观测性（自动生成 Trace）

### 5.3 防火墙规则

- ✅ 允许：API Gateway → Agent Core
- ✅ 允许：Agent Core → CoreBank（内网专线）
- ✅ 允许：Agent Core → 央行反洗钱系统（mTLS）
- ❌ 禁止：Agent Core 直接访问外部互联网（走 LLM API 代理）
- ❌ 禁止：Agent Core 直接暴露公网

---

## 6. 监控与日志（Target）

详见 [`02-monitoring-observability.md`](02-monitoring-observability.md)。

---

## 7. 备份与容灾（Target）

### 7.1 数据库备份

- **PostgreSQL**：自动每日全量备份 + binlog 实时归档
- 保留：30 天
- 异地备份：每日传输到 OSS

### 7.2 灾难恢复（DR）

详见 [`04-disaster-recovery.md`](04-disaster-recovery.md)。

| 等级 | 场景 | RTO | RPO |
|------|------|-----|-----|
| L1 | 单实例故障 | < 1 分钟 | 0 |
| L2 | K8s 集群故障 | < 15 分钟 | < 5 分钟 |
| L3 | 数据库故障 | < 30 分钟 | < 1 分钟 |
| L4 | 区域级灾难 | < 4 小时 | < 1 小时 |

> 以上指标尚无当前环境的部署或演练证据，不能作为现行 SLA。

---

## 8. 关联文档

- **监控**：[`02-monitoring-observability.md`](02-monitoring-observability.md)
- **事故响应**：[`03-incident-response.md`](03-incident-response.md)
- **灾难恢复**：[`04-disaster-recovery.md`](04-disaster-recovery.md)
- **CI/CD**：[`../03-development/05-ci-cd-pipeline.md`](../03-development/05-ci-cd-pipeline.md)