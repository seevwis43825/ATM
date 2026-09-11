/**
 * 主界面 —— 负责人：E
 *
 * 结构：移动端手机壳 → 顶栏 → 消息流 → 输入栏
 * 卡片渲染统一走 UiRenderer，未知类型自动兜底。
 */

import { useEffect, useRef, useState } from 'react';
import { sendChat, sendConfirm } from './api';
import type { Message, UiDirective } from './types';
import {
  ConfirmCard, BillChart, ProductList, CardList, Timeline, UnknownCard,
} from './components/cards';

/** 演示用的快捷话术 —— 答辩时点一下就能出效果 */
const SUGGESTIONS = [
  '给张三转500块，备注房租',
  '帮我看看这个月的账单',
  '账单里有没有异常交易',
  '我有5万闲钱，想做个稳健点的理财',
  '我有哪些自动扣费',
  '我最近有什么要花钱的地方吗',
];

/** UI 指令 → 组件 */
function UiRenderer({
  ui,
  done,
  onConfirm,
}: {
  ui: UiDirective;
  done?: boolean;
  onConfirm: (auditId: string, action: 'approve' | 'reject') => void;
}) {
  switch (ui.type) {
    case 'text':
      return null;
    case 'confirm_card':
      return <ConfirmCard payload={ui.payload} onConfirm={onConfirm} done={done} />;
    case 'bill_chart':
      return <BillChart payload={ui.payload} />;
    case 'product_list':
      return <ProductList payload={ui.payload} />;
    case 'card_list':
      return <CardList payload={ui.payload} />;
    case 'timeline':
      return <Timeline payload={ui.payload} />;
    default:
      // 契约约定：未知类型必须兜底，绝不白屏
      return <UnknownCard ui={ui} />;
  }
}

let msgSeq = 0;
const nextId = () => `m_${++msgSeq}`;

export default function App() {
  const [messages, setMessages] = useState<Message[]>([
    {
      id: nextId(),
      role: 'agent',
      text:
        '你好，我是 AI 银行助手。你可以直接用说话的方式办理业务，比如：\n' +
        '· 给张三转 500 块，备注房租\n' +
        '· 帮我看看这个月的账单\n' +
        '· 我有 5 万闲钱，想做个稳健点的理财',
    },
  ]);
  const [input, setInput] = useState('');
  const [loading, setLoading] = useState(false);
  const scrollRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight, behavior: 'smooth' });
  }, [messages, loading]);

  async function handleSend(text?: string) {
    const content = (text ?? input).trim();
    if (!content || loading) return;

    setInput('');
    setMessages((prev) => [...prev, { id: nextId(), role: 'user', text: content }]);
    setLoading(true);

    try {
      const res = await sendChat(content);
      setMessages((prev) => [
        ...prev,
        { id: nextId(), role: 'agent', text: res.reply, ui: res.ui },
      ]);
    } catch (err) {
      setMessages((prev) => [
        ...prev,
        {
          id: nextId(),
          role: 'agent',
          text: `出错了：${err instanceof Error ? err.message : '请求失败'}。请确认后端服务已启动（端口 3001）。`,
        },
      ]);
    } finally {
      setLoading(false);
    }
  }

  async function handleConfirm(auditId: string, action: 'approve' | 'reject') {
    // 先把这个卡片标记为已处理，避免重复点击
    setMessages((prev) =>
      prev.map((m) =>
        m.ui?.type === 'confirm_card' && m.ui.payload.auditId === auditId
          ? { ...m, done: true }
          : m,
      ),
    );

    setLoading(true);
    try {
      const res = await sendConfirm(auditId, action);
      setMessages((prev) => [...prev, { id: nextId(), role: 'agent', text: res.reply }]);
    } catch (err) {
      setMessages((prev) => [
        ...prev,
        {
          id: nextId(),
          role: 'agent',
          text: `操作失败：${err instanceof Error ? err.message : '未知错误'}`,
        },
      ]);
    } finally {
      setLoading(false);
    }
  }

  function handleReset() {
    setMessages([
      { id: nextId(), role: 'agent', text: '已重新开始。有什么可以帮你的？' },
    ]);
  }

  return (
    <div className="min-h-screen flex items-center justify-center bg-[#0b1220] sm:p-6">
      <div className="w-full h-screen sm:h-[860px] sm:w-[400px] sm:rounded-[36px] bg-white flex flex-col overflow-hidden sm:shadow-2xl sm:ring-1 sm:ring-white/10">

        {/* 顶栏 */}
        <div className="shrink-0 bg-slate-900 text-white px-4 pt-3 pb-3 flex items-center justify-between">
          <div className="flex items-center gap-2.5">
            <div className="w-8 h-8 rounded-lg bg-blue-600 flex items-center justify-center text-[13px] font-medium">
              AI
            </div>
            <div>
              <p className="text-[14px] font-medium leading-tight">AI 银行助手</p>
              <p className="text-[11px] text-slate-400 leading-tight">在线 · 可直接说需求</p>
            </div>
          </div>
          <button
            onClick={handleReset}
            className="text-[12px] text-slate-400 active:text-slate-200 px-2 py-1"
          >
            重新开始
          </button>
        </div>

        {/* 消息流 */}
        <div ref={scrollRef} className="flex-1 overflow-y-auto chat-scroll bg-slate-50 px-3.5 py-4">
          <div className="space-y-3.5">
            {messages.map((m) => (
              <div
                key={m.id}
                className={`flex ${m.role === 'user' ? 'justify-end' : 'justify-start'} animate-fade-up`}
              >
                <div className={`max-w-[85%] ${m.role === 'user' ? '' : 'w-full'}`}>
                  {m.role === 'agent' && (
                    <div className="flex items-start gap-2">
                      <div className="w-6 h-6 rounded-md bg-slate-900 text-white flex items-center justify-center text-[10px] shrink-0 mt-0.5">
                        AI
                      </div>
                      <div className="flex-1 min-w-0">
                        <div className="rounded-2xl rounded-tl-md bg-white px-3.5 py-2.5 text-[13px] text-slate-700 leading-relaxed whitespace-pre-wrap shadow-sm border border-slate-100">
                          {m.text}
                        </div>
                        {m.ui && (
                          <UiRenderer ui={m.ui} done={m.done} onConfirm={handleConfirm} />
                        )}
                      </div>
                    </div>
                  )}

                  {m.role === 'user' && (
                    <div className="rounded-2xl rounded-tr-md bg-blue-600 px-3.5 py-2.5 text-[13px] text-white leading-relaxed shadow-sm">
                      {m.text}
                    </div>
                  )}
                </div>
              </div>
            ))}

            {loading && (
              <div className="flex items-start gap-2">
                <div className="w-6 h-6 rounded-md bg-slate-900 text-white flex items-center justify-center text-[10px] shrink-0">
                  AI
                </div>
                <div className="rounded-2xl rounded-tl-md bg-white px-4 py-3 shadow-sm border border-slate-100 flex gap-1">
                  <span className="w-1.5 h-1.5 rounded-full bg-slate-400 typing-dot" />
                  <span className="w-1.5 h-1.5 rounded-full bg-slate-400 typing-dot" />
                  <span className="w-1.5 h-1.5 rounded-full bg-slate-400 typing-dot" />
                </div>
              </div>
            )}
          </div>
        </div>

        {/* 快捷话术 */}
        <div className="shrink-0 bg-white border-t border-slate-100 px-3 py-2 overflow-x-auto">
          <div className="flex gap-2 w-max">
            {SUGGESTIONS.map((s) => (
              <button
                key={s}
                onClick={() => handleSend(s)}
                disabled={loading}
                className="shrink-0 text-[12px] text-slate-600 border border-slate-200 rounded-full px-3 py-1.5 active:bg-slate-50 disabled:opacity-40"
              >
                {s}
              </button>
            ))}
          </div>
        </div>

        {/* 输入栏 */}
        <div className="shrink-0 bg-white border-t border-slate-100 px-3 py-2.5 pb-4 flex items-center gap-2">
          <input
            value={input}
            onChange={(e) => setInput(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                handleSend();
              }
            }}
            placeholder="说点什么，比如「给张三转500」"
            disabled={loading}
            className="flex-1 bg-slate-100 rounded-full px-4 py-2.5 text-[13px] outline-none focus:ring-1 focus:ring-blue-400 disabled:opacity-50"
          />
          <button
            onClick={() => handleSend()}
            disabled={loading || !input.trim()}
            className="shrink-0 w-10 h-10 rounded-full bg-blue-600 text-white flex items-center justify-center active:bg-blue-700 disabled:opacity-30"
            aria-label="发送"
          >
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M22 2L11 13" />
              <path d="M22 2l-7 20-4-9-9-4 20-7z" />
            </svg>
          </button>
        </div>
      </div>
    </div>
  );
}
