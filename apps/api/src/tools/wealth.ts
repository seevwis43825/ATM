/**
 * 理财操作工具集 —— 负责人：D
 *
 * 核心风控规则：只推荐风险等级 ≤ 用户风险承受能力的产品。这是硬约束，不可绕过。
 */

import type { ToolResult } from '../types';
import { db, findUser, makeAuditId, riskByAmount } from '../lib/store';

const RISK_ORDER = ['R1', 'R2', 'R3', 'R4', 'R5'];

/** 风险等级转数字，便于比较 */
function riskIndex(level: string): number {
  const i = RISK_ORDER.indexOf(level);
  return i === -1 ? 2 : i;
}

/** 中文风险偏好 → 风险等级 */
function preferenceToLevel(pref: string): string {
  const map: Record<string, string> = {
    保守: 'R1', 谨慎: 'R1', 保本: 'R1',
    稳健: 'R2', 低风险: 'R2',
    平衡: 'R3', 中等: 'R3', 中风险: 'R3',
    进取: 'R4', 成长: 'R4', 高风险: 'R4',
    激进: 'R5',
  };
  for (const [k, v] of Object.entries(map)) {
    if (pref.includes(k)) return v;
  }
  return 'R3';
}

/**
 * 风险评估 —— 读用户档案 + 问卷结果
 */
export async function assessRisk(
  userId: string,
  params: { answers?: number[] } = {},
): Promise<ToolResult> {
  const auditId = makeAuditId('aud_risk');
  const user = findUser(userId);
  if (!user) {
    return { ok: false, code: 'USER_NOT_FOUND', message: '找不到该用户。', data: null, auditId };
  }

  const level = user.riskLevel ?? 'R3';
  const levelName = user.riskLevelName ?? '平衡型';

  return {
    ok: true,
    code: 'OK',
    message: `你的风险承受能力评估为 ${level}（${levelName}）。适合的产品范围是 ${RISK_ORDER.slice(0, riskIndex(level) + 1).join(' / ')}。`,
    riskLevel: 'low',
    auditId,
    data: {
      userId,
      riskLevel: level,
      riskLevelName: levelName,
      allowedLevels: RISK_ORDER.slice(0, riskIndex(level) + 1),
      riskLevelMap: db.products().riskLevelMap,
    },
  };
}

/**
 * 理财产品推荐与对比
 * 演示话术：「我有5万闲钱，想做个稳健点的理财」
 */
export async function recommendProduct(
  userId: string,
  params: { riskPreference?: string; amount?: number; topN?: number } = {},
): Promise<ToolResult> {
  const auditId = makeAuditId('aud_recommend');
  const user = findUser(userId);
  if (!user) {
    return { ok: false, code: 'USER_NOT_FOUND', message: '找不到该用户。', data: null, auditId };
  }

  // 用户档案风险等级 与 本次表述的风险偏好，取更保守的那个
  const profileLevel = user.riskLevel ?? 'R3';
  const statedLevel = params.riskPreference ? preferenceToLevel(params.riskPreference) : profileLevel;
  const effectiveLevel =
    riskIndex(statedLevel) < riskIndex(profileLevel) ? statedLevel : profileLevel;

  const amount = params.amount ?? 0;
  const topN = params.topN ?? 3;

  const all: any[] = db.products().products;

  // 硬风控：只保留 风险等级 ≤ 有效等级 的产品
  let candidates = all.filter((p) => riskIndex(p.riskLevel) <= riskIndex(effectiveLevel));

  // 起投金额过滤（如果用户给了金额）
  if (amount > 0) {
    candidates = candidates.filter((p) => p.minAmount <= amount);
  }

  // 排序：预期收益降序
  candidates = candidates.sort((a, b) => b.expectedReturn - a.expectedReturn).slice(0, topN);

  const filtered = all.length - candidates.length;
  const amountText = amount > 0 ? `，可投金额 ${amount.toFixed(2)} 元` : '';

  return {
    ok: true,
    code: 'OK',
    message:
      candidates.length === 0
        ? `按你的风险等级 ${effectiveLevel}${amountText}，暂时没有匹配的产品。可以适当提高风险偏好，或调整投入金额。`
        : `根据你的风险等级 ${effectiveLevel}${amountText}，为你筛选出 ${candidates.length} 款产品。已自动排除 ${filtered} 款超出你风险承受能力的产品。`,
    riskLevel: 'low',
    auditId,
    data: {
      effectiveLevel,
      profileLevel,
      statedLevel,
      excludedCount: filtered,
      // 回传用户提到的可投金额，供多轮对话复用
      investAmount: amount > 0 ? amount : null,
      products: candidates.map((p) => ({
        productId: p.productId,
        name: p.name,
        type: p.type,
        riskLevel: p.riskLevel,
        expectedReturn: p.expectedReturn,
        minAmount: p.minAmount,
        termDays: p.termDays,
        liquidity: p.liquidity,
        manager: p.manager,
        estimatedIncome:
          amount > 0 ? Number(((amount * p.expectedReturn) / 100).toFixed(2)) : null,
      })),
    },
  };
}

/**
 * 一键申购
 * 演示话术：「就买第一个吧」
 */
export async function subscribeProduct(
  userId: string,
  params: { productId: string; amount: number },
): Promise<ToolResult> {
  const auditId = makeAuditId('aud_subscribe');
  const { productId, amount } = params;

  const product = db.products().products.find((p: any) => p.productId === productId);
  if (!product) {
    return { ok: false, code: 'PRODUCT_NOT_FOUND', message: '找不到该理财产品。', data: null, auditId };
  }

  const user = findUser(userId);
  if (!user) {
    return { ok: false, code: 'USER_NOT_FOUND', message: '找不到该用户。', data: null, auditId };
  }

  // 风控 1：风险等级不得超限
  if (riskIndex(product.riskLevel) > riskIndex(user.riskLevel ?? 'R3')) {
    return {
      ok: false, code: 'RISK_EXCEED',
      message: `无法申购：产品风险等级 ${product.riskLevel} 高于你的风险承受能力 ${user.riskLevel}。如确需购买，请先重新做风险评估。`,
      data: null, riskLevel: 'high', auditId,
    };
  }

  // 风控 1：金额必须合法
  if (!amount || Number.isNaN(amount) || amount <= 0) {
    return {
      ok: false, code: 'MISSING_AMOUNT',
      message: `申购「${product.name}」需要指定金额，该产品起投 ${product.minAmount} 元。你想投多少？`,
      data: null, riskLevel: 'low', auditId,
    };
  }

  // 风控 2：起投金额
  if (amount < product.minAmount) {
    return {
      ok: false, code: 'BELOW_MIN_AMOUNT',
      message: `该产品起投金额为 ${product.minAmount} 元，当前金额不足。`,
      data: null, riskLevel: 'low', auditId,
    };
  }

  // 风控 3：余额充足
  const acc = user.accounts?.find((a: any) => a.type === '活期储蓄');
  if (acc && amount > acc.balance) {
    return {
      ok: false, code: 'INSUFFICIENT_BALANCE',
      message: `活期余额不足。当前 ${acc.balance.toFixed(2)} 元，本次需 ${amount.toFixed(2)} 元。`,
      data: null, riskLevel: 'low', auditId,
    };
  }

  return {
    ok: true,
    code: 'OK',
    message: `已准备好申购：${product.name}（${product.riskLevel}），金额 ${amount.toFixed(2)} 元，预期年化 ${product.expectedReturn}%，期限 ${product.termDays === 0 ? '随时可赎回' : `${product.termDays} 天`}。请确认。`,
    needConfirm: true,
    riskLevel: riskByAmount(amount, 50000, 10000),
    auditId,
    data: {
      productId: product.productId,
      name: product.name,
      riskLevel: product.riskLevel,
      amount,
      expectedReturn: product.expectedReturn,
      estimatedIncome: Number(((amount * product.expectedReturn) / 100).toFixed(2)),
      termDays: product.termDays,
      liquidity: product.liquidity,
    },
  };
}

/** 已确认后的真实执行 */
export async function executeSubscribe(
  userId: string,
  params: { productId: string; amount: number },
): Promise<ToolResult> {
  const auditId = makeAuditId('aud_subscribe_exec');
  const product = db.products().products.find((p: any) => p.productId === params.productId);
  const user = findUser(userId);
  const acc = user?.accounts?.find((a: any) => a.type === '活期储蓄');

  if (acc) acc.balance = Number((acc.balance - params.amount).toFixed(2));

  return {
    ok: true,
    code: 'OK',
    message: `申购成功：${product?.name ?? params.productId}，金额 ${params.amount.toFixed(2)} 元，预计 T+1 确认份额。`,
    riskLevel: 'mid',
    auditId,
    data: {
      productId: params.productId,
      name: product?.name,
      amount: params.amount,
      balanceAfter: acc?.balance ?? 0,
      status: 'success',
    },
  };
}

/**
 * 赎回
 */
export async function redeemProduct(
  userId: string,
  params: { productId: string; amount: number },
): Promise<ToolResult> {
  const auditId = makeAuditId('aud_redeem');
  const product = db.products().products.find((p: any) => p.productId === params.productId);

  if (!product) {
    return { ok: false, code: 'PRODUCT_NOT_FOUND', message: '找不到该理财产品。', data: null, auditId };
  }
  if (product.termDays > 0 && product.liquidity === '封闭期') {
    return {
      ok: false, code: 'IN_CLOSED_PERIOD',
      message: `该产品处于封闭期（${product.termDays} 天），当前无法赎回。`,
      data: null, riskLevel: 'mid', auditId,
    };
  }

  return {
    ok: true,
    code: 'OK',
    message: `已准备赎回：${product.name}，金额 ${params.amount.toFixed(2)} 元，预计 ${product.liquidity} 到账。请确认。`,
    needConfirm: true,
    riskLevel: riskByAmount(params.amount, 50000, 10000),
    auditId,
    data: {
      productId: product.productId,
      name: product.name,
      amount: params.amount,
      arriveIn: product.liquidity,
    },
  };
}
