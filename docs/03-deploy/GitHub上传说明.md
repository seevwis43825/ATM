# GitHub 上传说明

> **仓库**：<https://github.com/seevwis43825/ATM>
> **远程地址**：`git@github.com:seevwis43825/ATM.git`
> **最后核对**：2026-10-01

---

## 1. 当前仓库事实

| 项目 | 当前状态 |
|---|---|
| 可靠基线 | `main` / `origin/main` @ `0efad10` |
| `origin/dev` | `7e17168`，落后 `main` 23 个提交，无独有提交 |
| 旧 `feature/agent-core` | 已进入 `main`，不是待合并工作 |
| Pull Request | PR #1、PR #2 均已合并到 `main` |
| 历史标签 | `v1.0.0-rc1` 已存在 |

**不要重新执行旧的 `feature/agent-core` 合并、推进 `dev` 或重打标签。** 当前团队尚未正式重新启用 `dev`，新工作统一从最新 `main` 开短期分支，PR 回 `main`。

可用 PowerShell 复核：

```powershell
git remote -v
git fetch origin --prune
git rev-parse origin/main
git rev-parse origin/dev
git rev-list --count origin/dev..origin/main
git rev-list --count origin/main..origin/dev
git log --merges --oneline origin/main -5
```

## 2. 新改动上传流程

```powershell
git fetch origin
git switch main
git pull --ff-only origin main
git switch -c docs/deploy-update

# 修改并完成本地验证后，只暂存相关文件
git add docs/03-deploy
git commit -m "docs(deploy): 对齐部署与上传说明"
git push -u origin docs/deploy-update
```

随后在 GitHub 创建 `docs/deploy-update` → `main` 的 PR。禁止直接 push `main`；至少 1 人 review 且 CI 全绿后才能合并。PR 中可以保留领域内原子 commit，并可使用 GitHub **Merge commit** 延续 PR #1、#2 的现有历史。

`origin/dev` 暂时只保留，不删除、不作为分支起点，也不为此修改 CI。若未来恢复，必须先由管理员同步到当时的 `main` 并统一通知。

## 3. 管理员上线前配置

### 3.1 CODEOWNERS

`.github/CODEOWNERS` 中的 `@architect`、`@backend`、`@ai`、`@platform`、`@compliance` 仍是角色占位符，不能视为有效 GitHub owner。

管理员应：

1. 确认每个角色对应的真实 GitHub 用户名；
2. 确认这些用户对仓库具有 **Write** 权限；
3. 在独立分支中统一替换占位符并提交；
4. 用测试 PR 验证自动请求 reviewer 和 Code Owner 审批是否生效。

在验证完成前，每个 PR 由作者人工添加 reviewer。

### 3.2 `main` 分支保护

在 GitHub Settings 中为 `main` 配置 Branch protection rule 或 Ruleset：

- Require a pull request before merging；
- Required approvals 至少 1；
- Require status checks to pass；
- 禁止直接 push 和绕过门禁；
- CODEOWNERS 验证有效后，再启用 Code Owner review 要求。

这是管理员操作；文档规则本身不会自动保护分支。

## 4. CI 验收

`.github/workflows/ci.yml` 会在以 `main` 为 base 的 PR 上运行。合并前要求 CI 全绿，至少确认：

- 编译及数据库迁移一致性门禁；
- `PluginValidator` 插件契约门禁；
- 单元测试；
- 集成测试与并发压测；
- 最终检查汇总。

不要为了恢复 `dev` 修改 CI。当前工作流仍包含 `dev` 触发项，这不代表团队已重新启用 `dev`。

## 5. 交付物③：源码 ZIP

交付包只选择：

- `src/`
- `docs/`
- `.github/`
- `scripts/`
- `README.md`
- `执行手册.md`

明确排除 `papers/`、任何 `bin/`、`obj/`、数据库文件和日志。`git archive` 只打包已提交内容，因此应在目标 PR 合并、`main` 更新且工作区干净后执行。

### 5.1 Windows PowerShell 打包

```powershell
git fetch origin
git switch main
git pull --ff-only origin main
git status --short

git archive --format=zip `
  --output="AI-Banking-Agent-源码.zip" `
  --prefix="AI-Banking-Agent/" `
  HEAD -- `
  src docs .github scripts README.md "执行手册.md" `
  ":(exclude)**/bin/**" `
  ":(exclude)**/obj/**" `
  ":(exclude)**/db/**" `
  ":(exclude)**/*.db" `
  ":(exclude)**/*.db-wal" `
  ":(exclude)**/*.db-shm" `
  ":(exclude)**/*.log" `
  ":(exclude)**/logs/**"
```

这里不包含 `papers/`，也不包含旧 `banking-core` 或已删除的 Node 应用。不要再使用 `/tmp`、`sed`、`unzip` 等 Unix 命令处理 Windows 交付包。

### 5.2 Windows PowerShell 验证

```powershell
$zip = Resolve-Path ".\AI-Banking-Agent-源码.zip"
$verify = Join-Path $env:TEMP "AI-Banking-Agent-verify"

if (Test-Path $verify) {
  Remove-Item $verify -Recurse -Force
}
Expand-Archive -Path $zip -DestinationPath $verify

$root = Join-Path $verify "AI-Banking-Agent"
$required = @(
  "src",
  "docs",
  ".github",
  "scripts",
  "README.md",
  "执行手册.md"
)
$required | ForEach-Object {
  $path = Join-Path $root $_
  if (-not (Test-Path $path)) { throw "交付包缺少：$_" }
}

$forbidden = Get-ChildItem $root -Recurse -Force | Where-Object {
  $_.FullName -match '\\(papers|bin|obj|db|logs?)(\\|$)' -or
  $_.Name -match '\.(db|db-wal|db-shm|log)$'
}
if ($forbidden) {
  $forbidden.FullName
  throw "交付包包含禁止内容"
}

dotnet build (Join-Path $root "src\BankingAgent.slnx") -c Release
Get-Item $zip | Select-Object FullName, Length, LastWriteTime
```

最终还应人工打开 ZIP，确认目录层级正确、文件可读，并核对平台大小限制。

## 6. 历史标签说明

`v1.0.0-rc1` 是已经发布的历史点，只用于查看或复核当时内容：

```powershell
git fetch origin --tags
git show --no-patch --decorate v1.0.0-rc1
```

**不要删除、移动或重新创建 `v1.0.0-rc1`。** 后续候选版本使用新标签，例如 `v1.0.0-rc2`，并且只在目标 `main` commit 已通过验收后由负责人创建。

## 7. 最终检查

- [ ] `origin` 为 `git@github.com:seevwis43825/ATM.git`
- [ ] 新改动从最新 `main` 开分支，PR base 为 `main`
- [ ] 没有重新合并旧 `feature/agent-core`，没有自行推进 `dev`
- [ ] 至少 1 人 review，CI 全绿
- [ ] 管理员已配置 `main` 分支保护
- [ ] CODEOWNERS 已替换为具有 Write 权限的真实用户，并经测试 PR 验证
- [ ] ZIP 只含指定六项，不含 `papers/bin/obj/db/log`
- [ ] 解压后 `dotnet build src/BankingAgent.slnx -c Release` 通过
- [ ] `v1.0.0-rc1` 保持原历史点，未重打