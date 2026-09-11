/**
 * 全局类型定义 —— 严格对齐 banking-core/contracts/agent-tool-contract.md
 *
 * 改这里 = 改契约。必须先群里说，再改，并更新契约文件版本号。
 */

export type RiskLevel = 'low' | 'mid' | 'high';

/** 所有业务工具的统一返回结构 */
export interface ToolResult<T = unknown> {
  ok: boolean;
  code: string;
  message: string;
  data: T | null;
  needConfirm?: boolean;
  riskLevel?: RiskLevel;
  auditId?: string;
}

/** 意图枚举 —— 六大场景 + 闲聊兜底 */
export type Intent =
  | 'transfer'
  | 'bill'
  | 'wealth'
  | 'card'
  | 'subscription'
  | 'compound'
  | 'unknown';

/** 前端 UI 指令类型 —— 新增必须通知前端负责人 E 并登记到契约 */
export type UiType =
  | 'text'
  | 'confirm_card'
  | 'bill_chart'
  | 'product_list'
  | 'card_list'
  | 'timeline';

export interface UiDirective {
  type: UiType;
  payload: Record<string, unknown>;
}

/** /api/chat 请求体 */
export interface ChatRequest {
  sessionId: string;
  userId: string;
  message: string;
}

/** /api/chat 响应体 */
export interface ChatResponse {
  sessionId: string;
  reply: string;
  intent: Intent;
  toolCalls: Array<ToolResult & { tool: string }>;
  ui: UiDirective;
}

/** /api/confirm 请求体 */
export interface ConfirmRequest {
  sessionId: string;
  auditId: string;
  action: 'approve' | 'reject';
}

/** 待确认操作（内存态，演示用；生产应落库） */
export interface PendingAction {
  auditId: string;
  sessionId: string;
  tool: string;
  params: Record<string, unknown>;
  createdAt: number;
  riskLevel: RiskLevel;
}

/** 审计日志条目 —— 安全自评报告的数据来源 */
export interface AuditLog {
  auditId: string;
  userId: string;
  tool: string;
  params: Record<string, unknown>;
  action: 'propose' | 'approve' | 'reject' | 'execute';
  riskLevel: RiskLevel;
  at: string;
}
