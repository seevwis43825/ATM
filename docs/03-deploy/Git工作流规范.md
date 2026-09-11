# Git 工作流规范

## 分支模型

```
main          ← 永远可运行、可演示（保护分支，禁止直接 push）
 └── dev      ← 集成分支，每周日合并一次 main
      ├── feature/transfer-by-name      (B)
      ├── feature/bill-analysis         (C)
      ├── feature/wealth-products       (D)
      ├── feature/web-chat-ui           (E)
      └── feature/agent-core            (A)
```

## 日常操作流程

```bash
# 1. 每天开工先同步
git checkout dev
git pull origin dev

# 2. 从 dev 切出自己的功能分支（分支名用英文或拼音，别用中文，避免编码问题）
git checkout -b feature/bill-analysis

# 3. 干活，随时提交
git add .
git commit -m "feat(bill): 实现消费分类统计"

# 4. 每天收工前推到远程
git push origin feature/bill-analysis

# 5. 功能完成后，去 GitHub 提 Pull Request 到 dev
# 6. 至少 1 人 review 通过后合并
```

## 提交信息格式

```
<类型>(<模块>): <做了什么>

类型：feat / fix / docs / refactor / test / chore
模块：agent / transfer / bill / wealth / card / web / docs
```

**好例子：**
- `feat(transfer): 支持按手机号查找收款人`
- `fix(bill): 修复月度统计漏算跨月流水`
- `docs(security): 补充权限分级说明`

**坏例子：**
- `update`（看不出改了什么）
- `改了一下`（无法检索）
- `111`（等于没写）

## 冲突处理

异地协作一定会冲突。处理原则：

1. **小步提交，天天推** — 冲突范围就小
2. **改前先 pull** — 尤其改 `banking-core/` 下的文件
3. **契约文件冲突 = 停下来问** — 不要自己决定保留哪边
4. **不要在别人的分支上改代码** — 需要他改，就在群里 @ 他

## 保护分支设置（仓库管理员 B 第一天就配好）

在 GitHub → Settings → Branches 添加规则：

- Branch name pattern: `main`
- ✅ Require a pull request before merging
- ✅ Require approvals: 1
- ✅ Require status checks to pass
- ✅ Do not allow bypassing the above settings

这样任何人都无法误操作推坏 main。

## 大文件禁忌

以下内容**永不入库**：

```
node_modules/
.env
dist/
*.mp4
*.zip
*.log
```

已在 `.gitignore` 中配置。视频、录屏放网盘，不要污染仓库。
