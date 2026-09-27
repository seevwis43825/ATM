# PR Review 清单（PR Review Checklist）

> **状态**：已通过 · **所有者**：架构师 · **版本**：v1.0
> **最后更新**：2026-09-21

---

## 1. Reviewer 角色

| 类型 | Reviewer | 时间 |
|------|---------|------|
| 普通 PR | 任意 1 名 | 24 小时内 |
| 架构相关 | 架构师 + 1 名 | 24 小时 |
| 安全/合规 | 架构师 + 安全 | 24 小时 |
| 数据库 migration | 架构师 | 24 小时 |
| 紧急 | 1 名即可 | 1 小时内 |

---

## 2. 通用检查（所有 PR）

### 2.1 代码质量
- [ ] 是否符合 [`01-coding-standards.md`](01-coding-standards.md)？
- [ ] 命名规范？类/方法/属性 PascalCase，私有字段 _camelCase？
- [ ] 圈复杂度 ≤ 10？
- [ ] 函数 ≤ 50 行？文件 ≤ 500 行？
- [ ] 无重复代码（可以抽取的公共方法）？
- [ ] 注释解释了"为什么"而非"是什么"？

### 2.2 测试
- [ ] 单元测试覆盖率 ≥ 80%（核心代码）？
- [ ] 所有公共方法都有测试？
- [ ] 边界条件测试（null、empty、最大值、负数）？
- [ ] 集成测试覆盖了端到端流程？
- [ ] 所有测试通过？

### 2.3 文档
- [ ] PR 描述清晰？
- [ ] 链接到相关 Issue？
- [ ] 重大变更更新了 ADR？
- [ ] API 变更更新了 OpenAPI 规范？
- [ ] 事件变更更新了 Schema 文档？
- [ ] README 或 docs/ 同步更新？

### 2.4 安全与合规
- [ ] 无硬编码密钥、密码？
- [ ] 用户输入经过校验？
- [ ] 敏感数据脱敏？
- [ ] 涉及金融操作有人工回环？
- [ ] 涉及个人信息遵循 PIPL？
- [ ] 见 [`../05-security-compliance/02-compliance-matrix.md`](../05-security-compliance/02-compliance-matrix.md)

### 2.5 性能
- [ ] 无 N+1 查询？
- [ ] 无循环内的 await？
- [ ] 慢查询已加索引？
- [ ] 大数据已分页？
- [ ] 缓存已设置合理的 TTL？

---

## 3. 架构相关 PR

- [ ] 符合 [`../00-architecture/05-module-boundaries.md`](../00-architecture/05-module-boundaries.md)？
- [ ] 没有跨 Context 直接调用？
- [ ] 没有引入新的共享实体？
- [ ] 接口隔离（仅依赖接口，不依赖具体类）？
- [ ] DI 注入而非 `new`？
- [ ] 异步方法使用 async/await 全链路？
- [ ] CancellationToken 传递到所有异步调用？

---

## 4. 数据库 migration PR

- [ ] migration 文件名格式 `V{number}__{description}.sql`？
- [ ] 只能向前，不可修改已应用的 migration？
- [ ] 大表变更考虑了 downtime？
- [ ] 索引合理？
- [ ] 字段 NOT NULL 有 default？
- [ ] 已考虑数据迁移（如重命名）？
- [ ] 测试环境已验证？

---

## 5. API 变更 PR

### 5.1 新增端点
- [ ] 路径符合 [`../02-api/01-rest-api-spec.md`](../02-api/01-rest-api-spec.md)？
- [ ] 请求/响应 DTO 在 Shared/Contracts 中？
- [ ] 错误响应符合统一格式？
- [ ] 支持 Idempotency-Key（POST）？
- [ ] 限流配置？
- [ ] 鉴权要求正确？

### 5.2 修改端点
- [ ] 向后兼容？
- [ ] 如果不兼容，版本号升级（v1 → v2）？
- [ ] 标记 deprecated（带 Sunset header）？

---

## 6. 事件 Schema PR

- [ ] 命名符合 [`../02-api/02-event-schema.md`](../02-api/02-event-schema.md)？
- [ ] 符合 CloudEvents 1.0？
- [ ] 字段向后兼容？
- [ ] 必填字段已说明原因？
- [ ] 至少 1 个订阅者已订阅（否则不该发布）？

---

## 7. AI/LLM 相关 PR

- [ ] Prompt 在版本控制下？
- [ ] 用了哪个 LLM provider？主/备？
- [ ] 是否有 Prompt 注入防护？
- [ ] 是否调用 Guardrails Agent？
- [ ] 是否记录决策到 Audit？
- [ ] 是否遵守"人类最终决策"原则（高风险操作）？

---

## 8. FeatureFlag PR

- [ ] 默认关闭？
- [ ] 命名符合规范？
- [ ] 添加到 Admin Console 可视化？
- [ ] Kill switch 文档？

---

## 9. 评审评论模板

### 9.1 正面反馈
```
👍 这部分的设计很清晰，命名也很好。
✅ 测试覆盖到位。
```

### 9.2 修改建议
```
💭 这里建议改用 `ICoreBankAdapter.ExecuteAsync(order, ct)`，
   避免直接依赖具体类。
```

### 9.3 必须修改（阻塞）
```
🚨 Blocking: 这里直接把 DbContext 暴露给了 Controller，
   违反了模块边界规则。需要改为通过 Application 层调用。
```

### 9.4 提问（非阻塞）
```
❓ 这里的 retries 策略是基于什么文档？
```

---

## 10. 自我 Review 检查（提交前）

提交者应**自己先 Review 一遍**，通过 PR 模板的所有 Checklist。

### 10.1 自检脚本
```bash
# 格式化
dotnet format src/

# 构建 + 测试
dotnet build
dotnet test

# 类型检查
mypy ai-service/

# Lint
eslint web/

# 完整 CI 脚本
./scripts/run-ci-checks.sh
```

---

## 11. 关联文档

- **代码规范**：[`01-coding-standards.md`](01-coding-standards.md)
- **Git 工作流**：[`02-git-workflow.md`](02-git-workflow.md)
- **测试策略**：[`04-testing-strategy.md`](04-testing-strategy.md)
- **合规矩阵**：[`../05-security-compliance/02-compliance-matrix.md`](../05-security-compliance/02-compliance-matrix.md)