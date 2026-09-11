/**
 * 会话上下文存储 —— 负责人：A
 *
 * 为什么需要它：
 * 真实对话是多轮的。用户说「给张三转500」之后，下一句往往只说「那再转200」，
 * 不会重复「给张三」。没有上下文，Agent 就会反问「转给谁」，体验立刻崩塌。
 *
 * 这里记录两类信息：
 * 1. 对话历史（turns）—— 供 LLM 做上下文理解，也用于回放调试
 * 2. 最近引用的实体（lastTarget / lastProducts / ...）—— 供指代消解
 *
 * 演示用内存存储；生产应换成 Redis 或数据库。
 */

import type { Intent } from '../types';

export interface Turn {
  role: 'user' | 'agent';
  text: string;
  at: string;
}

export interface SessionContext {
  sessionId: string;
  userId: string;
  turns: Turn[];

  /** 最近一次转账的收款人 —— 支持「再转200」 */
  lastTarget: string | null;
  /** 最近一次转账金额 —— 支持「再转一次」 */
  lastAmount: number | null;
  /** 最近推荐的产品列表 —— 支持「就买第一个」 */
  lastProducts: Array<{ productId: string; name: string }>;
  /** 最近列出的订阅 —— 支持「取消那个」 */
  lastSubscriptions: Array<{ subscriptionId: string; merchant: string }>;
  /** 最近列出的卡片 —— 支持「把第一张卡锁了」 */
  lastCards: Array<{ cardId: string; cardNo: string }>;

  lastIntent: Intent | null;
  updatedAt: number;
}

const MAX_TURNS = 40;
const SESSION_TTL = 2 * 60 * 60 * 1000; // 2 小时

const sessions = new Map<string, SessionContext>();

function blank(sessionId: string, userId: string): SessionContext {
  return {
    sessionId,
    userId,
    turns: [],
    lastTarget: null,
    lastAmount: null,
    lastProducts: [],
    lastSubscriptions: [],
    lastCards: [],
    lastIntent: null,
    updatedAt: Date.now(),
  };
}

/** 取会话（不存在则创建；过期则重置） */
export function getSession(sessionId: string, userId: string): SessionContext {
  const existing = sessions.get(sessionId);
  if (existing) {
    if (Date.now() - existing.updatedAt > SESSION_TTL) {
      const fresh = blank(sessionId, userId);
      sessions.set(sessionId, fresh);
      return fresh;
    }
    // 同一会话换了用户，视为新会话
    if (existing.userId !== userId) {
      const fresh = blank(sessionId, userId);
      sessions.set(sessionId, fresh);
      return fresh;
    }
    return existing;
  }
  const created = blank(sessionId, userId);
  sessions.set(sessionId, created);
  return created;
}

/** 追加一轮对话 */
export function appendTurn(sessionId: string, role: 'user' | 'agent', text: string): void {
  const s = sessions.get(sessionId);
  if (!s) return;
  s.turns.push({ role, text, at: new Date().toISOString() });
  if (s.turns.length > MAX_TURNS) s.turns.splice(0, s.turns.length - MAX_TURNS);
  s.updatedAt = Date.now();
}

/** 更新上下文中的实体引用 */
export function patchSession(sessionId: string, patch: Partial<SessionContext>): void {
  const s = sessions.get(sessionId);
  if (!s) return;
  Object.assign(s, patch, { updatedAt: Date.now() });
}

/** 取最近 N 轮对话（供 LLM 用） */
export function recentTurns(sessionId: string, n = 6): Turn[] {
  const s = sessions.get(sessionId);
  return s ? s.turns.slice(-n) : [];
}

/** 清空会话 */
export function resetSession(sessionId: string): void {
  sessions.delete(sessionId);
}

/** 调试用：查看会话状态 */
export function debugSession(sessionId: string): SessionContext | null {
  return sessions.get(sessionId) ?? null;
}
