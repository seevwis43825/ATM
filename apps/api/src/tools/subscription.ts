/**
 * 订阅 / 代扣管理工具集 —— 负责人：A
 *
 * 演示埋点：sub_004 会在 2 天后扣费（用户可能已遗忘），用于演示"续费提醒"。
 */

import type { ToolResult } from '../types';
import { findUser, makeAuditId, userSubscriptions } from '../lib/store';

/** 演示基准日（与 Mock 数据对齐） */
const TODAY = new Date('2026-09-12T00:00:00');

/** 本地日期格式化 —— 不要用 toISOString()，它会按 UTC 换算导致差一天 */
function fmtDate(d: Date): string {
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

/** 计算距下次扣费还有几天 */
function daysUntil(dateStr: string): number {
  const d = new Date(`${dateStr}T00:00:00`);
  return Math.round((d.getTime() - TODAY.getTime()) / 86400000);
}

/** 列出全部订阅，并标出即将扣费的 */
export async function listSubscriptions(
  userId: string,
  _params: Record<string, unknown> = {},
): Promise<ToolResult> {
  const auditId = makeAuditId('aud_sub_list');
  const subs = userSubscriptions(userId);

  if (subs.length === 0) {
    return { ok: true, code: 'OK', message: '你没有正在生效的订阅扣费。', riskLevel: 'low', auditId, data: { subscriptions: [] } };
  }

  const enriched = subs.map((s: any) => {
    const days = daysUntil(s.nextChargeAt);
    return {
      ...s,
      daysUntilCharge: days,
      isUpcoming: days >= 0 && days <= 3,
      monthlyCost: s.cycle === '年' ? Number((s.amount / 12).toFixed(2)) : s.amount,
    };
  });

  const totalMonthly = Number(
    enriched.reduce((sum: number, s: any) => sum + s.monthlyCost, 0).toFixed(2),
  );
  const upcoming = enriched.filter((s: any) => s.isUpcoming);

  let message = `你共有 ${enriched.length} 项自动扣费，折合每月约 ${totalMonthly.toFixed(2)} 元。`;
  if (upcoming.length > 0) {
    message += `\n\n⚠️ 有 ${upcoming.length} 项即将扣费：\n` +
      upcoming.map((s: any) => `· ${s.merchant}（${s.plan}）${s.daysUntilCharge} 天后扣 ${s.amount.toFixed(2)} 元`).join('\n');
  }

  return {
    ok: true,
    code: 'OK',
    message,
    riskLevel: upcoming.length > 0 ? 'mid' : 'low',
    auditId,
    data: { subscriptions: enriched, totalMonthly, upcomingCount: upcoming.length },
  };
}

/**
 * 取消订阅
 * 演示话术：「把那个云盘会员取消了吧」
 */
export async function cancelSubscription(
  userId: string,
  params: { subscriptionId?: string; merchant?: string },
): Promise<ToolResult> {
  const auditId = makeAuditId('aud_sub_cancel');
  const subs = userSubscriptions(userId);

  let target: any = null;
  if (params.subscriptionId) {
    target = subs.find((s: any) => s.subscriptionId === params.subscriptionId);
  } else if (params.merchant) {
    target = subs.find((s: any) => String(s.merchant).includes(params.merchant as string));
  } else {
    // 没指定就默认取最近要扣费的那个
    target = subs
      .slice()
      .sort((a: any, b: any) => daysUntil(a.nextChargeAt) - daysUntil(b.nextChargeAt))[0];
  }

  if (!target) {
    return {
      ok: false, code: 'SUB_NOT_FOUND',
      message: '没有找到对应的订阅。可以让我先列出你的全部自动扣费项目。',
      data: null, auditId,
    };
  }

  const days = daysUntil(target.nextChargeAt);

  return {
    ok: true,
    code: 'OK',
    message: `已准备取消「${target.merchant}」的${target.plan}订阅（${target.amount.toFixed(2)} 元/${target.cycle}）。${days >= 0 && days <= 3 ? `注意：它将在 ${days} 天后扣费，现在取消可以避免这笔支出。` : ''}请确认。`,
    needConfirm: true,
    riskLevel: 'low',
    auditId,
    data: {
      subscriptionId: target.subscriptionId,
      merchant: target.merchant,
      plan: target.plan,
      amount: target.amount,
      cycle: target.cycle,
      daysUntilCharge: days,
      cancelPath: target.cancelPath,
      savedAmount: target.amount,
    },
  };
}

/**
 * 已确认后的真实执行
 */
export async function executeCancelSubscription(
  userId: string,
  params: { subscriptionId: string },
): Promise<ToolResult> {
  const auditId = makeAuditId('aud_sub_cancel_exec');
  const subs = userSubscriptions(userId);
  const target = subs.find((s: any) => s.subscriptionId === params.subscriptionId);

  if (target) target.status = 'cancelled';

  return {
    ok: true,
    code: 'OK',
    message: `已取消「${target?.merchant ?? params.subscriptionId}」的自动续费。后续不会再产生扣费。`,
    riskLevel: 'low',
    auditId,
    data: {
      subscriptionId: params.subscriptionId,
      merchant: target?.merchant,
      status: 'cancelled',
      savedPerYear: target ? Number(((target.cycle === '年' ? target.amount : target.amount * 12)).toFixed(2)) : 0,
    },
  };
}

/**
 * 跨场景联动：生日关怀 + 预算锁定
 * 演示话术：「我最近有什么要花钱的地方吗？」
 *
 * 这是全队认为最有价值的场景 —— Agent 主动关联用户信息、订阅记录与账户余额。
 */
export async function birthdayWorkflow(
  userId: string,
  _params: Record<string, unknown> = {},
): Promise<ToolResult> {
  const auditId = makeAuditId('aud_birthday');
  const user = findUser(userId);

  if (!user) {
    return { ok: false, code: 'USER_NOT_FOUND', message: '找不到该用户。', data: null, auditId };
  }

  const bday = new Date(`${user.birthday}T00:00:00`);
  const thisYearBday = new Date(TODAY.getFullYear(), bday.getMonth(), bday.getDate());
  const daysToBirthday = Math.round((thisYearBday.getTime() - TODAY.getTime()) / 86400000);

  const subs = userSubscriptions(userId);
  const upcoming = subs
    .map((s: any) => ({ ...s, daysUntilCharge: daysUntil(s.nextChargeAt) }))
    .filter((s: any) => s.daysUntilCharge >= 0 && s.daysUntilCharge <= 45)
    .sort((a: any, b: any) => a.daysUntilCharge - b.daysUntilCharge);

  const acc = user.accounts?.find((a: any) => a.type === '活期储蓄');
  const balance = acc?.balance ?? 0;

  const findings: string[] = [];
  const actions: any[] = [];

  // 1) 生日提醒
  if (daysToBirthday >= 0 && daysToBirthday <= 30) {
    findings.push(
      daysToBirthday === 0
        ? `今天是你的生日 🎂`
        : `${daysToBirthday} 天后（${fmtDate(thisYearBday)}）是你的生日`,
    );

    if (balance >= 1000) {
      const budget = 1000;
      actions.push({
        type: 'lock_budget',
        title: '生日预算锁定',
        detail: `从活期账户预留 ${budget} 元作为生日预算，避免被日常消费花掉`,
        amount: budget,
      });
      findings.push(`你的活期余额 ${balance.toFixed(2)} 元，建议预留 ${budget} 元作为生日预算`);
    }

    actions.push({
      type: 'order_gift',
      title: '生日鲜花蛋糕订购',
      detail: '生日前 2 天自动下单，可提前指定配送地址',
      amount: 328,
    });
  }

  // 2) 订阅扣费提醒（跨场景关联）
  for (const s of upcoming) {
    findings.push(
      `「${s.merchant}」${s.plan}将在 ${s.daysUntilCharge} 天后自动扣费 ${s.amount.toFixed(2)} 元`,
    );
    if (s.warning) findings.push(`　└ ${s.warning}`);
  }

  if (findings.length === 0) {
    return {
      ok: true, code: 'OK',
      message: '近期没有特别需要留意的支出，账户状态正常。',
      riskLevel: 'low', auditId,
      data: { daysToBirthday, findings: [], actions: [] },
    };
  }

  return {
    ok: true,
    code: 'OK',
    message:
      `我帮你关联了几件事：\n\n` +
      findings.map((f, i) => `${i + 1}. ${f}`).join('\n') +
      (actions.length > 0
        ? `\n\n我可以帮你做这些：\n` + actions.map((a) => `· ${a.title}：${a.detail}`).join('\n')
        : ''),
    needConfirm: actions.length > 0,
    riskLevel: 'mid',
    auditId,
    data: {
      daysToBirthday,
      birthday: fmtDate(thisYearBday),
      balance,
      findings,
      actions,
      upcomingSubscriptions: upcoming,
    },
  };
}
