# 统一术语表（Glossary）

> **状态**：维护中 · **所有者**：架构师 + 业务开发 · **最后更新**：2026-10-01
> **目的**：消除跨团队沟通中的术语歧义

---

## A. 银行业务术语

| 中文 | English | 定义 |
|------|---------|------|
| 存款人 | Depositor | 在商业银行存入资金的客户 |
| 付款人 | Payer | 资金来源方 |
| 收款人 | Payee | 资金接收方 |
| 转账订单 | Transfer Order | 一次转账操作的指令 |
| 实时转账 | Real-time Transfer | 秒级到账的转账 |
| 普通转账 | Standard Transfer | 工作时间内到账 |
| 大额转账 | Large Transfer | 单笔 ≥ 5 万元（央行反洗钱标准） |
| 可疑交易 | Suspicious Transaction | 央行定义的需上报的交易 |
| 反洗钱 | Anti-Money Laundering (AML) | 防止通过金融系统洗钱 |
| 客户尽职调查 | Know Your Customer (KYC) | 验证客户身份的流程 |
| 风险测评 | Risk Profiling | 评估投资者风险承受能力 |
| 适当性管理 | Suitability Management | 确保产品与客户风险匹配 |
| 挂失 | Loss Report | 报告卡片丢失/被盗 |
| 止付 | Stop Payment | 停止某笔支付的执行 |
| 活期存款 | Demand Deposit | 可随时支取的存款 |
| 定期存款 | Time Deposit | 有固定期限的存款 |
| 理财产品 | Wealth Management Product | 银行发行的金融产品 |

---

## B. AI / LLM 术语

| 中文 | English | 定义 |
|------|---------|------|
| 大语言模型 | Large Language Model (LLM) | 大规模预训练语言模型 |
| 多智能体系统 | Multi-Agent System (MAS) | 多个自主 Agent 协作的系统 |
| 智能体 / 代理 | Agent | 能感知环境、做出决策、执行动作的实体 |
| 工具调用 | Function Calling / Tool Use | LLM 调用外部函数的能力 |
| 思维链 | Chain-of-Thought (CoT) | LLM 推理的中间步骤 |
| 检索增强生成 | Retrieval-Augmented Generation (RAG) | LLM + 知识库检索 |
| 嵌入 | Embedding | 文本的向量化表示 |
| 向量数据库 | Vector Database | 存储和检索向量的数据库 |
| 提示词 | Prompt | 输入给 LLM 的指令 |
| 提示词工程 | Prompt Engineering | 设计提示词的技术 |
| 微调 | Fine-tuning | 在预训练模型上继续训练 |
| Token | Token | LLM 处理文本的基本单位 |
| 推理 | Inference | 模型生成输出的过程 |
| 幻觉 | Hallucination | LLM 生成不准确内容 |
| 上下文窗口 | Context Window | LLM 单次可处理的最大长度 |
| 提示注入 | Prompt Injection | 恶意构造的提示词攻击 |
| 多模态 | Multi-modal | 同时处理多种输入（文本/图像/音频） |
| 强化学习 | Reinforcement Learning (RL) | 通过奖励信号训练 |
| 多智能体强化学习 | Multi-Agent RL (MARL) | 多 Agent 同时 RL |
| 角色扮演 | Role Playing | Agent 模拟特定角色行为 |
| 言语强化 | Verbal Reinforcement | 通过自然语言反馈改进决策 |
| 反思 | Reflection | Agent 评估自己行为的过程 |
| 辩论 | Debate | 多个 Agent 对同一问题争论 |

---

## C. 架构术语

| 中文 | English | 定义 |
|------|---------|------|
| 限界上下文 | Bounded Context | DDD 中一个独立的业务边界 |
| 聚合根 | Aggregate Root | 一个聚合中最重要的实体 |
| 实体 | Entity | 有唯一标识的对象 |
| 值对象 | Value Object | 无身份、仅由属性定义的对象 |
| 领域事件 | Domain Event | 业务中发生的重要事件 |
| 集成事件 | Integration Event | 跨 Context 通信的事件 |
| 防腐层 | Anti-Corruption Layer (ACL) | 隔离外部模型影响的中间层 |
| 共享内核 | Shared Kernel | 多个 Context 共享的代码 |
| 事件总线 | Event Bus | 异步分发事件的机制 |
| 命令查询职责分离 | CQRS | 分离读写操作的模式 |
| 事件溯源 | Event Sourcing | 用事件流表示状态变更 |
| 模块化单体 | Modular Monolith | 单一部署包但模块边界清晰 |
| 微服务 | Microservice | 独立部署的小型服务 |
| 编排 | Orchestration | 协调多个服务完成业务流程 |
| 编排器 | Orchestrator | 负责协调的组件 |
| 服务网格 | Service Mesh | 微服务间通信的基础设施层 |
| API 网关 | API Gateway | 统一 API 入口 |
| CQRS | Command Query Responsibility Segregation | 命令查询分离 |
| Saga | Saga | 跨服务事务管理模式 |

---

## D. 部署 / 运维术语

| 中文 | English | 定义 |
|------|---------|------|
| 容器 | Container | 标准化的应用打包单元 |
| Kubernetes | K8s | 容器编排系统 |
| 命名空间 | Namespace | K8s 中的逻辑隔离单位 |
| 副本 | Replica | 同一服务的多个实例 |
| 水平自动伸缩 | HPA (Horizontal Pod Autoscaler) | 根据负载自动增减副本 |
| 持续集成 | CI (Continuous Integration) | 自动化合并代码 |
| 持续部署 | CD (Continuous Deployment) | 自动化部署到生产 |
| GitOps | GitOps | 以 Git 为单一事实来源的运维 |
| 蓝绿部署 | Blue-Green Deployment | 两套环境切换 |
| 灰度发布 | Canary Release | 逐步放量的过程 |
| 回滚 | Rollback | 恢复到上一个版本 |
| 健康检查 | Health Check | 检查服务可用性 |
| 链路追踪 | Distributed Tracing | 跨服务调用追踪 |
| 链路标识 | Trace ID | 一次请求的唯一标识 |
| 跨度 | Span | 调用链中的一个步骤 |
| 指标 | Metric | 可聚合的数值度量 |
| 日志 | Log | 结构化事件记录 |
| 仪表板 | Dashboard | 监控数据可视化 |
| 告警 | Alert | 异常通知 |
| SLO | Service Level Objective | 服务等级目标 |
| SLA | Service Level Agreement | 服务等级协议 |

---

## E. 合规 / 安全术语

| 中文 | English | 定义 |
|------|---------|------|
| 反洗钱 | Anti-Money Laundering (AML) | 防止洗钱的法规要求 |
| 了解你的客户 | KYC (Know Your Customer) | 客户身份验证 |
| 客户尽职调查 | CDD (Customer Due Diligence) | 深入的客户身份和背景核查 |
| 增强型尽职调查 | EDD (Enhanced Due Diligence) | 高风险客户的额外核查 |
| 大额交易报告 | CTR (Currency Transaction Report) | 央行强制要求 |
| 实名认证 | Real-Name Authentication | 验证用户身份真实 |
| 多因素认证 | MFA (Multi-Factor Authentication) | 多种认证方式 |
| 数据脱敏 | Data Masking / De-identification | 隐藏敏感信息 |
| 加密 | Encryption | 数据编码保护 |
| 审计日志 | Audit Log | 不可篡改的操作记录 |
| 安全评估 | Security Assessment | 系统安全性评估 |
| 算法备案 | Algorithm Filing | 算法合规备案（网信办） |
| 安全评估 | Security Assessment | 强制性安全评估 |

---

## F. 项目内部代号

| 代号 | 含义 |
|------|------|
| **BankingAgent.Base** | 当前共享运行时：数据、安全、Agent 编排等 |
| **BankingAgent.Plugin.Sdk** | 当前插件公共契约 |
| **BankingAgent.Host** | 当前 .NET 8 宿主、API 与静态控制台 |
| **MockBank.Api** | 当前独立模拟核心银行服务，默认端口 5200 |
| **AI Service** | Target：未来可能引入的独立 AI 服务；当前仓库无 Python/FastAPI 服务 |
| **PluginRegistry** | 插件注册中心 |
| **Guardrails** | 合规拦截 |
| **Audit** | 横切审计 |
| **OBL** | Onboarding |
| **ITA** | Iteration to Transfer（暂时使用） |

---

## G. 项目结构代码

```
src/
├── BankingAgent.slnx
├── src/
│   ├── BankingAgent.Base/
│   ├── BankingAgent.Plugin.Sdk/
│   └── BankingAgent.Host/
│       └── wwwroot/                        # 当前静态控制台
├── plugins/
│   ├── BankingAgent.Plugin.Transfer/
│   ├── BankingAgent.Plugin.BillAnalysis/
│   ├── BankingAgent.Plugin.CardManagement/
│   └── BankingAgent.Plugin.Wealth/
├── mock-bank/MockBank.Api/
├── UnitTests/                              # xUnit 测试项目
├── E2ETest/                                # 独立可执行程序
├── StressTest/                             # 独立可执行程序
└── LoadTest/                               # 独立可执行程序
```

仓库当前没有 `src/agent-core`、`src/ai-service`、`AIService` 或 `Bootstrap` 项目。

---

## 关联文档

- **限界上下文**：[`01-bounded-contexts.md`](01-bounded-contexts.md)
- **领域模型**：[`02-domain-model.md`](02-domain-model.md)