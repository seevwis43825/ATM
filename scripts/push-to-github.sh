#!/usr/bin/env bash
# 备用手动推送脚本
# 用途：如果连接器无法推送，用这个脚本把项目推到你自己的 GitHub 仓库
#
# 使用方法：
#   1. 先装好 GitHub CLI 并登录：gh auth login
#   2. 在项目根目录执行：bash scripts/push-to-github.sh
#
# 脚本会：创建私有仓库 -> 绑定 remote -> 推送 main 分支

set -e

REPO_NAME="ai-banking"
BRANCH="main"

echo "=== AI Banking 推送到 GitHub ==="
echo

# 检查 gh 是否安装
if ! command -v gh >/dev/null 2>&1; then
  echo "[错误] 没有找到 GitHub CLI (gh)"
  echo "请先安装：https://cli.github.com/"
  exit 1
fi

# 检查是否已登录
if ! gh auth status >/dev/null 2>&1; then
  echo "[提示] 尚未登录 GitHub，即将启动登录流程..."
  gh auth login
fi

USERNAME=$(gh api user --jq .login)
echo "当前账号：$USERNAME"

# 确保在 git 仓库根目录
if [ ! -d .git ]; then
  echo "[错误] 当前目录不是 git 仓库，请在 AI-Banking 目录下执行"
  exit 1
fi

# 重命名分支为 main（如果是 master）
CURRENT_BRANCH=$(git rev-parse --abbrev-ref HEAD)
if [ "$CURRENT_BRANCH" != "$BRANCH" ]; then
  echo "将分支 $CURRENT_BRANCH 重命名为 $BRANCH"
  git branch -M "$BRANCH"
fi

# 创建仓库（已存在则跳过）
if gh repo view "$USERNAME/$REPO_NAME" >/dev/null 2>&1; then
  echo "仓库 $USERNAME/$REPO_NAME 已存在，跳过创建"
else
  echo "创建私有仓库 $USERNAME/$REPO_NAME ..."
  gh repo create "$REPO_NAME" --private --source=. --description "AI Banking Agent - 人工智能赛道参赛作品"
fi

# 绑定 remote
if git remote get-url origin >/dev/null 2>&1; then
  echo "remote origin 已存在，更新地址"
  git remote set-url origin "https://github.com/$USERNAME/$REPO_NAME.git"
else
  git remote add origin "https://github.com/$USERNAME/$REPO_NAME.git"
fi

# 推送
echo "推送到 origin/$BRANCH ..."
git push -u origin "$BRANCH"

echo
echo "=== 完成 ==="
echo "仓库地址：https://github.com/$USERNAME/$REPO_NAME"
