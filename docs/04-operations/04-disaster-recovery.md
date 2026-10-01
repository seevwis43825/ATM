# 容灾与备份（Disaster Recovery）

> **状态**：评审中 · **所有者**：平台 · **版本**：v1.0
> **最后更新**：2026-09-21

---

## 0. 现状与目标边界

> **Current（源码可验证）**：默认是单机 SQLite + `EnsureCreated`，没有仓库内可验证的自动备份、PITR、跨 AZ/Region、PostgreSQL 主从、Redis/Kafka 复制、KMS/Vault 或 WORM 归档。审计当前双写本地 HMAC 链 JSONL 与独立 `audit_events`；任一路径写失败记录 Critical，但不阻断业务。
>
> **Target / Runbook（规划）**：本文的 SLA、RTO/RPO、K8s 多副本、PostgreSQL HA、异地温备、PITR、Vault/KMS 和演练频率均为生产目标。未完成部署和恢复演练前，不得宣称已达到这些指标。

## 1. 目标

> 下表为 **Target SLO**，不是当前承诺。

| 指标 | 目标 |
|------|------|
| **RTO（Recovery Time Objective）** | P0 系统 ≤ 30 分钟 |
| **RPO（Recovery Point Objective）** | 数据库 ≤ 5 分钟（异步复制延迟）；资金类 ≤ 0（强同步） |
| **可用性 SLA** | 生产 ≥ 99.95%（年宕机时间 ≤ 4.38 小时） |
| **数据保留** | 业务数据 ≥ 5 年；审计日志 ≥ 5 年；备份保留 ≥ 1 年 |

---

## 2. 故障场景分级

| 场景 | RTO | RPO | 应对方案 |
|------|-----|-----|---------|
| **单实例故障** | 30 秒 | 0 | K8s 自动重启/迁移 |
| **单服务整体故障** | 2 分钟 | 0 | 多副本 + 健康检查 |
| **AZ（可用区）故障** | 5 分钟 | 0 | 跨 AZ 多副本 + LB 自动切换 |
| **Region（地域）故障** | 30 分钟 | ≤ 5 分钟 | 异地灾备 + DNS 切换 |
| **数据库主库损坏** | 5 分钟 | 0（强同步）/ ≤ 5 分钟（异步） | Patroni 自动主从切换 |
| **数据库整库丢失** | 1 小时 | ≤ 5 分钟 | 从备份恢复 + binlog replay |
| **人为误操作（删库/改数据）** | 1 小时 | 恢复到操作前 | 时间点恢复（PITR） |
| **勒索软件/灾难** | 4 小时 | ≤ 24 小时 | 异地冷备份恢复 |

---

## 3. 多 AZ 高可用（Target）

### 3.1 生产拓扑（同 Region 三 AZ）

```
                     Region: 华东1（杭州）
        ┌────────────────────────────────────────┐
        │                                        │
   AZ-A │ AZ-B │ AZ-C（每个 AZ 独立机房+供电+网络）│
        │      │      │                          │
   agent-core  Pod × 3 副本（每 AZ 1 个）         │
   ai-service  Pod × 3 副本                       │
   im-bot      Pod × 2 副本                       │
        │      │      │                          │
   PG-primary (AZ-A) → PG-sync-standby (AZ-B)     │
                  → PG-async-standby (AZ-C)      │
        │      │      │                          │
   Redis Cluster 6 节点（双 AZ）                  │
   Kafka 3 节点（每 AZ 1 副本）                  │
        └────────────────────────────────────────┘
```

### 3.2 流量分配

- API Gateway / SLB 在三个 AZ 间负载均衡
- 健康检查自动摘除异常 AZ 的 Pod
- 数据库主库在 AZ-A，但同步备库在 AZ-B（同步复制，RPO=0）

---

## 4. 异地灾备（Target）

### 4.1 异地 Region 备份

- **主 Region**：华东 1（杭州）
- **灾备 Region**：华东 2（上海）或 华南 1（深圳）
- **距离**：≥ 100 km，独立电网、独立机房

### 4.2 灾备方式：**异地温备**

| 数据 | 同步方式 | 频率 | 延迟 |
|------|---------|------|------|
| PostgreSQL 数据 | 物理备份 + WAL 异步传输 | 实时 | ≤ 5 分钟 |
| Redis | 异步复制 | 实时 | ≤ 1 分钟 |
| Kafka Mirror | MirrorMaker2 | 实时 | ≤ 1 分钟 |
| 上传文件 | OSS 跨区域复制 | 实时 | ≤ 1 分钟 |
| 审计日志 | 跨区归档到 OSS | 每 10 分钟 | ≤ 10 分钟 |
| 配置中心 | Apollo 多 Region 同步 | 实时 | ≤ 1 分钟 |

### 4.3 灾备切换（DR Drill）

**频率**：每年 2 次（3 月、9 月）

**流程**：
1. 提前 1 周通告（凌晨 2-4 点）
2. 启动灾备 Region 服务
3. DNS 切到灾备 Region（TTL 已预设 60s）
4. 验证关键业务：转账、查询、对账
5. 30 分钟内回切到主 Region
6. 形成 DR 报告

---

## 5. 数据库备份（Target）

### 5.1 备份策略（3-2-1 原则）

- **3 份副本**：1 份线上主库 + 1 份本地备份 + 1 份异地 OSS
- **2 种介质**：本地 SSD + 异地 OSS
- **1 份异地**：OSS 华南 Region

### 5.2 备份类型与频率

| 备份类型 | 频率 | 保留期 | 用途 |
|---------|------|--------|------|
| **全量备份** | 每日 1 次（凌晨 3 点） | 30 天本地 + 1 年 OSS | 整库恢复 |
| **WAL 归档** | 实时 | 7 天 | 时间点恢复 |
| **Schema 级备份** | 每周 | 1 年 OSS | 单 Schema 恢复 |
| **配置备份** | 每次变更 | 永久 | 配置回滚 |
| **审计日志备份** | 实时同步 | 5 年 | 合规要求 |

### 5.3 备份验证

**每月 1 次自动恢复演练**：
- 随机选一天的全量备份
- 在预发环境恢复
- 跑通核心业务验证脚本
- 报告恢复时长和数据完整性

---

## 6. 时间点恢复（Target：PostgreSQL）

### 6.1 适用场景

- 误删表/数据
- 错误的数据迁移/更新
- 勒索软件（恢复到加密前）

### 6.2 流程

```
1. 立即停止该数据库的所有写入（k8s 摘除流量）
2. 评估恢复时间点
3. 在隔离环境恢复备份 + replay WAL 到目标时间点
4. 数据比对、确认正确
5. 双写确认（先写到备库，确认后再切流量）
6. 业务验证
7. 恢复写流量
8. 形成事件 Postmortem
```

### 6.3 工具

- **pg_basebackup** + **pgBackRest**（推荐）
- **WAL-G**（云原生方案）

---

## 7. 配置与密钥备份（Target）

### 7.1 配置（Apollo）

- Apollo ConfigDB 单独备份（每日全量 + 实时 binlog）
- 配置变更记录入 git（GitOps 模式）

### 7.2 密钥（KMS + Vault）

- 生产密钥通过 **阿里云 KMS** 管理
- 数据库密码、API Key 等存储在 HashiCorp Vault
- Vault 自身做 HA + 异地灾备

### 7.3 紧急访问

**Break Glass 流程**（紧急情况下获取生产权限）：
- 必须在事故频道申请
- 架构师 + 合规双审批
- 所有操作录像 + 审计日志强制记录
- 事后 24h 内 review

---

## 8. 应用层容灾（Target）

### 8.1 限流（Rate Limit）

- API Gateway 层：1000 QPS / 用户、10000 QPS / IP
- 服务层：保护下游 DB（令牌桶）

### 8.2 熔断（Circuit Breaker）

- Polly（.NET 端） / resilience4j（Python 端）
- 触发条件：错误率 > 50% 且样本 > 20
- 状态：Closed → Open → Half-Open → Closed

### 8.3 降级（Degradation）

降级开关（FeatureFlag）：

| 开关 | 降级行为 |
|------|---------|
| `ai.llm.use_local_only` | 仅用本地缓存 + 规则兜底 |
| `transfer.smart_suggestion` | 关闭智能推荐，用关键字匹配 |
| `cross_scenario.enabled` | 关闭跨场景联动 |
| `ai-service.use_backup_model` | 切到备用模型 |
| `chatbot.rule_based_only` | 完全基于规则对话 |

### 8.4 重试与幂等

- **重试**：最多 3 次，指数退避（1s, 2s, 4s）
- **幂等**：所有写操作必须 `Idempotency-Key` 头，24 小时内同 key 复用结果
- **超时**：上游 30s，下游 5s，对外 10s

### 8.5 流量切走

- 蓝绿（Blue-Green）：日常发布用
- 灰度（Canary）：5% → 25% → 50% → 100%
- 故障时一键回滚：30 秒内完成

---

## 9. 数据层容灾（Target）

### 9.1 PostgreSQL（Patroni + etcd）

- 3 节点 Patroni 集群
- etcd 3 节点（用于选主）
- 主库故障自动切换（5-15s）
- HAProxy + vip 漂移

### 9.2 Redis Cluster

- 6 节点（3 主 3 从）
- 自动故障转移（< 30s）
- 持久化：AOF + RDB（混合模式）

### 9.3 Kafka

- 3 Broker + 跨 AZ 副本
- ISR ≥ 2 才 ack
- Topic 副本数 3

---

## 10. 应急工具包（Target）

### 10.1 必备脚本（运维仓库 `ops-toolkit/`）

| 脚本 | 功能 |
|------|------|
| `freeze-account.sh` | 冻结指定账户（紧急） |
| `rollback-deploy.sh` | 一键回滚最近发布 |
| `enable-feature-flag.sh` | 远程开启/关闭功能开关 |
| `restore-db-pitr.sh` | 数据库时间点恢复 |
| `dr-switchover.sh` | 灾备切换 |
| `break-glass.sh` | 紧急访问申请（自动审计） |

### 10.2 必备文档

- [ ] 网络拓扑图（含 IP 段、VIP、专线）
- [ ] 数据库密码 Vault（紧急访问路径）
- [ ] 外部服务联系人（LLM 厂商、托管商、合规）
- [ ] DR 切换 Runbook（手写步骤）

---

## 11. 演练计划（Target）

| 演练 | 频率 | 时长 | 范围 |
|------|------|------|------|
| **单实例故障恢复** | 每月 | 1 小时 | K8s Pod 重建 |
| **数据库主从切换** | 每季度 | 2 小时 | Patroni 切换 + 业务验证 |
| **AZ 故障** | 半年 | 4 小时 | 切走单 AZ 流量 |
| **完整灾备切换** | 每年 | 8 小时 | 跨 Region DR |
| **PITR 恢复** | 每季度 | 2 小时 | 模拟误删恢复 |
| **勒索软件应急** | 每年 | 4 小时 | 从冷备份恢复 |
| **断网演练** | 每年 | 2 小时 | 模拟专线中断 |

每次演练后形成报告，归档到 `docs/dr-drills/`。

> 当前仓库未见上述演练结果；表中频率不能视为已执行记录。

---

## 12. 通讯与上报

| 场景 | 上报对象 | 时限 |
|------|---------|------|
| 资金差错 ≥ 100 万 | 架构师 + 合规 + 业务 | ≤ 30 分钟 |
| 用户投诉 ≥ 1000 单 | 客服 + 产品 | ≤ 1 小时 |
| 系统宕机 ≥ 30 分钟 | 监管（央行/银保监）| ≤ 24 小时（如涉及金融服务中断） |
| 数据泄露 | 网信办 + 公安部 | ≤ 24 小时（《个保法》第 57 条） |
| 重大网络安全事件 | 网信部门 | ≤ 24 小时（《网络安全法》第 25 条） |

---

## 13. 参考

- 部署架构（环境信息）— `04-operations/01-deployment-architecture.md`
- 事故响应（具体流程）— `04-operations/03-incident-response.md`
- 监控告警（故障检测）— `04-operations/02-monitoring-observability.md`
- 合规矩阵（事故报告义务）— `05-security-compliance/02-compliance-matrix.md`
- 数据分级（不同级别数据的备份要求）— `05-security-compliance/03-data-classification.md`