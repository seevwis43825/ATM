/**
 * 后端接口封装
 *
 * 前端只需要知道两个接口：chat 和 confirm。
 * 开发时通过 vite proxy 转发到 localhost:3001，生产时走同域。
 */

import type { ChatResponse } from './types';

const BASE = import.meta.env.VITE_API_BASE_URL ?? '';

/** 演示用的固定会话与用户 */
const SESSION_ID = `sess_${Math.random().toString(36).slice(2, 10)}`;
const USER_ID = 'u_1001';

export async function sendChat(message: string): Promise<ChatResponse> {
  const res = await fetch(`${BASE}/api/chat`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ sessionId: SESSION_ID, userId: USER_ID, message }),
  });

  if (!res.ok) {
    const err = await res.json().catch(() => ({}));
    throw new Error(err.message ?? `请求失败（${res.status}）`);
  }
  return res.json();
}

export async function sendConfirm(
  auditId: string,
  action: 'approve' | 'reject',
): Promise<ChatResponse> {
  const res = await fetch(`${BASE}/api/confirm`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ sessionId: SESSION_ID, auditId, action }),
  });

  if (!res.ok) {
    const err = await res.json().catch(() => ({}));
    throw new Error(err.message ?? `请求失败（${res.status}）`);
  }
  return res.json();
}

export { SESSION_ID, USER_ID };
