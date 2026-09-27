# 多 Agent 协同架构（对齐 DeepSeek Harness）

> **参考架构**：DeepSeek Harness（2026-08 开源，MIT，`npx @deepseek-ai/dsh`）
> **核心范式**：`Agent = Model + Harness`
> **本文档状态**：已完成 Supervisor 编排器 + 轨迹日志；LLM 接入与 Spawn/Fork 未做

---

## 0. 先说清楚：原来缺什么

在你提这个问题之前，系统的 Agent 协同是**单跳路由**：

```
用户输入 → DetectIntent（关键词）→ AgentRouter.Resolve（最长前缀）→ 单个 Agent 执行
```

**这不是多 Agent 协同**，是「一个分发器 + N 个执行器」。缺失的是：

| 缺失能力 | 后果 |
|---------|------|
| 无任务规划 | 复杂需求无法拆解 |
| 无多 Agent 协作 | 「查卡 + 查账单 + 推理财」只能串行或干脆不做 |
| 无执行轨迹 | 出问题只能看最终结果，不知道中间发生了什么 |
| 无预算控制 | 可能无限循环 |
| 无失败降级 | 一个 Agent 挂了整条链断掉 |

---

## 1. DeepSeek Harness 是什么

### 1.1 核心主张

> **"Everything is a Plugin"** —— 没有特权内核，模型适配器、沙箱、工具、会话存储、甚至 Agent Loop 本身都是可替换插件。

内核用 **Cordis**（TypeScript 微内核），核心理念是**时空可组合性**（spatiotemporal composability）：组件在运行时安全地装卸，不泄漏内存、不污染状态。

### 1.2 五大子系统

| 子系统 | 职责 | 本项目对应 |
|-------|------|-----------|
| **Agent Loop & Orchestration** | 异步有状态运行时，决定何时调工具、何时重规划、何时协调子 Agent | ✅ `AgentOrchestrator`（本次新增） |
| **Context & Memory** | KV 缓存复用、智能截断、向量记忆 | 🟡 仅有槽位上下文，无记忆 |
| **Tool Use & MCP** | 工具注册表、类型化接口、敏感操作审批门 | 🟡 有 `IAgentTool` 契约，无实现 |
| **Execution Sandbox** | 隔离执行环境，捕获输出做自我诊断 | ❌ 未做 |
| **Trajectory System** | append-only 事件流，支持回放与分叉 | ✅ `InMemoryTrajectoryLog`（本次新增） |

### 1.3 四种运行模式

| 模式 | 说明 | 本项目 |
|------|------|-------|
| Standard | 完整工具集 | ✅ 默认 |
| PTC (Code) | 模型直接写编排代码 | ❌ |
| Minimal | 最小基准配置 | ❌ |
| Creative | 运行时插件实验环境 | ❌ |

---

## 2. 已实现：Supervisor 编排器

### 2.1 架构

```
                    ┌──────────────────────────┐
   AgentRequest ───→ │   AgentOrchestrator      │
                    │  （父 Agent / Supervisor）│
                    └───────────┬──────────────┘
                                │ 1. IOrchestrationStrategy.PlanAsync()
                                ↓
                    ┌──────────────────────────┐
                    │      执行计划             │
                    │  [Direct|Parallel|Seq|   │
                    │   HumanGate]             │
                    └───────────┬──────────────┘
                                │ 2. 逐步骤执行
                    ┌───────────┴───────────┬──────────┐
                    ↓                       ↓          ↓
              ┌──────────┐          ┌──────────┐  ┌──────────┐
              │ card     │          │ bill     │  │ transfer │
              │ .agent   │          │ .agent   │  │ .agent   │
              └─────┬────┘          └────┬─────┘  └────┬─────┘
                    └────────────────────┴─────────────┘
                                │ 3. 每步都写轨迹
                                ↓
                    ┌──────────────────────────┐
                    │   ITrajectoryLog         │
                    │  （append-only 事件流）   │
                    └──────────────────────────┘
```

### 2.2 四种步骤类型

| 类型 | 语义 | 使用场景 |
|------|------|---------|
| `Direct` | 单个 Agent 执行 | 90% 的确定性请求 |
| `Parallel` | 多个 Agent 并行，结果聚合 | 「查卡 + 查账单 + 推理财」 |
| `Sequential` | 串行，前一步结果注入后一步 | 需要上下文传递的流程 |
| `HumanGate` | 挂起等人工决策 | 资金操作合规前置 |

### 2.3 三种内置策略（可替换）

| 策略 | 逻辑 | 适用 |
|------|------|------|
| `DirectRoutingStrategy` | 意图 → 单 Agent | 简单查询 |
| `ParallelGatherStrategy` | 多 Agent 并行聚合 | 资产总览类请求 |
| `SequentialPipelineStrategy` | 合规门 → 执行 | **资金操作强制走** |
| `AdaptiveStrategy` | 按意图自动选 | **默认** |

策略实现 `IOrchestrationStrategy` 接口，**可作为插件热替换** —— 这正是 Harness 的「策略即插件」理念。

### 2.4 预算控制

```csharp
public sealed record OrchestrationBudget
{
    public int MaxSteps { get; init; } = 10;              // 防无限循环
    public int MaxParallelism { get; init; } = 4;          // 防资源打爆
    public int TotalTimeoutSeconds { get; init; } = 60;   // 防长尾
    public int MaxHumanGates { get; init; } = 1;          // 防反复打断
}
```

### 2.5 实测验证

**只读场景**（`我有哪些卡`）：

```json
{
  "success": true,
  "content": "您共有 2 张银行卡",
  "requiresHumanApproval": false,
  "stepsExecuted": 1,
  "totalElapsedMs": 1660,
  "steps": [{
    "stepId": "direct",
    "success": true,
    "agents": [{
      "agentId": "card.agent",
      "success": true,
      "intent": "card.list"
    }]
  }]
}
```

**资金场景**（`转账 30000 元`）：

```json
{
  "success": false,
  "content": "资金操作需人工确认后执行",
  "requiresHumanApproval": true,
  "pendingStepId": "compliance-gate",
  "stepsExecuted": 0,
  "totalElapsedMs": 1
}
```

> 资金操作在**第 0 步**就挂起在合规门，不会误触发扣款。

---

## 3. 已实现：轨迹日志（Trajectory）

### 3.1 与审计日志的区别

| 维度 | AuditLog | TrajectoryLog |
|------|----------|---------------|
| 回答的问题 | 「谁在什么时候做了什么」 | 「Agent 看到了什么、为什么这么做」 |
| 视角 | 合规 | 调试 |
| 事件粒度 | 业务操作 | 每一步推理与工具调用 |
| 可回放 | ❌ | ✅ |
| 可分叉 | ❌ | ✅ |

### 3.2 事件类型

```
SessionStart → PlanCreated → StepStarted → AgentInvoked
             → AgentCompleted → StepCompleted → SessionCompleted
                    ↓ (异常)
              SessionFailed / HumanGate
```

### 3.3 实测轨迹

一次卡片查询的完整轨迹（7 个事件）：

```
#1  SessionStart     session=orch-001
#2  PlanCreated      strategy=adaptive, stepCount=1
#3  StepStarted      step=direct
#4  AgentInvoked     step=direct  agent=card.agent
#5  AgentCompleted   step=direct  agent=card.agent  success=true
#6  StepCompleted    step=direct  success=true
#7  SessionCompleted success=true
```

**排查价值**：Agent 答错时，能精确看到是「路由错了」还是「Agent 内部错了」还是「下游返回错了」。

### 3.4 API

| 端点 | 用途 |
|------|------|
| `GET /api/trajectory/{sessionId}` | 回放指定会话的完整轨迹 |
| `GET /api/trajectory?limit=200` | 查看最近事件流 |

### 3.5 分叉与回放

`InMemoryTrajectoryLog` 已实现 `Fork(sessionId, atSequence, newSessionId)`：

```
在任意步骤分叉，新会话继承分叉点之前的所有事件
→ 可对比「不同编排策略在同一上下文下的表现」
```

---

## 4. 与 DeepSeek Harness 的差距（诚实清单）

| Harness 能力 | Harness | 本项目 | 差距说明 |
|-------------|---------|-------|---------|
| 插件内核 | Cordis（时空可组合） | `AssemblyLoadContext`（空间隔离） | **无时间维度**：不支持运行时热替换已挂载插件的依赖 |
| Agent Loop | 可插拔循环 | 固定编排流程 | 循环策略不可换 |
| Supervisor-Worker | ✅ | ✅ Supervisor | 架构一致 |
| Spawn / Fork | 子 Agent 创建与继承 | ❌ 未做 | 需运行时创建子 Agent 上下文 |
| Pipeline / Parallel | ✅ | ✅ 已实现 | 一致 |
| Ralph Loop | 轮转接管 | ❌ 未做 | 多 Agent 轮次迭代 |
| Workflow 工具 | 运行时写 JS 编排 | ❌ 未做 | 过度设计，暂不需要 |
| Trajectory | append-only + 回放分叉 | ✅ 已实现 | 缺持久化 |
| KV Cache 优化 | DeepSeek 深度集成 | ❌ 不适用 | 未接 DeepSeek 模型 |
| MCP 工具协议 | 完整实现 | ❌ 未做 | 有 `IAgentTool` 契约 |
| Sandbox | Docker 隔离 | ❌ 未做 | 当前无代码执行，风险较低 |
| Skills | 工具的高层组合 | ❌ 未做 | |
| LLM 接入 | 完整 | ❌ 关键词匹配 | **最大差距** |

---

## 5. 尚未实现的关键能力

### 5.1 LLM 接入（P0）

当前 `DetectIntent` 是关键词匹配。接入 LLM 后需要：

```csharp
public interface IIntentClassifier
{
    Task<IntentResult> ClassifyAsync(string input, CancellationToken ct = default);
}

public sealed record IntentResult
{
    public required string Intent { get; init; }
    public required double Confidence { get; init; }
    public IReadOnlyDictionary<string, object?> Slots { get; init; } = [];
    public string? Reasoning { get; init; }   // 决策依据，写入轨迹
}
```

**这会改变整个容量模型** —— 单请求延迟从 3ms 变成 500-2000ms，QPS 下降 100-500 倍。需要配套：
- 语义缓存（相似问题复用结果）
- 请求合并（并发相同问题合并为一次 LLM 调用）
- 异步化（意图识别不阻塞主流程）

### 5.2 Spawn / Fork 子 Agent（P1）

```csharp
public interface ISubAgentSpawner
{
    Task<AgentHandle> SpawnAsync(AgentSpec spec, CancellationToken ct = default);
    Task<AgentResult> ForkAsync(string sessionId, int atStep, CancellationToken ct = default);
}
```

适用场景：转账 Agent 发现需要查询账单才能判断风险 → Spawn 一个账单子 Agent。

### 5.3 上下文与记忆（P1）

当前每轮请求上下文是全新的。需要：
- 短期情节记忆（本次会话内）
- 长期语义记忆（跨会话的用户偏好）
- 上下文智能截断（超长对话）

### 5.4 轨迹持久化（P1）

当前 `InMemoryTrajectoryLog` 进程重启即丢。需落文件或数据库，支持跨会话回放。

---

## 6. 建议的演进路线

### 阶段 1：接入 LLM（当前可做）
```
关键词匹配 → LLM 分类器（带语义缓存）
容量模型重估
```

### 阶段 2：多 Agent 协作
```
单 Agent 执行 → Spawn/Fork 子 Agent
支持「转账前查风险」「理财推荐前查持仓」
```

### 阶段 3：策略可组合
```
固定策略 → 配置驱动的编排（YAML 定义工作流）
对齐 Harness 的 4 种预设模式
```

### 阶段 4：生产化
```
轨迹落库 → 跨实例回放
记忆外置 → Redis
事件总线 → Kafka
```

---

## 7. 关键设计决策

### 7.1 为什么编排器与路由器并存

| | AgentRouter | AgentOrchestrator |
|---|---|---|
| 跳数 | 1 | N |
| 规划 | 无 | 有 |
| 并行 | 无 | 有 |
| 人工门 | Agent 内部实现 | 编排器统一控制 |
| 适用 | 简单查询 | 复杂流程 |

**保留两个**是因为：简单查询走路由器延迟更低（P50 15ms），复杂流程才需要编排器的规划开销。

### 7.2 为什么轨迹与审计分离

审计日志需要**长期、加密、不可篡改**，走正式存储；轨迹日志需要**高频、可丢弃、便于调试**，走内存 + 文件。混在一起会导致：
- 调试时翻加密数据麻烦
- 审计存储被调试数据污染

### 7.3 为什么策略可替换

如果编排逻辑写死在 `AgentOrchestrator` 里，每种业务场景都要改核心代码。抽成 `IOrchestrationStrategy` 后：
- 新增策略 = 新增一个类 + DI 注册
- 核心代码零改动
- 对齐 Harness「编排即插件」

---

## 8. 参考

- DeepSeek Harness 官网：https://deepseekharness.dev/
- Cordis 设计论文：*A Programming Paradigm for Spatiotemporal Composability*（arXiv:2608.25512）
- FinCon（NeurIPS 2024）：https://arxiv.org/abs/2407.06567 —— LLM 多专家金融决策
- TradingAgents（AAAI 2025）：https://arxiv.org/abs/2412.20138 —— 多 Agent 辩论
- 本项目 ADR：[`09-uml/02-architecture-decision-record.md`](../09-uml/02-architecture-decision-record.md)
