# Git 工作流执行清单

> 完整政策以 [`../03-development/02-git-workflow.md`](../03-development/02-git-workflow.md) 为唯一规范；本文只给出部署协作时的最短执行路径。
> 最后核对：2026-10-01。

## 当前结论

- 可靠基线：`main` / `origin/main` @ `0efad10`。
- `origin/dev` @ `7e17168`，落后 `main` 23 个提交且无独有提交。
- 团队尚未重新启用 `dev`：**当前不要从 `dev` 开分支，也不要向 `dev` 提 PR。**
- 不删除 `dev`、不修改 CI；未来若恢复，必须由管理员先同步并统一通知。

## 开工

```bash
git fetch origin
git switch main
git pull --ff-only origin main
git switch -c docs/deploy-update
```

按工作内容使用 `feature/...`、`fix/...` 或 `docs/...` 短期分支。提交信息沿用仓库现有风格：

```bash
git add <本次相关文件>
git commit -m "docs(deploy): 对齐部署协作说明"
git push -u origin docs/deploy-update
```

不要用 `git add .` 顺手带入数据库、日志、`bin/`、`obj/`、密钥或无关文件。

## 提 PR

1. base 选择 `main`，head 选择自己的短期分支。
2. 写清变更、验证结果、风险和关联 Issue。
3. 至少 1 人 review 批准。
4. CI 全绿后才合并。
5. 禁止直接 push `main`。

推荐保留 PR 内清晰的领域原子 commit。GitHub **Merge commit** 可以使用，并与现有 PR #1、#2 的历史一致；是否 Squash 或 Rebase 按完整规范判断，不再规定“禁止 merge commit”。

## 管理员待办

- 为 `main` 配置分支保护或 Ruleset：必须 PR、至少 1 人批准、必需检查通过、禁止绕过。
- `.github/CODEOWNERS` 仍是角色占位符。先确认真实 GitHub 用户名和 **Write** 权限，再统一替换并用测试 PR 验证；此前人工指定 reviewer。

## 冲突与收尾

- 先同步 `origin/main`，再处理自己分支上的冲突。
- `BankingAgent.Plugin.Sdk`、安全、迁移、CI 冲突必须找对应负责人确认。
- 合并后删除短期远程分支；下一项工作重新从最新 `origin/main` 开分支。
