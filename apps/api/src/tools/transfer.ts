/**
 * 智能转账工具集 —— 负责人：B
 *
 * 契约见 banking-core/contracts/agent-tool-contract.md
 * 铁律：所有函数返回 ToolResult<T>，MOCK_MODE 下可独立运行。
 */

import type { ToolResult } from '../types';
import { findUser, makeAuditId, riskByAmount } from '../lib/store';

/** 从用户通讯录里查联系人（支持姓名或手机号） */
function findContact(userId: string, keyword: string) {
  const user = findUser(userId);
  if (!user?.contacts) return null;
  return (
    user.contacts.find((c: any) => c.name === keyword || c.phone === keyword) ?? null
  );
}

/** 取主账户（活期储蓄）余额 */
function mainBalance(userId: string): number {
  const user = findUser(userId);
  const acc = user?.accounts?.find((a: any) => a.type === '活期储蓄');
  return acc?.balance ?? 0;
}

/**
 * 按人名 / 手机号转账
 * 演示话术：「给张三转500块，备注房租」
 */
export async function transferByContact(
  userId: string,
  params: { target: string; amount: number; remark?: string },
): Promise<ToolResult> {
  const auditId = makeAuditId('aud_transfer');
  const { target, amount, remark } = params;

  if (!target || !String(target).trim()) {
    return {
      ok: false, code: 'MISSING_TARGET',
      message: '没有识别到收款人，请告诉我要转给谁。',
      data: null, auditId,
    };
  }
  if (!amount || Number.isNaN(amount) || amount <= 0) {
    return {
      ok: false, code: 'INVALID_AMOUNT',
      message: '转账金额需要是大于 0 的数字。',
      data: null, auditId,
    };
  }
  if (amount > 50000) {
    return {
      ok: false, code: 'EXCEED_LIMIT',
      message: '单笔转账超过 5 万元限额，请分笔操作或前往柜台办理。',
      data: null, riskLevel: 'high', auditId,
    };
  }

  const balance = mainBalance(userId);
  if (amount > balance) {
    return {
      ok: false, code: 'INSUFFICIENT_BALANCE',
      message: `账户余额不足。当前活期余额 ${balance.toFixed(2)} 元，本次需转 ${amount.toFixed(2)} 元。`,
      data: null, riskLevel: 'low', auditId,
    };
  }

  const contact = findContact(userId, target);
  const riskLevel = riskByAmount(amount);

  return {
    ok: true,
    code: 'OK',
    message: contact
      ? `已准备好转账：收款人 ${contact.name}（尾号 ${contact.phone.slice(-4)}），金额 ${amount.toFixed(2)} 元${remark ? `，备注「${remark}」` : ''}。请确认后执行。`
      : `未在通讯录找到「${target}」，将通过对方手机号转账 ${amount.toFixed(2)} 元。请确认。`,
    needConfirm: true,
    riskLevel,
    auditId,
    data: {
      target: contact?.name ?? target,
      phone: contact?.phone ?? target,
      amount,
      remark: remark ?? '',
      fee: 0,
      willArriveIn: '实时到账',
      balanceAfter: Number((balance - amount).toFixed(2)),
    },
  };
}

/**
 * 定时转账
 * 演示话术：「每月1号自动给房东转1200房租」
 */
export async function scheduleTransfer(
  userId: string,
  params: { target: string; amount: number; cron: string },
): Promise<ToolResult> {
  const auditId = makeAuditId('aud_schedule');
  const contact = findContact(userId, params.target);

  if (!params.amount || params.amount <= 0) {
    return {
      ok: false, code: 'INVALID_AMOUNT',
      message: '定时转账金额需要大于 0。', data: null, auditId,
    };
  }

  return {
    ok: true,
    code: 'OK',
    message: `已创建定时转账计划：每月 1 日自动向 ${contact?.name ?? params.target} 转账 ${params.amount.toFixed(2)} 元。可在「我的计划」中随时修改或终止。`,
    needConfirm: true,
    riskLevel: 'mid',
    auditId,
    data: {
      target: contact?.name ?? params.target,
      amount: params.amount,
      cron: params.cron,
      cronText: '每月 1 日 09:00',
      nextRunAt: '2026-10-01 09:00',
      status: 'active',
    },
  };
}

/**
 * 拆分 AA 收款
 * 演示话术：「这顿饭620，我们4个人AA」
 */
export async function splitBill(
  userId: string,
  params: { totalAmount: number; members: string[] },
): Promise<ToolResult> {
  const auditId = makeAuditId('aud_split');
  const { totalAmount, members } = params;

  if (!members || members.length < 2) {
    return {
      ok: false, code: 'INVALID_MEMBERS',
      message: 'AA 至少需要 2 个人。', data: null, auditId,
    };
  }
  if (!totalAmount || totalAmount <= 0) {
    return {
      ok: false, code: 'INVALID_AMOUNT',
      message: 'AA 总金额需要大于 0。', data: null, auditId,
    };
  }

  const perPerson = Number((totalAmount / members.length).toFixed(2));
  const shares = members.map((m) => ({ member: m, amount: perPerson, status: 'pending' }));

  return {
    ok: true,
    code: 'OK',
    message: `已生成 AA 收款：共 ${totalAmount.toFixed(2)} 元，${members.length} 人分摊，每人 ${perPerson.toFixed(2)} 元。确认后向各位发起收款。`,
    needConfirm: true,
    riskLevel: 'low',
    auditId,
    data: { totalAmount, memberCount: members.length, perPerson, shares },
  };
}

/** 用户确认后的真实执行（演示用：扣减内存余额并留痕） */
export async function executeTransfer(
  userId: string,
  params: { target: string; amount: number; remark?: string },
): Promise<ToolResult> {
  const auditId = makeAuditId('aud_transfer_exec');
  const user = findUser(userId);
  const acc = user?.accounts?.find((a: any) => a.type === '活期储蓄');

  if (acc) acc.balance = Number((acc.balance - params.amount).toFixed(2));

  return {
    ok: true,
    code: 'OK',
    message: `转账成功：已向 ${params.target} 转出 ${params.amount.toFixed(2)} 元，实时到账。`,
    riskLevel: riskByAmount(params.amount),
    auditId,
    data: {
      target: params.target,
      amount: params.amount,
      remark: params.remark ?? '',
      balanceAfter: acc?.balance ?? 0,
      status: 'success',
    },
  };
}
