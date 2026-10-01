# 🎨 前端现状与目标设计

> **状态**：评审中 · **所有者**：前端全栈 + 业务开发 · **版本**：v0.1
> **最后更新**：2026-10-01
> **配套原型**：[`prototype/chat.html`](prototype/chat.html)（IM 对话 H5）· [`prototype/admin.html`](prototype/admin.html)（Admin 后台）
>
> **重要**：`prototype/` 是可直接打开的静态设计资产，不是当前生产前端；Next.js/Tailwind/Ant Design/Playwright 均为 **Target**。

---

## 1. 当前实现（Current）

当前可运行 UI 位于：

```text
src/src/BankingAgent.Host/wwwroot/
├── index.html
└── app.js
```

它由 Host 通过 `UseDefaultFiles()` / `UseStaticFiles()` 提供，访问 `http://localhost:5243/`，无需 Node.js 构建。其职责是演示登录、对话、插件能力与人工确认，并直接调用 Host 当前 API（如 `/api/auth/token`、`/api/chat`、`/api/chat/confirm`）。

`docs/08-frontend/prototype/chat.html` 与 `admin.html` 仅用于讨论交互和视觉方案，不应作为接口已实现、页面已上线或框架已选型的证据。

## 2. 目标产品（Target）

| 端 | 用途 | 用户 | 技术栈 |
|----|------|------|--------|
| **IM 对话 H5** | 用户对话主入口（核心）| 普通客户 | Target：Next.js + React + Tailwind CSS |
| **Admin / Auditor 后台** | 客服、风控、合规、运营 | 内部员工 | Target：Next.js + Ant Design Pro |

**IM 通道（微信/支付宝/飞书）**：通过 Bot SDK 接入，核心 UI 是 H5 webview。

---

## 3. 目标设计原则

1. **对话优先**：所有功能都是对话的延伸（卡片/按钮/表单嵌入对话流）
2. **移动优先**：H5 主交互（80% 用户在手机）
3. **可访问性**：符合 WCAG 2.1 AA（金融合规要求）
4. **设计令牌（Design Tokens）**：颜色/字体/间距统一管理（Tailwind config）
5. **微交互**：关键操作有 loading、success、error 反馈
6. **降级体验**：LLM/AI 不可用时，回退到表单 + 规则

---

## 4. 目标设计令牌（Design Tokens）

### 3.1 颜色

```js
// tailwind.config.js
export default {
  theme: {
    extend: {
      colors: {
        // 品牌色（银行蓝/紫）
        primary: {
          50:  '#EEF2FF',
          100: '#E0E7FF',
          500: '#6366F1',  // 主色（Indigo）
          600: '#4F46E5',
          700: '#4338CA',
        },
        // 状态色
        success: '#10B981',
        warning: '#F59E0B',
        danger:  '#EF4444',
        info:    '#3B82F6',
        // 中性色
        ink: {
          900: '#0F172A',
          700: '#334155',
          500: '#64748B',
          300: '#CBD5E1',
          100: '#F1F5F9',
        },
        // 背景
        canvas: '#F8FAFC',
      }
    }
  }
}
```

### 3.2 字体

```css
font-family: -apple-system, BlinkMacSystemFont, "PingFang SC",
             "Microsoft YaHei", "Segoe UI", Roboto, sans-serif;
```

字号体系（基于 4 倍数）：

| 名称 | 用途 | 字号 / 行高 |
|------|------|------------|
| Display | 大标题 | 32/40 |
| H1 | 页面标题 | 24/32 |
| H2 | 区块标题 | 20/28 |
| Body-L | 主内容 | 16/24 |
| Body-M | 辅助文字 | 14/20 |
| Caption | 角标 | 12/16 |

### 3.3 间距

8 倍数系统：4 / 8 / 12 / 16 / 24 / 32 / 48 / 64

### 3.4 圆角

- 卡片：12px
- 按钮：8px
- 输入框：8px
- 头像：50%

### 3.5 阴影

- 卡片：0 2px 8px rgba(0,0,0,.06)
- 弹窗：0 8px 24px rgba(0,0,0,.12)

---

## 5. 目标核心组件

### 4.1 对话流（Chat Stream）

```tsx
<ChatStream>
  <Message role="user">  "我要给张三转 5000 块"           </Message>
  <Message role="assistant">  
    <TransferConfirmCard
      recipient="张三"
      amount={5000}
      fromAccount="****-1234"
      onConfirm={...}
      onCancel={...}
    />
  </Message>
</ChatStream>
```

**Message 类型**：
- text（纯文本）
- card（业务卡片）
- actions（按钮组）
- quickReplies（快捷回复）
- typing（输入中）
- system（系统消息）

### 4.2 业务卡片（Business Cards）

| 卡片 | 用途 |
|------|------|
| **TransferConfirmCard** | 转账确认（含收款人、金额、限额、HITL） |
| **BillAnalysisCard** | 账单分析（分类、可视化） |
| **WealthRecommendCard** | 理财推荐（产品、风险评级） |
| **CardManageCard** | 卡片操作（激活、挂失、查询） |
| **SubscriptionCard** | 订阅管理（签约、查看、撤销） |
| **RiskAlertCard** | 风险提示（异常登录、大额转账） |

### 4.3 快捷指令

```tsx
<QuickReplies
  items={[
    { label: '查询余额', icon: '💰', onClick: ... },
    { label: '转账', icon: '💸', onClick: ... },
    { label: '账单分析', icon: '📊', onClick: ... },
    { label: '我的卡片', icon: '💳', onClick: ... },
  ]}
/>
```

### 4.4 HITL 确认组件（强合规）

```tsx
<HumanConfirmation
  title="转账金额较大，需要您确认"
  scenario="transfer"
  riskLevel="high"
  amount={50000}
  ruleId="transfer.daily_limit.amount"
  ruleVersion="v1.5"
  onApprove={...}    // 用户指纹 / 短信验证码
  onReject={...}
/>
```

### 4.5 AI 标识（合规要求）

所有 AI 生成的回复必须带标识：

```tsx
<Message role="assistant">
  <AIBadge /> 本回答由 AI 生成
  <Card>...</Card>
</Message>
```

---

## 6. 目标信息架构（IA）

### 5.1 IM 对话 H5

```
[首页]
├── 顶部 Header（账户头像 + 余额 + 设置）
├── 欢迎区（问候 + 快捷指令）
├── 历史对话（可下拉）
└── 输入区（文字 / 语音 / 加号）

[对话流]
├── 用户消息（右）
├── AI 回复（左）/ 卡片 / 按钮
└── 输入区

[抽屉]
├── 我的账户
├── 账单
├── 理财
├── 卡片
├── 订阅
└── 设置（含隐私、退出、用户协议）
```

### 5.2 Admin 后台

```
[Dashboard 总览]
├── 业务指标（对话量、转账额、错误率）
├── 告警面板
└── 实时对话流（抽样）

[对话管理]
├── 实时对话（在线客服接管）
├── 历史对话（查询、导出）
└── 用户画像（标签、记忆）

[合规中心]
├── 审计日志查询
├── 合规拦截记录
├── PIA 管理
└── 监管报送

[风控中心]
├── AML 监控
├── 可疑交易
└── 黑名单管理

[LLM 管理]
├── 黄金集
├── 评估结果
├── Prompt 管理
└── 成本监控

[用户管理]
├── 用户列表
├── 权限管理
└── 客服账号
```

---

## 7. 目标接口契约（尚未等同当前 Host API）

### 6.1 对话接口（SSE 流式）

```
POST /api/v1/conversations
Content-Type: application/json
Authorization: Bearer <jwt>

{
  "conversation_id": "conv_xxx",  // 可选，首次创建
  "message": "我要给张三转 5000"
}

→ SSE 流式返回
data: {"type": "intent", "intent": "transfer.to_individual"}
data: {"type": "card", "card_type": "TransferConfirm", "data": {...}}
data: {"type": "actions", "actions": [...]}
data: {"type": "done"}
```

详见 [`../02-api/01-rest-api-spec.md` §6](../02-api/01-rest-api-spec.md)。实施前必须与 `BankingAgent.Host/Program.cs` 的当前端点核对，不能直接假定 SSE/WebSocket 已存在。

### 6.2 事件订阅（WebSocket）

```typescript
ws://api.example.com/ws
→ 服务端推送：HITL 请求、新消息、状态变更
```

---

## 8. 性能与体验目标（Target）

| 指标 | 目标 |
|------|------|
| 首屏加载 | ≤ 2s（4G） |
| 对话首响 | ≤ 2s |
| 流式响应 | 首字 ≤ 500ms |
| 交互响应（P95）| ≤ 100ms |
| Lighthouse | ≥ 90 |
| 包体积 | ≤ 200KB（gzip） |

---

## 9. 可访问性（A11Y，Target）

- 所有按钮有 `aria-label`
- 所有图片有 `alt`
- 颜色对比度 ≥ 4.5:1（WCAG AA）
- 支持键盘导航
- 支持屏幕阅读器
- 字号可放大（最大 200%）
- 不依赖颜色传达信息（合规要求）

---

## 10. i18n（Target）

- 当前：**简体中文** 唯一
- 预留架构：`next-intl`
- 文案集中在 `locales/zh-CN.json`
- 所有日期/金额/数字格式按 locale

---

## 11. 静态原型说明（Design Assets）

### 10.1 IM 对话 H5（`prototype/chat.html`）

**包含场景**：
1. 首屏（问候 + 快捷指令）
2. 转账确认流程（含 HITL）
3. 账单分析卡片
4. 理财推荐卡片
5. 卡片管理
6. 风险提示
7. 错误状态（网络、LLM 不可用）

### 10.2 Admin 后台（`prototype/admin.html`）

**包含页面**：
1. Dashboard 总览
2. 实时对话
3. 审计查询
4. 合规拦截
5. LLM 评估
6. AML 告警

---

## 12. 目标技术栈（尚未落地）

| 层 | 技术 |
|----|------|
| 框架 | Next.js 14 (App Router) + React 18 |
| 样式 | Tailwind CSS 3 |
| 组件库 | shadcn/ui（H5） + Ant Design Pro（Admin） |
| 状态管理 | Zustand + React Query |
| HTTP | fetch + React Query |
| 实时 | SSE + WebSocket |
| 表单 | React Hook Form + Zod |
| 测试 | Vitest + React Testing Library + Playwright |
| 部署 | Vercel（开发） / 阿里云 ESA（生产） |

---

## 13. 参考

- 设计参考：[微信支付 H5](https://pay.weixin.qq.com/) · [支付宝 H5](https://www.alipay.com/)
- 合规要求：[`../05-security-compliance/02-compliance-matrix.md`](../05-security-compliance/02-compliance-matrix.md)
- API 规范：[`../02-api/01-rest-api-spec.md`](../02-api/01-rest-api-spec.md)
- 团队角色：[`../07-team/01-team-roles.md`](../07-team/01-team-roles.md)

---

## 14. 原型演示

打开 `prototype/chat.html` 即可看到 IM 对话 H5 原型。
打开 `prototype/admin.html` 即可看到 Admin 后台原型。

无需构建、无需依赖，浏览器直接打开即可。