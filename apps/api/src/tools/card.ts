/**
 * 卡片管理工具集 —— 负责人：D
 *
 * 四个核心操作：申请 / 调额 / 交易限制 / 挂失
 * 全部为敏感操作，需二次确认。
 */

import type { ToolResult } from '../types';
import { db, findUser, makeAuditId, userCards } from '../lib/store';

/** 列出用户所有卡片 */
export async function listCards(
  userId: string,
  _params: Record<string, unknown> = {},
): Promise<ToolResult> {
  const auditId = makeAuditId('aud_card_list');
  const cards = userCards(userId);

  if (cards.length === 0) {
    return { ok: true, code: 'OK', message: '你名下还没有银行卡，可以申请一张。', riskLevel: 'low', auditId, data: { cards: [] } };
  }

  return {
    ok: true,
    code: 'OK',
    message: `你名下有 ${cards.length} 张卡：\n` +
      cards.map((c: any) => `· ${c.cardType} ${c.cardNo}（${c.statusText}）单笔限额 ${c.singleLimit} 元`).join('\n'),
    riskLevel: 'low',
    auditId,
    data: { cards, cardTypes: db.cards().cardTypes },
  };
}

/**
 * 申请新卡
 * 演示话术：「我想办一张金卡」
 */
export async function applyCard(
  userId: string,
  params: { cardType: string },
): Promise<ToolResult> {
  const auditId = makeAuditId('aud_card_apply');
  const types: any[] = db.cards().cardTypes;
  const matched = types.find((t) => t.type === params.cardType);

  if (!matched) {
    return {
      ok: false, code: 'UNKNOWN_CARD_TYPE',
      message: `暂不支持「${params.cardType}」。可申请的卡种有：${types.map((t) => t.type).join('、')}。`,
      data: null, auditId,
    };
  }

  const user = findUser(userId);

  return {
    ok: true,
    code: 'OK',
    message: `已准备为你申请 ${matched.type}：年费 ${matched.annualFee} 元，额度范围 ${matched.limitRange}。${matched.desc}。确认后将进入审核流程，预计 3 个工作日。`,
    needConfirm: true,
    riskLevel: 'mid',
    auditId,
    data: {
      cardType: matched.type,
      annualFee: matched.annualFee,
      limitRange: matched.limitRange,
      desc: matched.desc,
      applicant: user?.name,
      estimatedDays: 3,
    },
  };
}

/**
 * 调整额度
 * 演示话术：「把我的卡额度提到3万」
 */
export async function adjustLimit(
  userId: string,
  params: { cardId?: string; newLimit: number },
): Promise<ToolResult> {
  const auditId = makeAuditId('aud_card_limit');
  const cards = userCards(userId);

  if (cards.length === 0) {
    return { ok: false, code: 'NO_CARD', message: '你名下没有可调整的银行卡。', data: null, auditId };
  }

  const card = params.cardId
    ? cards.find((c: any) => c.cardId === params.cardId)
    : cards[0];

  if (!card) {
    return { ok: false, code: 'CARD_NOT_FOUND', message: '找不到该卡片。', data: null, auditId };
  }
  if (!params.newLimit || params.newLimit <= 0) {
    return { ok: false, code: 'INVALID_LIMIT', message: '额度需要是大于 0 的数字。', data: null, auditId };
  }
  if (params.newLimit > 500000) {
    return {
      ok: false, code: 'EXCEED_MAX',
      message: '单卡额度上限为 50 万元，请调整目标额度或前往柜台办理。',
      data: null, riskLevel: 'high', auditId,
    };
  }

  const delta = params.newLimit - card.singleLimit;

  return {
    ok: true,
    code: 'OK',
    message: `已准备将 ${card.cardType} ${card.cardNo} 的单笔限额从 ${card.singleLimit} 元调整为 ${params.newLimit} 元（${delta >= 0 ? '提升' : '降低'} ${Math.abs(delta)} 元）。请确认。`,
    needConfirm: true,
    riskLevel: 'high',
    auditId,
    data: {
      cardId: card.cardId,
      cardNo: card.cardNo,
      oldLimit: card.singleLimit,
      newLimit: params.newLimit,
      delta,
    },
  };
}

/**
 * 交易限制 / 解锁
 * 演示话术：「把我的卡锁了」/「解锁」
 */
export async function toggleCardLock(
  userId: string,
  params: { cardId?: string; lock: boolean },
): Promise<ToolResult> {
  const auditId = makeAuditId('aud_card_lock');
  const cards = userCards(userId);
  const card = params.cardId ? cards.find((c: any) => c.cardId === params.cardId) : cards[0];

  if (!card) {
    return { ok: false, code: 'CARD_NOT_FOUND', message: '找不到该卡片。', data: null, auditId };
  }

  const action = params.lock ? '锁定' : '解锁';

  return {
    ok: true,
    code: 'OK',
    message: `已准备${action} ${card.cardType} ${card.cardNo}。${params.lock ? '锁定后该卡将无法进行任何交易。' : '解锁后恢复正常使用。'}请确认。`,
    needConfirm: true,
    riskLevel: params.lock ? 'mid' : 'low',
    auditId,
    data: { cardId: card.cardId, cardNo: card.cardNo, lock: params.lock, action },
  };
}

/**
 * 挂失
 * 演示话术：「我的卡丢了」
 */
export async function reportCardLoss(
  userId: string,
  params: { cardId?: string },
): Promise<ToolResult> {
  const auditId = makeAuditId('aud_card_loss');
  const cards = userCards(userId);
  const card = params.cardId ? cards.find((c: any) => c.cardId === params.cardId) : cards[0];

  if (!card) {
    return { ok: false, code: 'CARD_NOT_FOUND', message: '找不到该卡片。', data: null, auditId };
  }

  return {
    ok: true,
    code: 'OK',
    message: `⚠️ 挂失是不可逆操作。确认后将立即冻结 ${card.cardType} ${card.cardNo}，并为你补发新卡（工本费 20 元，7 个工作日寄达）。请确认。`,
    needConfirm: true,
    riskLevel: 'high',
    auditId,
    data: {
      cardId: card.cardId,
      cardNo: card.cardNo,
      fee: 20,
      reissueDays: 7,
      irreversible: true,
    },
  };
}
