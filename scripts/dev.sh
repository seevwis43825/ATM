#!/usr/bin/env bash
# 一键启动前后端 —— 所有人用同一个脚本，避免端口和路径搞错
# 用法：bash scripts/dev.sh

set -e

echo "==================================="
echo "  AI Banking - 开发环境启动"
echo "==================================="

if [ ! -f "apps/api/.env" ]; then
  echo "[提示] apps/api/.env 不存在，从模板复制..."
  cp apps/api/.env.example apps/api/.env
  echo "[提示] 已复制。当前为 MOCK_MODE=true，无需 AI key 即可运行。"
fi

if [ ! -d "apps/api/node_modules" ]; then
  echo "[1/2] 安装后端依赖..."
  (cd apps/api && npm install)
fi

if [ ! -d "apps/web/node_modules" ]; then
  echo "[2/2] 安装前端依赖..."
  (cd apps/web && npm install)
fi

echo ""
echo "启动后端 (http://localhost:3001) 和前端 (http://localhost:5173)"
echo "按 Ctrl+C 停止全部服务"
echo ""

(cd apps/api && npm run dev) &
API_PID=$!

(cd apps/web && npm run dev) &
WEB_PID=$!

trap "kill $API_PID $WEB_PID 2>/dev/null; exit 0" INT TERM

wait
