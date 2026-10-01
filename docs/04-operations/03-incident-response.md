# 事故响应（Incident Response）

> **状态**：评审中 · **所有者**：平台 · **版本**：v1.0
> **最后更新**：2026-09-21

---

## 0. 适用状态

> **Current**：当前仓库仅能验证本地 `BankingAgent.Host :5243`、`MockBank.Api :5200`、SQLite、健康检查和应用日志；未发现已启用的 PagerDuty/飞书告警、K8s 回滚、FeatureFlag 管理平台、PostgreSQL 主从、Prometheus/ELK 或正式 on-call 排班证据。
>
> **Target / Runbook**：本文是生产事故响应预案。响应时限、MTTR、轮值人数、通知渠道、监管报告时限及命令均需由组织和法务确认；涉及 K8s、PostgreSQL HA、LLM 双供应商的步骤只能在对应目标设施落地后执行。

## 1. 目标

**目标 MTTR（Mean Time To Recover）≤ 30 分钟**（P0/P1；尚无演练数据证明）

把事故从「救火」变成「流程化、可演练、可改进」的工程实践。

---

## 2. 事故分级

| 级别 | 影响 | 触发条件（举例） | 响应时限 | 升级 |
|------|------|-----------------|---------|------|
| **P0** | 业务中断 / 资金风险 | 转账失败率 > 10%、账户资金计算错误、用户无法登录 | 5 分钟内首响应 | 架构师 + 全员 |
| **P1** | 核心功能受损 | 单场景全量失败、智能体全部回退到兜底 | 15 分钟 | oncall + 业务开发 |
| **P2** | 非核心功能受损 | 单功能故障、看板数据延迟 | 1 小时 | oncall |
| **P3** | 体验问题 | UI 显示异常、个别请求慢 | 下个工作日 | 工单记录 |

### 特殊事故类型（无论级别都必须升级）

| 类型 | 升级对象 |
|------|---------|
| 资金差错（账实不符） | **架构师 + 合规 + 业务开发** |
| 数据泄露疑似 | **架构师 + 合规 + 法务**（24h 内报监管） |
| 监管报送失败 | **架构师 + 合规** |
| LLM 输出违规 | **合规**（评估是否触发安全事件） |

---

## 3. 组织

### 3.1 角色

| 角色 | 职责 | 谁担任 |
|------|------|--------|
| **Incident Commander (IC)** | 总指挥，定级、拍板、宣布恢复 | 第一个响应的资深工程师 |
| **On-call 工程师** | 一线排查、止血 | 轮值（详见下） |
| **Subject Matter Expert (SME)** | 领域专家（AI/数据库/前端） | 对应角色工程师 |
| **Communications Lead (CL)** | 对外同步状态 | 产品或 PM |
| **Scribe** | 实时记录 Timeline | 自动机器人 + 人工补充 |

### 3.2 On-call 轮值（Target）

- **5 人轮值**，每人值班 7×24 一周
- 值班福利：调休 1 天
- 排班表：每月 25 号前发布下月排班（见 `07-team/02-collaboration.md`）
- 值班要求：
  - 5 分钟内响应电话/PagerDuty/飞书电话
  - 15 分钟内确认是否到达事故现场
  - 笔记本电脑 + 4G 备用网络

---

## 4. 事故生命周期

```
┌──────────┐    ┌──────────┐    ┌──────────┐    ┌──────────┐    ┌──────────┐
│  检测    │ →  │  响应    │ →  │  缓解    │ →  │  恢复    │ →  │  复盘    │
│ Detect   │    │ Respond  │    │ Mitigate │    │ Recover  │    │ Review   │
└──────────┘    └──────────┘    └──────────┘    └──────────┘    └──────────┘
   监控告警       定级+通知       止血+隔离       根因+回滚      改进+归档
```

### 4.1 检测（Detect，Target）

- Prometheus / AlertManager 自动告警
- 用户投诉（客服系统转入）
- 监控拨测异常

### 4.2 响应（Respond）

**On-call 在 5 分钟内必须完成：**

1. 确认告警真实性（点开 Grafana / Kibana）
2. 在事故频道（钉钉 `incident-response`）发送：
   > `🚨 [疑似 P?] <一句话描述> @oncall-2nd`
3. 启动事故频道（创建 Zoom/腾讯会议 + 文档协作）
4. 担任 IC 或指定 IC

### 4.3 缓解（Mitigate）

**优先级：先止血，再查根因。**

常用止血手段（按优先级）：

| 手段 | 适用场景 | 操作 |
|------|---------|------|
| **功能开关关闭** | 新功能异常 | `FeatureFlag.Disable("xxx")` |
| **版本回滚** | 新版本引入 bug | `kubectl rollout undo deployment/agent-core` |
| **流量切走** | 单实例/单 AZ 异常 | 摘除 Pod / 切走流量 |
| **降级到兜底** | 依赖服务异常 | 启用 `IChatbotFallbackService` |
| **限流** | 容量不足 | API Gateway 调低 QPS 上限 |
| **人工接管** | AI 决策不可靠 | 切到客服人工 |

### 4.4 恢复（Recover）

确认：
- [ ] 监控恢复正常（错误率、P99、QPS）
- [ ] 业务抽样验证（5 笔关键交易人工核对）
- [ ] 用户侧无新投诉（10 分钟观察窗）
- [ ] 数据一致性核对（必要时）

恢复动作完成后，IC 在事故频道宣布：
> `✅ <事故名> 已恢复。开始时间：<T0>，恢复时间：<T1>，持续 <duration>。`

### 4.5 复盘（Review）

**事故结束后 48 小时内** 完成 Postmortem。

详见下文 §6。

---

## 5. 关键 Runbook

> 以下为目标环境 Runbook 模板。当前本地环境优先使用 `/health*`、应用控制台日志、SQLite 文件备份和进程重启排查；不要在未确认集群、命名空间和数据库类型时直接执行示例命令。

### 5.1 转账大面积失败

```yaml
trigger: TransferFailureSpike / transfer_failed_total 突增
severity: P0
first_check:
  - name: 查看 CBS（核心银行系统）连通性
    cmd: |
      kubectl exec -n prod-agent deploy/agent-core -- \
        curl -sS -m 5 https://cbs.internal/health
  - name: 查看数据库连接
    cmd: kubectl logs -n prod-agent deploy/agent-core --tail 100 | grep -i "connection\|timeout"

mitigation:
  - 启用 transfer.fallback_to_manual（关闭智能转账，走原有人工流程）
  - 检查最近 1 小时发布：git log --since="1 hour ago" --oneline
  - 如新发布导致，立即回滚：kubectl rollout undo deployment/agent-core -n prod-agent

escalation:
  - 资金差错：立刻通知架构师 + 合规
  - 用户投诉 > 50：在官网发公告
```

### 5.2 LLM 服务异常

```yaml
trigger: agent_llm_error_total 错误率 > 10%
severity: P1
first_check:
  - 检查 LLM API 配额（阿里云百炼 / OpenAI 控制台）
  - 检查 LLM API Key 有效期与权限
  - 检查 ai-service 日志中的具体错误

mitigation:
  - 切到备用模型（DeepSeek ↔ Qwen3）
  - 启用 llm.local_cache（提高缓存命中率）
  - 启用 chatbot.fallback_rule_based（启用基于规则的老对话）

prevention:
  - LLM 调用必须有重试 + 熔断 + 降级（详见 ai-service 客户端封装）
```

### 5.3 数据库主从切换

> **Target only**：当前默认数据库为 SQLite，不存在 PostgreSQL 主从或 Patroni。

```yaml
trigger: pg_replication_lag_seconds > 30 / 主库不可用
severity: P0
mitigation:
  - Patroni 自动切换（5-15s 内完成）
  - 检查切换后读写是否正常
  - 通知 DBA 团队（外部支持）

post_check:
  - 比对主从数据一致性：patronictl list
  - 检查应用日志中的连接错误
```

### 5.4 数据泄露疑似

```yaml
trigger: 任何渠道接到疑似数据泄露报告
severity: P0（合规优先）
escalation_immediate:
  - 架构师
  - 合规
  - 法务
actions_first_24h:
  - 保留所有证据（日志、请求、数据库快照）
  - 评估泄露范围（PII 数量、用户范围）
  - 评估是否触发《个保法》第 57 条 通知义务
  - 评估是否触发《网络安全法》第 42 条 报告义务（向网信部门报告）
actions_first_72h:
  - 形成初步调查报告
  - 通知受影响用户（如确认泄露）
  - 向监管部门报送（如触发报告义务）
```

### 5.5 误识/误执行风险

```yaml
trigger: 用户反馈智能体做错事 / 风控告警检测到可疑交易
severity: P0（如涉及资金）/ P1（其他）
actions:
  - 立即冻结相关账户（人工 + 系统）
  - 拉取该用户最近 1 小时的审计 JSONL 与 `audit_events`；目标追踪平台落地后再关联 trace
  - 回滚可疑交易（与运营同事协作）
  - 评估是否需要回滚对应功能开关
```

---

## 6. Postmortem（事后复盘）

### 6.1 模板

每次 P0/P1 必须写 Postmortem，发布到 `docs/postmortems/` 目录。

```markdown
# Postmortem: <事故名>

- 事故 ID：IM-YYYYMMDD-001
- 级别：P0
- 开始时间：YYYY-MM-DD HH:MM
- 恢复时间：YYYY-MM-DD HH:MM
- 持续时长：<duration>
- 影响范围：<影响用户数 / 资金额 / 持续时间>
- IC：<name>
- 参与人：<name>, <name>

## 摘要（一段话说清）

## Timeline

| 时间 | 事件 | 责任人 |
|------|------|--------|
| HH:MM | 告警触发 | 系统 |
| HH:MM | On-call 响应 | 张三 |
| ... | ... | ... |

## 根因（Root Cause）

## 触发因素（Trigger）

## 为什么没拦住（Detection Gap）

## 为什么没快速恢复（Response Gap）

## 改进措施（Action Items）

| 措施 | 优先级 | 负责人 | 截止日期 |
|------|--------|--------|---------|
| 增加 Y 监控 | P1 | 张三 | 周内 |
| 优化 Z 代码 | P2 | 李四 | 下迭代 |

## 学到的教训

## 做得好的（Good）
```

### 6.2 原则

**Blameless（不指责）**：追究系统问题，不追究个人失误。问题出现说明系统没有保护好人。

---

## 7. 演练

### 7.1 季度演练（每季度 1 次）

**GameDay**：
- 随机选一个场景，由非当值人员发起
- On-call 当场响应
- 全员旁观
- 演练后形成改进项

### 7.2 演练清单（每季度轮换）

- [ ] 转账大面积失败
- [ ] LLM 服务异常
- [ ] 数据库主从切换
- [ ] 数据泄露应急
- [ ] 风控误报导致大面积拦截

---

## 8. 通讯模板

### 8.1 内部通知（事故开始）

> **【事故 P0】AI Banking Agent 转账功能大面积失败**
>
> - 影响：所有用户无法发起转账
> - 开始时间：HH:MM
> - 当前状态：调查中
> - IC：张三
> - 事故频道：钉钉 #incident-20260921-transfer
>
> 下次更新：HH:MM 或有重大进展

### 8.2 内部通知（事故恢复）

> **【恢复】AI Banking Agent 转账功能**
>
> - 恢复时间：HH:MM
> - 持续时长：35 分钟
> - 影响用户：约 12,000 人
> - 资金影响：无（失败交易全部正确拦截）
> - 根因：<简述>
> - Postmortem 将在 48 小时内发布

### 8.3 外部通知（如需用户告知）

> **尊敬的用户：**
>
> HH:MM-HH:MM，<应用名>转账功能出现短暂异常，目前已完全恢复。期间发起的转账会**自动撤销**，资金未受影响。
>
> 如有问题，请联系客服 95XXX。
>
> 再次致歉。

---

## 9. 参考

- 监控指标定义 — `04-operations/02-monitoring-observability.md`
- 部署架构（环境信息）— `04-operations/01-deployment-architecture.md`
- 合规矩阵（事故报告义务）— `05-security-compliance/02-compliance-matrix.md`
- 审计日志（事故取证）— `05-security-compliance/04-audit-logging.md`
- 团队协作（On-call 排班）— `07-team/02-collaboration.md`