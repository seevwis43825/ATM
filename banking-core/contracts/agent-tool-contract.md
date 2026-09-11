# Agent 工具接口契约（冻结版 v1.0）

> **这份文件是全队的"宪法"。**
> 5 个人在不同地方并行开发，靠的就是这份契约。谁要改契约，必须在群里说，并在这里改版本号。
> 冻结时间：项目启动第 1 天。修改需 2 人以上同意。

---

## 一、核心原则

1. **大脑层只认契约，不认实现。** A 写 Agent 时看到的只是工具签名，B/C/D 写业务时只要签名对上，就能各跑各的。
2. **先 Mock，后真实。** 所有工具第一版必须能在没有网络、没有 AI key 的情况下返回假数据，保证任何人 clone 下来就能跑。
3. **返回值结构统一。** 任何工具都返回同一个 `ToolResult` 壳，前端只写一套渲染逻辑。

---

## 二、统一返回结构

所有工具函数必须返回这个结构：

```ts
interface ToolResult<T = unknown> {
  ok: boolean;              // 执行是否成功
  code: string;             // 成功为 "OK"，失败为错误码，如 "INSUFFICIENT_BALANCE"
  message: string;          // 给用户看的中文提示，Agent 会直接转述
  data: T | null;           // 业务数据，失败时为 null
  needConfirm?: boolean;    // 是否为敏感操作，需要用户二次确认
  riskLevel?: 'low' | 'mid' | 'high';  // 风控等级（安全自评报告要用）
  auditId?: string;         // 审计日志 ID（安全自评报告要用）
}
```

**为什么必须统一？** 因为 Agent 大脑拿到任何工具结果后，都走同一套「回话逻辑」；前端也只用一套「卡片渲染」。不统一的话，5 个人会写出 5 种格式，集成时全废。

---

## 三、工具清单与归属

| 场景 | 工具函数名 | 负责人 | 关键参数 |
|---|---|---|---|
| 智能转账 | `transferByContact` | B | `target: string`（姓名/手机号）, `amount: number`, `remark?: string` |
| 定时转账 | `scheduleTransfer` | B | `target: string`, `amount: number`, `cron: string` |
| 拆分 AA 收款 | `splitBill` | B | `totalAmount: number`, `members: string[]` |
| 账单分类统计 | `analyzeBill` | C | `period: 'month'\|'year'`, `category?: string` |
| 异常交易识别 | `detectAnomaly` | C | `period: string` |
| 生成账单报告 | `generateBillReport` | C | `period: string`, `format: 'text'\|'chart'` |
| 理财产品推荐/对比 | `recommendProduct` | D | `riskLevel: string`, `amount?: number` |
| 风险评估 | `assessRisk` | D | `userId: string` |
| 一键申购/赎回 | `subscribeProduct` / `redeemProduct` | D | `productId: string`, `amount: number` |
| 卡片申请 | `applyCard` | D | `cardType: string` |
| 额度调整 | `adjustLimit` | D | `cardId: string`, `newLimit: number` |
| 交易限制/解锁 | `toggleCardLock` | D | `cardId: string`, `lock: boolean` |
| 卡片挂失 | `reportCardLoss` | D | `cardId: string` |
| 订阅识别/取消 | `listSubscriptions` / `cancelSubscription` | A | `subscriptionId?: string` |
| 生日联动 | `birthdayWorkflow` | A | `userId: string` |

> 场景覆盖度：转账 + 账单 + 理财 + 卡片 + 订阅 + 跨场景联动 = **6/6 全覆盖**，拿到"鼓励全覆盖"的加分。

---

## 四、HTTP 接口（前后端唯一的对接点）

### `POST /api/chat`

前端 **只需要调这一个接口**。

**请求：**
```json
{
  "sessionId": "sess_abc123",
  "userId": "u_1001",
  "message": "帮我给张三转500块，备注房租"
}
```

**响应：**
```json
{
  "sessionId": "sess_abc123",
  "reply": "已为你准备好转账：收款人张三，金额 500 元，备注房租。请确认。",
  "intent": "transfer",
  "toolCalls": [
    {
      "tool": "transferByContact",
      "ok": true,
      "code": "OK",
      "message": "待确认",
      "needConfirm": true,
      "riskLevel": "mid",
      "auditId": "aud_20260912_001",
      "data": { "target": "张三", "amount": 500, "remark": "房租" }
    }
  ],
  "ui": {
    "type": "confirm_card",
    "payload": { "title": "确认转账", "fields": [["收款人", "张三"], ["金额", "500.00 元"]] }
  }
}
```

### `POST /api/confirm`
用户点"确认"后调这个，真正执行操作。

```json
{ "sessionId": "sess_abc123", "auditId": "aud_20260912_001", "action": "approve" }
```

### `GET /api/health`
健康检查，部署材料里要用，返回 `{"status":"ok","version":"1.0.0"}`。

---

## 五、前端 UI 指令协议（`ui.type` 枚举）

前端只认这几种类型，Agent 必须从中选一个：

| type | 用途 | 谁产出 |
|---|---|---|
| `text` | 纯文字回复 | A |
| `confirm_card` | 敏感操作二次确认卡片 | A / B / D |
| `bill_chart` | 账单图表数据 | C |
| `product_list` | 理财产品对比卡片 | D |
| `card_list` | 银行卡列表 | D |
| `timeline` | 定时任务/订阅时间轴 | A / B |

**规则：** 新增 `ui.type` 必须同步通知前端负责人 E，并在本文件登记。E 端必须做默认兜底渲染（未知 type 渲染成 JSON 文本），防止崩溃。

---

## 六、Mock 数据约定

统一放在 `banking-core/mock-data/`，由 **A 负责维护**，其他人只读不改：

- `users.json` — 用户与账户余额
- `transactions.json` — 至少 200 条流水，覆盖 8 个消费分类
- `products.json` — 12 个理财产品，风险等级 R1-R5
- `cards.json` — 银行卡与额度
- `subscriptions.json` — 订阅/代扣记录

> 数据要有"戏"：故意埋入 3 条异常交易、1 个即将到期的订阅、1 个生日在 10 月的用户——演示视频全靠这些埋点出效果。

---

## 七、变更记录

| 版本 | 日期 | 修改人 | 内容 |
|---|---|---|---|
| v1.0 | 2026-09-12 | 全队 | 首次冻结 |
