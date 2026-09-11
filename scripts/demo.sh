#!/usr/bin/env bash
# 六大场景演示脚本 —— 录视频前先跑一遍，确认每句话都能出效果
#
# 用法：
#   1. 先启动后端：cd apps/api && npm run dev
#   2. 另开终端：bash scripts/demo.sh
#
# 输出会按场景分节打印，方便对照视频脚本分镜。

API="${API:-http://localhost:3001}"
USER="u_1001"

say() { printf "\n\033[1;36m%s\033[0m\n" "$1"; }
ask() {
  curl -s --max-time 20 -X POST "$API/api/chat" \
    -H "Content-Type: application/json" \
    -d "{\"sessionId\":\"$SESSION\",\"userId\":\"$USER\",\"message\":$2}" \
  | python -c "import sys,json
d=json.load(sys.stdin)
print('  你：$1')
print('  AI：'+d['reply'].replace(chr(10),chr(10)+'      '))
print('  [意图] '+d['intent']+'  [卡片] '+d['ui']['type'])" 2>/dev/null || echo "  (请求失败，请确认后端已启动)"
}

SESSION="demo_$(date +%s)"

echo ""
echo "======================================================"
echo "  AI Banking 六大场景演示"
echo "  会话 ID: $SESSION"
echo "======================================================"

say "【场景一】智能转账"
ask "给张三转500块，备注房租" '"给张三转500块，备注房租"'
ask "这顿饭620，我们4个人AA" '"这顿饭620，我们4个人AA"'

say "【场景二】账单分析"
ask "帮我看看这个月的账单" '"帮我看看这个月的账单"'
ask "账单里有没有异常交易" '"账单里有没有异常交易"'

say "【场景三】理财操作"
ask "我有5万闲钱，想做个稳健点的理财" '"我有5万闲钱，想做个稳健点的理财"'
ask "就买第一个" '"就买第一个"'

say "【场景四】卡片管理"
ask "我有哪些银行卡" '"我有哪些银行卡"'
ask "把我的卡额度提到3万" '"把我的卡额度提到3万"'

say "【场景五】订阅管理"
ask "我有哪些自动扣费" '"我有哪些自动扣费"'

say "【场景六】跨场景联动（加分项）"
ask "我最近有什么要花钱的地方吗" '"我最近有什么要花钱的地方吗"'

say "【多轮对话上下文】验证 Agent 记住了什么"
curl -s --max-time 10 "$API/api/session/$SESSION" \
| python -c "import sys,json
d=json.load(sys.stdin)
r=d['remembered']
print('  对话轮数：', d['turnCount'])
print('  记住的收款人：', r['lastTarget'])
print('  记住的金额：', r['lastAmount'])
print('  记住的产品：', ', '.join(p['name'] for p in r['lastProducts']) or '无')
print('  记住的订阅：', ', '.join(s['merchant'] for s in r['lastSubscriptions']) or '无')
print('  记住的卡片：', ', '.join(c['cardNo'] for c in r['lastCards']) or '无')" 2>/dev/null

say "【审计日志】安全自评报告的数据来源"
curl -s --max-time 10 "$API/api/audit?userId=$USER" \
| python -c "import sys,json
d=json.load(sys.stdin)
print('  共', d['count'], '条记录，最近 5 条：')
for l in d['logs'][-5:]:
    print('   ', l['action'].ljust(8), l['tool'].ljust(22), l['riskLevel'])" 2>/dev/null

echo ""
echo "======================================================"
echo "  演示结束"
echo "======================================================"
echo ""
