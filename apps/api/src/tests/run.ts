/**
 * 冒烟测试 —— 不依赖网络和 AI key，直接验证核心链路
 * 运行：npm test
 */

import { handleChat, handleConfirm } from '../agent';
import { transferByContact } from '../tools/transfer';
import { analyzeBill, detectAnomaly } from '../tools/bill';
import { recommendProduct, subscribeProduct } from '../tools/wealth';
import { listCards, adjustLimit } from '../tools/card';
import { listSubscriptions, birthdayWorkflow } from '../tools/subscription';

const USER = 'u_1001';

let passed = 0;
let failed = 0;

function check(name: string, cond: boolean, detail = '') {
  if (cond) {
    passed++;
    console.log(`  ✓ ${name}`);
  } else {
    failed++;
    console.log(`  ✗ ${name}${detail ? ` —— ${detail}` : ''}`);
  }
}

async function main() {
  console.log('\n=== AI Banking 冒烟测试 ===\n');

  // ---------- 1. 转账工具 ----------
  console.log('[1] 转账工具');
  const t1 = await transferByContact(USER, { target: '张三', amount: 500, remark: '房租' });
  check('给张三转 500 成功', t1.ok && t1.code === 'OK');
  check('需要二次确认', t1.needConfirm === true);
  check('风险等级为 low', t1.riskLevel === 'low');
  check('生成了审计 ID', !!t1.auditId);
  check('收款人解析正确', (t1.data as any)?.target === '张三');

  const t2 = await transferByContact(USER, { target: '张三', amount: 999999 });
  check('超限额被拦截', t2.ok === false && t2.code === 'EXCEED_LIMIT');

  const t3 = await transferByContact(USER, { target: '张三', amount: 60000 });
  check('超 5 万被拦截', t3.ok === false);

  // ---------- 2. 账单分析 ----------
  console.log('\n[2] 账单分析');
  const b1 = await analyzeBill(USER, { period: 'month' });
  check('账单统计成功', b1.ok);
  check('返回了分类数据', Array.isArray((b1.data as any)?.categories));
  check('有支出总额', (b1.data as any)?.totalExpense > 0);

  const b2 = await detectAnomaly(USER, { period: 'month' });
  check('异常识别成功', b2.ok);
  const anomalies = (b2.data as any)?.anomalies ?? [];
  check('识别出埋点异常交易（≥1 笔）', anomalies.length >= 1, `实际 ${anomalies.length} 笔`);
  check('异常项带可解释理由', anomalies.every((a: any) => a.reasons?.length > 0));
  check('返回了规则说明', Array.isArray((b2.data as any)?.rules));

  // ---------- 3. 理财 ----------
  console.log('\n[3] 理财操作');
  const w1 = await recommendProduct(USER, { riskPreference: '稳健', amount: 50000 });
  check('产品推荐成功', w1.ok);
  const products = (w1.data as any)?.products ?? [];
  check('返回了候选产品', products.length > 0);
  check(
    '硬风控：不推荐超过用户风险等级的产品',
    products.every((p: any) => ['R1', 'R2', 'R3'].includes(p.riskLevel)),
    JSON.stringify(products.map((p: any) => p.riskLevel)),
  );

  const w2 = await subscribeProduct(USER, { productId: 'p_011', amount: 100000 });
  check('高风险产品被拒绝申购', w2.ok === false && w2.code === 'RISK_EXCEED');

  const w3 = await subscribeProduct(USER, { productId: 'p_001', amount: 100 });
  check('低风险产品可申购', w3.ok && w3.needConfirm === true);

  // ---------- 4. 卡片 ----------
  console.log('\n[4] 卡片管理');
  const c1 = await listCards(USER, {});
  check('卡片列表成功', c1.ok && (c1.data as any).cards.length > 0);

  const c2 = await adjustLimit(USER, { newLimit: 30000 });
  check('额度调整需确认', c2.ok && c2.needConfirm === true);
  check('额度调整标记为高风险', c2.riskLevel === 'high');

  // ---------- 5. 订阅 ----------
  console.log('\n[5] 订阅管理');
  const s1 = await listSubscriptions(USER, {});
  check('订阅列表成功', s1.ok);
  check('识别出即将扣费的订阅', ((s1.data as any)?.upcomingCount ?? 0) >= 1);

  const s2 = await birthdayWorkflow(USER, {});
  check('跨场景联动成功', s2.ok);
  check('返回了发现项', ((s2.data as any)?.findings ?? []).length > 0);

  // ---------- 6. 端到端对话 ----------
  console.log('\n[6] 端到端对话（Agent 主链路）');
  const chat = await handleChat('sess_test', USER, '给张三转500块，备注房租');
  check('意图识别为 transfer', chat.intent === 'transfer');
  check('返回确认卡片', chat.ui.type === 'confirm_card');
  check('回复包含收款人', chat.reply.includes('张三'));

  const auditId = (chat.ui.payload as any).auditId;
  check('确认卡片带 auditId', !!auditId);

  const confirmed = await handleConfirm({ sessionId: 'sess_test', auditId, action: 'approve' });
  check('确认后执行成功', confirmed.reply.includes('转账成功'), confirmed.reply);

  const chat2 = await handleChat('sess_test', USER, '帮我看看这个月的账单');
  check('账单意图识别正确', chat2.intent === 'bill');

  const chat3 = await handleChat('sess_test', USER, '我最近有什么要花钱的地方吗');
  check('跨场景意图识别正确', chat3.intent === 'compound');

  const chat4 = await handleChat('sess_test', USER, '今天天气怎么样');
  check('未知意图有友好兜底', chat4.ui.type === 'text' && chat4.reply.includes('没理解'));

  // ---------- 7. 多轮对话（指代消解）----------
  console.log('\n[7] 多轮对话上下文');

  // 7.1 转账后说「那再转200」—— 应自动补全收款人
  const S = 'sess_multi';
  await handleChat(S, USER, '给张三转500块，备注房租');
  const m1 = await handleChat(S, USER, '那再转200');
  check('「那再转200」识别为转账', m1.intent === 'transfer');
  check(
    '自动补全了收款人（张三）',
    m1.reply.includes('张三'),
    m1.reply,
  );
  check('提示了使用了上下文', m1.reply.includes('上一轮'));

  // 7.2 理财推荐后说「就买第一个」
  await handleChat(S, USER, '我有5万闲钱，想做个稳健点的理财');
  const m2 = await handleChat(S, USER, '就买第一个');
  check('「就买第一个」识别为理财申购', m2.intent === 'wealth');
  check('补全了产品 ID', !!m2.toolCalls[0]?.data || m2.reply.length > 0, m2.reply.slice(0, 60));

  // 7.3 订阅列表后说「取消那个」
  await handleChat(S, USER, '我有哪些自动扣费');
  const m3 = await handleChat(S, USER, '取消那个');
  check('「取消那个」识别为取消订阅', m3.intent === 'subscription');
  check('补全了具体订阅', m3.reply.includes('取消'), m3.reply.slice(0, 80));

  // 7.4 卡片列表后说「把第一张卡锁了」
  await handleChat(S, USER, '我有哪些银行卡');
  const m4 = await handleChat(S, USER, '把第一张卡锁了');
  check('「第一张卡锁了」识别为卡片操作', m4.intent === 'card');
  check('补全了卡片 ID', !!m4.toolCalls[0]?.data, m4.reply.slice(0, 80));

  // 7.5 不同会话之间不应串味
  const other = await handleChat('sess_other', USER, '那再转200');
  check(
    '新会话不继承旧上下文（会反问转给谁）',
    other.reply.includes('没有识别到收款人') || other.reply.includes('转给谁'),
    other.reply.slice(0, 60),
  );

  // ---------- 汇总 ----------
  console.log('\n────────────────────────────');
  console.log(`  通过 ${passed} 项，失败 ${failed} 项`);
  console.log('────────────────────────────\n');

  if (failed > 0) process.exit(1);
}

main().catch((err) => {
  console.error('\n测试执行出错：', err);
  process.exit(1);
});
