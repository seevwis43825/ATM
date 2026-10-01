# 功能全生命周期管理（Feature Lifecycle）

> **状态**：评审中 · **所有者**：产品 · **版本**：v1.0
> **最后更新**：2026-10-01
>
> **口径**：本文是目标产品治理流程。当前仓库已落地短分支 + PR、插件 manifest、单元/集成 CI、HITL 与审计基础；运行时 FeatureFlag、灰度平台、staging/production、产品看板和自动下线流程尚未落地。

---

## 1. 目的

建立从「想法 → 上线 → 下线」全生命周期的标准做法，确保：

1. 新功能安全、合规、可控地上线
2. 老功能有序下线，不留技术债
3. 每个状态变更都有记录、可追溯

---

## 2. 功能生命周期 6 个阶段

> 在运行时 FeatureFlag 落地前，不得把 `PluginManifest.FeatureFlags` 当成可灰度开关；需要隔离的未完成功能应避免接入默认路由，或使用经过评审的显式配置并补测试。

```
   提议              设计            实现             上线            成熟            下线
[Proposed] → [Designed] → [Developing] → [Launched] → [Mature] → [Deprecated]
                              ↑                              ↓
                              └──────── Beta（灰度） ────────┘
```

### 2.1 阶段定义

| 阶段 | 状态 | 说明 | 上线允许？ |
|------|------|------|-----------|
| **Proposed** | 提议 | 需求已识别，未设计 | ❌ |
| **Designed** | 已设计 | 架构/PRD 已评审 | ❌ |
| **Developing** | 开发中 | 编码进行 | ❌ |
| **Beta** | 灰度 | 通过 FeatureFlag 对小流量开放 | ⚠️ 仅灰度 |
| **Launched** | 已上线 | 全量开放 | ✅ |
| **Mature** | 成熟 | 运行稳定，进入维护 | ✅ |
| **Deprecated** | 弃用 | 标记弃用，不再投入 | ⚠️ 警告 |
| **Removed** | 下线 | 代码与数据已清理 | ❌ |

---

## 3. 阶段 1：提议（Proposed）

### 3.1 入口
- 用户反馈、运营需求、合规要求、竞品分析、技术债清理

### 3.2 PRD 模板（精简）

```markdown
# PRD: <功能名>

## 1. 背景与目标
为什么做？要解决什么问题？成功指标？

## 2. 用户故事
作为 <角色>，我想要 <动作>，以便 <价值>

## 3. 范围
- In Scope:
- Out of Scope:

## 4. 业务流程
简述流程（可用文字）

## 5. 合规初判
- 是否涉及 PII？
- 是否涉及自动化决策？
- 是否需要 HITL？
- 是否需要 PIA？
- 是否需要算法备案？

## 6. 风险
| 风险 | 缓解 |
|------|------|

## 7. 度量
如何衡量成功？
```

### 3.3 评审

- **必须参与**：产品 + 架构师
- **可选参与**：业务开发 / 安全合规 / AI（按需）
- **决议**：通过 / 修改后通过 / 拒绝 / 暂缓

---

## 4. 阶段 2：已设计（Designed）

### 4.1 输出物

- [ ] PRD v1.0
- [ ] 架构方案（架构师 Review）
- [ ] ADR（如涉及重大架构决策）
- [ ] API 设计草案（参见 `02-api/01-rest-api-spec.md`）
- [ ] 事件 Schema 草案（如发布事件）
- [ ] 数据模型变更（DDL）
- [ ] Feature Flag 命名（参见 `02-api/03-feature-flags.md`）
- [ ] **PIA 报告**（如触发）
- [ ] 测试方案

### 4.2 设计评审会（必开）

**参与人**：架构师 + 业务开发 + 安全合规 + AI + 平台（按需）

**检查清单**：
- [ ] 与现有架构兼容（模块边界、依赖方向）
- [ ] 合规义务已映射（对照 `05-security-compliance/02-compliance-matrix.md`）
- [ ] 涉及 PII 的字段已分级（参见 `05-security-compliance/03-data-classification.md`）
- [ ] 高风险操作有 HITL 设计
- [ ] 审计埋点已规划（参见 `05-security-compliance/04-audit-logging.md`）
- [ ] 可观测性已规划（指标 + 日志 + Trace）
- [ ] Feature Flag 默认关闭
- [ ] 有明确的关闭 / 回滚方案

---

## 5. 阶段 3：开发中（Developing）

### 5.1 开发规范

- 分支策略：`feature/<ticket-id>-<short-name>`，参见 `03-development/02-git-workflow.md`
- 代码规范：参见 `03-development/01-coding-standards.md`
- 提交规范：Conventional Commits
- PR Review：参见 `03-development/03-pr-review-checklist.md`

### 5.2 测试要求

| 测试类型 | 强制？ |
|----------|--------|
| 单元测试（覆盖率 ≥ 80% 新代码） | ✅ |
| 集成测试（API + DB） | ✅ |
| 合约测试（如对外暴露 API） | ✅ |
| E2E（核心场景） | ✅ |
| 性能测试（影响性能路径） | ⚠️ 视情况 |
| 安全测试（涉及 PII / 权限） | ✅ |
| 合规测试（如拒绝路径） | ✅ |

参见 `03-development/04-testing-strategy.md`。

### 5.3 文档同步

- API 文档（OpenAPI）已生成
- 事件 Schema 已注册到 Schema Registry
- ADR（如有）已合并
- README / Wiki 已更新

---

## 6. 阶段 4：Beta（灰度）

### 6.1 灰度策略

通过 **FeatureFlag** 控制：

```csharp
if (await _featureFlag.IsEnabledAsync("transfer.smart_suggestion", userContext))
{
    // 新逻辑
}
```

### 6.2 灰度阶段

| 阶段 | 流量 | 观察时长 | 放行条件 |
|------|------|---------|---------|
| **内部灰度** | 团队 + 种子用户（≤ 5%）| ≥ 3 天 | 无 P0 bug |
| **小流量** | 10% | ≥ 7 天 | 错误率 < 2%、人工接管率正常 |
| **中流量** | 50% | ≥ 3 天 | 错误率 < 1% |
| **全量** | 100% | — | 全量发布 |

### 6.3 灰度监控

新增功能必须新增：
- 业务指标（功能使用率 / 转化率 / 错误率）
- 异常告警（错误率突增、用户投诉突增）
- 性能指标（P99 延迟、Token 消耗）

### 6.4 灰度期间必须每日 review

每日 standup 时，新功能 owner 报告：
- 启用用户数 / 失败次数
- 用户反馈
- 是否符合放行条件
- 是否需要继续 / 回滚 / 调整

---

## 7. 阶段 5：Launched（已上线）

### 7.1 全量发布

详见 `06-product/03-release-process.md`。

### 7.2 稳态运营

- 进入维护模式（响应 bug 修复、少量优化）
- 业务指标纳入日常监控
- 月度 review（业务 + 技术）

---

## 8. 阶段 6：Deprecated（弃用）

### 8.1 触发条件

- 业务价值低 / 已被替代 / 合规不再允许 / 维护成本过高

### 8.2 弃用流程

1. 标记 `FeatureFlag.Disable(<feature name>)` — 关闭开关
2. 在 UI/接口加 `@Deprecated` 警告
3. 通知用户（涉及用户功能）
4. 设置 Sunset Date（建议 ≥ 3 个月后下线）
5. 编写 Migration Guide（如有替代方案）

### 8.3 代码标记

```csharp
[Obsolete("此接口将于 2026-12-31 下线，请迁移到 NewTransferService", false)]
public class OldTransferService { ... }
```

### 8.4 文档同步

- API 文档标 `Deprecated`
- 在 README 通告
- 内部 Wiki 标记

---

## 9. 阶段 7：Removed（下线）

### 9.1 下线流程

1. 提前 30 天告知（重大功能 ≥ 90 天）
2. 删除代码（合并到主干，CI 校验无引用）
3. 删除数据库表 / 列（如确定无外部引用）
4. 删除 Feature Flag 配置
5. 关闭相关监控告警
6. 文档归档（写入历史记录）

### 9.2 数据保留

- 用户数据：保留 ≥ 5 年（合规）
- 审计日志：保留 ≥ 5 年
- 代码：归档到历史分支（保留 ≥ 1 年）

---

## 10. Feature Flag 规范（强制）

### 10.1 默认状态

**新功能默认 FeatureFlag 关闭**，必须通过灰度发布。

### 10.2 Flag 生命周期

```
创建（默认关闭） → 启用（白名单/小流量） → 全量 → 永久开启 → 下线前清理
```

### 10.3 Flag 命名

```
{module}.{feature}.{variant}
例：
transfer.smart_suggestion.v2
bill.email_parser
wealth.risk_assessment_v3
```

详见 `02-api/03-feature-flags.md`。

### 10.4 Flag 清理

- 全量开启 ≥ 30 天且不再需要的 Flag → 清理代码中的判断逻辑
- 删除 Flag 配置
- 每次迭代结束清理本迭代的过期 Flag

---

## 11. 状态机图

```
   ┌──────────┐
   │ Backlog  │
   └────┬─────┘
        │ 提议
        ▼
   ┌──────────┐  拒绝   ┌────────┐
   │ Proposed │────────→│ Rejected │
   └────┬─────┘         └────────┘
        │ 评审通过
        ▼
   ┌──────────┐
   │ Designed │
   └────┬─────┘
        │ 开发
        ▼
   ┌──────────┐
   │Developing│
   └────┬─────┘
        │ 开发完成
        ▼
   ┌──────────┐
   │   Beta   │ ←──→ 紧急回滚
   └────┬─────┘
        │ 全量发布
        ▼
   ┌──────────┐
   │ Launched │
   └────┬─────┘
        │ 长期运行
        ▼
   ┌──────────┐
   │  Mature  │
   └────┬─────┘
        │ 计划下线
        ▼
   ┌──────────┐
   │Deprecated│
   └────┬─────┘
        │ 到期清理
        ▼
   ┌──────────┐
   │ Removed  │
   └──────────┘
```

---

## 12. 上线检查清单（总览）

每个功能上线前必须确认：

**功能侧**：
- [ ] PRD 已评审
- [ ] 设计方案已评审
- [ ] ADR 已合并（如涉及架构）
- [ ] 代码已 Review + 合入主干
- [ ] 单元/集成/E2E 测试通过
- [ ] 文档已同步（API + 事件 + Wiki）

**架构侧**：
- [ ] 模块边界已遵守
- [ ] 依赖方向正确（向下依赖）
- [ ] 事件 Schema 已注册

**合规侧**：
- [ ] PIA 已完成（涉及 PII）
- [ ] 数据分级已应用
- [ ] 审计埋点已添加
- [ ] 同意范围已更新（如新增 PII 字段）

**可观测性侧**：
- [ ] 业务指标已添加
- [ ] 告警规则已配置
- [ ] Dashboard 已更新

**发布侧**：
- [ ] FeatureFlag 已创建，默认关闭
- [ ] 灰度方案已规划
- [ ] 回滚方案已准备
- [ ] Release Note 已撰写

---

## 13. 参考

- 路线图 — `06-product/01-roadmap.md`
- 发布流程 — `06-product/03-release-process.md`
- FeatureFlag 规范 — `02-api/03-feature-flags.md`
- 合规矩阵 — `05-security-compliance/02-compliance-matrix.md`
- PIA 模板 — `05-security-compliance/02-compliance-matrix.md` §9