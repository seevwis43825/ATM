/**
 * Agent 大脑主循环 —— 负责人：A
 *
 * 职责：接收自然语言 → 识别意图 → 调度工具 → 生成回复与 UI 指令
 *
 * 核心安全设计：Agent 没有执行权，只有发起权。
 * 任何 needConfirm 的工具结果都不会被执行，而是转成确认卡片交给用户。
 */

import type {
  ChatResponse, ConfirmRequest, Intent, RiskLevel, ToolResult, UiDirective,
} from '../types';
import { recognize } from './intent';
import { addPending, makeAuditId, takePending, writeAudit } from '../lib/store';

import {
  transferByContact, scheduleTransfer, splitBill, executeTransfer,
} from '../tools/transfer';
import {
  analyzeBill, detectAnomaly, generateBillReport,
} from '../tools/bill';
import {
  assessRisk, recommendProduct, subscribeProduct, redeemProduct, executeSubscribe,
} from '../tools/wealth';
import {
  listCards, applyCard, adjustLimit, toggleCardLock, reportCardLoss,
} from '../tools/card';
import {
  listSubscriptions, cancelSubscription, executeCancelSubscription, birthdayWorkflow,
} from '../tools/subscription';

/** 工具名 → 执行函数（用户确认后调用） */
const EXECUTORS: Record<string, (userId: string, params: any) => Promise<ToolResult>> = {
  transferByContact: executeTransfer,
  scheduleTransfer: async (_u, p) => ({
    ok: true, code: 'OK',
    message: `定时转账计划已生效：每月 1 日向 ${p.target} 转账 ${Number(p.amount).toFixed(2)} 元。`,
    riskLevel: 'mid', auditId: makeAuditId('aud_sched_exec'), data: { ...p, status: 'active' },
  }),
  splitBill: async (_u, p) => ({
    ok: true, code: 'OK',
    message: `已向 ${p.members?.length ?? 0} 位成员发起 AA 收款，每人 ${Number(p.perPerson).toFixed(2)} 元。`,
    riskLevel: 'low', auditId: makeAuditId('aud_split_exec'), data: { ...p, status: 'sent' },
  }),
  subscribeProduct: executeSubscribe,
  redeemProduct: async (_u, p) => ({
    ok: true, code: 'OK',
    message: `赎回申请已提交：${p.productId}，金额 ${Number(p.amount).toFixed(2)} 元。`,
    riskLevel: 'mid', auditId: makeAuditId('aud_redeem_exec'), data: { ...p, status: 'submitted' },
  }),
  applyCard: async (_u, p) => ({
    ok: true, code: 'OK',
    message: `${p.cardType}申请已提交，预计 ${p.estimatedDays ?? 3} 个工作日完成审核。`,
    riskLevel: 'mid', auditId: makeAuditId('aud_card_apply_exec'), data: { ...p, status: 'reviewing' },
  }),
  adjustLimit: async (_u, p) => ({
    ok: true, code: 'OK',
    message: `额度调整成功：单笔限额已从 ${p.oldLimit} 元调整为 ${p.newLimit} 元。`,
    riskLevel: 'high', auditId: makeAuditId('aud_card_limit_exec'), data: { ...p, status: 'success' },
  }),
  toggleCardLock: async (_u, p) => ({
    ok: true, code: 'OK',
    message: p.lock ? `已锁定卡片 ${p.cardNo}，该卡暂停一切交易。` : `已解锁卡片 ${p.cardNo}，恢复正常使用。`,
    riskLevel: p.lock ? 'mid' : 'low', auditId: makeAuditId('aud_card_lock_exec'), data: { ...p, status: 'success' },
  }),
  reportCardLoss: async (_u, p) => ({
    ok: true, code: 'OK',
    message: `卡片 ${p.cardNo} 已挂失冻结。新卡将在 ${p.reissueDays ?? 7} 个工作日内寄达，工本费 ${p.fee ?? 20} 元。`,
    riskLevel: 'high', auditId: makeAuditId('aud_card_loss_exec'), data: { ...p, status: 'reported' },
  }),
  cancelSubscription: executeCancelSubscription,
  birthdayWorkflow: async (_u, p) => ({
    ok: true, code: 'OK',
    message: `已按你的确认执行：${(p.actions ?? []).map((a: any) => a.title).join('、')}。`,
    riskLevel: 'mid', auditId: makeAuditId('aud_birthday_exec'), data: { ...p, status: 'done' },
  }),
};

/** 各场景默认 UI 指令 */
function uiFor(tool: string, result: ToolResult): UiDirective {
  if (result.needConfirm) {
    return {
      type: 'confirm_card',
      payload: {
        auditId: result.auditId,
        title: '请确认这笔操作',
        riskLevel: result.riskLevel ?? 'mid',
        fields: flattenFields(result.data),
        warning:
          result.riskLevel === 'high'
            ? '这是一笔高风险操作，请再次核对信息后再确认。'
            : undefined,
      },
    };
  }

  switch (tool) {
    case 'generateBillReport':
      return { type: 'bill_chart', payload: (result.data ?? {}) as Record<string, unknown> };
    case 'recommendProduct':
      return { type: 'product_list', payload: (result.data ?? {}) as Record<string, unknown> };
    case 'listCards':
      return { type: 'card_list', payload: (result.data ?? {}) as Record<string, unknown> };
    case 'listSubscriptions':
    case 'birthdayWorkflow':
      return { type: 'timeline', payload: (result.data ?? {}) as Record<string, unknown> };
    default:
      return { type: 'text', payload: {} };
  }
}

/** 把 data 拍平成「标签-值」列表，供确认卡片展示 */
function flattenFields(data: unknown): Array<[string, string]> {
  if (!data || typeof data !== 'object') return [];
  const d = data as Record<string, unknown>;
  const LABELS: Record<string, string> = {
    target: '收款人',
    phone: '手机号',
    amount: '金额',
    remark: '备注',
    fee: '手续费',
    willArriveIn: '到账时间',
    balanceAfter: '操作后余额',
    name: '产品名称',
    riskLevel: '风险等级',
    expectedReturn: '预期年化',
    termDays: '期限',
    liquidity: '流动性',
    cardNo: '卡号',
    oldLimit: '原限额',
    newLimit: '新限额',
    merchant: '商户',
    plan: '套餐',
    cycle: '周期',
    daysUntilCharge: '距扣费',
    cardType: '卡种',
    annualFee: '年费',
  };

  const out: Array<[string, string]> = [];
  for (const [k, v] of Object.entries(d)) {
    if (v === null || v === undefined || typeof v === 'object') continue;
    const label = LABELS[k];
    if (!label) continue;
    let val = String(v);
    if (k === 'amount' || k === 'fee' || k === 'oldLimit' || k === 'newLimit' || k === 'balanceAfter') {
      val = `${Number(v).toFixed(2)} 元`;
    } else if (k === 'termDays') {
      val = Number(v) === 0 ? '随时可赎回' : `${v} 天`;
    } else if (k === 'expectedReturn') {
      val = `${v}%`;
    } else if (k === 'daysUntilCharge') {
      val = `${v} 天后`;
    } else if (k === 'annualFee') {
      val = `${v} 元/年`;
    }
    out.push([label, val]);
  }
  return out;
}

/** 调度：意图 + 参数 → 工具结果 + UI */
async function dispatch(
  userId: string,
  intent: Intent,
  params: Record<string, unknown>,
): Promise<{ tool: string; result: ToolResult; ui: UiDirective }> {
  const action = (params.action as string) ?? '';
  const p = params as any;

  let tool = 'unknown';
  let result: ToolResult;

  switch (intent) {
    // ================= 转账 =================
    case 'transfer': {
      if (action === 'split') {
        tool = 'splitBill';
        if (!p.totalAmount || !p.members?.length) {
          result = {
            ok: false, code: 'NEED_MORE_INFO',
            message: 'AA 收款需要知道总金额和人数，比如「这顿饭 620，我们 4 个人 AA」。',
            data: null,
          };
        } else {
          const per = Number((p.totalAmount / p.members.length).toFixed(2));
          result = await splitBill(userId, { totalAmount: p.totalAmount, members: p.members });
          if (result.ok) (result.data as any).perPerson = per;
        }
      } else if (action === 'schedule') {
        tool = 'scheduleTransfer';
        if (!p.target || !p.amount) {
          result = {
            ok: false, code: 'NEED_MORE_INFO',
            message: '定时转账需要知道转给谁、转多少，比如「每月 1 号给房东转 1200」。',
            data: null,
          };
        } else {
          result = await scheduleTransfer(userId, {
            target: p.target, amount: p.amount, cron: p.cron ?? '0 9 1 * *',
          });
        }
      } else {
        tool = 'transferByContact';
        if (!p.target) {
          result = {
            ok: false, code: 'NEED_MORE_INFO',
            message: '没有识别到收款人，请告诉我要转给谁，比如「给张三转 500」。',
            data: null,
          };
        } else if (!p.amount) {
          result = {
            ok: false, code: 'NEED_MORE_INFO',
            message: `要转给${p.target}多少钱呢？`,
            data: null,
          };
        } else {
          result = await transferByContact(userId, {
            target: p.target, amount: p.amount, remark: p.remark ?? undefined,
          });
        }
      }
      break;
    }

    // ================= 账单 =================
    case 'bill': {
      if (action === 'anomaly') {
        tool = 'detectAnomaly';
        result = await detectAnomaly(userId, { period: p.period });
      } else if (action === 'report') {
        tool = 'generateBillReport';
        result = await generateBillReport(userId, { period: p.period });
      } else {
        tool = 'analyzeBill';
        result = await analyzeBill(userId, { period: p.period });
        // 分析后附上报告数据，方便前端画图
        if (result.ok) {
          const report = await generateBillReport(userId, { period: p.period });
          if (report.ok) (result.data as any).chart = report.data;
        }
      }
      break;
    }

    // ================= 理财 =================
    case 'wealth': {
      if (action === 'assess') {
        tool = 'assessRisk';
        result = await assessRisk(userId, {});
      } else if (action === 'subscribe') {
        if (p.productId) {
          tool = 'subscribeProduct';
          result = await subscribeProduct(userId, { productId: p.productId, amount: p.amount });
        } else {
          tool = 'recommendProduct';
          result = await recommendProduct(userId, {
            riskPreference: p.riskPreference, amount: p.amount, topN: 3,
          });
        }
      } else if (action === 'redeem') {
        tool = 'redeemProduct';
        if (!p.productId) {
          result = {
            ok: false, code: 'NEED_MORE_INFO',
            message: '请告诉我你要赎回哪只产品。可以先说「看看我的理财」让我列出持仓。',
            data: null,
          };
        } else {
          result = await redeemProduct(userId, { productId: p.productId, amount: p.amount ?? 0 });
        }
      } else {
        tool = 'recommendProduct';
        result = await recommendProduct(userId, {
          riskPreference: p.riskPreference, amount: p.amount, topN: 3,
        });
      }
      break;
    }

    // ================= 卡片 =================
    case 'card': {
      if (action === 'apply') {
        tool = 'applyCard';
        result = await applyCard(userId, { cardType: p.cardType ?? '金卡' });
      } else if (action === 'limit') {
        tool = 'adjustLimit';
        if (!p.newLimit) {
          result = {
            ok: false, code: 'NEED_MORE_INFO',
            message: '你想把额度调整到多少？比如「额度提到 3 万」。',
            data: null,
          };
        } else {
          result = await adjustLimit(userId, { cardId: p.cardId, newLimit: p.newLimit });
        }
      } else if (action === 'lock' || action === 'unlock') {
        tool = 'toggleCardLock';
        result = await toggleCardLock(userId, { cardId: p.cardId, lock: action === 'lock' });
      } else if (action === 'loss') {
        tool = 'reportCardLoss';
        result = await reportCardLoss(userId, { cardId: p.cardId });
      } else {
        tool = 'listCards';
        result = await listCards(userId, {});
      }
      break;
    }

    // ================= 订阅 =================
    case 'subscription': {
      if (action === 'cancel') {
        tool = 'cancelSubscription';
        result = await cancelSubscription(userId, {
          subscriptionId: p.subscriptionId, merchant: p.merchant ?? undefined,
        });
      } else {
        tool = 'listSubscriptions';
        result = await listSubscriptions(userId, {});
      }
      break;
    }

    // ================= 跨场景联动 =================
    case 'compound': {
      tool = 'birthdayWorkflow';
      result = await birthdayWorkflow(userId, {});
      break;
    }

    // ================= 兜底 =================
    default: {
      tool = 'fallback';
      result = {
        ok: false,
        code: 'UNKNOWN_INTENT',
        message:
          '我暂时没理解这句话。你可以试试这些说法：\n' +
          '· 转账：「给张三转 500 块，备注房租」\n' +
          '· 账单：「帮我看看这个月的账单」\n' +
          '· 理财：「我有 5 万闲钱，想做个稳健点的理财」\n' +
          '· 卡片：「把我的卡额度提到 3 万」\n' +
          '· 订阅：「我有哪些自动扣费」\n' +
          '· 主动服务：「我最近有什么要花钱的地方吗」',
        data: null,
      };
    }
  }

  const ui = uiFor(tool, result);

  // 敏感操作：登记待确认队列，绝不直接执行
  if (result.needConfirm && result.auditId) {
    addPending({
      auditId: result.auditId,
      sessionId: '',
      tool,
      params: { ...(result.data as any), userId },
      createdAt: Date.now(),
      riskLevel: (result.riskLevel ?? 'mid') as RiskLevel,
    });
    writeAudit({
      auditId: result.auditId, userId, tool,
      params: params as Record<string, unknown>,
      action: 'propose', riskLevel: (result.riskLevel ?? 'mid') as RiskLevel,
    });
  }

  return { tool, result, ui };
}

/** 对外主入口：处理一条用户消息 */
export async function handleChat(
  sessionId: string,
  userId: string,
  message: string,
): Promise<ChatResponse> {
  const { intent, params } = await recognize(message);
  const { tool, result, ui } = await dispatch(userId, intent, params);

  return {
    sessionId,
    reply: result.message,
    intent,
    toolCalls: [{ tool, ...result }],
    ui,
  };
}

/** 处理用户确认 / 拒绝 */
export async function handleConfirm(req: ConfirmRequest): Promise<ChatResponse> {
  const { auditId, action, sessionId } = req;

  const pending = takePending(auditId);
  if (!pending) {
    return {
      sessionId,
      reply: '这笔操作已经处理过了，或者已超时失效。请重新发起。',
      intent: 'unknown',
      toolCalls: [],
      ui: { type: 'text', payload: {} },
    };
  }

  const { userId, ...params } = pending.params as any;

  // ---- 用户拒绝 ----
  if (action === 'reject') {
    writeAudit({
      auditId, userId, tool: pending.tool, params,
      action: 'reject', riskLevel: pending.riskLevel,
    });
    return {
      sessionId,
      reply: '好的，已取消这笔操作，没有产生任何资金变动。',
      intent: 'unknown',
      toolCalls: [],
      ui: { type: 'text', payload: {} },
    };
  }

  // ---- 用户确认 → 执行 ----
  const executor = EXECUTORS[pending.tool];
  if (!executor) {
    return {
      sessionId,
      reply: `暂不支持执行 ${pending.tool}。`,
      intent: 'unknown',
      toolCalls: [],
      ui: { type: 'text', payload: {} },
    };
  }

  const result = await executor(userId, params);

  writeAudit({
    auditId, userId, tool: pending.tool, params,
    action: 'execute', riskLevel: pending.riskLevel,
  });

  return {
    sessionId,
    reply: result.message,
    intent: 'unknown',
    toolCalls: [{ tool: pending.tool, ...result }],
    ui: { type: 'text', payload: { success: true } },
  };
}
