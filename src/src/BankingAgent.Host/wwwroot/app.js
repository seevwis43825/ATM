// ===== AI Banking Agent 控制台（单页，无构建，同源调用宿主 API）=====
// 说明：宿主下发了严格的 CSP（script-src 'self'），因此本文件必须是独立
// 同源脚本，且页面内不允许使用内联事件处理器。

const state = {
  token: null,
  user: null,
  password: null,
  sessionId: newSessionId(),
  mode: "chat",
  agents: [],
  plugins: [],
  tab: "plugins",
  pending: null // { amount, slots, sessionId }
};

const el = {
  account: document.getElementById("account"),
  password: document.getElementById("password"),
  login: document.getElementById("login"),
  status: document.getElementById("status"),
  stream: document.getElementById("stream"),
  input: document.getElementById("input"),
  send: document.getElementById("send"),
  mode: document.getElementById("mode"),
  sessionId: document.getElementById("sessionId"),
  sideBody: document.getElementById("sideBody"),
  clear: document.getElementById("clear"),
  newSession: document.getElementById("newSession")
};

function newSessionId() {
  return "web-" + Date.now().toString(36) + "-" + Math.random().toString(36).slice(2, 8);
}

// ===== 网络 =====
async function api(path, options = {}) {
  const headers = { "Content-Type": "application/json" };
  if (state.token) headers.Authorization = "Bearer " + state.token;

  const resp = await fetch(path, {
    method: options.method || "GET",
    headers,
    body: options.body ? JSON.stringify(options.body) : undefined
  });

  const text = await resp.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = { raw: text }; }

  if (!resp.ok) {
    const err = new Error((data && (data.message || data.errorMessage)) || ("HTTP " + resp.status));
    err.status = resp.status;
    err.payload = data;
    throw err;
  }
  return data;
}

// ===== 登录 =====
async function login() {
  const userId = el.account.value;
  const password = el.password.value;
  el.login.disabled = true;
  try {
    const res = await api("/api/auth/token", {
      method: "POST",
      body: { userId, password }
    });
    state.token = res.token;
    state.user = userId;
    state.password = password;
    el.status.className = "pill ok";
    el.status.textContent = userId + " · " + res.role;
    await loadSideData();

    // 顺带展示意图识别当前由谁提供（大模型 / 规则表），避免"以为接了模型其实没接"
    try {
      const health = await api("/health");
      const ai = health && health.ai;
      el.status.textContent += " · 意图:" +
        (ai && ai.intentRecognition === "llm+rule" ? "大模型 " + ai.model : "规则表");
    } catch { /* 健康检查失败不影响使用 */ }

    renderSide();
  } catch (e) {
    el.status.className = "pill bad";
    el.status.textContent = e.status === 429 ? "登录被限流，请稍后重试" : "登录失败";
    pushSystem("登录失败：" + e.message);
  } finally {
    el.login.disabled = false;
  }
}

// ===== 侧栏数据 =====
async function loadSideData() {
  try {
    const [plugins, agents] = await Promise.all([
      api("/api/plugins"),
      api("/api/plugins/agents")
    ]);
    state.plugins = plugins || [];
    state.agents = agents || [];
  } catch (e) {
    pushSystem("加载插件/路由信息失败：" + e.message);
  }
}

async function loadTabData() {
  if (state.tab === "compliance") {
    try { return await api("/api/plugins/compliance"); } catch { return []; }
  }
  if (state.tab === "events") {
    try { return await api("/api/plugins/events"); } catch { return null; }
  }
  return null;
}

async function renderSide() {
  if (!state.token) return;
  const body = el.sideBody;
  body.textContent = "";

  if (state.tab === "plugins") {
    if (!state.plugins.length) { body.appendChild(empty("暂无插件")); return; }
    for (const p of state.plugins) body.appendChild(pluginNode(p));
    return;
  }

  if (state.tab === "agents") {
    if (!state.agents.length) { body.appendChild(empty("暂无 Agent")); return; }
    for (const a of state.agents) {
      const box = document.createElement("div");
      box.className = "plugin";
      const h = document.createElement("h3");
      h.textContent = a.name;
      const badge = document.createElement("span");
      badge.className = "pill";
      badge.textContent = a.id;
      h.appendChild(badge);
      box.appendChild(h);
      box.appendChild(kv("角色", a.role));
      box.appendChild(tags("意图前缀", a.intents));
      box.appendChild(tags("触发关键词", a.keywords));
      body.appendChild(box);
    }
    return;
  }

  const data = await loadTabData();
  if (state.tab === "compliance") {
    if (!data || !data.length) { body.appendChild(empty("暂无合规规则")); return; }
    const table = document.createElement("table");
    table.innerHTML = "<thead><tr><th>规则 ID</th><th>版本</th><th>适用场景</th></tr></thead>";
    const tb = document.createElement("tbody");
    for (const r of data) {
      const tr = document.createElement("tr");
      tr.appendChild(td(r.ruleId));
      tr.appendChild(td(r.version));
      tr.appendChild(td((r.scenarios || []).join(", ")));
      tb.appendChild(tr);
    }
    table.appendChild(tb);
    body.appendChild(table);
    return;
  }

  if (state.tab === "events") {
    if (!data) { body.appendChild(empty("事件总线不可用")); return; }
    const summary = document.createElement("div");
    summary.className = "muted";
    summary.textContent = `已发布 ${data.published} 条 / 死信 ${data.deadLetters} 条`;
    body.appendChild(summary);
    const recent = data.recent || [];
    if (!recent.length) { body.appendChild(empty("暂无事件（转账成功后会发布 transfer.completed）")); return; }
    const table = document.createElement("table");
    table.innerHTML = "<thead><tr><th>事件</th><th>来源</th><th>时间</th></tr></thead>";
    const tb = document.createElement("tbody");
    for (const e of recent) {
      const tr = document.createElement("tr");
      tr.appendChild(td(e.eventType));
      tr.appendChild(td(e.source));
      tr.appendChild(td(new Date(e.occurredAt).toLocaleTimeString()));
      tb.appendChild(tr);
    }
    table.appendChild(tb);
    body.appendChild(table);
    return;
  }
}

function empty(text) {
  const d = document.createElement("div");
  d.className = "empty";
  d.textContent = text;
  return d;
}
function td(text) {
  const c = document.createElement("td");
  c.textContent = text == null ? "" : String(text);
  return c;
}
function kv(label, value) {
  const d = document.createElement("p");
  d.className = "kv";
  d.textContent = label + "：" + value;
  return d;
}
function tags(label, list) {
  const d = document.createElement("p");
  d.className = "kv";
  d.append(label + "：");
  const items = list && list.length ? list : ["（未声明）"];
  for (const t of items) {
    const s = document.createElement("span");
    s.className = "tag";
    s.textContent = t;
    d.appendChild(s);
  }
  return d;
}
function pluginNode(p) {
  const box = document.createElement("div");
  box.className = "plugin";
  const h = document.createElement("h3");
  h.textContent = p.name;
  const badge = document.createElement("span");
  badge.className = p.isActive ? "pill ok" : "pill bad";
  badge.textContent = p.isActive ? "已启用" : "已停用";
  h.appendChild(badge);
  box.appendChild(h);
  box.appendChild(kv("插件 ID", p.id + " v" + p.version));
  box.appendChild(kv("说明", p.description));
  box.appendChild(tags("场景", p.scenarios));
  box.appendChild(tags("Agent", p.agents));
  box.appendChild(kv("最高数据级别", p.maxClassification));
  if (p.dependencies && p.dependencies.length) {
    box.appendChild(kv("依赖", p.dependencies.map(d => d.id + "@" + d.minVersion).join(", ")));
  }
  return box;
}

// ===== 消息渲染 =====
function pushSystem(text) {
  const wrap = document.createElement("div");
  wrap.className = "msg";
  const av = document.createElement("div");
  av.className = "avatar";
  av.textContent = "S";
  const bubble = document.createElement("div");
  bubble.className = "bubble";
  const t = document.createElement("div");
  t.className = "text";
  t.textContent = text;
  t.style.background = "#FEF3C7";
  bubble.appendChild(t);
  wrap.append(av, bubble);
  el.stream.appendChild(wrap);
  el.stream.scrollTop = el.stream.scrollHeight;
}

function pushUser(text) {
  const wrap = document.createElement("div");
  wrap.className = "msg user";
  const av = document.createElement("div");
  av.className = "avatar";
  av.textContent = "我";
  const bubble = document.createElement("div");
  bubble.className = "bubble";
  const t = document.createElement("div");
  t.className = "text";
  t.textContent = text;
  bubble.appendChild(t);
  wrap.append(av, bubble);
  el.stream.appendChild(wrap);
  el.stream.scrollTop = el.stream.scrollHeight;
}

function pushAssistant(text, metaItems, extraNodes) {
  const wrap = document.createElement("div");
  wrap.className = "msg";
  const av = document.createElement("div");
  av.className = "avatar";
  av.textContent = "AI";
  const bubble = document.createElement("div");
  bubble.className = "bubble";

  const t = document.createElement("div");
  t.className = "text";
  t.textContent = text;
  bubble.appendChild(t);

  if (metaItems && metaItems.length) {
    const meta = document.createElement("div");
    meta.className = "meta";
    for (const m of metaItems) {
      if (typeof m === "string") {
        const s = document.createElement("span");
        s.textContent = m;
        meta.appendChild(s);
      } else {
        const c = document.createElement("code");
        c.textContent = m.code;
        meta.appendChild(c);
      }
    }
    bubble.appendChild(meta);
  }

  for (const n of extraNodes || []) bubble.appendChild(n);
  wrap.append(av, bubble);
  el.stream.appendChild(wrap);
  el.stream.scrollTop = el.stream.scrollHeight;
}

function card(title, buildBody) {
  const box = document.createElement("div");
  box.className = "card";
  const head = document.createElement("div");
  head.className = "card-head";
  head.textContent = title;
  const body = document.createElement("div");
  body.className = "card-body";
  buildBody(body);
  box.append(head, body);
  return box;
}

function table(headers, rows) {
  const t = document.createElement("table");
  const thead = document.createElement("thead");
  const trh = document.createElement("tr");
  for (const h of headers) {
    const th = document.createElement("th");
    th.textContent = h.label;
    if (h.num) th.className = "num";
    trh.appendChild(th);
  }
  thead.appendChild(trh);
  t.appendChild(thead);
  const tb = document.createElement("tbody");
  for (const r of rows) {
    const tr = document.createElement("tr");
    for (const h of headers) {
      const cell = document.createElement("td");
      cell.textContent = h.value(r);
      if (h.num) cell.className = "num";
      tr.appendChild(cell);
    }
    tb.appendChild(tr);
  }
  t.appendChild(tb);
  return t;
}

// 意图 → Agent（最长前缀优先，与后端 AgentRouter 同规则）
function agentFor(intent) {
  if (!intent) return null;
  let best = null, bestLen = 0;
  for (const a of state.agents) {
    for (const p of a.intents || []) {
      if (intent.toLowerCase().startsWith(p.toLowerCase()) && p.length > bestLen) {
        best = a; bestLen = p.length;
      }
    }
  }
  return best;
}

// ===== 发送 =====
async function send(text) {
  if (!state.token) { pushSystem("请先登录。"); return; }
  const message = (text != null ? text : el.input.value).trim();
  if (!message) return;
  el.input.value = "";
  pushUser(message);
  el.send.disabled = true;

  try {
    if (state.mode === "orchestrate") {
      await sendOrchestrate(message);
    } else {
      await sendChat(message);
    }
  } catch (e) {
    pushAssistant("请求失败：" + e.message, [], []);
  } finally {
    el.send.disabled = false;
    el.input.focus();
  }
}

async function sendChat(message) {
  const res = await api("/api/chat", {
    method: "POST",
    body: { message, userId: state.user, sessionId: state.sessionId, slots: {} }
  });

  const agent = agentFor(res.resultIntent || res.intent);
  const meta = [
    { code: "intent=" + (res.intent || "-") },
    { code: res.intentSource === "llm" ? "来源=大模型" : "来源=规则" },
    { code: "agent=" + (agent ? agent.id : "-") },
    { code: (res.elapsedMs || 0) + "ms" }
  ];
  if (res.sideEffectCommitted) meta.push("已提交副作用");
  if (res.requiresHumanInLoop) meta.push("需人工确认");

  const extras = [];
  const data = res.data || {};

  if (data.cards && data.cards.length) {
    extras.push(card("银行卡", b => b.appendChild(table(
      [
        { label: "卡号", value: c => c.card_no },
        { label: "类型", value: c => c.card_type },
        { label: "状态", value: c => c.status },
        { label: "日限额", num: true, value: c => fmt(c.daily_limit) }
      ], data.cards))));
  }

  if (data.categories && data.categories.length) {
    extras.push(card("消费分类", b => {
      for (const c of data.categories) {
        const row = document.createElement("div");
        row.style.marginBottom = "8px";
        const line = document.createElement("div");
        line.textContent = `${c.category}　${fmt(c.amount)} 元　${(c.ratio * 100).toFixed(1)}%`;
        const bar = document.createElement("div");
        bar.className = "bar";
        const fill = document.createElement("i");
        fill.style.width = Math.min(100, (c.ratio * 100)).toFixed(1) + "%";
        bar.appendChild(fill);
        row.append(line, bar);
        b.appendChild(row);
      }
      const t = document.createElement("div");
      t.className = "muted";
      t.textContent = `合计支出 ${fmt(data.total_expense)} 元 / 收入 ${fmt(data.total_income)} 元 / ${data.transaction_count} 笔`;
      b.appendChild(t);
    }));
  }

  if (data.products && data.products.length) {
    extras.push(card("在售理财产品", b => b.appendChild(table(
      [
        { label: "代码", value: p => p.code },
        { label: "名称", value: p => p.name },
        { label: "风险", value: p => p.risk_level },
        { label: "年化%", num: true, value: p => p.annual_rate },
        { label: "期限", num: true, value: p => p.term_days + "天" },
        { label: "起投", num: true, value: p => fmt(p.min_amount) }
      ], data.products))));
  }

  if (res.requiresHumanInLoop) {
    const pending = {
      amount: data.amount || 0,
      slots: data,
      sessionId: state.sessionId
    };
    extras.push(confirmCard(res.content, pending));
  }

  if (res.errorCode) {
    const e = document.createElement("div");
    e.className = "error";
    e.textContent = res.errorCode + "：" + (res.errorMessage || "");
    extras.push(e);
  }

  pushAssistant(res.content || (res.success ? "（无文本回复）" : "（未返回内容）"), meta, extras);
}

async function sendOrchestrate(message) {
  const res = await api("/api/orchestrate", {
    method: "POST",
    body: { message, userId: state.user, sessionId: state.sessionId, slots: {} }
  });

  const meta = [
    { code: "steps=" + res.stepsExecuted },
    { code: (res.totalElapsedMs || 0) + "ms" }
  ];
  if (res.requiresHumanApproval) meta.push("挂起：等待人工确认");

  const extras = [];
  if (res.steps && res.steps.length) {
    extras.push(card("编排轨迹", b => {
      for (const s of res.steps) {
        const box = document.createElement("div");
        box.className = "step";
        const head = document.createElement("div");
        head.textContent = `${s.stepId}　${s.success ? "成功" : "失败"}　${s.elapsedMs}ms`;
        box.appendChild(head);
        for (const a of s.agents || []) {
          const line = document.createElement("div");
          line.className = "muted";
          line.textContent = `→ ${a.agentId}（${a.intent || "-"}）：${a.content || a.failureReason || "-"}`;
          box.appendChild(line);
        }
        if (s.failureReason) {
          const f = document.createElement("div");
          f.className = "muted";
          f.textContent = "失败原因：" + s.failureReason;
          box.appendChild(f);
        }
        b.appendChild(box);
      }
    }));
  }

  pushAssistant(res.content || "（无文本回复）", meta, extras);
}

function confirmCard(reason, pending) {
  return card("待人工确认（资金操作不落地）", b => {
    const info = document.createElement("div");
    info.textContent = reason || "该操作需要你显式确认后才会提交";
    b.appendChild(info);

    const rows = [];
    if (pending.slots.to_name) rows.push(["收款人", pending.slots.to_name]);
    rows.push(["收款账号", pending.slots.to_account || "-"]);
    if (pending.slots.from_account) rows.push(["付款账号", pending.slots.from_account]);
    rows.push(["金额", fmt(pending.amount) + " 元"]);
    if (pending.slots.rule_id) rows.push(["命中规则", pending.slots.rule_id]);
    b.appendChild(table(
      [{ label: "项目", value: r => r[0] }, { label: "内容", value: r => r[1] }], rows));

    const actions = document.createElement("div");
    actions.className = "actions";
    const ok = document.createElement("button");
    ok.textContent = "确认执行";
    ok.addEventListener("click", async () => {
      ok.disabled = true;
      cancel.disabled = true;
      try {
        const res = await api("/api/chat/confirm", {
          method: "POST",
          body: {
            userId: state.user,
            sessionId: pending.sessionId,
            amount: pending.amount,
            slots: pending.slots
          }
        });
        const meta = [{ code: res.committed ? "committed=true" : "committed=false" }];
        const extras = [];
        if (res.error) {
          const e = document.createElement("div");
          e.className = "error";
          e.textContent = res.error;
          extras.push(e);
        }
        pushAssistant(res.content || "（无文本回复）", meta, extras);
      } catch (e) {
        pushSystem("确认失败：" + e.message);
        ok.disabled = false; cancel.disabled = false;
      }
    });

    const cancel = document.createElement("button");
    cancel.className = "ghost";
    cancel.textContent = "取消";
    cancel.addEventListener("click", () => {
      ok.disabled = true;
      cancel.disabled = true;
      pushSystem("已取消该操作，未产生任何资金变动。");
    });

    actions.append(ok, cancel);
    b.appendChild(actions);
  });
}

function fmt(v) {
  const n = Number(v);
  return Number.isFinite(n) ? n.toLocaleString("zh-CN", { minimumFractionDigits: 2, maximumFractionDigits: 2 }) : String(v);
}

// ===== 事件绑定 =====
el.login.addEventListener("click", login);
el.send.addEventListener("click", () => send());
el.input.addEventListener("keydown", e => { if (e.key === "Enter") send(); });
el.mode.addEventListener("change", () => {
  state.mode = el.mode.value;
  pushSystem(state.mode === "orchestrate"
    ? "已切换到多 Agent 编排：由 Supervisor 规划步骤并并行/串行调度多个 Agent。"
    : "已切换到单跳路由：意图 → 单个领域 Agent。");
});
el.newSession.addEventListener("click", () => {
  state.sessionId = newSessionId();
  el.sessionId.textContent = state.sessionId;
  pushSystem("已开启新会话：" + state.sessionId);
});
el.clear.addEventListener("click", () => {
  el.stream.textContent = "";
});

document.querySelectorAll(".quick button").forEach(btn => {
  btn.addEventListener("click", () => send(btn.dataset.say));
});

document.querySelectorAll(".tabs button").forEach(btn => {
  btn.addEventListener("click", async () => {
    document.querySelectorAll(".tabs button").forEach(b => b.classList.remove("active"));
    btn.classList.add("active");
    state.tab = btn.dataset.tab;
    el.sideBody.textContent = "";
    el.sideBody.appendChild(empty("加载中…"));
    await renderSide();
  });
});

el.account.addEventListener("change", () => {
  // 演示账号的密码规则一致，选中即自动填入，减少操作成本
  const id = el.account.value;
  el.password.value = id.startsWith("u_demo") ? "demo1234"
    : id === "staff_01" ? "staff1234"
    : id === "audit_01" ? "audit1234" : "admin1234";
  state.token = null;
  el.status.className = "pill";
  el.status.textContent = "未登录";
});

el.sessionId.textContent = state.sessionId;
el.input.focus();
