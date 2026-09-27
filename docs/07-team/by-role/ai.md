# 🤖 AI / 数据开发 / AI & Data Developer 分册

> **角色定位**：智能体、LLM、数据层的主要作者
> **主战场**：`src/ai-service/` `02-api/02-event-schema.md` `06-product/`
> **更新时间**：2026-09-21

---

## 1. 你是什么

你是 5 人团队里**让系统"变聪明"的人**。智能体编排、Prompt 工程、LLM 集成、RAG、记忆系统、模型评估——这些是你的主场。

但你的工作有**特殊性**：
- LLM 输出**不确定**（同样的输入可能产出不同）
- LLM 可能**幻觉**（编造事实）
- LLM 可能**被攻击**（Prompt 注入）

所以你必须：
- 所有 LLM 决策必须可解释、可审计
- 关键业务数据**必须从 DB 查**，不能依赖 LLM 编造
- Prompt 工程必须配合**内容安全审查**
- 模型效果必须**可度量、可监控**

---

## 2. 你的文档清单（按重要性排序）

### 2.1 必须精通 ⭐⭐⭐

| # | 文档 | 用途 |
|---|------|------|
| 1 | [`00-architecture/03-c4-components.md`](../../00-architecture/03-c4-components.md) | 组件视图，看 AI Service 与 Core 的关系 |
| 2 | [`00-architecture/06-extension-points.md`](../../00-architecture/06-extension-points.md) | 插件化（你的工具和 Agent 都是插件） |
| 3 | [`01-domain/02-domain-model.md`](../../01-domain/02-domain-model.md) | 领域模型（AI 调用的数据形态） |
| 4 | [`02-api/02-event-schema.md`](../../02-api/02-event-schema.md) | **事件 Schema（AI 订阅的事件源）** |
| 5 | [`02-api/03-feature-flags.md`](../../02-api/03-feature-flags.md) | **FeatureFlag（AI 功能默认关闭）** |
| 6 | [`05-security-compliance/01-threat-model.md`](../../05-security-compliance/01-threat-model.md) | **LLM 特有威胁（必读）** |
| 7 | [`06-product/02-feature-lifecycle.md`](../../06-product/02-feature-lifecycle.md) | AI 功能的全生命周期 |

### 2.2 必须了解 ⭐⭐

| # | 文档 | 用途 |
|---|------|------|
| 8 | [`00-architecture/07-event-driven-contract.md`](../../00-architecture/07-event-driven-contract.md) | 事件契约 |
| 9 | [`01-domain/01-bounded-contexts.md`](../../01-domain/01-bounded-contexts.md) | 限界上下文 |
| 10 | [`05-security-compliance/02-compliance-matrix.md`](../../05-security-compliance/02-compliance-matrix.md) | 合规义务（LLM 相关） |
| 11 | [`05-security-compliance/03-data-classification.md`](../../05-security-compliance/03-data-classification.md) | **数据分级（传给 LLM 前必脱敏）** |
| 12 | [`05-security-compliance/04-audit-logging.md`](../../05-security-compliance/04-audit-logging.md) | LLM 调用审计 |
| 13 | [`06-product/01-roadmap.md`](../../06-product/01-roadmap.md) | 路线图 |
| 14 | [`06-product/03-release-process.md`](../../06-product/03-release-process.md) | 发布流程（灰度、AB） |

### 2.3 偶读 ⭐

| # | 文档 | 用途 |
|---|------|------|
| 15 | [`03-development/04-testing-strategy.md`](../../03-development/04-testing-strategy.md) | 测试（含 LLM 黄金集） |
| 16 | [`04-operations/02-monitoring-observability.md`](../../04-operations/02-monitoring-observability.md) | LLM Token/成本监控 |
| 17 | [`01-domain/03-glossary.md`](../../01-domain/03-glossary.md) | 术语 |

---

## 3. 你负责的代码模块

```
src/
├── ai-service/                                ← 【你的主战场】
│   ├── Agent/                                 # Agent 编排
│   │   ├── Orchestrator/                      # 主 Agent + 副 Agent
│   │   ├── Tools/                             # Function Call 工具
│   │   ├── Memory/                            # 短期/长期记忆
│   │   └── Profile/                           # 用户画像
│   ├── LLM/                                   # LLM 客户端封装
│   │   ├── Clients/                           # Qwen3 / DeepSeek 适配
│   │   ├── Prompt/                            # Prompt 模板
│   │   └── Parsers/                           # 输出解析
│   ├── RAG/                                   # RAG 系统
│   │   ├── Embeddings/                        # 嵌入（pgvector）
│   │   ├── Retrievers/                        # 检索器
│   │   └── KnowledgeBase/                     # 知识库
│   ├── Intent/                                # 意图识别
│   ├── Recommendation/                        # 推荐（理财、卡片等）
│   ├── Eval/                                  # 模型评估
│   │   ├── GoldenSet/                         # 黄金集
│   │   └── Metrics/                           # 评估指标
│   └── Safety/                                # 内容安全审查
│
├── agent-core/                                # 【业务开发，但 AI 协作部分】
│   ├── AgentOrchestration/                    # C# 端的 Agent 桥接
│   ├── Memory&Profile/                        # 长期记忆落 DB
│   └── Recommendation/                        # 推荐落业务
│
└── admin-web/                                 # 【前端角色】
    └── /admin/llm-eval/                       # LLM 评估后台（你设计）
```

---

## 4. 你必须掌握的"硬规则"

### 4.1 Prompt 安全（参考 05-security-compliance/01 §5.1）

```python
# ✅ 正确：system prompt 与 user input token 隔离
messages = [
    {"role": "system", "content": SYSTEM_PROMPT},  # 来自配置中心，不可变
    {"role": "user", "content": user_input}        # 用户输入
]

# ✅ 正确：输出必须匹配 Schema
tools = [
    {
        "name": "transfer.execute",
        "parameters": {
            "type": "object",
            "properties": {
                "from_account": {"type": "string", "pattern": "^acc_[a-z0-9]{16}$"},
                "amount": {"type": "number", "minimum": 0, "maximum": 50000}
            },
            "required": ["from_account", "amount"]
        }
    }
]

# ❌ 错误：让 LLM 编造金额或账户
# ❌ 错误：不限制输出格式
# ❌ 错误：把 user input 当 system prompt 的一部分
```

### 4.2 数据脱敏（参考 05-security-compliance/03 §5）

```python
# ✅ 传给 LLM 前必须脱敏
def mask_for_llm(user: User) -> dict:
    return {
        "name": mask_name(user.name),              # 张*
        "phone": mask_phone(user.phone),            # 138****1234
        "account": mask_bankcard(user.account),     # ****1234
        # 永远不传：身份证、密码、CVV、生物特征
    }
```

### 4.3 内容安全审查

```python
# ✅ 所有 LLM 输出必须经过 Safety Guard
def safe_llm_call(prompt: str) -> str:
    raw = llm.invoke(prompt)
    
    # 1. PII 检测
    raw = pii_filter(raw)
    
    # 2. 违规检测（暴力、歧视、违法）
    raw = content_safety_check(raw)
    
    # 3. 业务校验（如转账金额是否合理）
    raw = business_rule_check(raw)
    
    return raw
```

### 4.4 关键决策 HITL

高风险操作（转账/卡片/理财/跨场景）必须**人工回环**：

```python
# AI 给出建议 → 业务校验 → 人工审批 → 执行
if amount > 50000 or risk_score > 0.7:
    return require_human_approval(
        scenario="transfer",
        suggested_action=ai_decision,
        reason="超过单笔限额，需要人工确认"
    )
```

### 4.5 可审计性

```python
# ✅ 所有 LLM 调用必须审计
with audit_context(
    scenario="transfer",
    intent="transfer.to_individual",
    ai_metadata={
        "model": "qwen3-72b",
        "tokens_in": len(prompt),
        "tokens_out": len(response),
        "confidence": response.confidence,
    }
):
    response = llm.invoke(prompt)
```

---

## 5. 你的关键产出

### 5.1 每周

- ≥ **2 个 AI Service PR**
- **黄金集** 维护 + 评估（intent 准确率 ≥ 95%）
- **Prompt 优化**：分析失败案例，迭代 Prompt
- **LLM 成本** 监控（每日 ≤ 预算）

### 5.2 每月

- ≥ 1 次模型效果 review
- ≥ 1 次 Prompt 安全 review
- ≥ 1 篇技术分享（论文解读 / 工程实践）

### 5.3 关键节点

- 新功能上线前 → **黄金集准确率 + 内容安全 review**
- LLM 厂商变更 → 必须回归黄金集
- 模型版本升级 → 必须灰度 + 评估

---

## 6. 你的协作接口

| 你对接 | 对接什么 | 频率 |
|--------|---------|------|
| **架构师** | Agent 架构、LLM 选型、事件 Schema | 每周 2-3 次 |
| **业务开发** | Function Call 工具实现、数据查询接口 | 每日 |
| **平台** | 模型部署、GPU 资源、监控 | 每周 1 次 |
| **安全合规** | Prompt 安全、内容审查、数据脱敏 | 每次涉及合规时 |
| **产品** | 黄金集构建、效果评估、AB 测试 | 每周 2-3 次 |

---

## 7. 关键技术栈

| 技术 | 用途 | 学习资源 |
|------|------|---------|
| **Python 3.11** | 主语言 | Python.org |
| **FastAPI** | HTTP 框架 | FastAPI.tiangolo.com |
| **LangChain / LlamaIndex** | LLM 编排 | LangChain 文档 |
| **Qwen3 / DeepSeek-V3** | 国内合规 LLM | 阿里云百炼、DeepSeek 平台 |
| **pgvector** | 向量库 | GitHub README |
| **Pydantic** | 数据校验 | Pydantic 文档 |
| **pytest + pytest-asyncio** | 测试 | pytest.org |
| **Weights & Biases** | 实验追踪 | wandb.ai |

---

## 8. 黄金集（Golden Set）规范

### 8.1 黄金集结构

```json
{
  "id": "transfer_001",
  "scenario": "transfer",
  "input": "我要给张三转 5000 块钱",
  "expected_intent": "transfer.to_individual",
  "expected_slots": {
    "amount": 5000,
    "recipient": "张三"
  },
  "expected_action": "transfer.create",
  "tags": ["normal", "individual"],
  "difficulty": "easy",
  "created_at": "2026-09-01",
  "updated_at": "2026-09-15"
}
```

### 8.2 黄金集分类

| 类别 | 数量目标 | 说明 |
|------|---------|------|
| **正常** | 60% | 典型场景 |
| **边界** | 20% | 极值情况（金额最大/最小、字符超长等） |
| **对抗** | 15% | Prompt 注入、混淆、绕过 |
| **困难** | 5% | 多意图、上下文依赖 |

### 8.3 评估指标

| 指标 | 目标 | 说明 |
|------|------|------|
| **意图识别准确率** | ≥ 95% | intent classification accuracy |
| **槽位抽取 F1** | ≥ 90% | slot extraction F1 |
| **幻觉率** | ≤ 2% | LLM 编造事实的比例 |
| **拒答准确率** | ≥ 99% | 拒答合理 + 不拒答合理 |
| **响应时间 P99** | ≤ 3s | LLM 调用耗时 |

---

## 9. 你必须监控的指标

```yaml
# Prometheus 指标（参见 04-operations/02）
ai_metrics:
  - agent_llm_request_duration_seconds{model}
  - agent_llm_tokens_total{model, direction}
  - agent_llm_error_total{error_type}
  - agent_intent_classification_accuracy  # 来自评估
  - agent_hallucination_rate
  - agent_tool_call_failure_total{tool_name}
  - llm_cost_usd_total{model}  # 成本！
```

告警规则：
- LLM 错误率 > 5% → P1
- LLM Token 1h 消耗 > 100 万 → P1（成本）
- 意图识别准确率 < 90% → P1

---

## 10. 你的"严"（必须为的）

1. **不传 PII 给 LLM**（除非脱敏后）
2. **不依赖 LLM 编造的业务数据**（必须从 DB 查）
3. **关键决策必须 HITL**
4. **所有 LLM 调用必须审计埋点**
5. **所有 Prompt 必须有版本管理**（git / 配置中心）
6. **所有输出必须内容安全审查**
7. **黄金集必须持续维护**（每次重大变更回归）
8. **成本必须可控**（用户级 + 全局配额）

---

## 11. 学习资源

### 11.1 必读

- LangChain / LlamaIndex 官方文档
- OpenAI Prompt Engineering Guide
- 阿里云百炼 Qwen 文档
- DeepSeek-V3 技术报告

### 11.2 论文（必读 5 篇）

1. [Ryt Bank (EMNLP 2025)](https://arxiv.org/abs/2510.07645) — 真实银行场景
2. [TradingAgents (AAAI 2025)](https://arxiv.org/abs/2412.20138) — 多 Agent 协同
3. [FinCon (NeurIPS 2024)](https://arxiv.org/abs/2407.06567) — LLM 金融决策
4. [FinAgent (KDD 2024)](https://arxiv.org/abs/2410.08061) — 金融 Agent 工具
5. [DeepFund (NeurIPS 2025)](https://arxiv.org/abs/2505.11065) — 实时基金

完整 53 篇论文在 `../papers/`

### 11.3 进阶

- [OWASP LLM Top 10](https://owasp.org/www-project-top-10-for-large-language-model-applications/)
- Anthropic《Building Effective Agents》
- Lilian Weng《LLM Powered Autonomous Agents》

---

## 12. FAQ

### Q1：LLM 出现幻觉怎么办？

**3 层防护**：
1. LLM 输出结构化（JSON Schema）— 限制编造
2. 业务校验（金额、账户必须从 DB 查）— 二次确认
3. 关键决策 HITL — 兜底

### Q2：Prompt 被注入怎么办？

**Prompt 防火墙**：
- 系统级：独立模型检测注入 + 拦截
- 业务级：工具白名单 + RBAC
- 用户级：高风险操作强制 HITL

### Q3：LLM 调用慢怎么办？

- 缓存（语义缓存 + 结果缓存）
- 流式输出（SSE）
- 模型分级（简单问题用小模型）

### Q4：LLM 成本太高怎么办？

- 用户级 Token 配额
- 全局预算 + 告警
- 模型分级（80% 用小模型）
- 黄金集缓存

---

## 13. 联系

- Slack / 飞书：@ai
- Mentor：架构师（兼 AI 资深）