# 远程协作 SOP（5 人异地版）

> 原则：GitHub 记录任务和代码事实，短会只处理阻塞与跨人决策。
> Git 细则以 [`../03-development/02-git-workflow.md`](../03-development/02-git-workflow.md) 为准。

---

## 一、三层同步

### 第 1 层：代码同步

- 当前从 `main` 创建 `feature/...`、`fix/...`、`docs/...` 短期分支，PR 回 `main`。
- `origin/dev` 尚未重新启用，**不要从 `dev` 开分支**；未来由管理员同步并统一通知后再使用。
- 禁止直接 push `main`；PR 至少 1 人 review，CI 全绿后合并。
- 提交使用仓库现有风格，例如 `feat(transfer): ...`、`fix(host): ...`、`docs(deploy): ...`、`chore(scripts): ...`。
- 每天收工前推送自己的工作分支，未完成内容在 PR 或 Issue 中明确标记，不在 `main` 留半成品。

### 第 2 层：契约同步

当前插件的唯一稳定依赖面是 `src/src/BankingAgent.Plugin.Sdk/`。修改 SDK 前先在群里说明影响范围并由架构负责人确认，修改后同步测试与文档。

`src/PluginValidator/` 是插件接入门禁，检查入口、依赖边界、Agent 暴露和分区约定。提交插件改动前运行：

```bash
dotnet build src/BankingAgent.slnx -c Release
dotnet run --project src/PluginValidator --configuration Release -- \
  src/src/BankingAgent.Host/bin/Release/net8.0/plugins
```

不要用旧的 `banking-core` 契约文档代替实际 SDK，也不要口头约定未进入代码和验证器的接口。

### 第 3 层：人的同步

每天 21:00 开 15 分钟短会，每人只回答：

1. 已完成什么（贴 Issue、PR 或 commit 链接）；
2. 当前卡在哪里（贴可复现步骤或报错）；
3. 需要谁在何时前配合。

超过 2 小时仍无进展的阻塞直接发群，不等到下一次会议。需要深入讨论的议题另开小会，不占用全员站会。

## 二、角色与轮值

| 角色 | 人选 | 职责 |
|---|---|---|
| 项目经理 | A | 排期、依赖协调、升级阻塞 |
| 架构负责人 | A | SDK、宿主装配、跨插件变更裁决 |
| 轮值记录员 | 每周轮换 | 记录决定，回写 Issue / PR |
| 轮值清道夫 | 每周轮换 | 协助复现环境问题和 CI 失败 |

清道夫负责推进定位，不替任务 Owner 隐性接管功能；负责人和插件分工以 `docs/07-team/04-分工与插件对照表.md` 为准。

## 三、环境一致性

统一使用 **.NET SDK 9.0.200+**；`.slnx` 由 SDK 9 编译，各项目目标框架为 `net8.0`。首次开工先记录环境：

```bash
dotnet --version
dotnet --list-runtimes
dotnet restore src/BankingAgent.slnx
dotnet build src/BankingAgent.slnx -c Release
```

运行时使用 **Release 双进程**，不要混用 Debug 构建与 Release 启动：

```bash
# 终端 1：模拟银行 :5200
dotnet run --project src/mock-bank/MockBank.Api -c Release

# 终端 2：宿主 :5243
dotnet run --project src/src/BankingAgent.Host -c Release
```

验证：

```bash
curl http://localhost:5200/health
curl http://localhost:5243/health
```

真实密钥只放环境变量或被忽略的本地开发配置，不进仓库。环境问题必须附 SDK、运行时、执行命令、完整错误和对应 commit，避免只发截图。

## 四、任务看板

GitHub Projects 使用：

```text
Backlog → This Week → In Progress → Review → Done
```

- 每个 Issue 标注场景、角色和类型，并写清验收标准。
- 认领任务必须 assign，不以口头约定代替。
- 开发分支和 PR 关联 Issue；进入 Review 前补齐验证结果。
- 每天站会前更新卡片；只有 PR 已合并且验收完成才能进入 Done。

## 五、PR 与评审

1. 作者先自查 diff，确认没有密钥、运行产物和无关文件。
2. 按改动范围完成构建、测试和 PluginValidator 验证。
3. PR 指向 `main`，写明风险和回退方式。
4. 至少 1 人批准且 CI 全绿后合并。
5. 契约、安全、迁移和 CI 改动主动邀请对应负责人。

`.github/CODEOWNERS` 当前还是角色占位符。真实 GitHub 用户名及其 Write 权限确认前，由作者人工添加 reviewer；分支保护由仓库管理员配置。

## 六、文件与会议记录

| 内容 | 存放位置 |
|---|---|
| 代码、文档、PPT 源文件 | GitHub 仓库 |
| 大文件（视频、录屏素材） | 团队网盘，Issue 中记录链接 |
| 可复现截图与日志片段 | 对应 Issue / PR |
| 会议决定与待办 | GitHub Issue + 群内摘要 |

周计划会用于确定本周 Issue、负责人和验收时间；每日站会只同步进度与阻塞。涉及接口、范围或交付标准的决定必须回写 Issue / PR，不能只留在聊天记录里。
