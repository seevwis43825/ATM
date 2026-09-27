# Git 工作流（Git Workflow）

> **状态**：已通过 · **所有者**：架构师 · **版本**：v1.0
> **最后更新**：2026-09-21

---

## 1. 分支策略

采用 **Trunk-Based Development + 短期 Feature Branch**：

```
main (生产分支，永远可发布)
├── feature/CONTEXT-name             # 功能分支（≤ 3 天）
├── fix/CONTEXT-name                 # 修复分支（≤ 1 天）
├── release/vX.Y.Z                  # 发布分支（可选）
└── hotfix/CRITICAL-fix             # 紧急修复
```

---

## 2. 分支命名规范

### 2.1 Feature 分支
```
feature/<context>-<short-description>
```

**示例**：
- `feature/transfer-qrcode`（转账 Context 增加扫码转账）
- `feature/wealth-robo-advisor`（理财 Context 增加智能投顾）
- `feature/cross-scenario-birthday`（跨场景生日功能）

### 2.2 Fix 分支
```
fix/<context>-<bug-id-or-short-desc>
```

**示例**：
- `fix/transfer-null-payee`
- `fix/bill-analysis-categorization-error`

### 2.3 Hotfix 分支
```
hotfix/<critical-issue>
```

**示例**：
- `hotfix/payment-double-charge`
- `hotfix/llm-token-leak`

---

## 3. 提交规范

### 3.1 提交信息格式
```
<type>(<scope>): <subject>

<footer>
```

详见 [`01-coding-standards.md` § 5](01-coding-standards.md#5-git-提交规范)。

### 3.2 提交原子性
- 一个 commit 只做一件事
- 单元测试 + 实现 **同一个 commit**
- 不要 commit 注释掉的代码、调试 print、半成品

### 3.3 提交频率
- 每完成一个逻辑单元 → 一次 commit
- 一天至少 2-5 次 commit
- 不积攒

---

## 4. Pull Request 流程

### 4.1 创建 PR

```bash
git checkout main
git pull origin main
git checkout -b feature/transfer-qrcode

# 开发 + 推送
git push origin feature/transfer-qrcode

# 在 GitHub 创建 PR
```

### 4.2 PR 标题

```
[Context] <subject>
```

**示例**：
- `[Transfer] 增加扫码转账功能`
- `[Wealth] 实现风险测评问卷`

### 4.3 PR 模板（`.github/pull_request_template.md`）

```markdown
## 变更说明
<!-- 简述本次变更 -->

## 关联 Issue
<!-- Closes #xxx -->

## 变更类型
- [ ] 新功能
- [ ] Bug 修复
- [ ] 重构
- [ ] 文档
- [ ] 性能优化

## 测试
- [ ] 单元测试已添加
- [ ] 集成测试已添加
- [ ] 已在本地跑通

## Checklist
- [ ] 已更新相关文档
- [ ] 已考虑向后兼容性
- [ ] 已添加/更新 FeatureFlag（如适用）
- [ ] 已检查审计日志（如适用）
- [ ] 已检查合规要求（如适用）

## 截图/演示（如适用）
```

### 4.4 Review 要求

| 变更类型 | Reviewer 数 | 必须 Reviewer |
|---------|-----------|---------------|
| 普通功能 | 1 | 任意 1 名团队成员 |
| 核心架构变更 | 2 | 架构师 + 1 名 |
| 安全/合规相关 | 2 | 架构师 + 安全责任人 |
| 数据库 migration | 1 | 架构师 |
| FeatureFlag | 1 | 业务开发 |

详见 [`03-pr-review-checklist.md`](03-pr-review-checklist.md)。

### 4.5 合并策略

- **Squash and merge**：所有 commit 合并为 1 个（用于 feature 分支）
- **Rebase and merge**：保持线性历史（用于小修复）
- **禁止 merge commit**：保持 main 分支线性

```bash
# 推荐：在 GitHub UI 中选择 "Squash and merge"
```

---

## 5. 发布流程

### 5.1 版本号（Semantic Versioning）

```
MAJOR.MINOR.PATCH
```

- **MAJOR**：不兼容 API 变更
- **MINOR**：向后兼容的新功能
- **PATCH**：向后兼容的 bug 修复

**示例**：`v1.2.3`

### 5.2 Tag 规范

```bash
git tag -a v1.2.3 -m "Release 1.2.3 - 增加扫码转账"
git push origin v1.2.3
```

### 5.3 发布分支（可选）

```bash
git checkout main
git pull
git checkout -b release/v1.2.3
# 仅修复 critical bug，cherry-pick
git commit -m "fix: ..."
git tag v1.2.3
```

---

## 6. Commit Hooks（本地检查）

`.git/hooks/pre-commit`：

```bash
#!/bin/bash
# 格式化检查
dotnet format --verify-no-changes src/
# 单元测试必须通过
dotnet test --no-build src/ProjectName.UnitTests/
```

### 7. 紧急修复流程

```bash
# 1. 从 main 创建 hotfix
git checkout main
git checkout -b hotfix/payment-double-charge

# 2. 修复
git commit -m "fix(payment): prevent double charge on retry"

# 3. 测试 + 推送
git push origin hotfix/payment-double-charge

# 4. 快速 review（仅 1 人）+ merge to main
# 5. 立即部署到生产
```

详见 [`../04-operations/03-incident-response.md`](../04-operations/03-incident-response.md)。

---

## 8. 仓库布局

### 8.1 Monorepo 单一仓库

```
bank-agent/                          # 单仓库
├── .github/
│   ├── workflows/                   # CI/CD
│   └── pull_request_template.md
├── docs/                             # 文档（本目录）
├── src/
│   ├── Core/
│   ├── Contexts/
│   ├── AIService/
│   └── Bootstrap/
├── tests/
│   ├── UnitTests/
│   ├── IntegrationTests/
│   └── E2ETests/
├── deploy/
│   ├── k8s/
│   ├── helm/
│   └── terraform/
├── tools/
│   └── scripts/
├── .editorconfig
├── .gitignore
├── Directory.Build.props            # .NET 共享配置
├── package.json                      # Node 依赖
└── README.md
```

---

## 9. 关联文档

- **代码规范**：[`01-coding-standards.md`](01-coding-standards.md)
- **PR Review 清单**：[`03-pr-review-checklist.md`](03-pr-review-checklist.md)
- **CI/CD**：[`05-ci-cd-pipeline.md`](05-ci-cd-pipeline.md)
- **环境搭建**：[`06-environment-setup.md`](06-environment-setup.md)