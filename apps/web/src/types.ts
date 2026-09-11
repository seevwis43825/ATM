/**
 * 前端类型定义 —— 必须与后端 apps/api/src/types.ts 保持一致
 * 改这里要同步改后端，并更新契约文件。
 */

export type RiskLevel = 'low' | 'mid' | 'high';

export type Intent =
  | 'transfer' | 'bill' | 'wealth' | 'card' | 'subscription' | 'compound' | 'unknown';

export type UiType =
  | 'text' | 'confirm_card' | 'bill_chart' | 'product_list' | 'card_list' | 'timeline';

export interface ToolResult {
  ok: boolean;
  code: string;
  message: string;
  data: unknown;
  needConfirm?: boolean;
  riskLevel?: RiskLevel;
  auditId?: string;
}

export interface UiDirective {
  type: UiType;
  payload: Record<string, any>;
}

export interface ChatResponse {
  sessionId: string;
  reply: string;
  intent: Intent;
  toolCalls: Array<ToolResult & { tool: string }>;
  ui: UiDirective;
}

/** 会话里的一条消息 */
export interface Message {
  id: string;
  role: 'user' | 'agent';
  text: string;
  ui?: UiDirective;
  pending?: boolean;
  done?: boolean;
}
