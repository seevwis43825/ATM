/**
 * 意图识别与参数抽取 —— 负责人：A
 *
 * 设计原则：
 * 1. MOCK_MODE 下不依赖任何 AI key，用规则引擎保证任何人 clone 就能跑
 * 2. 配了 AI_API_KEY 时，可切换到 LLM 做意图识别（见 recognizeWithLLM）
 * 3. 规则引擎的判定理由可解释 —— 答辩时能讲清为什么这样识别
 */

import type { Intent } from '../types';

export interface ParsedIntent {
  intent: Intent;
  params: Record<string, unknown>;
  confidence: number;
}

/** 中文数字 → 阿拉伯数字（覆盖常见口语表达） */
const CN_DIGITS: Record<string, number> = {
  零: 0, 一: 1, 二: 2, 两: 2, 三: 3, 四: 4, 五: 5,
  六: 6, 七: 7, 八: 8, 九: 9, 十: 10,
};

/** 把 "两千" / "三百" / "十五" 这类中文数字转成数字 */
function cnToNumber(text: string): number | null {
  if (!text) return null;
  if (/^\d+$/.test(text)) return parseInt(text, 10);

  let result = 0;
  let section = 0;
  let current = 0;

  for (const ch of text) {
    if (ch in CN_DIGITS) {
      current = CN_DIGITS[ch];
    } else if (ch === '百') {
      section += (current || 1) * 100;
      current = 0;
    } else if (ch === '千') {
      section += (current || 1) * 1000;
      current = 0;
    } else if (ch === '万') {
      result += (section + current || 1) * 10000;
      section = 0;
      current = 0;
    }
  }
  const total = result + section + current;
  return total > 0 ? total : null;
}

/**
 * 从文本里抽金额
 * 支持："500" / "500块" / "1万" / "1万5" / "1.5万" / "两千" / "3k"
 */
export function parseAmount(text: string): number | null {
  // 1) 阿拉伯数字 + 可选单位
  const m = text.match(/(\d+(?:\.\d+)?)\s*(万|w|W|k|K|千)?/g);
  if (m) {
    // 取最长的那个匹配（通常是金额）
    let best: number | null = null;
    for (const raw of m) {
      const mm = raw.match(/^(\d+(?:\.\d+)?)\s*(万|w|W|k|K|千)?$/);
      if (!mm) continue;
      let val = parseFloat(mm[1]);
      const unit = mm[2];
      if (unit === '万' || unit === 'w' || unit === 'W') val *= 10000;
      else if (unit === 'k' || unit === 'K' || unit === '千') val *= 1000;

      // 处理 "1万5" 这种省略写法
      const afterUnit = text.split(raw)[1] ?? '';
      const tail = afterUnit.match(/^(\d)/);
      if (tail && (unit === '万' || unit === 'w' || unit === 'W')) {
        val += parseInt(tail[1], 10) * 1000;
      }

      if (val > 0 && (best === null || val > best)) best = val;
    }
    if (best !== null) return best;
  }

  // 2) 中文数字
  const cn = text.match(/([零一二两三四五六七八九十百千万]+)/);
  if (cn) {
    const v = cnToNumber(cn[1]);
    if (v !== null && v > 0) return v;
  }

  return null;
}

/** 抽取收款人姓名 */
function parseTarget(text: string): string | null {
  const patterns = [
    /给\s*([^\s，,。的]{1,10}?)\s*(?:转|打|汇|发)/,
    /(?:转给|转账给|打给|汇给)\s*([^\s，,。]{1,10})/,
    /向\s*([^\s，,。的]{1,10}?)\s*(?:转|打|汇)/,
    /帮\s*([^\s，,。的]{1,10}?)\s*(?:转|打|汇)/,
  ];
  for (const p of patterns) {
    const m = text.match(p);
    if (m && m[1]) {
      const name = m[1].trim();
      // 排除"我""自己"这类
      if (name && !['我', '自己', '他', '她'].includes(name)) return name;
    }
  }
  return null;
}

/** 抽取备注 */
function parseRemark(text: string): string | null {
  const m = text.match(/备注\s*[「"']?([^」"'，,。]+)[」"']?/) ||
            text.match(/[「"']([^」"']+)[」"']\s*(?:备注)?/);
  return m?.[1]?.trim() ?? null;
}

/** 抽取 AA 人数 */
function parseMemberCount(text: string): number | null {
  const m = text.match(/(\d+)\s*(?:个)?人/) || text.match(/([一二两三四五六七八九十])\s*个人/);
  if (!m) return null;
  if (/^\d+$/.test(m[1])) return parseInt(m[1], 10);
  return CN_DIGITS[m[1]] ?? null;
}

/** 主入口：规则引擎意图识别 */
export function parseIntent(message: string): ParsedIntent {
  const text = message.trim();
  const has = (...keys: string[]) => keys.some((k) => text.includes(k));

  // ---- 跨场景联动（优先判定，因为它的表述最独特）----
  if (
    has('要花钱', '要交的', '要扣的', '最近有什么', '有什么支出', '生日', '要提醒我') ||
    (has('最近', '下个月') && has('花钱', '支出', '扣费'))
  ) {
    return { intent: 'compound', params: {}, confidence: 0.85 };
  }

  // ---- 订阅 / 代扣 ----
  if (has('订阅', '会员', '续费', '自动扣费', '代扣', '自动续费')) {
    const amount = parseAmount(text);
    // 「取消XX」判定为取消
    if (has('取消', '关掉', '退订', '不要')) {
      const merchant =
        text.match(/取消\s*[「"']?([^」"'的，,。]+?)[」"']?\s*(?:会员|订阅|的)?/)?.[1]?.trim() ?? null;
      return { intent: 'subscription', params: { action: 'cancel', merchant, amount }, confidence: 0.9 };
    }
    return { intent: 'subscription', params: { action: 'list' }, confidence: 0.9 };
  }

  // ---- 卡片管理 ----
  if (has('挂失', '丢了', '不见了')) {
    return { intent: 'card', params: { action: 'loss' }, confidence: 0.9 };
  }
  if (has('额度', '限额')) {
    const amount = parseAmount(text);
    return { intent: 'card', params: { action: 'limit', newLimit: amount }, confidence: 0.9 };
  }
  if (has('办卡', '申请卡', '开卡', '办一张')) {
    const type = ['白金卡', '金卡', '普卡'].find((t) => text.includes(t)) ?? '金卡';
    return { intent: 'card', params: { action: 'apply', cardType: type }, confidence: 0.85 };
  }
  if (has('锁卡', '锁定', '冻结')) {
    return { intent: 'card', params: { action: 'lock', lock: true }, confidence: 0.9 };
  }
  if (has('解锁', '解冻', '恢复正常')) {
    return { intent: 'card', params: { action: 'unlock', lock: false }, confidence: 0.9 };
  }
  if (has('我的卡', '有哪些卡', '银行卡')) {
    return { intent: 'card', params: { action: 'list' }, confidence: 0.85 };
  }

  // ---- 理财 ----
  if (has('赎回', '取出')) {
    return { intent: 'wealth', params: { action: 'redeem', amount: parseAmount(text) }, confidence: 0.9 };
  }
  if (has('理财', '基金', '产品', '收益', '申购', '投资', '稳健', '闲钱', '推荐')) {
    const riskPref =
      ['保守', '谨慎', '保本', '稳健', '平衡', '中等', '进取', '激进'].find((k) => text.includes(k)) ?? null;
    const amount = parseAmount(text);
    if (has('买', '申购', '投')) {
      return { intent: 'wealth', params: { action: 'subscribe', riskPreference: riskPref, amount }, confidence: 0.85 };
    }
    return { intent: 'wealth', params: { action: 'recommend', riskPreference: riskPref, amount }, confidence: 0.9 };
  }
  if (has('风险测评', '风险等级', '风险承受')) {
    return { intent: 'wealth', params: { action: 'assess' }, confidence: 0.9 };
  }

  // ---- 账单分析 ----
  if (has('异常', '可疑', '不对', '有没有问题', '奇怪')) {
    return { intent: 'bill', params: { action: 'anomaly' }, confidence: 0.9 };
  }
  if (has('账单', '花了多少', '消费', '开销', '支出', '花钱')) {
    const period = has('年', '年度') ? 'year' : 'month';
    return { intent: 'bill', params: { action: 'analyze', period }, confidence: 0.9 };
  }

  // ---- 转账类 ----
  if (has('AA', 'aa', '分摊', '平摊')) {
    const totalAmount = parseAmount(text);
    const count = parseMemberCount(text);
    const members = count ? Array.from({ length: count }, (_, i) => `成员${i + 1}`) : [];
    return { intent: 'transfer', params: { action: 'split', totalAmount, members }, confidence: 0.85 };
  }
  if (has('每月', '定时', '自动转', '固定转')) {
    const amount = parseAmount(text);
    return {
      intent: 'transfer',
      params: { action: 'schedule', target: parseTarget(text), amount, cron: '0 9 1 * *' },
      confidence: 0.85,
    };
  }
  if (has('转', '打钱', '汇款', '发红包')) {
    const amount = parseAmount(text);
    const target = parseTarget(text);
    const remark = parseRemark(text);
    if (target && amount) {
      return { intent: 'transfer', params: { action: 'send', target, amount, remark }, confidence: 0.9 };
    }
    if (amount && !target) {
      return { intent: 'transfer', params: { action: 'send', target: null, amount, remark }, confidence: 0.6 };
    }
    return { intent: 'transfer', params: { action: 'send', target, amount: null, remark }, confidence: 0.5 };
  }

  // ---- 兜底 ----
  return { intent: 'unknown', params: {}, confidence: 0.2 };
}

/**
 * LLM 意图识别（可选）
 *
 * 配置了 AI_API_KEY 时启用。当前保留接口，规则引擎作为默认实现，
 * 保证没有 key 的组员也能完整跑通。
 */
export async function recognizeWithLLM(message: string): Promise<ParsedIntent | null> {
  const apiKey = process.env.AI_API_KEY;
  if (!apiKey || process.env.MOCK_MODE === 'true') return null;

  // TODO(负责人 A)：接入 OpenAI 兼容接口
  // const res = await fetch(`${process.env.AI_BASE_URL}/chat/completions`, {...});
  // 返回结构化意图，失败则回落到 parseIntent
  return null;
}

/** 统一入口：优先 LLM，失败回落规则引擎 */
export async function recognize(message: string): Promise<ParsedIntent> {
  const llm = await recognizeWithLLM(message).catch(() => null);
  return llm ?? parseIntent(message);
}
