/**
 * UI 卡片渲染组件 —— 负责人：E
 *
 * 契约里的六种 ui.type 各对应一个组件：
 *   confirm_card  敏感操作确认卡片
 *   bill_chart    账单图表
 *   product_list  理财产品对比
 *   card_list     银行卡列表
 *   timeline      订阅 / 待办时间轴
 *   text          纯文字（无卡片）
 *
 * 未知类型由 UiRenderer 兜底渲染成 JSON，永不白屏。
 */

import type { UiDirective } from '../types';

const RISK_LABEL: Record<string, string> = {
  low: '低风险',
  mid: '中风险',
  high: '高风险',
};

const RISK_STYLE: Record<string, string> = {
  low: 'bg-emerald-50 text-emerald-700 border-emerald-200',
  mid: 'bg-amber-50 text-amber-700 border-amber-200',
  high: 'bg-red-50 text-red-700 border-red-200',
};

const CATEGORY_COLORS = [
  '#3b82f6', '#10b981', '#f59e0b', '#ef4444',
  '#8b5cf6', '#06b6d4', '#ec4899', '#84cc16',
];

// ============================================================
// 1. 确认卡片
// ============================================================

export function ConfirmCard({
  payload,
  onConfirm,
  done,
}: {
  payload: Record<string, any>;
  onConfirm: (auditId: string, action: 'approve' | 'reject') => void;
  done?: boolean;
}) {
  const risk = payload.riskLevel ?? 'mid';
  const fields: Array<[string, string]> = payload.fields ?? [];

  return (
    <div className="mt-2 rounded-xl border border-slate-200 bg-white overflow-hidden shadow-sm">
      <div className="px-3.5 py-2.5 border-b border-slate-100 flex items-center justify-between">
        <span className="text-[13px] font-medium text-slate-800">
          {payload.title ?? '请确认'}
        </span>
        <span className={`text-[11px] px-2 py-0.5 rounded-full border ${RISK_STYLE[risk]}`}>
          {RISK_LABEL[risk]}
        </span>
      </div>

      <div className="px-3.5 py-2.5 space-y-1.5">
        {fields.map(([label, value]) => (
          <div key={label} className="flex justify-between text-[13px]">
            <span className="text-slate-500">{label}</span>
            <span className="text-slate-900 font-medium">{value}</span>
          </div>
        ))}
      </div>

      {payload.warning && (
        <div className="mx-3.5 mb-2.5 px-2.5 py-2 rounded-lg bg-red-50 text-[12px] text-red-700 leading-relaxed">
          {payload.warning}
        </div>
      )}

      {done ? (
        <div className="px-3.5 py-2.5 border-t border-slate-100 text-[12px] text-slate-400 text-center">
          该操作已处理
        </div>
      ) : (
        <div className="px-3.5 py-2.5 border-t border-slate-100 flex gap-2">
          <button
            onClick={() => onConfirm(payload.auditId, 'reject')}
            className="flex-1 py-2 rounded-lg border border-slate-200 text-[13px] text-slate-600 active:bg-slate-50"
          >
            取消
          </button>
          <button
            onClick={() => onConfirm(payload.auditId, 'approve')}
            className="flex-1 py-2 rounded-lg bg-blue-600 text-white text-[13px] font-medium active:bg-blue-700"
          >
            确认
          </button>
        </div>
      )}
    </div>
  );
}

// ============================================================
// 2. 账单图表
// ============================================================

export function BillChart({ payload }: { payload: Record<string, any> }) {
  const categories: Array<{ name: string; amount: number }> = payload.categories ?? [];
  const total = payload.total ?? 0;
  const max = Math.max(...categories.map((c) => c.amount), 1);

  // 用 conic-gradient 画环形图，避免引入图表库
  let acc = 0;
  const segments = categories.slice(0, 8).map((c, i) => {
    const start = (acc / (total || 1)) * 360;
    acc += c.amount;
    const end = (acc / (total || 1)) * 360;
    return `${CATEGORY_COLORS[i % CATEGORY_COLORS.length]} ${start}deg ${end}deg`;
  });

  return (
    <div className="mt-2 rounded-xl border border-slate-200 bg-white p-3.5 shadow-sm">
      <div className="flex items-center gap-4">
        <div
          className="w-[86px] h-[86px] rounded-full shrink-0 relative"
          style={{ background: `conic-gradient(${segments.join(',')})` }}
        >
          <div className="absolute inset-[18px] rounded-full bg-white flex flex-col items-center justify-center">
            <span className="text-[10px] text-slate-400">合计</span>
            <span className="text-[12px] font-medium text-slate-800">
              {total >= 10000 ? `${(total / 10000).toFixed(1)}万` : total.toFixed(0)}
            </span>
          </div>
        </div>

        <div className="flex-1 space-y-1.5 min-w-0">
          {categories.slice(0, 5).map((c, i) => (
            <div key={c.name} className="flex items-center gap-2 text-[12px]">
              <span
                className="w-2 h-2 rounded-sm shrink-0"
                style={{ background: CATEGORY_COLORS[i % CATEGORY_COLORS.length] }}
              />
              <span className="text-slate-600 truncate flex-1">{c.name}</span>
              <span className="text-slate-900 font-medium">
                {c.amount >= 10000 ? `${(c.amount / 10000).toFixed(1)}万` : c.amount.toFixed(0)}
              </span>
            </div>
          ))}
        </div>
      </div>

      {categories.length > 0 && (
        <div className="mt-3 pt-3 border-t border-slate-100 space-y-2">
          {categories.slice(0, 4).map((c, i) => (
            <div key={c.name}>
              <div className="flex justify-between text-[11px] text-slate-500 mb-1">
                <span>{c.name}</span>
                <span>{c.amount.toFixed(2)} 元</span>
              </div>
              <div className="h-1.5 rounded-full bg-slate-100 overflow-hidden">
                <div
                  className="h-full rounded-full transition-all"
                  style={{
                    width: `${(c.amount / max) * 100}%`,
                    background: CATEGORY_COLORS[i % CATEGORY_COLORS.length],
                  }}
                />
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

// ============================================================
// 3. 理财产品对比
// ============================================================

export function ProductList({ payload }: { payload: Record<string, any> }) {
  const products: any[] = payload.products ?? [];

  return (
    <div className="mt-2 space-y-2">
      {products.map((p) => (
        <div key={p.productId} className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
          <div className="flex items-start justify-between gap-2">
            <div className="min-w-0">
              <p className="text-[13px] font-medium text-slate-900 truncate">{p.name}</p>
              <p className="text-[11px] text-slate-400 mt-0.5">
                {p.type} · {p.manager}
              </p>
            </div>
            <span className={`text-[11px] px-1.5 py-0.5 rounded border shrink-0 ${
              p.riskLevel === 'R1' || p.riskLevel === 'R2'
                ? RISK_STYLE.low
                : p.riskLevel === 'R3'
                ? RISK_STYLE.mid
                : RISK_STYLE.high
            }`}>
              {p.riskLevel}
            </span>
          </div>

          <div className="mt-2.5 grid grid-cols-3 gap-2">
            <div>
              <p className="text-[10px] text-slate-400">预期年化</p>
              <p className="text-[15px] font-medium text-red-500">{p.expectedReturn}%</p>
            </div>
            <div>
              <p className="text-[10px] text-slate-400">期限</p>
              <p className="text-[13px] text-slate-700">
                {p.termDays === 0 ? '随时赎回' : `${p.termDays}天`}
              </p>
            </div>
            <div>
              <p className="text-[10px] text-slate-400">起投</p>
              <p className="text-[13px] text-slate-700">{p.minAmount}元</p>
            </div>
          </div>

          {p.estimatedIncome != null && (
            <p className="mt-2 text-[11px] text-slate-500">
              按你的金额测算，预计年收益约 <span className="text-red-500 font-medium">{p.estimatedIncome} 元</span>
            </p>
          )}
        </div>
      ))}
    </div>
  );
}

// ============================================================
// 4. 银行卡列表
// ============================================================

export function CardList({ payload }: { payload: Record<string, any> }) {
  const cards: any[] = payload.cards ?? [];

  return (
    <div className="mt-2 space-y-2">
      {cards.map((c) => (
        <div
          key={c.cardId}
          className={`rounded-xl p-3.5 text-white shadow-sm ${
            c.status === 'locked'
              ? 'bg-slate-500'
              : 'bg-gradient-to-br from-slate-800 to-slate-600'
          }`}
        >
          <div className="flex items-center justify-between">
            <span className="text-[12px] opacity-80">{c.cardType}</span>
            <span className="text-[11px] opacity-80">{c.brand}</span>
          </div>
          <p className="mt-3 text-[15px] tracking-wider font-medium">{c.cardNo}</p>
          <div className="mt-3 flex items-end justify-between">
            <div>
              <p className="text-[10px] opacity-70">单笔限额</p>
              <p className="text-[13px]">{c.singleLimit.toLocaleString()} 元</p>
            </div>
            <span
              className={`text-[11px] px-2 py-0.5 rounded-full ${
                c.status === 'locked' ? 'bg-red-500/90' : 'bg-emerald-500/90'
              }`}
            >
              {c.statusText}
            </span>
          </div>
        </div>
      ))}
    </div>
  );
}

// ============================================================
// 5. 时间轴（订阅 / 待办）
// ============================================================

export function Timeline({ payload }: { payload: Record<string, any> }) {
  const subs: any[] = payload.subscriptions ?? [];
  const actions: any[] = payload.actions ?? [];
  const findings: string[] = payload.findings ?? [];

  const items = subs.length > 0
    ? subs.map((s) => ({
        title: s.merchant,
        sub: `${s.plan} · ${s.amount.toFixed(2)} 元/${s.cycle}`,
        badge: s.isUpcoming ? `${s.daysUntilCharge} 天后` : `${s.daysUntilCharge} 天后`,
        urgent: s.isUpcoming,
      }))
    : [];

  return (
    <div className="mt-2 rounded-xl border border-slate-200 bg-white p-3.5 shadow-sm">
      {findings.length > 0 && (
        <div className="mb-3 space-y-1.5">
          {findings.map((f, i) => (
            <p key={i} className="text-[12px] text-slate-600 leading-relaxed">
              <span className="text-slate-400 mr-1">{i + 1}.</span>
              {f}
            </p>
          ))}
        </div>
      )}

      {items.length > 0 && (
        <div className="space-y-2.5">
          {items.map((it, i) => (
            <div key={i} className="flex items-start gap-2.5">
              <div className="flex flex-col items-center pt-1">
                <span
                  className={`w-1.5 h-1.5 rounded-full ${
                    it.urgent ? 'bg-red-500' : 'bg-slate-300'
                  }`}
                />
                {i < items.length - 1 && <span className="w-px flex-1 bg-slate-200 mt-1" />}
              </div>
              <div className="flex-1 flex items-center justify-between min-w-0 pb-1">
                <div className="min-w-0">
                  <p className="text-[12px] text-slate-800 truncate">{it.title}</p>
                  <p className="text-[11px] text-slate-400 truncate">{it.sub}</p>
                </div>
                <span
                  className={`text-[11px] shrink-0 ml-2 ${
                    it.urgent ? 'text-red-500 font-medium' : 'text-slate-400'
                  }`}
                >
                  {it.badge}
                </span>
              </div>
            </div>
          ))}
        </div>
      )}

      {actions.length > 0 && (
        <div className="mt-3 pt-3 border-t border-slate-100 space-y-2">
          <p className="text-[11px] text-slate-400">我可以帮你做</p>
          {actions.map((a, i) => (
            <div key={i} className="rounded-lg bg-blue-50 px-2.5 py-2">
              <p className="text-[12px] text-blue-800 font-medium">{a.title}</p>
              <p className="text-[11px] text-blue-600 mt-0.5 leading-relaxed">{a.detail}</p>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

// ============================================================
// 兜底渲染：未知 ui.type 也不白屏
// ============================================================

export function UnknownCard({ ui }: { ui: UiDirective }) {
  return (
    <div className="mt-2 rounded-xl border border-dashed border-slate-300 bg-slate-50 p-3">
      <p className="text-[11px] text-slate-400 mb-1.5">
        未知的卡片类型「{ui.type}」，已降级显示
      </p>
      <pre className="text-[10px] text-slate-500 overflow-x-auto whitespace-pre-wrap break-all">
        {JSON.stringify(ui.payload, null, 2)}
      </pre>
    </div>
  );
}
