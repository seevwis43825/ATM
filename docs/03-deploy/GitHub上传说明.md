# GitHub 上传说明

> **仓库**：https://github.com/seevwis43825/ATM
> **最后更新**：2026-09-28

---

## 1. 当前状态（已实测）

| 项目 | 状态 |
|---|---|
| 远程仓库 | `origin` = https://github.com/seevwis43825/ATM.git |
| 已推送分支 | `feature/agent-core` @ `ef6920e`（与远程一致） |
| 已推送标签 | `v1.0.0-rc1` |
| `origin/main` | `7e17168`（**落后 13 个提交**，仍是早期骨架） |
| `origin/dev` | `7e17168`（**落后 13 个提交**） |

> `main` / `dev` 落后是**事实状态**，不是错误。本次工作全部落在
> `feature/agent-core` 分支上，符合"不直接推 main"的协作铁律。

---

## 2. 上线前必做（人工，无法自动化）

### 2.1 替换 CODEOWNERS 占位符（**P0，不做等于没有 CODEOWNERS**）

`.github/CODEOWNERS` 里的 `@architect` / `@backend` / `@ai` / `@platform` / `@compliance`
是**角色占位符，不是真实 GitHub 用户名**。

GitHub 的行为：**只要文件里存在无法解析的 owner，就静默忽略整个文件** ——
表现为 PR 不自动请求任何 reviewer，且不给任何报错。目前共 **49 处**占位符。

```bash
# 在仓库根目录，把 5 个占位符换成真实用户名
sed -i 's/@architect/ @你的用户名/g' .github/CODEOWNERS
# 其余同理：@backend / @ai / @platform / @compliance
git add .github/CODEOWNERS && git commit -m "chore: 替换 CODEOWNERS 占位符为真实用户名"
```

替换后**务必用一个测试 PR 验证** reviewer 被自动添加。

### 2.2 配置分支保护

GitHub → Settings → Branches → 对 `main` 添加规则：
- Require a pull request before merging
- Require status checks to pass（勾选 CI 的 `build` / `unit-test` / `integration`）
- Require review from Code Owners

### 2.3 确认 CI 真的会跑

CI 触发分支为 `main` / `master` / `dev`（见 `.github/workflows/ci.yml`）。
`feature/*` 分支的 push **不会**触发 —— 这是有意的（省额度），
但意味着推 `feature/agent-core` 时没有 CI 反馈。

要立即验证流水线，可手动触发或在 `dev` 上开 PR。

---

## 3. 把集成分支推进到当前交付版本

有两种做法，任选其一（**都需要你确认后再执行**）：

### 方案 A：走 PR（推荐，符合协作铁律）

在 GitHub 网页上：
1. 打开 `feature/agent-core` → 点 "Contribute" → "Open pull request"
2. base 选 `dev`（先把集成版本合进 dev），或直接选 `main`
3. 等 CI 跑完 + 至少 1 人 review → 合并

### 方案 B：命令行（仅在确认 `dev` 没有别人未合并的工作时使用）

```bash
# 先确认 dev 上没有别人的提交
git fetch origin
git log --oneline origin/dev -5

# 若确认可安全推进（dev 仍停在里程碑起点）
git checkout dev
git merge --ff-only feature/agent-core
git push origin dev
```

> ⚠️ 不要对 `main` 直接强推。`main` 是保护分支，应通过 PR 合并。

---

## 4. 交付物③（源码 ZIP）打包说明

### 4.1 关键：不要把 papers/ 打进交付包

| 目录 | 体积 | 是否入交付 ZIP |
|---|---|---|
| `src/` | ~1.5 MB | ✅ 必须 |
| `docs/` | ~0.9 MB | ✅ 必须 |
| `.github/`、`scripts/`、`README.md` | <0.1 MB | ✅ 必须 |
| `apps/`（早期 Node/TS 版） | ~0.3 MB 源码 | 🟡 可选（不含 node_modules） |
| `papers/`（参考文献 PDF） | **136 MB** | ❌ **不要**，与交付物无关且吃掉配额 |
| `src/**/bin`、`obj`、`node_modules` | ~270 MB | ❌ 不要（已被 .gitignore 排除） |

**建议做法**：直接用 `git archive` 打包，天然排除所有未跟踪文件：

```bash
cd <仓库根>
git archive --format=zip -o AI-Banking-Agent-源码.zip \
  --prefix=AI-Banking-Agent/ \
  HEAD src docs .github scripts README.md banking-core
```

或用选择性排除：

```bash
git archive --format=zip -o /tmp/all.zip HEAD
# 再用 7z/zip 删掉 papers/ 目录后另存
```

打包后请核对体积远小于 300 MB，并解压验证能编译：

```bash
unzip -q AI-Banking-Agent-源码.zip -d /tmp/verify
cd /tmp/verify/AI-Banking-Agent
dotnet build src/BankingAgent.slnx -c Release
```

### 4.2 仓库整体体积

- 跟踪文件：**298 个 / 138 MB**（其中 `papers/` 占 136 MB）
- 最大单文件：16.7 MB（`papers/pdfs/64_...pdf`）—— 未触及 GitHub 100 MB 单文件硬限制
- `.git` 目录：约 144 MB

> 若希望仓库更轻，可把 `papers/` 移出仓库或改用 Git LFS。
> `.gitignore` 里已留有 `# papers/pdfs/` 的注释开关，取消注释即可停止跟踪
> （需另做 `git rm -r --cached papers/pdfs`）。

---

## 5. 上传后的验收清单

- [ ] `feature/agent-core` 与本地一致（`git rev-parse feature/agent-core` == `git rev-parse origin/feature/agent-core`）
- [ ] `v1.0.0-rc1` 标签已上传
- [ ] CODEOWNERS 5 个占位符已替换为真实用户名，且测试 PR 能自动请求 reviewer
- [ ] `main` 分支保护已配置（PR + 状态检查 + Code Owner 审批）
- [ ] 至少触发过一次 CI 并全绿
- [ ] 交付物③ ZIP 已打包且**不含** `papers/`、`bin/`、`obj/`、`node_modules/`
- [ ] 解压后的 ZIP 能通过 `dotnet build src/BankingAgent.slnx -c Release`

---

## 6. 本地存档命令（已执行，留档备查）

```bash
# 完整性校验（只会有 dangling blob，属正常残留，不是损坏）
git fsck --no-progress

# 提交并打交付标签
git add -A && git commit -m "..."
git tag -a v1.0.0-rc1 -m "AI Banking Agent — 完整可运行基座"

# 推送分支与标签
git push origin feature/agent-core
git push origin v1.0.0-rc1
```

### 恢复方式（万一需要回滚）

```bash
git checkout v1.0.0-rc1          # 查看该交付点
git reset --hard v1.0.0-rc1      # 把当前分支回滚到该交付点（谨慎）
```