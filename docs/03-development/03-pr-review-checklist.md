# PR Review 清单（PR Review Checklist）

> **状态**：已通过 · **所有者**：架构师 · **版本**：v1.0
> **最后更新**：2026-10-01
>
> **适用范围**：先检查当前 .NET 8 模块化单体。标为 **Target** 的检查仅在 PR 实际引入对应能力时适用。

---

## 1. 当前合并基线

- PR 从短期工作分支提交到 `main`，至少 1 人批准且 CI 全绿；不以 `dev` 为当前主线。
- PR 描述应列出变更、验证、风险和关联 Issue。
- 不得假定仓库存在 Python/FastAPI、Next.js、PostgreSQL、Kafka、Testcontainers 或 Playwright。
- 按改动范围执行最低验证：

```bash
dotnet restore src/BankingAgent.slnx
dotnet build src/BankingAgent.slnx -c Release
dotnet test src/UnitTests/UnitTests.csproj -c Release
```

涉及插件装载时，再运行：

```bash
dotnet run --project src/PluginValidator --configuration Release -- \
  src/src/BankingAgent.Host/bin/Release/net8.0/plugins
```

E2E、StressTest、LoadTest 是需要已启动 Host（`:5243`）与 MockBank（`:5200`）的独立程序，不包含在 `dotnet test` 中。

## 2. Reviewer 角色

| 类型 | Reviewer | 时间 |
|------|---------|------|
| 普通 PR | 任意 1 名 | 24 小时内 |
| 架构相关 | 架构师 + 1 名 | 24 小时 |
| 安全/合规 | 架构师 + 安全 | 24 小时 |
| 数据库 migration | 架构师 | 24 小时 |
| 紧急 | 1 名即可 | 1 小时内 |

---

## 3. 通用检查（所有 PR）

### 3.1 代码质量
- [ ] 是否符合 [`01-coding-standards.md`](01-coding-standards.md)？
- [ ] 命名规范？类/方法/属性 PascalCase，私有字段 _camelCase？
- [ ] 复杂度和文件规模是否合理，超长代码是否有拆分理由？
- [ ] 无重复代码（可以抽取的公共方法）？
- [ ] 注释解释了"为什么"而非"是什么"？

### 3.2 测试
- [ ] 行为变化是否在 `src/UnitTests` 增补或调整测试？
- [ ] 边界条件测试（null、empty、最大值、负数）？
- [ ] 声称的测试数量、覆盖率、性能数字是否来自本次运行输出？
- [ ] 与改动范围对应的构建、单元测试和独立验证程序是否通过？

### 3.3 文档
- [ ] PR 描述清晰？
- [ ] 链接到相关 Issue？
- [ ] 重大变更更新了 ADR？
- [ ] API 变更更新了 OpenAPI 规范？
- [ ] 事件变更更新了 Schema 文档？
- [ ] README 或 docs/ 同步更新？

### 3.4 安全与合规
- [ ] 无硬编码密钥、密码？
- [ ] 用户输入经过校验？
- [ ] 敏感数据脱敏？
- [ ] 涉及金融操作有人工回环？
- [ ] 涉及个人信息遵循 PIPL？
- [ ] 见 [`../05-security-compliance/02-compliance-matrix.md`](../05-security-compliance/02-compliance-matrix.md)

### 3.5 性能
- [ ] 无 N+1 查询？
- [ ] 无循环内的 await？
- [ ] 慢查询已加索引？
- [ ] 大数据已分页？
- [ ] 缓存已设置合理的 TTL？

---

## 4. 架构与插件 PR

- [ ] 符合 [`../00-architecture/05-module-boundaries.md`](../00-architecture/05-module-boundaries.md)？
- [ ] Host 是否仍只负责装配/API，没有吸收场景业务逻辑？
- [ ] 共享运行时放 Base、公共插件契约放 Sdk、业务能力放对应插件？
- [ ] Host 是否避免编译期依赖插件实现类型？
- [ ] 新插件是否提供清单、依赖声明、Agent/贡献器注册，并通过 PluginValidator？
- [ ] DI 注入而非 `new`？
- [ ] 异步方法使用 async/await 全链路？
- [ ] CancellationToken 传递到所有异步调用？

---

## 5. 数据库 migration PR

- [ ] 当前改动是 SQLite `EnsureCreated` 模型变化，还是正式 EF Core Migration？
- [ ] Migration 是否由 `dotnet ef migrations add` 生成迁移类、Designer 与 ModelSnapshot，而非 Flyway `V*.sql`？
- [ ] 生成前是否构建解决方案并确认四插件贡献器均被设计时工厂发现？
- [ ] 是否使用 Base 为迁移项目、Host 为启动项目，并明确目标 Provider（默认 PostgreSQL）？
- [ ] 只能向前追加，不可修改已应用的 migration？
- [ ] 大表变更考虑了 downtime？
- [ ] 索引合理？
- [ ] 字段 NOT NULL 有 default？
- [ ] 已考虑数据迁移（如重命名）？
- [ ] 测试环境已验证？

---

## 6. API 变更 PR

### 6.1 新增端点
- [ ] 路径符合 [`../02-api/01-rest-api-spec.md`](../02-api/01-rest-api-spec.md)？
- [ ] 请求/响应类型是否放在实际拥有该契约的 Host、Base 或 Plugin.Sdk 中，而不是引用不存在的 `Shared/Contracts`？
- [ ] 错误响应符合统一格式？
- [ ] 支持 Idempotency-Key（POST）？
- [ ] 限流配置？
- [ ] 鉴权要求正确？

### 6.2 修改端点
- [ ] 向后兼容？
- [ ] 如果不兼容，版本号升级（v1 → v2）？
- [ ] 标记 deprecated（带 Sunset header）？

---

## 7. 事件 Schema PR（按实际采用的契约）

- [ ] 命名符合 [`../02-api/02-event-schema.md`](../02-api/02-event-schema.md)？
- [ ] 进程内事件是否符合当前 `DomainEvent` 契约；对外事件若采用 CloudEvents，是否符合 CloudEvents 1.0？
- [ ] 字段向后兼容？
- [ ] 必填字段已说明原因？
- [ ] 至少 1 个订阅者已订阅（否则不该发布）？

---

## 8. AI/LLM 相关 PR

- [ ] Prompt 在版本控制下？
- [ ] 用了哪个 LLM provider？主/备？
- [ ] 是否有 Prompt 注入防护？
- [ ] 是否经过当前 Base/插件中的安全、合规与审计组件？
- [ ] 是否记录决策到 Audit？
- [ ] 是否遵守"人类最终决策"原则（高风险操作）？

---

## 9. FeatureFlag PR（Target；引入后适用）

- [ ] 默认关闭？
- [ ] 命名符合规范？
- [ ] 添加到 Admin Console 可视化？
- [ ] Kill switch 文档？

---

## 10. 评审评论模板

### 10.1 正面反馈
```
👍 这部分的设计很清晰，命名也很好。
✅ 测试覆盖到位。
```

### 10.2 修改建议
```
💭 这里建议改用 `ICoreBankAdapter.ExecuteAsync(order, ct)`，
   避免直接依赖具体类。
```

### 10.3 必须修改（阻塞）
```
🚨 Blocking: 这里直接把 DbContext 暴露给了 Controller，
   违反了模块边界规则。需要改为通过 Application 层调用。
```

### 10.4 提问（非阻塞）
```
❓ 这里的 retries 策略是基于什么文档？
```

---

## 11. 自我 Review 检查（提交前）

提交者应**自己先 Review 一遍**，通过 PR 模板的所有 Checklist。

### 11.1 自检脚本
```bash
# 在仓库根目录执行当前 .NET 主线检查
dotnet restore src/BankingAgent.slnx
dotnet build src/BankingAgent.slnx -c Release
dotnet test src/UnitTests/UnitTests.csproj -c Release
```

只有 PR 实际引入并配置了 Python/前端工具链时，才增加相应 lint/test；不要运行仓库中不存在的 `ai-service/`、`web/` 或脚本。

---

## 12. 关联文档

- **代码规范**：[`01-coding-standards.md`](01-coding-standards.md)
- **Git 工作流**：[`02-git-workflow.md`](02-git-workflow.md)
- **测试策略**：[`04-testing-strategy.md`](04-testing-strategy.md)
- **合规矩阵**：[`../05-security-compliance/02-compliance-matrix.md`](../05-security-compliance/02-compliance-matrix.md)