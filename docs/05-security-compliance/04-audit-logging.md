# 审计日志规范（Audit Logging）

> **状态**：评审中 · **所有者**：安全合规 · **版本**：v1.0
> **最后更新**：2026-09-21

---

## 1. 目的

为 AI Banking Agent 系统的所有**有合规意义**的操作提供**不可篡改、可追溯、可查询**的审计日志，满足：

1. **合规要求**（《网络安全法》《个保法》《商业银行法》《反洗钱法》）
2. **事故取证**（发生争议、纠纷、犯罪时）
3. **业务分析**（用户行为、风控建模）

---

## 2. 适用范围

### 2.1 必须审计的操作

| 类别 | 操作 | 法规依据 |
|------|------|---------|
| **认证授权** | 登录、登出、MFA、会话建立/销毁、Token 刷新、密码修改 | 网络安全法 |
| **转账** | 转账发起、确认、撤销、失败、转账限额变更 | 商业银行法 |
| **账户** | 开户、销户、账户冻结/解冻、信息变更 | 反洗钱法 |
| **理财** | 理财产品购买、赎回、风险评估、风险等级变更 | 银保监 |
| **卡片** | 申请、激活、挂失、销卡、额度变更 | 商业银行法 |
| **订阅/代扣** | 签约、解约、扣款失败、代扣规则变更 | 消费者权益保护法 |
| **数据权利** | 用户查询、更正、删除、复制、撤回同意 | 个保法 §46 |
| **风控合规** | 风控拦截、人工接管、合规拒绝、可疑交易标记 | 反洗钱法 |
| **LLM 调用** | 所有 LLM 调用（输入摘要、模型、输出摘要、Token） | 生成式 AI 暂行办法 |
| **管理员** | 后台登录、非只读操作、配置变更、用户管理 | 内控 |
| **运维** | 数据库故障恢复、配置变更、紧急操作 | 内控 |
| **安全** | 密钥使用、权限变更、Break Glass | 内控 |

### 2.2 不需要审计的操作

- 只读查询（用户查询自己的账户余额）
- 内部技术日志（GC、线程池）
- 监控指标（Metrics）

> 注：只读查询应记录到普通 INFO 日志，但不进审计日志（避免数据爆炸）。

---

## 3. 审计日志结构

### 3.1 核心字段

```json
{
  "audit_id": "aud_2026092110234567890123",     // 全局唯一 ID
  "ts": "2026-09-21T10:23:45.123Z",              // UTC 时间（毫秒精度）
  "service": "agent-core",                        // 服务名
  "actor_type": "USER",                           // USER | ADMIN | STAFF | SYSTEM | AI
  "actor_id": "u_8a7b6c6c5d",                     // 哈希后的 ID
  "actor_role": "customer",                       // customer | agent | auditor
  "operation": "transfer.create",                 // 操作域.动作
  "target_type": "transfer",                      // 资源类型
  "target_id": "tx_20260921102345678",            // 资源 ID
  "scenario": "transfer",                         // 业务场景
  "intent": "transfer.to_individual",            // AI 识别意图
  "decision": "APPROVED",                         // APPROVED|REJECTED|PENDING|...
  "decision_reason": "Within limit, AML clear",
  "rule_id": "transfer.individual.daily_limit",   // 命中的规则
  "rule_version": "v1.5",                         // 规则版本
  "request_id": "req_...",                        // 与链路追踪关联
  "trace_id": "abc123def456",                     // OTel trace id
  "span_id": "789xyz",
  "source_ip": "203.0.113.42",                    // 用户 IP
  "device_id": "d_abc123",                        // 设备指纹哈希
  "geo_country": "CN",                            // 国家
  "geo_city": "Hangzhou",                         // 城市
  "consent_snapshot": ["basic", "transfer"],      // 命中同意列表
  "risk_score": 0.12,                             // 风控评分（如有）
  "amount": 5000.00,                              // 涉及金额（如有）
  "currency": "CNY",
  "ai_metadata": {                                // AI 决策元数据
    "model": "qwen3-72b",
    "tokens_in": 350,
    "tokens_out": 80,
    "confidence": 0.94
  },
  "human_in_loop": {
    "required": true,
    "approved_by": "staff_xyz",
    "approved_at": "...",
    "approval_method": "biometric"
},
  "policy_version": "policy-2025-09-21",
  "additional": { /* 场景特定字段 */ }
}
```

### 3.2 必填校验

| 字段 | 必填？ | 说明 |
|------|--------|------|
| `audit_id` | ✅ | 全局唯一 |
| `ts` | ✅ | UTC |
| `service` | ✅ | 服务名 |
| `actor_type` | ✅ | 行为人类型 |
| `actor_id` | ✅ | 行为人 ID（哈希）|
| `operation` | ✅ | 操作 |
| `request_id` | ✅ | 与日志关联 |
| `trace_id` | ✅ | 与 OTel 关联 |
| `consent_snapshot` | ✅（如涉及 PII） | 同意快照 |

---

## 4. 存储架构

### 4.1 双写策略

```
业务请求
   ↓
业务处理
   ↓
   ├── ① 业务表写入（事务）
   └── ② 审计日志写入（独立通道）

【设计原则】审计日志必须**事务外**或**独立连接**写入，
避免主业务失败导致审计丢失。
```

### 4.2 三层存储

| 层 | 存储 | 保留期 | 用途 |
|----|------|--------|------|
| **热** | PostgreSQL `audit` Schema | 90 天 | 实时查询 |
| **温** | ClickHouse / OSS + Elasticsearch | 1 年 | 调查分析 |
| **冷** | OSS 归档 + 异地灾备 | 5 年（合规）| 长期保留 |

### 4.3 表结构（PostgreSQL）

```sql
CREATE SCHEMA audit;

CREATE TABLE audit.events (
    audit_id      UUID NOT NULL DEFAULT gen_random_uuid(),
    ts            TIMESTAMPTZ NOT NULL DEFAULT now(),
    service       VARCHAR(64) NOT NULL,
    actor_type    VARCHAR(16) NOT NULL,
    actor_id      VARCHAR(64) NOT NULL,
    actor_role    VARCHAR(32),
    operation      VARCHAR(64) NOT NULL,
    target_type   VARCHAR(64),
    target_id     VARCHAR(128),
    scenario      VARCHAR(32),
    intent        VARCHAR(64),
    decision      VARCHAR(16),
    decision_reason TEXT,
    rule_id       VARCHAR(64),
    rule_version  VARCHAR(16),
    request_id    VARCHAR(64) NOT NULL,
    trace_id      VARCHAR(64),
    span_id       VARCHAR(32),
    source_ip     INET,
    device_id     VARCHAR(64),
    geo_country   VARCHAR(2),
    geo_city      VARCHAR(64),
    consent_snapshot JSONB,
    risk_score    DECIMAL(5,4),
    amount        DECIMAL(18,2),
    currency      CHAR(3),
    ai_metadata   JSONB,
    human_in_loop JSONB,
    policy_version VARCHAR(32),
    additional    JSONB,
    
    PRIMARY KEY (audit_id, ts)
) PARTITION BY RANGE (ts);

-- 按月分区
CREATE TABLE audit.events_2026_09 PARTITION OF audit.events
    FOR VALUES FROM ('2026-09-01') TO ('2026-10-01');
```

### 4.4 不可篡改设计

```sql
-- 数据库 trigger：禁止 audit 表的 UPDATE / DELETE
CREATE OR REPLACE FUNCTION audit.prevent_modify()
RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION 'Audit log is immutable';
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER no_update BEFORE UPDATE ON audit.events
    FOR EACH ROW EXECUTE FUNCTION audit.prevent_modify();

CREATE TRIGGER no_delete BEFORE DELETE ON audit.events
    FOR EACH ROW EXECUTE FUNCTION audit.prevent_modify();
```

**应用层只允许 INSERT，不提供 UPDATE/DELETE API。**

### 4.5 异地只读归档

- 每日归档到异地 OSS（启用对象锁定 Object Lock）
- OSS Bucket 启用 **WORM（Write Once Read Many）** 模式
- 保留期 ≥ 5 年，无法手动删除

### 4.6 完整性校验（每日）

- 对当日审计日志计算 HMAC 签名
- 签名同步到独立 KMS 存储
- 如发现不匹配，发出告警

---

## 5. 代码实现

### 5.1 C#/.NET 端

```csharp
public interface IAuditLogger
{
    Task LogAsync(AuditEvent evt, CancellationToken ct = default);
}

public class AuditLogger : IAuditLogger
{
    private readonly IAuditDbContext _db;
    private readonly IClock _clock;
    
    public async Task LogAsync(AuditEvent evt, CancellationToken ct = default)
    {
        // 1. 强制校验必填字段
        evt.ValidateRequired();
        
        // 2. 自动填充上下文（trace_id, source_ip, user_id）
        AuditContextEnricher.Enrich(evt);
        
        // 3. HMAC 签名
        evt.Signature = HmacSigner.Sign(evt);
        
        // 4. 写入独立连接（避免与业务事务耦合）
        await _db.AuditEvents.AddAsync(evt, ct);
        await _db.SaveChangesAsync(ct);
    }
}
```

### 5.2 使用示例

```csharp
public class TransferService
{
    public async Task<TransferResult> Execute(TransferRequest req, CancellationToken ct)
    {
        var result = ...; // 业务执行
        
        // 业务完成后审计
        await _audit.LogAsync(new AuditEvent
        {
            Operation = "transfer.execute",
            Scenario = "transfer",
            TargetId = result.TransferId,
            Decision = result.Status.ToString(),
            DecisionReason = result.Reason,
            Amount = req.Amount,
            Additional = new {
                    req.FromAccount, req.ToAccount, req.Currency
            }
        }, ct);
        
        return result;
    }
}
```

### 5.3 自动埋点（推荐）

通过 **AOP / MediatR Pipeline** 自动审计，减少手动调用遗漏：

```csharp
public class AuditBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IAuditable
{
    public async Task<TResponse> Handle(TRequest req, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        var preAudit = new AuditEvent { Operation = req.OperationName, /* ... */ };
        await _audit.LogAsync(preAudit, ct);
        
        var response = await next(ct);
        
        var postAudit = preAudit.WithResult(response);
        await _audit.LogAsync(postAudit, ct);
        
        return response;
    }
}
```

---

## 6. 查询与导出

### 6.1 查询接口

| 接口 | 用途 | 权限 |
|------|------|------|
| `GET /api/v1/audit/events` | 查询事件列表 | 审计员 |
| `GET /api/v1/audit/events/{id}` | 单事件详情 | 审计员 |
| `GET /api/v1/audit/users/{user_id}/timeline` | 用户时间线 | 审计员 + 风控 |
| `GET /api/v1/audit/scenarios/{scenario}/stats` | 场景统计 | 审计员 + 产品 |

### 6.2 导出

- **审计员导出**：需二级审批，导出文件加密 + 数字签名
- **司法取证导出**：必须法务 + 合规 + 架构师三方审批
- **监管报送**：直接走报送通道（不通过导出）

### 6.3 查询性能

- 90 天内数据：PostgreSQL 直接查询（响应 < 1s）
- 90 天以上：ClickHouse 聚合（响应 < 5s）
- 跨年归档：OSS + Athena（异步查询）

---

## 7. 监控与告警

### 7.1 审计本身监控

| 指标 | 告警阈值 |
|------|---------|
| 审计写入失败率 | > 0.1% 红色 |
| 审计写入延迟 | P99 > 500ms 黄色 |
| 审计分区膨胀 | > 1 亿行/分区 黄色 |
| 归档失败 | > 0 红色 |

### 7.2 审计内容异常告警

| 异常 | 含义 |
|------|------|
| 同一用户 1h 内 100+ 笔转账 | 异常行为 |
| 同一账户被 10+ 用户访问 | 账户滥用 |
| 管理员操作时间异常（凌晨 3 点） | 内鬼风险 |
| LLM 输出含敏感词 + 已执行 | AI 输出违规 |
| 合规拒绝率突增 | 模型异常 |

---

## 8. 保留与销毁

### 8.1 保留期

| 类型 | 保留期 | 法规依据 |
|------|--------|---------|
| 客户身份资料 | ≥ 5 年 | 反洗钱法 §23 |
| 交易记录 | ≥ 5 年 | 反洗钱法 §23 |
| 审计日志 | ≥ 5 年（不可篡改）| 银保监 + 网络安全法 |
| 普通操作日志 | ≥ 6 个月 | 网络安全法 §21 |
| 会话日志 | 90 天 | 公司规范 |
| LLM 调用日志 | 180 天 | 公司规范 |

### 8.2 销毁流程

到期数据销毁时：

1. 提前 30 天发起销毁审批
2. 合规 + 架构师 双签
3. 数据库硬删除 + OSS 对象删除
4. 形成销毁记录（不可删除的元数据：销毁时间、数量、审批人）

---

## 9. 法律取证

发生纠纷/犯罪时：

1. **冻结**相关数据（暂停销毁任务）
2. **导出**完整时间线（事件 + 上下文 + Trace）
3. **签名 + 时间戳**（第三方可信时间戳服务）
5. **移交**法务 + 司法鉴定机构
6. **归档**为案件档案

---

## 10. 与其他系统的关系

| 系统 | 关系 |
|------|------|
| **业务数据库** | 审计库独立 Schema，独立备份 |
| **业务日志（ELK）** | 业务日志是诊断用，审计是合规用，**两者必须分离** |
| **链路追踪（OTel）** | 通过 `trace_id` 关联，便于串联 |
| **监控（Prometheus）** | 审计写入指标 + 审计内容异常监控 |
| **事件总线（Kafka）** | 关键审计事件可作为事件总线输入（如风控分析）|

---

## 11. 上线检查清单（审计部分）

- [ ] 关键操作都有审计埋点（覆盖 §2.1）
- [ ] 必填字段已校验（§3.4）
- [ ] 数据库 trigger 防 UPDATE/DELETE 生效（§4.4）
- [ ] OSS WORM 异地归档已启用（§4.5）
- [ ] HMAC 签名已实现（§4.6）
- [ ] 查询接口已实现 + 权限控制（§6.1）
- [ ] 监控告警已配置（§7）
- [ ] 留存期配置正确（§8.1）

---

## 12. 参考

- 合规矩阵 — `05-security-compliance/02-compliance-matrix.md`
- 数据分级 — `05-security-compliance/03-data-classification.md`
- 威胁模型 — `05-security-compliance/01-threat-model.md`
- 监控告警 — `04-operations/02-monitoring-observability.md`
- 域模型（C#）— `01-domain/02-domain-model.md`