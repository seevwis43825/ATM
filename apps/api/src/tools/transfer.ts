/**
 * 智能转账工具集 —— 负责人：B
 *
 * 契约见 banking-core/contracts/agent-tool-contract.md
 * 铁律：所有函数必须返回 ToolResult<T> 结构，MOCK_MODE 下可独立运行。
 */

export type RiskLevel = 'low' | 'mid' | 'high';

export interface ToolResult<T = unknown> {
  ok: boolean;
  code: string;
  message: string;
  data: T | null;
  needConfirm?: boolean;
  riskLevel?: RiskLevel;
  auditId?: string;
}

/** 生成审计 ID —— 安全自评报告需要，每次操作都要留痕 */
export function makeAuditId(prefix = 'aud'): string {
  const d = new Date();
  const pad = (n: number) => String(n).padStart(2, '0');
  const stamp = `${d.getFullYear()}${pad(d.getMonth() + 1)}${pad(d.getDate())}${pad(d.getHours())}${pad(d.getMinutes())}${pad(d.getSeconds())}`;
  const rand = Math.random().toString(36).slice(2, 6);
  return `${prefix}_${stamp}_${rand}`;
}

/** 从 Mock 数据里查联系人 —— 按姓名或手机号 */
function findContact(keyword: string) {
  const contacts = [
    { name: '张三', phone: '13900139001' },
    { name: '王五', phone: '13900139002' },
    { name: '赵小美', phone: '13900139003' },
  ];
  return contacts.find((c) => c.name === keyword || c.phone === keyword) || null;
}

/**
 * 按人名 / 手机号转账
 * 演示话术：「给张三转500块，备注房租」
 */
export async function transferByContact(params: {
  target: string;
  amount: number;
  remark?: string;
}): Promise<ToolResult> {
  const auditId = makeAuditId('aud_transfer');
  const { target, amount, remark } = params;

  if (!target || !target.trim()) {
    return { ok: false, code: 'MISSING_TARGET', message: '没有识别到收款人，请告诉我要转给谁。', data: null, auditId };
  }
  if (!amount || amount <= 0) {
    return { ok: false, code: 'INVALID_AMOUNT', message: '转账金额需要是大于 0 的数字。', data: null, auditId };
  }
  if (amount > 50000) {
    return {
      ok: false, code: 'EXCEED_LIMIT', message: '单笔转账超过 5 万元限额，请分笔操作或前往柜台。',
      data: null, riskLevel: 'high', auditId,
    };
  }

  const contact = findContact(target);

  // 大额转账标记为中风险，走二次确认
  const riskLevel: RiskLevel = amount >= 5000 ? 'high' : amount >= 1000 ? 'mid' : 'low';

  return {
    ok: true,
    code: 'OK',
    message: contact
      ? `已准备好转账：收款人 ${contact.name}（${contact.phone.slice(-4)}），金额 ${amount.toFixed(2)} 元${remark ? `，备注「${remark}」` : ''}。请确认后执行。`
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
    },
  };
}

/**
 * 定时转账
 * 演示话术：「每月1号自动给房东转1200房租」
 */
export async function scheduleTransfer(params: {
  target: string;
  amount: number;
  cron: string;
}): Promise<ToolResult> {
  const auditId = makeAuditId('aud_schedule');
  const contact = findContact(params.target);

  return {
    ok: true,
    code: 'OK',
    message: `已创建定时转账计划：${params.cron} 自动向 ${contact?.name ?? params.target} 转账 ${params.amount.toFixed(2)} 元。可在「我的计划」中随时修改或终止。`,
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
export async function splitBill(params: {
  totalAmount: number;
  members: string[];
}): Promise<ToolResult> {
  const auditId = makeAuditId('aud_split');
  const { totalAmount, members } = params;

  if (!members || members.length < 2) {
    return { ok: false, code: 'INVALID_MEMBERS', message: 'AA 至少需要 2 个人。', data: null, auditId };
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
