# ⚙️ 平台 / Platform Engineer 分册

> **角色定位**：基础设施、CI/CD、环境的守护者
> **主战场**：`src/BankingAgent.slnx` 仓库现有 CI/脚本与运行配置、`docs/04-operations/`
> **更新时间**：2026-10-01
> **口径**：当前先保障 .NET 8 解决方案、Host/MockBank 和测试程序；K8s、Helm、Terraform、PostgreSQL HA 与完整观测平台均为 Target。

---

## 1. 你是什么

你是 5 人团队里**让系统能跑起来**的人。Kubernetes、CI/CD、监控、数据库、备份——这些都是你的责任。

你不一定写业务逻辑，但你要保证：
- 开发者拉代码 → 5 分钟跑起来
- CI 全绿 → 自动部署
- 监控告警 → 5 分钟内有人响应
- 故障发生 → 30 分钟恢复
- 季度演练 → 不掉链子

你是 5 人里**最懂基础设施**的人，也是 **On-call 第一线**。

---

## 2. 你的文档清单（按重要性排序）

### 2.1 必须精通 ⭐⭐⭐

| # | 文档 | 用途 |
|---|------|------|
| 1 | [`04-operations/01-deployment-architecture.md`](../../04-operations/01-deployment-architecture.md) | **部署架构（你守的）** |
| 2 | [`04-operations/02-monitoring-observability.md`](../../04-operations/02-monitoring-observability.md) | **监控与可观测性（你建的）** |
| 3 | [`04-operations/03-incident-response.md`](../../04-operations/03-incident-response.md) | **事故响应（你是 SME）** |
| 4 | [`04-operations/04-disaster-recovery.md`](../../04-operations/04-disaster-recovery.md) | **容灾与备份** |
| 5 | [`03-development/05-ci-cd-pipeline.md`](../../03-development/05-ci-cd-pipeline.md) | **CI/CD 流水线** |
| 6 | [`03-development/06-environment-setup.md`](../../03-development/06-environment-setup.md) | **开发环境搭建** |
| 7 | [`05-security-compliance/04-audit-logging.md`](../../05-security-compliance/04-audit-logging.md) | 审计日志基础设施 |

### 2.2 必须了解 ⭐⭐

| # | 文档 | 用途 |
|---|------|------|
| 8 | [`00-architecture/02-c4-containers.md`](../../00-architecture/02-c4-containers.md) | 容器视图 |
| 9 | [`03-development/02-git-workflow.md`](../../03-development/02-git-workflow.md) | Git 工作流（CI 触发） |
| 10 | [`06-product/03-release-process.md`](../../06-product/03-release-process.md) | 发布流程（你执行灰度） |
| 11 | [`05-security-compliance/01-threat-model.md`](../../05-security-compliance/01-threat-model.md) | 基础设施威胁 |
| 12 | [`07-team/02-collaboration.md`](../02-collaboration.md) | On-call 排班 |

### 2.3 偶读 ⭐

| # | 文档 | 用途 |
|---|------|------|
| 13 | [`05-security-compliance/02-compliance-matrix.md`](../../05-security-compliance/02-compliance-matrix.md) | 日志留存合规要求 |
| 14 | [`05-security-compliance/03-data-classification.md`](../../05-security-compliance/03-data-classification.md) | 备份加密 |
| 15 | [`03-development/04-testing-strategy.md`](../../03-development/04-testing-strategy.md) | 集成测试环境 |

---

## 3. 你负责的代码模块

当前责任面：

- `src/BankingAgent.slnx` 的 restore/build/test 基线；
- 四插件构建顺序、Host 输出目录与 PluginValidator；
- Host `:5243`、MockBank `:5200` 的启动配置；
- 默认 SQLite + EnsureCreated 的开发体验，以及 EF Core Migration 生产化路径；
- `E2ETest`、`StressTest`、`LoadTest` 独立程序的运行编排和结果留证。

**Target**：`infra/k8s`、Helm、Terraform、Prometheus/Grafana、ELK、Jaeger、PostgreSQL HA、Redis/Kafka、备份与 GitOps。只有仓库出现真实目录和流水线后，才能视为当前平台资产。

---

## 4. 你的工作职责

### 4.1 集群与基础设施

> **Target 职责**；当前仓库未证明存在对应集群。

- 生产 K8s 集群维护（升级、调优）
- 预发 / 测试集群维护
- 数据库高可用（Patroni）
- Redis Cluster / Kafka 集群维护
- 存储、网络、负载均衡

### 4.2 CI/CD

> 当前先维护实际仓库 CI；镜像、Helm 与自动部署是 Target。

- 流水线维护（GitHub Actions / GitLab CI）
- 镜像构建 + 推送
- Helm Chart 版本管理
- 自动部署到预发
- 手动灰度发布到生产

### 4.3 可观测性

> **Target 职责**。

- Prometheus + Grafana 维护
- ELK 日志栈维护
- Jaeger 链路追踪维护
- 告警规则维护
- 业务大盘设计

### 4.4 数据库运维

- 当前：SQLite 开发基线、EF Core 配置与迁移命令可复现
- Target：PostgreSQL 备份与恢复
- Redis 持久化
- 慢查询治理
- Schema Migration 协助
- 索引优化

### 4.5 On-call（轮值 SLA）

| 维度 | 要求 |
|------|------|
| 响应时间 | 5 分钟内 |
| 在线要求 | 7×24 |
| 备机 | 笔记本 + 4G |
| 升级机制 | P0 升级架构师 |

---

## 5. 你的关键产出

### 5.1 每周

- **On-call 值班**（每人 1 周）
- **On-call 日报**（每日 18:00 在 `#oncall` 群）
- 监控大盘维护
- ≥ 1 项基础设施优化

### 5.2 每月

- **季度 DR 演练**准备（每季度 1 次）
- **月度告警 review**（降低噪音）
- **成本优化**（云资源）

### 5.3 每季度

- **灾备切换演练**（一次完整 DR）
- **On-call 轮换 review**
- **基础设施升级规划**

---

## 6. 你的协作接口

| 你对接 | 对接什么 | 频率 |
|--------|---------|------|
| **架构师** | 部署架构、容量规划 | 每周 1-2 次 |
| **业务开发** | CI 配置、本地环境、Migration | 每日 |
| **AI/数据** | GPU 资源、模型部署、Token 监控 | 每周 1 次 |
| **安全合规** | 备份加密、审计日志基础设施 | 每周 1 次 |
| **所有人** | On-call、事故响应 | 随时 |

---

## 7. 目标生产 SLA（Target，尚无当前部署证据）

| 指标 | 目标 |
|------|------|
| 生产可用性 | ≥ 99.95% |
| RTO（恢复时间） | ≤ 30 分钟 |
| RPO（数据丢失） | ≤ 5 分钟 |
| CI 流水线时长 | ≤ 10 分钟 |
| 告警响应 | ≤ 5 分钟 |
| 数据库备份成功率 | 100% |
| 监控覆盖率 | 100% |

---

## 8. 生产运行纪律（Target）

1. **生产操作必须双人复核**（除紧急 On-call）
2. **所有变更走 GitOps**（Git 是唯一真相源）
3. **Backups 必须定期验证**（每月 1 次恢复演练）
4. **告警必须有响应人**（无主告警 = 隐患）
5. **On-call 必须 7×24 在线**
6. **基础设施变更必须通知**（#dev 群通告）
7. **必须留 Runbook**（任何修复都要文档化）

---

## 9. 关键技术栈

| 技术 | 用途 | 学习资源 |
|------|------|---------|
| **Kubernetes** | 容器编排 | kubernetes.io/docs |
| **Helm + Kustomize** | K8s 配置管理 | helm.sh |
| **Argo Rollouts** | 蓝绿/灰度发布 | argoproj.io |
| **Docker** | 容器 | docker.com |
| **当前：SQLite + EF Core** | 本地持久化与模型验证 | 微软文档 |
| **Target：PostgreSQL + Patroni** | 数据库 HA | patroni.readthedocs.io |
| **Redis Cluster** | 缓存 | redis.io |
| **Kafka** | 消息队列 | kafka.apache.org |
| **Prometheus + Grafana** | 监控 | prometheus.io |
| **ELK（Elasticsearch + Logstash + Kibana）** | 日志 | elastic.co |
| **Jaeger + OpenTelemetry** | 链路追踪 | jaegertracing.io |
| **GitHub Actions / GitLab CI** | CI/CD | 官方文档 |
| **Terraform / Pulumi** | IaC | terraform.io |

---

## 10. 目标平台 Runbook（Target）

| 场景 | 操作 | RTO |
|------|------|-----|
| **Pod OOM** | kubectl describe + 调 limits | 30s |
| **Pod CrashLoopBackOff** | kubectl logs + 回滚镜像 | 1min |
| **数据库主从切换** | patronictl failover | 5min |
| **Redis 节点故障** | 自动恢复，监控 | 30s |
| **Kafka 积压** | 扩容 consumer + 调 partition | 5min |
| **磁盘满** | 扩容 + 清理日志 | 5min |
| **证书过期** | cert-manager 自动续期 | — |
| **LB 异常** | 切流量到备用 | 1min |
| **K8s 节点故障** | 自动迁移 + 监控 | 5min |
| **ELK 不可用** | 切到备用日志通道 | 5min |

---

## 11. 你的工作日历（典型一周）

| 日 | 活动 |
|----|------|
| 周一 | On-call 交接（上周→本周）、本周发布清单确认 |
| 周二 | 常规发布协助、监控 review |
| 周三 | 数据库 / 备份维护、文档更新 |
| 周四 | 常规发布协助、CI/CD 优化 |
| 周五 | 周报、基础设施优化、本周回顾 |
| 周末 | 值班（如本周 On-call） |

---

## 12. 学习资源

### 12.1 必读

- Brendan Burns《Kubernetes 权威指南》
- Kubernetes 官方文档
- Prometheus 官方文档
- Google SRE Book（免费）

### 12.2 推荐

- Cloudflare / GitHub 的 Postmortem 公开案例
- Brendan Gregg《性能之巅》

---

## 13. FAQ

### Q1：半夜被叫醒怎么办？

**响应 + 解决 + 写 Postmortem + 调休**。调休政策见 [`../07-team/02-collaboration.md`](../02-collaboration.md)。

### Q2：CI 流水线挂了怎么办？

看 CI 日志 → 定位失败 stage → 修复 → 重跑。**不绕过 CI**（除非 hotfix，事后补）。

### Q3：数据库主库挂了怎么办？

Patroni 自动切换（5-15s）。确认 → 通知 → Postmortem。

### Q4：监控告警太多怎么办？

**收敛 + 静默**：
- 同规则 5 分钟内不重复发
- 维护期静默
- 严重告警抑制次要告警

### Q5：成本超预算怎么办？

- 大实例换成小实例 + 弹性
- 清理无用资源（僵尸 Pod）
- 存储降级（冷数据归档 OSS）

---

## 14. 联系

- Slack / 飞书：@platform
- On-call 群：`#oncall`
- Mentor：架构师（兼资深 SRE）