# 架构决策记录（ADR - Architecture Decision Records）

> **状态**：维护中 · **所有者**：架构师 · **最后更新**：2026-09-21
> **格式参考**：Michael Nygard 的 ADR 模板

---

## 什么是 ADR？

**架构决策记录（ADR）** 是捕获项目中重要架构决策的文档。每条 ADR 描述一次决策、决策背景、备选方案、决策理由，以及后果。

### 命名规则

`ADR-NNNN-<简短标题>.md`，编号**永远不重用**，即使 ADR 后续被废弃，编号保留并加 `Status: SUPERSEDED` 标记。

---

## 索引

| 编号 | 标题 | 状态 | 日期 |
|------|------|------|------|
| [ADR-0001](#adr-0001) | 采用模块化单体架构 | ✅ 已通过 | 2026-09-21 |
| [ADR-0002](#adr-0002) | 未来可演进为微服务 | ✅ 已通过 | 2026-09-21 |
| [ADR-0003](#adr-0003) | 事件总线：进程内 MediatR → 未来 Kafka | ✅ 已通过 | 2026-09-21 |
| [ADR-0004](#adr-0004) | 双语言栈：C# .NET 8 + Python 3.11 | ✅ 已通过 | 2026-09-21 |
| [ADR-0005](#adr-0005) | LLM 主用国内模型（Qwen3/DeepSeek） | ✅ 已通过 | 2026-09-21 |
| [ADR-0006](#adr-0006) | 数据库选型 PostgreSQL 16 + pgvector | ✅ 已通过 | 2026-09-21 |
| [ADR-0007](#adr-0007) | API 网关使用 Kong / APISIX | 🟡 评审中 | 2026-09-21 |
| [ADR-0008](#adr-0008) | 强制人工回环（Human-in-the-Loop） | ✅ 已通过 | 2026-09-21 |
| [ADR-0009](#adr-0009) | 可插拔 Agent 注册机制 | ✅ 已通过 | 2026-09-21 |
| [ADR-0010](#adr-0010) | 功能开关系统（FeatureFlag） | 🟡 评审中 | 2026-09-21 |

---

## ADR-0001：采用模块化单体架构

**日期**：2026-09-21
**状态**：✅ 已通过

### 背景
5 人小团队开发一个 AI Banking Agent 系统。需要支持"功能随时增加"，同时控制运维复杂度。

### 备选方案
| 方案 | 优点 | 缺点 |
|------|------|------|
| A. 传统单体（一个进程一个部署包） | 简单 | 模块边界不清 → 难以扩展 |
| B. **模块化单体（Monorepo + 多模块）** ✅ | 边界清晰、独立部署组件、共享进程 | 仍受单进程资源限制 |
| C. 微服务（每个 Context 一个服务） | 完全独立扩展 | 5 人无法维护分布式系统 |
| D. Serverless（FaaS） | 自动扩缩容 | 冷启动 + 调试困难 |

### 决策
**采用 B：模块化单体**。

### 理由
- 5 人团队运维能力有限
- 模块化保证后续可演进为微服务（架构特征可见）
- 部署简单（一个进程一个镜像）

### 后果
- 所有模块共用一个数据库连接池
- 一个模块的 OOM 会拖垮整个进程
- 需要严格的内存/性能基线

---

## ADR-0002：未来可演进为微服务

**日期**：2026-09-21
**状态**：✅ 已通过

### 背景
ADR-0001 决定采用模块化单体，但某些模块（如 CrossScenario Coordinator）预期负载高、性能要求严。

### 决策
**架构特征（Architectural Characteristics）必须**：

| 特征 | 要求 | 备注 |
|------|------|------|
| 模块独立可部署 | 至少代码级别 | |
| 模块独立可启动 | 至少进程内可禁用 | FeatureFlag 默认关闭 |
| 模块独立可测试 | 强制要求 | |
| 数据访问通过接口 | 强制 | 不允许直接共享 DbContext |
| 事件通信 | 强制 | 不允许直接服务调用 |

### 演进路径
1. **短期**：模块化单体（当前）
2. **中期**：高负载模块（Cross-Scenario / Conversation）拆分为独立服务
3. **长期**：完整微服务化，按业务能力拆分

### 后果
- 必须使用接口隔离（Interface Segregation）
- 必须为每个模块建立独立部署包路径（即使现在共用一个包）

---

## ADR-0003：事件总线：进程内 MediatR → 未来 Kafka

**日期**：2026-09-21
**状态**：✅ 已通过

### 决策
- **当前**：使用 MediatR 进程内事件总线
- **未来**：跨服务通信切换为 Kafka

### 理由
- 进程内总线性能最优（纳秒级）
- 不引入分布式事务复杂度
- 通过 `IEventBus` 接口抽象 → 切换 Kafka 不影响业务代码

### 事件契约
详见 [`07-event-driven-contract.md`](07-event-driven-contract.md)

---

## ADR-0004：双语言栈：C# .NET 8 + Python 3.11

**日期**：2026-09-21
**状态**：✅ 已通过

### 决策
- **C# .NET 8**：业务核心（API、Context、EventBus、Compliance）
- **Python 3.11**：AI Service（LLM 网关、Skill、嵌入、Agent 编排）

### 理由
- .NET 强类型适合金融业务的合规、审计、可追溯
- Python 生态（Semantic Kernel、LangGraph、HuggingFace）适合 AI
- gRPC 通信，类型契约稳定

### 后果
- 团队需具备双语言能力（或专项分工）
- 部署镜像增大（需同时打包 .NET runtime + Python runtime）

详见 [`../03-development/01-coding-standards.md`](../03-development/01-coding-standards.md)

---

## ADR-0005：LLM 主用国内模型（Qwen3/DeepSeek）

**日期**：2026-09-21
**状态**：✅ 已通过

### 决策
- **主用**：通义千问 Qwen3 / DeepSeek-V3（境内）
- **备选**：OpenAI GPT-4o（兜底）

### 理由
- 数据本地化合规（生成式 AI 暂行办法）
- 成本优势
- 中文金融语料更优

详见 [`../05-security-compliance/02-compliance-matrix.md`](../05-security-compliance/02-compliance-matrix.md)

---

## ADR-0006：数据库选型 PostgreSQL 16 + pgvector

**日期**：2026-09-21
**状态**：✅ 已通过

### 决策
- 主库：**PostgreSQL 16**（含 pgvector 扩展）
- 缓存：**Redis 7**
- 事件：**Kafka 3.x**（生产环境）

### 理由
- pgvector 单一数据库支持关系+向量，减少基础设施
- 金融业务 PostgreSQL 生态成熟（事务、ACID）
- 5 人团队运维友好（一个运维体系）

---

## ADR-0007：API 网关使用 Kong / APISIX

**日期**：2026-09-21
**状态**：🟡 评审中

### 待决策
- Kong（成熟、插件丰富）
- APISIX（云原生、性能强）
- Traefik（轻量）

---

## ADR-0008：强制人工回环（Human-in-the-Loop）

**日期**：2026-09-21
**状态**：✅ 已通过

### 决策
**所有高风险操作必须有人工回环**（参考 Ryt Bank A01）：

| 操作 | 回环要求 |
|------|---------|
| 转账 > 5000 元 | 必须 |
| 卡片额度调整 | 必须 |
| 理财产品申购/赎回 | 必须 |
| 跨场景联动（订蛋糕、订机票） | 必须 |
| 卡片挂失 | 必须 |
| 任何对外 API 调用 | 视金额 |

### 实现
- Guardrails Agent 强制拦截
- APP/IM 推送确认卡片
- 超时默认拒绝

### 依据法规
- 生成式人工智能服务管理暂行办法第 10 条
- 商业银行法第 6 条（存款自愿、取款自由）

---

## ADR-0009：可插拔 Agent 注册机制

**日期**：2026-09-21
**状态**：✅ 已通过

### 决策
新 Agent/功能通过实现接口 + 注册到 PluginRegistry 加入系统，**无需改动核心代码**。

### 接口定义
```csharp
public interface IAgentPlugin
{
    string Name { get; }
    string Version { get; }
    Task<PluginResponse> ExecuteAsync(PluginContext context);
}

public interface IBoundedContext
{
    string Name { get; }
    void Register(IEventBus bus, IServiceProvider sp);
}
```

详见 [`06-extension-points.md`](06-extension-points.md)

---

## ADR-0010：功能开关系统（FeatureFlag）

**日期**：2026-09-21
**状态**：🟡 评审中

### 决策
每个新功能必须有 FeatureFlag 控制：

- 默认关闭
- 灰度发布：内部用户 → 1% → 10% → 50% → 100%
- 一键回滚

### 实现
- 数据库存储规则
- Redis 缓存，10 秒 TTL
- 通过 AOP / Middleware 拦截

---

## 如何新增 ADR？

1. 复制 [`template.md`](template.md)（如不存在可参考 Nygard 模板）
2. 编号递增
3. 在 README.md 索引表中追加
4. 提交 PR → 全体 5 人 Review → 通过

---

## 关联文档

- **架构总览**：[`00-overview.md`](00-overview.md)
- **模块边界**：[`05-module-boundaries.md`](05-module-boundaries.md)
- **扩展点**：[`06-extension-points.md`](06-extension-points.md)