# Git 工作流（唯一规范）

> **所有者**：架构师 · **最后更新**：2026-10-01
> 本文是仓库 Git 协作的唯一规范。部署目录中的 [`../03-deploy/Git工作流规范.md`](../03-deploy/Git工作流规范.md) 只保留执行清单；两处不重复定义政策。

---

## 1. 当前基线与分支策略

截至 2026-10-01，已核对的远程状态：

- `main` / `origin/main` 位于 `0efad10`，是当前可靠基线；
- `origin/dev` 位于 `7e17168`，落后 `main` 23 个提交，且没有 `main` 之外的独有提交；
- 团队尚未正式重新启用 `dev`。

因此当前采用 **`main` + 短期工作分支 + PR 回 `main`**：

```text
main（可靠基线；禁止直接 push）
├── feature/<scope>-<description>   功能
├── fix/<scope>-<description>       修复
└── docs/<scope>-<description>      文档
```

**当前不要从 `dev` 开分支，也不要把 PR 提交到 `dev`。** 不删除 `dev`，也不为此修改 CI。若未来恢复 `dev`，必须先由仓库管理员将其同步到当时的 `main`，确认无遗漏后统一通知团队；在通知发出前，任何人都不得自行使用。

## 2. 开分支与同步

分支名只用小写英文、数字和连字符，例如：

- `feature/transfer-qrcode`
- `fix/ci-migration-gate`
- `docs/deploy-workflow`

从最新 `origin/main` 创建分支：

```bash
git fetch origin
git switch main
git pull --ff-only origin main
git switch -c feature/transfer-qrcode
```

工作期间只推自己的分支：

```bash
git push -u origin feature/transfer-qrcode
```

PR 前若 `main` 已推进，先更新工作分支并解决冲突，再重新验证。不要改写他人的远程分支，不要使用强推绕过协作检查。

## 3. 提交规范

使用与仓库历史一致的 Conventional Commits：

```text
<type>(<scope>): <一句话说明>
```

常用类型与真实风格示例：

- `feat(ai): 接入可选大模型意图识别`
- `fix(host+ci): 声明插件构建顺序依赖`
- `docs(readme): 补充插件目录排查说明`
- `chore(scripts): 添加手动推送 GitHub 的备用脚本`

规则：

1. 一个 commit 只完成一个可说明、可验证的逻辑单元。
2. 实现、对应测试和必要文档应在同一组领域内原子 commit 中完成。
3. 不提交密钥、数据库、日志、`bin/`、`obj/` 或调试残留。
4. PR 中推荐保留有意义的领域内原子 commit；不要为了追求单一 commit 把不同领域改动压在一起。

详见 [`01-coding-standards.md` § 5](01-coding-standards.md#5-git-提交规范)。

## 4. Pull Request 与合并门禁

所有改动都通过工作分支向 `main` 提 PR，**禁止直接 push `main`**。团队合并规则是：

1. 至少 1 名团队成员完成 review 并批准；
2. CI 全绿；
3. PR 描述写清变更、验证结果、风险与相关 Issue；
4. 契约、安全、数据库迁移等高风险改动按 [`03-pr-review-checklist.md`](03-pr-review-checklist.md) 增加对应负责人 review。

本地最低验证按改动范围执行；涉及当前 .NET 主线时通常至少运行：

```bash
dotnet build src/BankingAgent.slnx -c Release
dotnet test src/UnitTests/UnitTests.csproj -c Release
dotnet run --project src/PluginValidator --configuration Release -- \
  src/src/BankingAgent.Host/bin/Release/net8.0/plugins
```

GitHub 合并方式按 PR 内容选择：

- **Merge commit**：推荐用于保留多个清晰的领域内原子 commit；当前 PR #1、#2 已采用该历史形态，可继续延续。
- **Squash and merge**：仅当分支提交都是修补、回滚价值很低时使用。
- **Rebase and merge**：仅在提交关系清晰、不会破坏协作者引用时使用。

仓库不要求线性历史，**不要再以“禁止 merge commit”为规则**。合并后删除已完成的短期远程分支，避免继续在旧分支上开发。

## 5. GitHub 管理员配置

团队规则必须由管理员在 GitHub 为 `main` 配置分支保护或 Ruleset，至少包括：

- Require a pull request before merging；
- Require approvals：至少 1；
- Require status checks to pass；
- 禁止直接 push 和绕过门禁（仅保留必要的管理员应急权限）。

`.github/CODEOWNERS` 当前仍使用 `@architect`、`@backend`、`@ai`、`@platform`、`@compliance` 等角色占位符，不能当作已生效的自动审批。管理员应先确认真实 GitHub 用户名及其仓库 **Write** 权限，再统一替换并用测试 PR 验证。完成前以人工指定 reviewer 为准。

## 6. 冲突与紧急修复

- 小步提交、及时同步，减少冲突范围。
- `BankingAgent.Plugin.Sdk`、安全、迁移或 CI 文件发生冲突时，停止猜测，找对应负责人共同确认。
- 紧急修复仍从最新 `main` 创建 `fix/...` 分支，经至少 1 人 review 与 CI 全绿后合并；紧急不等于绕过门禁。

```bash
git fetch origin
git switch -c fix/payment-double-charge origin/main
# 修复、测试、提交
git push -u origin fix/payment-double-charge
```

## 7. 发布与标签

发布标签只在 `main` 上、且目标 commit 已完成验收后创建。标签一旦发布不得移动或重打；需要修订时使用新的版本号。版本号采用 Semantic Versioning：`vMAJOR.MINOR.PATCH`，预发布版本可用 `-rcN`。

## 8. 关联文档

- **部署侧执行清单**：[`../03-deploy/Git工作流规范.md`](../03-deploy/Git工作流规范.md)
- **代码规范**：[`01-coding-standards.md`](01-coding-standards.md)
- **PR Review 清单**：[`03-pr-review-checklist.md`](03-pr-review-checklist.md)
- **CI/CD**：[`05-ci-cd-pipeline.md`](05-ci-cd-pipeline.md)
- **环境搭建**：[`06-environment-setup.md`](06-environment-setup.md)