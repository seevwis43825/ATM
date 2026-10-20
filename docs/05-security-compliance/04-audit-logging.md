# 审计日志规范（Audit Logging）

> **状态**：评审中 · **所有者**：安全合规 · **版本**：v1.0
> **最后更新**：2026-09-21

---

## 0. 当前实现与目标边界

> **Current（源码可验证）**：`AuditLogger` 先生成 HMAC-SHA256 链签名，再追加 JSONL 文件，并把事件放入有界队列，由后台 `AuditDbWriter` 攒批（默认 200 条）后写入独立 `AuditDbContext` 的 `audit_events` 表；SQLite 下为独立表，PostgreSQL 配置下映射到 `audit` Schema。仓储接口只暴露 Append/Query/Verify/Count，不提供 Update/Delete。文件或数据库写失败均记录 Critical；异常被捕获，**不会阻断业务流程**。**权威记录是同步落盘的 JSONL + 链签名，数据库是可检索副本**（批量异步提交，读路径先 flush 保证"写完立刻查"查得到）；因此两端并非原子事务，也不保证两端总是一致，崩溃时最多丢失尚未批量提交的数据库副本尾部，JSONL 与签名链完好。
>
> **Target**：PostgreSQL 分区、数据库禁止改删 trigger、WORM/OSS 异地归档、KMS 签名存储、ClickHouse/Elasticsearch、完整审计 API、指标告警与五年留存均尚未落地。下文相关内容是生产设计或 Runbook。

## 1. 目的

目标是为 AI Banking Agent 系统的所有**有合规意义**的操作提供可验证完整性、可追溯、可查询的审计日志。当前 HMAC 链能检测篡改，但在 WORM 落地前不能等同于物理不可篡改。

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

### 3.1 目标字段（Target，尚未全部实现）

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

### 3.1.1 Current 实际落盘字段（源码可验证）

> 上面的目标字段里有相当一部分**当前并不存在**。实现真正落盘的内容如下，
> 以源码为准：`src/src/BankingAgent.Base/Security/Audit/AuditLogger.cs`（JSONL 写入）
> 与 `src/src/BankingAgent.Base/Data/AuditRepository.cs`（`audit_events` 表）。

**JSONL 归档（权威记录）实际只有 11 个键**：

| 键 | 来源 | 说明 |
|----|------|------|
| `AuditId` | `evt.AuditId` | 全局唯一（12 位十六进制） |
| `ts` | `evt.Timestamp` | ISO-8601 UTC |
| `ActorType` | `evt.ActorType` | USER / STAFF / SYSTEM / AGENT |
| `actor` | `HashActor(evt.ActorId)` | **用户 ID 的 SHA256 前 12 位**，不落明文 |
| `Operation` | `evt.Operation` | 操作域.动作 |
| `Scenario` | `evt.Scenario` | 业务场景 |
| `Decision` | `evt.Decision` | SUCCESS / APPROVED / REJECTED … |
| `RuleId` | `evt.ComplianceRule ?? evt.RuleId` | 命中的合规规则 |
| `Amount` | `evt.Amount` | 涉及金额（可空） |
| `prev` | `evt.PreviousSignature` | 上一条的签名（链式结构） |
| `sig` | `evt.Signature` | 本条的 HMAC-SHA256 签名 |

**目标字段中当前不存在的**（不要在文档或代码里引用它们）：
`service`、`actor_role`、`target_type`、`target_id`、`span_id`、`device_id`、
`geo_country`、`geo_city`、`consent_snapshot`、`ai_metadata`、`human_in_loop`、
`rule_version`（库表有 `RuleVersion` 列，JSONL 未写）、`additional`
（扩展字段的成员名是 `Extra`，且 JSONL 也不写它）。

**`audit_events` 表额外有的列**（JSONL 里没有）：`ActorHash`、`PolicyVersion`、
`RuleVersion`、`Currency`、`SourceIp`、`UserAgent`、`Intent`、`DecisionReason`、
`RiskScore`、`RequestId`、`TraceId`、`ElapsedMs`。

### 3.2 必填校验（Target 目标；当前实现**未做**校验）

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

> ⚠️ **当前实现没有做任何必填校验**：`AuditLogger.WriteAsync` 不调用校验方法，
> 也不存在 `ValidateRequired` 之类的 API（2026-10-20 复核确认，全仓 0 命中）。
> 上表描述的是**目标态**要求；上线检查清单里"必填字段已校验"一项因此**仍未完成**。

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

【当前行为】JSONL 与 `audit_events` 两路写入，但**权威性不同**：

- **JSONL + 链签名 = 权威记录**，请求返回前同步落盘。审计的**可篡改性**由
  链式签名保证（任何篡改都会导致链断裂），不依赖数据库。
- **`audit_events` 表 = 可检索副本**，事件先入有界队列，由后台写入器攒批提交。
  这不是可选优化而是必需项：逐条同步提交意味着每条审计事件付一次 SQLite
  事务提交成本（含 fsync），而审计写在请求关键路径上，进程内实测会把落库吞吐
  压到 **142~233 条/秒**（批量提交为 **2989~4129 条/秒**，快 17.7~25.7 倍）。
- **检索能力的一个 Provider 差异**（2026-10-20 修复并补测试）：EF 的 SQLite provider
  **拒绝翻译 `DateTimeOffset` 的 ORDER BY**，直接抛 `NotSupportedException`，
  导致审计库的查询与验链路径 100% 失败 —— 文档长期声称"数据库提供按操作者/时间/场景检索的能力"，
  实际是**假的**。现按 Provider 分支处理：SQLite（开发环境）排序放内存、**不改存储格式**
  （存量审计库照常可读），PostgreSQL（生产环境 `timestamptz`）保持库内排序。
  覆盖测试见 `src/UnitTests/AuditBatchedWriterTests.cs`。

两路任一写入失败都只记 Critical，不回滚或阻断业务。队列**有界且满了背压等待，
绝不丢审计记录**。生产需增加可靠队列/补偿、对账与告警闭环。
```

### 4.2 三层存储（Target）

| 层 | 存储 | 保留期 | 用途 |
|----|------|--------|------|
| **热** | PostgreSQL `audit` Schema | 90 天 | 实时查询 |
| **温** | ClickHouse / OSS + Elasticsearch | 1 年 | 调查分析 |
| **冷** | OSS 归档 + 异地灾备 | 5 年（合规）| 长期保留 |

### 4.3 表结构（Target PostgreSQL）

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

### 4.4 不可篡改设计（Target）

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

### 4.5 异地只读归档（Target，未实现）

- 每日归档到异地 OSS（启用对象锁定 Object Lock）
- OSS Bucket 启用 **WORM（Write Once Read Many）** 模式
- 保留期 ≥ 5 年，无法手动删除

### 4.6 完整性校验（Current + Target）

- 当前每条记录使用 HMAC-SHA256 并链接前一条签名，可由代码校验链断裂
- Target：签名/锚点同步到独立 KMS 或可信时间戳服务，并每日自动校验告警
- 如发现不匹配，发出告警

---

## 5. 代码实现

### 5.1 C#/.NET 端（Current 真实实现）

> 以下代码与仓库源码**逐字对应**（`src/src/BankingAgent.Base/Security/Audit/AuditLogger.cs`）。
> 本节只做**裁剪展示**，完整实现以源码为准。

```csharp
public interface IAuditLogger
{
    Task WriteAsync(AuditEvent evt, CancellationToken ct = default);
    AuditChainVerification VerifyChain(IReadOnlyList<AuditEvent> events);
}

public sealed partial class AuditLogger(
    IOptions<AuditOptions> options,
    ILogger<AuditLogger> logger,
    IAuditRepository? repository = null) : IAuditLogger
{
    public async Task WriteAsync(AuditEvent evt, CancellationToken ct = default)
    {
        // 1. 链式签名：读链尾 → 算 HMAC → 写链尾，三步在同一临界区内
        var signed = SignAndAdvance(evt);

        // 2. 权威记录：JSONL 归档，请求返回前已落盘
        await AppendToFileAsync(signed, ct);

        // 3. 可检索副本：入有界队列，由 AuditDbWriter 攒批落库
        await AppendToRepositoryAsync(signed, ct);
    }
}
```

**与本文档早期版本的关键差异**（2026-10-20 复核修正）：

| 早期版本写的 | 实际情况 |
|-------------|---------|
| `IAuditLogger.LogAsync(...)` | 方法名是 **`WriteAsync(...)`** |
| `IAuditDbContext` / `IClock` | **不存在**。审计库用 `IAuditRepository`（`EfAuditRepository`） |
| `HmacSigner.Sign(evt)` | **不存在**。签名由 `AuditLogger` 内部的 `SignAndAdvance()` / `ComputeHmac()` 完成 |
| `evt.ValidateRequired()` | **不存在**，当前**没有必填字段校验**（见 §3.2 与 §11 检查清单） |
| `AuditContextEnricher.Enrich(evt)` | **不存在**，上下文由调用方显式填写 |
| `AuditEvent.TargetId` / `.Additional` | 字段名是 **`Extra`**（`IReadOnlyDictionary<string, object?>`），且**没有** `TargetId` |
| 直接 `SaveChangesAsync` 落库 | 现在是**有界队列 + 后台攒批提交**（`AuditDbWriter`），文件才是权威记录 |

### 5.2 使用示例（Current 真实写法）

```csharp
// 插件或宿主端点通过注入的 IAuditLogger 写审计
var audit = ctx.RequestServices.GetRequiredService<IAuditLogger>();

await audit.WriteAsync(new AuditEvent
{
    AuditId = Guid.NewGuid().ToString("N")[..12],
    Timestamp = DateTimeOffset.UtcNow,
    ActorType = "USER",
    ActorId = principal.UserId,
    Operation = "transfer.execute",
    Scenario = "transfer",
    Decision = "SUCCESS",
    DecisionReason = result.Reason,
    Amount = req.Amount,
    RequestId = req.SessionId,
    // 扩展字段放在 Extra（没有 TargetId / Additional 这两个成员）
    Extra = new Dictionary<string, object?>
    {
        ["from_account"] = req.FromAccount,
        ["to_account"] = req.ToAccount,
        ["currency"] = req.Currency
    }
}, ct);
```

> `AuditEvent` 的完整成员见 `AuditLogger.cs` 的 record 定义（`AuditId` / `Timestamp` / `ActorType` /
> `ActorId` / `Operation` / `Scenario` / `Intent` / `Decision` / `DecisionReason` / `RuleId` /
> `RequestId` / `TraceId` / `SourcePlugin` / `Amount` / `RiskScore` / `ComplianceRule` /
> `ElapsedMs` / `Extra` / `Signature` / `PreviousSignature`）。

### 5.3 自动埋点（Target / 设计示意）

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

## 6. 查询与导出（Target API）

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

## 7. 监控与告警（Target）

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
- [x] HMAC 链签名已实现（§4.6）
- [x] JSONL + `audit_events` 双写已实现（失败不阻断）
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