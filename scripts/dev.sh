#!/usr/bin/env bash
# 一键启动前后端 —— 负责人：B
#
# 用法：bash scripts/dev.sh
# 效果：同时启动后端(3001) 和 前端(5173)，Ctrl+C 一起退出
#
# MOCK_MODE 默认开启 —— 没有 AI key 也能完整跑通所有场景。

set -e

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

echo ""
echo "  AI Banking — 启动开发环境"
echo "  ────────────────────────────────────"

# 1. 准备后端环境变量
if [ ! -f "apps/api/.env" ]; then
  cp apps/api/.env.example apps/api/.env
  echo "  [1/3] 已从模板生成 apps/api/.env（MOCK_MODE=true）"
else
  echo "  [1/3] apps/api/.env 已存在"
fi

# 2. 安装依赖
if [ ! -d "apps/api/node_modules" ]; then
  echo "  [2/3] 安装后端依赖..."
  (cd apps/api && npm install --no-audit --no-fund)
else
  echo "  [2/3] 后端依赖已就绪"
fi

if [ ! -d "apps/web/node_modules" ]; then
  echo "        安装前端依赖..."
  (cd apps/web && npm install --no-audit --no-fund)
else
  echo "        前端依赖已就绪"
fi

# 3. 启动
echo "  [3/3] 启动服务..."
echo "  ────────────────────────────────────"
echo "  后端  http://localhost:3001"
echo "  前端  http://localhost:5173   ← 浏览器打开这个"
echo "  ────────────────────────────────────"
echo "  按 Ctrl+C 停止全部服务"
echo ""

cleanup() {
  echo ""
  echo "  正在停止服务..."
  kill 0 2>/dev/null || true
  exit 0
}
trap cleanup INT TERM EXIT

(cd apps/api && MOCK_MODE=true npm run dev) &
sleep 3
(cd apps/web && npm run dev) &

wait
