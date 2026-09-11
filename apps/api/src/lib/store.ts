/**
 * 统一数据访问层 —— Mock 数据读取 + 审计日志 + 待确认操作队列
 *
 * 数据来源：banking-core/mock-data/*.json（由 A 维护，其他人只读）
 * 生产环境替换这一层即可接入真实数据库，上层工具函数无需改动。
 */

import fs from 'node:fs';
import path from 'node:path';
import type { AuditLog, PendingAction, RiskLevel } from '../types';

/** Mock 数据目录：apps/api/src/lib → 项目根 → banking-core/mock-data */
const DATA_DIR = path.resolve(__dirname, '../../../../banking-core/mock-data');

function readJson<T>(file: string): T {
  const full = path.join(DATA_DIR, file);
  if (!fs.existsSync(full)) {
    throw new Error(`[store] Mock 数据缺失：${full}\n请确认 banking-core/mock-data/ 目录完整。`);
  }
  return JSON.parse(fs.readFileSync(full, 'utf-8')) as T;
}

/** 带简单缓存，避免每次请求都读盘 */
const cache = new Map<string, unknown>();
function load<T>(file: string): T {
  if (!cache.has(file)) cache.set(file, readJson<T>(file));
  return cache.get(file) as T;
}

export const db = {
  users: () => load<any>('users.json'),
  transactions: () => load<any>('transactions.json'),
  products: () => load<any>('products.json'),
  cards: () => load<any>('cards.json'),
  subscriptions: () => load<any>('subscriptions.json'),
  reload() {
    cache.clear();
  },
};

/** 按 userId 取用户，找不到返回 null */
export function findUser(userId: string) {
  return db.users().users.find((u: any) => u.userId === userId) ?? null;
}

/** 取某用户的全部交易 */
export function userTransactions(userId: string) {
  return db.transactions().transactions.filter((t: any) => t.userId === userId);
}

/** 取某用户的卡片 */
export function userCards(userId: string) {
  return db.cards().cards.filter((c: any) => c.userId === userId);
}

/** 取某用户的订阅 */
export function userSubscriptions(userId: string) {
  return db.subscriptions().subscriptions.filter((s: any) => s.userId === userId);
}

// ============ 审计日志（安全自评报告的数据来源）============

const auditLogs: AuditLog[] = [];

export function writeAudit(entry: Omit<AuditLog, 'at'>): AuditLog {
  const log: AuditLog = { ...entry, at: new Date().toISOString() };
  auditLogs.push(log);
  // 只保留最近 500 条，演示用；生产应持久化
  if (auditLogs.length > 500) auditLogs.shift();
  return log;
}

export function listAudit(userId?: string): AuditLog[] {
  return userId ? auditLogs.filter((l) => l.userId === userId) : [...auditLogs];
}

// ============ 待确认操作队列 ============

const pending = new Map<string, PendingAction>();

export function addPending(action: PendingAction): void {
  pending.set(action.auditId, action);
}

export function takePending(auditId: string): PendingAction | null {
  const item = pending.get(auditId) ?? null;
  if (item) pending.delete(auditId);
  return item;
}

export function peekPending(auditId: string): PendingAction | null {
  return pending.get(auditId) ?? null;
}

/** 生成审计 ID —— 格式 aud_<场景>_<时间戳>_<随机> */
export function makeAuditId(prefix = 'aud'): string {
  const d = new Date();
  const p = (n: number) => String(n).padStart(2, '0');
  const stamp = `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
  const rand = Math.random().toString(36).slice(2, 6);
  return `${prefix}_${stamp}_${rand}`;
}

/** 按金额推断风险等级 —— 全队统一口径，不要各自实现 */
export function riskByAmount(amount: number, high = 5000, mid = 1000): RiskLevel {
  if (amount >= high) return 'high';
  if (amount >= mid) return 'mid';
  return 'low';
}
