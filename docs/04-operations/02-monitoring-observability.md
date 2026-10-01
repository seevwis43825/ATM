# 监控与可观测性（Monitoring & Observability）

> **状态**：评审中 · **所有者**：平台 · **版本**：v1.0
> **最后更新**：2026-09-21

---

## 0. 现状与目标边界

> **Current（源码可验证）**：宿主使用 .NET 控制台日志，并提供 `/health`、`/health/ready`、`/health/live`；健康检查会实际查询数据库。审计另走 HMAC 链 JSONL 与 `audit_events` 双写。仓库中未发现 Prometheus 指标端点、Grafana Dashboard、AlertManager 规则、ELK/Filebeat、OpenTelemetry/Jaeger 上报或生产告警路由的落地配置。
>
> **Target / Runbook（规划）**：本文其余指标名、阈值、保留期、Dashboard、告警和值班流程是生产可观测性目标。上线前需以实际埋点、部署清单、告警试触发和留存策略作为验收证据。

## 1. 设计原则

可观测性三大支柱：**Metrics（指标）+ Logs（日志）+ Traces（链路追踪）**，缺一不可。

针对 AI Banking Agent 这种**强合规 + 高风险操作密集**的系统，还必须额外加：

- **Audit Trail（审计追踪）**：所有高风险操作的全量轨迹（见 `05-security-compliance/04-audit-logging.md`）
- **Business KPI（业务指标）**：业务成功率、误识率、人工接管率等

---

## 2. Metrics（Target）

### 2.1 技术指标（Prometheus + Grafana）

| 类别 | 指标 | 告警阈值 |
|------|------|---------|
| **API 健康** | `api_request_duration_seconds{endpoint, status}` | P99 > 1s 黄色，> 3s 红色 |
| | `api_requests_total{status}` | 5xx 比例 > 1% 红色 |
| | `api_active_requests` | 持续 > 1000 黄色 |
| **Agent 推理** | `agent_llm_request_duration_seconds{model}` | P99 > 10s 黄色 |
| | `agent_llm_tokens_total{model, direction}` | 单用户 1h > 100k 黄色 |
| | `agent_llm_error_total{error_type}` | 错误率 > 5% 红色 |
| **EventBus** | `eventbus_publish_total{event_type}` | — |
| | `eventbus_consume_lag_seconds{consumer_group}` | > 60s 黄色，> 300s 红色 |
| | `eventbus_dead_letter_total{event_type}` | > 0 红色 |
| **数据库** | `pg_connection_pool_active` | > 80% 黄色 |
| | `pg_slow_queries_total` | > 10 条/分钟 黄色 |
| | `pg_replication_lag_seconds` | > 5s 黄色 |
| **缓存** | `redis_memory_used_bytes` | > 80% 黄色 |
| | `redis_hit_ratio` | < 80% 黄色 |
| **JVM/.NET** | `dotnet_gc_pause_seconds` | P99 > 200ms 黄色 |
| | `dotnet_threadpool_queue_length` | > 100 黄色 |

### 2.2 业务指标（自定义 + 业务 Dashboard）

| 类别 | 指标 | 含义 |
|------|------|------|
| **对话** | `conv_total_started` / `conv_total_completed` | 总对话量 |
| | `conv_avg_turns` | 平均对话轮次 |
| | `conv_fallback_rate` | 用户输入"转人工"的比率 |
| **意图识别** | `intent_classification_accuracy` | 黄金集准确率 |
| | `intent_unknown_rate` | 意图识别不了的比例（> 15% 异常） |
| **转账场景** | `transfer_total_count` / `transfer_total_amount` | 转账总笔数与金额 |
| | `transfer_approval_rate` | 人工审批通过率（异常波动 = 风控问题） |
| | `transfer_fraud_blocked` | 风控拦截笔数 |
| **理财** | `wealth_recommendation_click_rate` | 推荐点击率 |
| | `wealth_trade_conversion_rate` | 交易转化率 |
| **合规** | `compliance_violation_total{violation_type}` | 合规违规次数 |
| | `human_takeover_total{scenario, reason}` | 人工接管次数 |
| **LLM 成本** | `llm_cost_usd_total{model}` | LLM 花费（每日上限告警） |

业务 Dashboard 在 **Grafana** 上独立配置，**只有产品负责人 + 架构师** 可编辑。

---

## 3. Logs（Current + Target）

### 3.1 结构化日志规范

```json
{
  "ts": "2026-09-21T10:23:45.123Z",
  "level": "INFO",
  "service": "agent-core",
  "trace_id": "abc123def456",
  "span_id": "789xyz",
  "user_id_hash": "u_8a7b6c5d",          // 哈希，绝不打明文
  "request_id": "req_20260921102345678",
  "scenario": "transfer",
  "intent": "transfer.to_individual",
  "amount": 5000.00,
  "decision": "require_approval",
  "msg": "Transfer intent detected, amount > 5000, hit approval rule",
  "context": {
    "from_account": "****-1234",
    "to_account": "****-5678",
    "feature_flags": ["transfer.smart_suggestion"]
  }
}
```

### 3.2 日志级别与保留

| 环境 | 级别 | 保留期 | 存储 |
|------|------|--------|------|
| 开发 | DEBUG | 7 天 | 本地文件 |
| 测试 | INFO | 30 天 | ELK |
| 预发 | INFO | 90 天 | ELK + 冷归档 OSS |
| 生产 | INFO | **180 天热 + 5 年冷归档**（合规要求） | ELK + OSS + 不可篡改归档 |

> 合规要求参见 `05-security-compliance/02-compliance-matrix.md`（《个人信息保护法》第 17 条、《数据安全法》第 21 条、《网络安全法》第 21 条要求网络日志留存不少于 6 个月）。

### 3.3 敏感信息过滤

**强制规则**：
- ❌ 永远不打：身份证号、银行卡号全段、密码、CVV、验证码、PIN、生物特征
- ✅ 可以打：银行卡号后 4 位、手机号后 4 位、哈希后的 user_id

目标实现方式：
- C# 端：接入结构化日志过滤器；当前已有 `DataMasker` 等脱敏能力，但未形成本文所述 Serilog/ELK 全链路
- Python 端：仅在未来引入独立 Python 服务时适用
- 落盘前再做一次敏感字段检测（兜底）

### 3.4 关键事件日志（必须落审计）

参考 `05-security-compliance/04-audit-logging.md`，下列事件**必须**单独通道存储：
- 登录/登出、会话建立/销毁
- 转账、卡片操作、理财交易、订阅变更
- 合规拦截、人工接管、风控拒绝
- 管理员后台任何非只读操作

---

## 4. Traces（Target）

### 4.1 OpenTelemetry 标准

所有服务（`agent-core` / `ai-service` / `im-bot` / `admin`）必须接入 OpenTelemetry，统一上报到 **Jaeger**（开发）+ **阿里云链路追踪 SLS**（生产）。

Span 命名规范：

```
{Service}.{Operation}.{SubOperation}
例：
agent-core.Conversation.Process
agent-core.Transfer.Execute
ai-service.Intent.Classify
ai-service.LLM.Invoke
```

### 4.2 关键 Trace 标识

| Span Attribute | 说明 | 必须？ |
|----------------|------|---------|
| `user_id_hash` | 用户哈希 | ✅ |
| `request_id` | 请求 ID（贯穿全链路） | ✅ |
| `scenario` | 业务场景（transfer/bill/wealth/...） | ✅ |
| `intent` | 识别出的意图 | ✅ |
| `feature_flags` | 命中的功能开关 | ✅ |
| `llm.model` | LLM 模型名 | ✅（如调用） |
| `llm.tokens` | Token 消耗 | ✅（如调用） |
| `amount` | 涉及金额 | 涉及转账/理财时 |
| `risk_score` | 风控评分 | 涉及风控时 |
| `human_in_loop` | 是否人工回环 | 关键场景时 |

### 4.3 慢链路定位

当 P99 延迟超过阈值时，Grafana Trace 面板会直接跳转到 Jaeger，定位到具体 span。

---

## 5. 告警（Target）

### 5.1 告警分级

| 分级 | 含义 | 响应时间 | 通知方式 |
|------|------|---------|---------|
| **P0** | 系统不可用、资金风险 | 5 分钟 | 电话 + 短信 + 钉钉/飞书 @全员 |
| **P1** | 核心功能受损 | 15 分钟 | 钉钉/飞书 @oncall |
| **P2** | 非核心功能受损 | 1 小时 | 钉钉群消息 |
| **P3** | 提示性 | 下一个工作日 | 邮件汇总 |

### 5.2 关键告警规则

```yaml
# alert-rules.yaml（AlertManager）
groups:
  - name: ai-banking-core
    rules:
      - alert: HighErrorRate
        expr: sum(rate(api_requests_total{status=~"5.."}[5m])) / sum(rate(api_requests_total[5m])) > 0.01
        for: 2m
        labels: { severity: P0 }
        annotations: { summary: "5xx 错误率超 1%" }

      - alert: TransferFailureSpike
        expr: rate(transfer_failed_total[5m]) > rate(transfer_failed_total[1h] offset 1h)
        labels: { severity: P0 }
        annotations: { summary: "转账失败率突增（可能资金风险）" }

      - alert: HumanTakeoverSurge
        expr: rate(human_takeover_total[5m]) > rate(human_takeover_total[1h] offset 1h) * 2
        labels: { severity: P1 }
        annotations: { summary: "人工接管率突增 2 倍以上（模型可能异常）" }

      - alert: LLMTokenBudgetExceeded
        expr: increase(llm_tokens_total[1h]) > 1000000
        labels: { severity: P1 }
        annotations: { summary: "LLM Token 单小时消耗超 100 万（成本告警）" }
```

### 5.3 告警收敛（去噪）

- 同规则 5 分钟内不重复发
- 维护期静默（手动维护窗口）
- 严重告警抑制次要告警（AlertManager `inhibit_rule`）

---

## 6. 大数据与视图（Target）

### 6.1 Grafana Dashboard 清单

| Dashboard | 用途 | 所有者 | 可见性 |
|-----------|------|--------|--------|
| **系统总览** | QPS / P99 / 错误率 / 在线用户 | 平台 | 全员 |
| **服务拓扑** | 调用链热力图、依赖健康 | 平台 | 全员 |
| **LLM 成本** | Token 消耗、成本分析 | AI 数据 | AI 数据 + 架构师 |
| **业务大盘** | 对话量、场景渗透、转账额 | 产品 | 产品 + 业务开发 |
| **合规大盘** | 违规次数、人工接管率 | 安全合规 | 安全合规 + 架构师 |
| **On-Call 战情室** | 当前告警 + 历史事件 | 平台 | On-call |

### 6.2 日志查询（Kibana）

预置查询模板：
- 「某用户的所有操作」：`user_id_hash: "u_8a7b6c5d"`
- 「某笔转账的全链路」：`request_id: "req_..."`
- 「今天所有合规拦截」：`compliance_violation_total: * AND level: WARN`
- 「LLM 调用失败」：`service: ai-service AND level: ERROR AND msg: "llm_*"`

---

## 7. 工具栈（Target）

| 用途 | 工具 | 备注 |
|------|------|------|
| 指标采集 | Prometheus | 兼容 OpenTelemetry |
| 指标可视化 | Grafana | 统一认证（接入公司 SSO） |
| 日志采集 | Filebeat / Fluent Bit | DaemonSet 部署 |
| 日志存储与查询 | ELK（Elasticsearch + Logstash + Kibana） | 7.17+ 版本 |
| 链路追踪 | Jaeger + OpenTelemetry SDK | 采样率 100%（开发）/ 10%（生产） |
| 告警 | AlertManager | 路由到钉钉/飞书/电话 |
| 拨测 | Blackbox Exporter | HTTPS / TCP / ICMP |

---

## 8. 上线检查清单（监控部分）

生产化上线前确认（当前仓库尚未全部满足）：

- [ ] 新增自定义业务指标已在 Prometheus 注册
- [ ] 关键 Span Attribute 已埋点
- [ ] 新日志字段已加敏感信息过滤
- [ ] 告警规则已评审（在 `alert-rules.yaml`）
- [ ] Grafana Dashboard 已更新
- [ ] On-Call 文档已更新（见 `03-incident-response.md`）

---

## 9. 参考

- ADR-003：可观测性技术栈选择 — `00-architecture/04-architecture-decisions.md`
- 审计日志规范 — `05-security-compliance/04-audit-logging.md`
- 合规矩阵（日志留存）— `05-security-compliance/02-compliance-matrix.md`