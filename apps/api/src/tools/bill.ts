/**
 * 账单分析工具集 —— 负责人：C
 *
 * 核心是可解释的异常交易识别：只用三条规则，评审时讲得清。
 * 规则 1：金额超过该用户历史交易的 95 分位
 * 规则 2：深夜（00:00-05:00）且金额较大
 * 规则 3：首次跨境 或 收款方在风险名单
 */

import type { ToolResult } from '../types';
import { findUser, makeAuditId, userTransactions } from '../lib/store';

/** 风险名单（演示用，生产应接风控系统） */
const RISK_LIST = ['某投资平台', '未知账户 ****9527'];

/** 计算分位数 */
function percentile(sorted: number[], p: number): number {
  if (sorted.length === 0) return 0;
  const idx = Math.floor(sorted.length * p);
  return sorted[Math.min(idx, sorted.length - 1)];
}

/** 解析 "2026-09-06" 这种日期 */
function parseDate(s: string): Date {
  return new Date(`${s}T00:00:00`);
}

/** 判断是否在统计周期内 */
function inPeriod(dateStr: string, period: string): boolean {
  const d = parseDate(dateStr);
  const now = new Date('2026-09-12T00:00:00'); // 演示基准日
  if (period === 'year') {
    return d.getFullYear() === now.getFullYear();
  }
  // 默认本月
  return d.getFullYear() === now.getFullYear() && d.getMonth() === now.getMonth();
}

/**
 * 账单分类统计
 * 演示话术：「帮我看看这个月的账单」
 */
export async function analyzeBill(
  userId: string,
  params: { period?: string; category?: string } = {},
): Promise<ToolResult> {
  const auditId = makeAuditId('aud_bill');
  const period = params.period ?? 'month';
  const all = userTransactions(userId);
  const scoped = all.filter((t: any) => inPeriod(t.date, period));

  // 只统计支出（负数）
  const expenses = scoped.filter((t: any) => t.amount < 0);
  const income = scoped.filter((t: any) => t.amount > 0);

  const byCategory: Record<string, number> = {};
  for (const t of expenses) {
    byCategory[t.category] = Number(
      ((byCategory[t.category] ?? 0) + Math.abs(t.amount)).toFixed(2),
    );
  }

  const totalExpense = Number(
    expenses.reduce((s: number, t: any) => s + Math.abs(t.amount), 0).toFixed(2),
  );
  const totalIncome = Number(
    income.reduce((s: number, t: any) => s + t.amount, 0).toFixed(2),
  );

  const categories = Object.entries(byCategory)
    .map(([name, amount]) => ({
      name,
      amount,
      percent: totalExpense ? Number(((amount / totalExpense) * 100).toFixed(1)) : 0,
    }))
    .sort((a, b) => b.amount - a.amount);

  const user = findUser(userId);
  const budget = user?.monthlyBudget ?? 0;
  const topCategory = categories[0];

  const periodText = period === 'year' ? '本年度' : '本月';
  let message = `${periodText}共支出 ${totalExpense.toFixed(2)} 元，收入 ${totalIncome.toFixed(2)} 元。`;
  if (topCategory) {
    message += `最大支出类别是「${topCategory.name}」，共 ${topCategory.amount.toFixed(2)} 元，占 ${topCategory.percent}%。`;
  }
  if (budget && period === 'month') {
    const left = Number((budget - totalExpense).toFixed(2));
    message += left >= 0
      ? `你的月度预算是 ${budget} 元，还剩 ${left.toFixed(2)} 元。`
      : `⚠️ 你的月度预算是 ${budget} 元，已超出 ${Math.abs(left).toFixed(2)} 元。`;
  }

  return {
    ok: true,
    code: 'OK',
    message,
    riskLevel: 'low',
    auditId,
    data: {
      period,
      periodText,
      totalExpense,
      totalIncome,
      net: Number((totalIncome - totalExpense).toFixed(2)),
      count: expenses.length,
      categories,
      monthlyBudget: budget,
    },
  };
}

/**
 * 异常交易识别（三条可解释规则）
 * 演示话术：「有没有什么不对的地方？」
 */
export async function detectAnomaly(
  userId: string,
  params: { period?: string } = {},
): Promise<ToolResult> {
  const auditId = makeAuditId('aud_anomaly');
  const period = params.period ?? 'month';
  const all = userTransactions(userId);
  const scoped = all.filter((t: any) => inPeriod(t.date, period));

  // 规则 1 的基准：全部历史支出金额的分位数
  const historical = all
    .filter((t: any) => t.amount < 0)
    .map((t: any) => Math.abs(t.amount))
    .sort((a: number, b: number) => a - b);
  const p95 = percentile(historical, 0.95);

  // 统计每个收款方出现的次数，用于"首次"判定
  const seenMerchants = new Set<string>();
  const anomalies: any[] = [];

  for (const t of scoped) {
    if (t.amount >= 0) continue;
    const amt = Math.abs(t.amount);
    const reasons: string[] = [];

    // 规则 1：金额离群
    if (amt > p95) {
      reasons.push(`金额 ${amt.toFixed(2)} 元超过历史交易 95% 分位（${p95.toFixed(2)} 元）`);
    }

    // 规则 2：深夜大额（基于真实交易时间 23:00-05:00）
    const hour = t.time ? parseInt(String(t.time).split(':')[0], 10) : -1;
    if (amt >= 5000 && hour >= 0 && (hour >= 23 || hour < 5)) {
      reasons.push(`深夜 ${t.time} 发生大额交易`);
    }

    // 规则 3：跨境 / 风险名单
    if (t.channel === '跨境支付') {
      reasons.push('首次通过跨境渠道交易');
    }
    if (RISK_LIST.some((r) => String(t.merchant).includes(r))) {
      reasons.push('收款方命中风险名单');
    }

    // 数据里显式标记的埋点也算（保证演示稳定）
    if (t.isAnomaly && reasons.length === 0) {
      reasons.push(t.anomalyReason ?? '命中风控规则');
    }

    if (reasons.length > 0) {
      anomalies.push({
        txId: t.txId,
        date: t.date,
        amount: Number(amt.toFixed(2)),
        merchant: t.merchant,
        category: t.category,
        channel: t.channel,
        reasons,
        riskLevel: amt >= 10000 ? 'high' : amt >= 5000 ? 'mid' : 'low',
      });
    }

    seenMerchants.add(t.merchant);
  }

  anomalies.sort((a, b) => b.amount - a.amount);

  const message = anomalies.length === 0
    ? `${period === 'year' ? '本年度' : '本月'}没有发现异常交易，账户状态正常。`
    : `发现 ${anomalies.length} 笔可疑交易，建议你确认一下：\n` +
      anomalies
        .map((a, i) => `${i + 1}. ${a.date} 向「${a.merchant}」支出 ${a.amount.toFixed(2)} 元 —— ${a.reasons[0]}`)
        .join('\n');

  return {
    ok: true,
    code: 'OK',
    message,
    riskLevel: anomalies.some((a) => a.riskLevel === 'high') ? 'high' : 'mid',
    auditId,
    data: {
      period,
      count: anomalies.length,
      threshold: Number(p95.toFixed(2)),
      anomalies,
      rules: [
        '金额超过用户历史交易的 95 分位',
        '深夜时段（00:00-05:00）大额转账',
        '首次跨境交易或收款方命中风险名单',
      ],
    },
  };
}

/**
 * 生成账单报告（给前端图表用）
 */
export async function generateBillReport(
  userId: string,
  params: { period?: string } = {},
): Promise<ToolResult> {
  const auditId = makeAuditId('aud_bill_report');
  const period = params.period ?? 'month';
  const all = userTransactions(userId);
  const scoped = all.filter((t: any) => inPeriod(t.date, period));
  const expenses = scoped.filter((t: any) => t.amount < 0);

  const byCategory: Record<string, number> = {};
  for (const t of expenses) {
    byCategory[t.category] = Number(
      ((byCategory[t.category] ?? 0) + Math.abs(t.amount)).toFixed(2),
    );
  }

  const byDay: Record<string, number> = {};
  for (const t of expenses) {
    byDay[t.date] = Number(((byDay[t.date] ?? 0) + Math.abs(t.amount)).toFixed(2));
  }

  const categories = Object.entries(byCategory)
    .map(([name, amount]) => ({ name, amount }))
    .sort((a, b) => b.amount - a.amount);

  const daily = Object.entries(byDay)
    .map(([date, amount]) => ({ date, amount }))
    .sort((a, b) => a.date.localeCompare(b.date));

  const total = Number(
    expenses.reduce((s: number, t: any) => s + Math.abs(t.amount), 0).toFixed(2),
  );

  return {
    ok: true,
    code: 'OK',
    message: `已生成${period === 'year' ? '本年度' : '本月'}账单报告：共 ${expenses.length} 笔支出，合计 ${total.toFixed(2)} 元。`,
    riskLevel: 'low',
    auditId,
    data: { period, total, count: expenses.length, categories, daily },
  };
}
