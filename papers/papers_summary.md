# 银行智能体与金融智能体：多智能体系统（MAS）论文综述

**整理日期**：2026-09-21
**论文总数**：53 篇有效论文 + 1 篇重复（保留作为存档）
**覆盖范围**：银行核心流程、信贷风控、AML 反洗钱、监管科技、量化交易、高频交易、投资组合管理、智能投顾、保险科技、区块链/DeFi、市场模拟、银行挤兑与系统性风险等

---

## 论文分类与编号一览

| 编号 | 类别 | 篇数 |
|------|------|------|
| A | 银行核心流程与零售银行对话 AI | 5 |
| B | 信贷风险评估 | 5 |
| C | 反洗钱（AML）/反欺诈 | 5 |
| D | 量化交易/投资组合管理（MARL） | 6 |
| E | 高频交易/做市 | 4 |
| F | LLM 驱动的金融交易多智能体 | 12 |
| G | 监管科技/合规 | 5 |
| H | 保险科技 InsurTech | 4 |
| I | 区块链 / DeFi / 智能合约 | 4 |
| J | 财富管理 / 智能投顾 | 4 |
| K | 市场模拟 / 系统性风险 / 银行挤兑 | 4 |
| L | 加密货币 / Meme Coin 交易 | 5 |

---

## A. 银行核心流程与零售银行对话 AI（Banking Core & Conversational AI）

### A01 — Banking Done Right: Redefining Retail Banking with Language-Centric AI
- **作者**：Chua X. J. 等（Ryt Bank / ILMU 团队）
- **来源**：EMNLP 2025 Industry Track；arXiv:2510.07645
- **核心方法**：基于自研 LLM（ILMU）+ 4 个 LoRA 适配的智能体（Guardrails、Intent、Payment、FAQ）；首次在全球范围内实现"对话式 AI 作为主要银行界面"的监管落地
- **关键结论**：替代多屏工作流为单对话；人类回环（human-in-the-loop）+ 无状态审计架构提供纵深防御
- **意义**：首个**经监管批准**的 AI 直接驱动核心金融操作的部署，是 LLM-Agent 银行核心的最强工业落地证据

### A02 — Modeling Trust and Liquidity Under Payment System Stress: A Multi-Agent Approach
- **作者**：Masoud Amouzgar（Sharif University / bluBank）
- **来源**：arXiv:2602.16186
- **核心方法**：MAS 框架，将支付中断 → 信任损耗 → 阈值门控提款行为形式化；基于 Watts–Strogatz 小世界网络
- **关键结论**：证明在记忆持久性和阈值门控的轻度条件下，挤兑压力峰值可**严格晚于**系统技术恢复期（"状态绿 ≠ 风险解"）
- **意义**：连接支付可靠性与存款人行为的桥接模型，对央行/支付监管有直接启示

### A03 — CyberAId: AI-Driven Cybersecurity for Financial Service Providers
- **作者**：Fatouros G. 等
- **来源**：arXiv:2605.01892（立场论文）
- **核心方法**：混合 MAS 平台：主代理协调层 + LLM 子代理 + 隐私保护联邦
- **关键结论**：金融 SOC 三大瓶颈——覆盖范围窄、警报淹没、警报未追查；提出"基于技能的代理适应"研究方向
- **意义**：将金融 SOC + MAS + 联邦学习 + 数字孪生整合的开源化定位

### A04 — Agentic AI for Autonomous, Explainable, and Real-Time Credit Risk Decision-Making
- **作者**：Kubam C. S.（独立研究者，集成架构师）
- **来源**：IJISAE 2024；arXiv:2601.00818
- **核心方法**：Data Acquisition / Risk Scoring / Decision / Explainability / Feedback 五 Agent 协同；强化学习 + 自然语言推理 + XAI + 实时数据流
- **关键结论**：相比传统评分模型，决策速度、透明度、响应性显著提升；存在模型漂移、解释一致性、监管不确定性等局限
- **意义**：Agentic AI 在数字借贷、BNPL、移动信贷场景中的端到端参考架构

### A18 — AI Development Trajectory in Polish Bank
- **作者**：Chomiak-Orsa I., Wójcik F., Hudaszek K.
- **来源**：ECAI 2025（Lecture Notes in Networks and Systems, Vol 1643, Springer）
- **核心方法**：质性研究 + 案例研究（Credit Agricole Polska 现有 AI 工具 WALL-E、ASIA + 假设 agentic 集成）
- **关键结论**：MAS + LLM 在文档处理、客户端沟通、客服等运营环节效率提升明显；建议分阶段、模块化实施
- **意义**：**银行内部实际部署案例**，是少见的非硅谷银行实证研究

---

## B. 信贷风险评估（Credit Risk Assessment）

### B10 — MASCA: LLM based-Multi Agents System for Credit Assessment
- **作者**：Jajoo G. (Kairos AI), Chitale P. A. (Microsoft Research), Aggarwal S.
- **来源**：arXiv:2507.22758
- **核心方法**：层次化 LLM MAS，融入信号博弈理论（signaling game）建模借/贷双方的策略交互；对比学习用于风险/回报评估；含偏见分析
- **关键结论**：在信贷评分任务上优于单 LLM 基线；信号博弈提供了层次 MAS 交互的理论基础
- **意义**：少数从**博弈论**视角严肃处理 MAS 信贷决策的工作

### B04 — Agentic AI for Autonomous, Explainable, and Real-Time Credit Risk Decision-Making
- （详见 A04；同时归入 B 类）

### B06 — Automate Strategy Finding with LLM in Quant Investment
- **作者**：Kou Z., Yu H., Luo J. 等（HKUST 等）
- **来源**：arXiv:2409.06289
- **核心方法**：三阶段框架——LLM 生成 alpha 因子候选 → 多模态 MAS 评估过滤 → 动态权重优化
- **关键结论**：在 SSE50 上 53.17% 累计回报（2023.1–2024.1），优于基线；强调 Mixture-of-Experts、自适应架构、迁移学习为未来方向
- **意义**：**中文 + 美股市场双验证**的 alpha 因子 MAS

### B66 — LLM Credit Risk Assessment（标题示意类）
- arXiv:2403.12508；与 MASCA 同期同主题工作，关注 LLM 在信贷中的偏见与评估
- **意义**：与 MASCA 互补

### B46 — Multi-Agent Influence Diagrams for DeFi Governance
- **作者**：Nag A., Gupta S., Sinha S., Datta A.
- **来源**：arXiv:2402.15037
- **核心方法**：用影响图（influence diagrams）建模 DeFi 协议治理中多利益相关方决策；多 Agent 框架
- **关键结论**：将传统决策论工具（influence diagram）现代化应用于去中心化金融治理
- **意义**：连接**信贷决策论**与**链上治理**的桥梁

---

## C. 反洗钱（AML）/反欺诈

### C09 — AMLNet: A Knowledge-Based Multi-Agent Framework
- **作者**：Huda S., Foo E., Jadidi Z., Newton M. H. A., Sattar A.（Griffith University, U. Newcastle）
- **来源**：Expert Systems with Applications 2025；arXiv:2509.11595
- **核心方法**：监管感知交易生成器（AUSTRAC 75% 对齐）+ 集成学习检测管线
- **关键结论**：生成 1,090,173 笔交易，F1=0.90；在 SynthAML 数据集上泛化良好
- **意义**：首个**公开可用 + 监管对齐**的 AML 合成数据集（DOI 已发布）

### C49 — Helping Customers in Distress: An LLM-powered Agent that Converses, Probes, and Routes
- **作者**：Atreya A., Wanger S. S. 等
- **来源**：arXiv:2605.16268
- **核心方法**：LLM 分诊 Agent；合成"数字孪生"客户做大规模评估；分层安全护栏
- **关键结论**：相比人工分诊，分类准确率提升 30.6%；满足 FINRA/SEC/合规要求
- **意义**：银行业**已部署**的 LLM Agent 案例，处理欺诈/骗局/争议案件分诊

### C54 — Multi-Agent AI Systems for Secure, Transparent, and Compliant Fraud Surveillance in Cross-Border FinTech
- **作者**：Ibitoye J. S.（Southeast Missouri State University）
- **来源**：IJRPR 2025
- **核心方法**：行为画像、风险评分、异常检测、监管执行四大 Agent；联邦学习 + XAI + SWIFT/ISO 20022 适配
- **关键结论**：改善跨境 FinTech 场景下的检测精度、解决时间、监管信任
- **意义**：少数覆盖**跨境 FinTech + 异构监管**的 MAS 论文

### C57 — AML via Knowledge-Based Multi-Agent System（Alexandre & Balsa 2015）
- **作者**：Alexandre C., Balsa J.
- **来源**：SciTePress 2015
- **核心方法**：基于 BDI 模型的多智能体 AML 系统；处理交易量与规则优化两大难题
- **关键结论**：通过智能过滤、异常分析、规则学习减少人工工作量
- **意义**：**AML MAS 经典基础工作**

### C63 — Incorporating machine learning and a risk-based strategy in an AML multiagent system
- **作者**：Alexandre C. 等
- **来源**：Expert Systems with Applications 2023
- **核心方法**：Jano 系统：数据挖掘 + 风险策略 + BDI 智能体；2 年真实数据（30–32M 交易）
- **关键结论**：F1 显著优于银行已有系统；76% 的"漏报"被新方法识别
- **意义**：**真实银行部署实证** + 显著超越传统系统

---

## D. 量化交易/投资组合管理（MARL）

### D11 — An Agent-Based Model With Realistic Financial Time Series
- **作者**：Faria L. G. de（Coventry University London）
- **来源**：arXiv:2206.09772
- **核心方法**：基于 JASA/JABM 工具包的 ABM；Basel III 合规约束；订单驱动市场模拟
- **关键结论**：系统能复现 stylized facts（波动聚集、厚尾等）；JASA 适合复杂 ABM 实验
- **意义**：**工具链**+**金融监管约束**集成的实证 ABM 范式

### D12 — Recursive Multi-Agent Trading System (RMATS)
- **作者**：RMATS 团队
- **来源**：arXiv:2605.25311
- **核心方法**：异构信号生成器（GPR、动量、HMM regime、CVaR）+ 形式化递归协调协议（AgentMessage schema，ε=0.005）
- **关键结论**：GPR 触发断路器（+0.045 Sharpe）；递归协议架构合理但边际收益小（Δ Sharpe = −0.002）
- **意义**：**递归式而非单轮** MAS 协调的早期重要尝试

### D13 — Optimizing Trading Strategies Using Multi-Agent RL: CPPI/TIPP-MADDPG
- **作者**：Zhang H., Shi Z., Hu Y., Ding W., Kuruoğlu E. E., Zhang X.-P.（清华深圳）
- **来源**：arXiv:2303.11959
- **核心方法**：将 CPPI 与 TIPP（投资组合保险策略）融合到 MADDPG；100 只真实股票
- **关键结论**：CPPI-MADDPG/TIPP-MADDPG 持续优于传统对应策略
- **意义**：**传统风控策略 + MARL**结合的代表性工作

### D14 — EvoNash-MARL: Closed-Loop MARL for Equity Allocation
- **作者**：Jia C. 等（UConn, 西安理工, 复旦, Iowa State）
- **来源**：arXiv:2604.10911
- **核心方法**：多智能体策略种群 + 博弈论聚合 + 约束感知验证 + walk-forward
- **关键结论**：120 月 walk-forward，年化 19.6% vs SPY 11.7%；统计显著性不强但稳健
- **意义**：**执行层面的可部署性**优于统计层面的过拟合

### D26 — JaxMARL-HFT（详见 E 高频）

### D16 — Large Language Model Agent in Financial Trading: A Survey
- **作者**：Ding H. 等
- **来源**：arXiv:2408.06361（53 引）
- **核心方法**：分类——news-driven、reflection-driven、debate-driven、RL-driven、alpha mining
- **关键结论**：几乎所有 Agent 在日频；HFT 被推理延迟排除；中位回测期仅 1.3 年
- **意义**：**领域第一篇系统综述**

---

## E. 高频交易/做市

### E26 — JaxMARL-HFT: GPU-Accelerated MARL for HFT
- **作者**：Mohl V. 等（Oxford, UCLA）
- **来源**：ICAIF 2025；arXiv:2511.02136
- **核心方法**：JAX 加速的 MARL 环境，支持异构 Agent；IPPO 训练做市 + 订单执行
- **关键结论**：训练时间减少 240×；1 年 LOB 数据（4 亿订单）；开源
- **意义**：**首个开源 GPU 加速 MARL HFT 环境**

### E27 — Reinforcement Learning in High-frequency Market Making
- **作者**：Yan Y., Deng Z.（Princeton）
- **来源**：arXiv:2407.21025
- **核心方法**：将现代 RL 理论与连续时间高频金融经济学桥接；两玩家一般和博弈 + Nash Q-learning
- **关键结论**：随采样频率 Δ→0，Nash 均衡收敛到连续时间博弈均衡；采样频率与误差/复杂度权衡
- **意义**：**理论分析**而非方法论工作，少数建立 RL-HFT 严格数学基础的论文

### E64 — Performance of Deep RL for HFT Market Making on Tick Data
- **作者**：Xu Z., Cheng X., He Y.（Peking University）
- **来源**：AAMAS 2022
- **核心方法**：Dueling Double DQN（D3QN）+ 新颖奖励函数；真实 tick 数据
- **关键结论**：双 Agent 情境下，D3QN Agent 经训练学习到**更窄报价**以提高成交概率
- **意义**：AAMAS 上少数 HFT 做市的实证工作

### E57 — Optimizing Market Making using Multi-Agent RL（Bitcoin）
- **作者**：Patel Y.
- **来源**：arXiv:1812.10252
- **核心方法**：宏观 Agent（买卖/持有决策）+ 微观 Agent（限价单簿内下单）；应用于 BTC
- **关键结论**：RL 在复杂 HFT 环境中可行
- **意义**：**MAS HFT 早期经典**

### E56 — Adaptive Multi-Strategy Market-Making Agent For Volatile Markets
- **作者**：Raheman A. 等
- **来源**：arXiv:2204.13265（AGI 2022 投稿）
- **核心方法**：AMSA 框架——多子 Agent 各自实现不同策略，根据市场状态动态选择
- **关键结论**：长期"正 alpha"高概率（在合适超参下）
- **意义**：**做市策略自适应选择**的代表

---

## F. LLM 驱动的金融交易多智能体

### F05 — TradingAgents: Multi-Agents LLM Financial Trading Framework
- **作者**：Xiao Y., Sun E., Luo D., Wang W.
- **来源**：AAAI 2025 Workshop；arXiv:2412.20138
- **核心方法**：受真实交易公司启发的多角色 LLM Agent（基本面/情绪/技术/牛熊研究员/风控/交易员/基金经理）
- **关键结论**：AAPL/AMZN/GOOGL 2024 Q1：累计回报 ≥23.21%，Sharpe 高达 8.21
- **意义**：**最具影响力的 LLM 交易 MAS 框架**，GitHub 高 star

### F07 — HedgeAgents: A Balanced-aware Multi-agent Financial Trading System
- **作者**：HedgeAgents 团队
- **来源**：WWW 2025 Companion；arXiv:2502.13165
- **核心方法**：股票/外汇/比特币专家 Agent + 基金经理 Agent（对冲导向）
- **关键结论**：3 年回测 400% 总回报；优于 FinAgent、FinGPT 等
- **意义**：**对冲视角**的金融 MAS

### F08 — To Trade or Not to Trade: An Agentic Approach to Estimating Market Risk
- **作者**：研究团队（AWS/NVIDIA 资助）
- **来源**：arXiv:2507.08584（Opus 2025）
- **核心方法**：LLM 进行 SDE 发现 + 风险估计 + 交易决策；模型感知风险
- **关键结论**：模型知情风险指标提升交易决策；存在 LLM 跨任务能力差异
- **意义**：**LLM 用于数学建模 + 金融决策**的开创性工作

### F12 — Recursive Multi-Agent Trading System (RMATS)
- （详见 D12；同时归入 F）

### F14 — EvoNash-MARL（详见 D14；同时归入 F）

### F28 — FinCon: Synthesized LLM MAS with Conceptual Verbal Reinforcement
- **作者**：Yu Y. 等（Stevens / 复旦 / The Fin AI）
- **来源**：NeurIPS 2024；arXiv:2407.06567
- **核心方法**：经理-分析师层次 + 双层风险控制（CVaR + 概念化言语强化）；跨期更新投资信念
- **关键结论**：单股交易和组合管理上优于 FinGPT、FinMem、FinAgent 等基线
- **意义**：**NeurIPS 主会论文**；首创"概念化言语强化"

### F29 — The Alpha Illusion: Reported Alpha from LLM Trading Agents Should Not Be Treated as Deployment Evidence
- **作者**：Ye Y., Han J., Hu A. 等（复旦等）
- **来源**：arXiv:2605.16895
- **核心方法**：立场论文；提出 P1–P6 最小报告协议
- **关键结论**：当前 LLM 交易 Agent 公开证据不能区分"真正的预测能力 vs 时间污染/摩擦未建模/短期 Sharpe 不确定性"
- **意义**：**重要的批判性工作**，警示 LLM-MAS 交易研究的过度炒作

### F35 — TradingAgents (AAAI 2025 Workshop)
- （与 F05 同论文不同版本；arXiv:2410.18968）
- **意义**：扩展版本

### F36 — FinRobot: An Open-Source AI Agent Platform for Financial Applications using LLMs
- **作者**：Yang H. (Bruce) 等（AI4Finance Foundation）
- **来源**：arXiv:2402.01129 / arXiv:2412.06219
- **核心方法**：分层架构 + Financial Chain-of-Thought + LLMOps/DataOps；动态选择 LLM 策略
- **关键结论**：开源平台；多任务覆盖（报告生成、交易、问答）
- **意义**：**最广泛使用的金融 LLM Agent 开源平台之一**

### F38 — FinAgent: A Multimodal Foundation Agent for Financial Trading
- **作者**：Zhang W. 等（Nanyang Technological University）
- **来源**：KDD 2024；arXiv:2410.08061
- **核心方法**：首个多模态金融 Agent（工具增强 + 多样化 + 通才）
- **关键结论**：多模态（文本 + 价格 + 视觉图表）融合，单资产交易优于 FinMem
- **意义**：**KDD 2024**；多模态金融 Agent 标杆

### F40 — FinMem: Performance-Enhanced LLM Trading Agent with Layered Memory
- **作者**：Yu Y., Li H. 等（Stevens Institute）
- **来源**：AAAI 2024 Spring Symposium；arXiv:2311.13743
- **核心方法**：三层记忆 + 性格化设计 + 自适应风险
- **关键结论**：TSLA 等波动资产回测 34.6% 累计回报；类人认知衰减与强化机制
- **意义**：**LLM 交易 Agent 基础工作**

### F39 — TradingGPT: Multi-Agent System with Layered Memory and Custom Decay
- **来源**：arXiv:2412.17408
- **核心方法**：三层自定义衰减记忆 + Agent 间辩论 + 个性化交易特征
- **关键结论**：整合历史数据与实时信号，提升决策
- **意义**：TradingAgents 的前身之一

### F59 — FinVision: A Multi-Agent Framework for Stock Market Prediction
- **作者**：Fatemi S., Hu Y.（UIC）
- **来源**：arXiv:2411.02557
- **核心方法**：多模态金融数据（文本 + 图表）+ 反射模块（review 过去交易信号、视觉线索）
- **关键结论**：反思式多 Agent 提升股市预测
- **意义**：**视觉金融反射**的代表

### F60 — StockAgent: LLM-based Stock Trading in Simulated Real-world Environments
- **作者**：Zhang C. 等（Rutgers, 北大, 利物浦等）
- **来源**：TIST 2025；arXiv:2407.18957
- **核心方法**：多 Agent 模拟投资者行为（保守/激进/平衡/成长型）；外部事件（财报、BBS、利率）
- **关键结论**：LLM 性格影响行为；GPT vs Gemini 表现差异显著；外部因素显著影响交易
- **意义**：**模拟真实环境的金融 LLM Agent 行为研究**

### F61 — DeepFund: Time Travel is Cheating
- **作者**：HKUST Dial 等
- **来源**：NeurIPS 2025；arXiv:2505.11065
- **核心方法**：**实时**基金投资基准（无回测数据泄漏）；9 个 LLM × $100K 起始资金
- **关键结论**：24 个交易日仅 Grok 3 取得正回报（+1.1%，靠**保守现金管理 ~60% 储备**而非预测）
- **意义**：**L2F / 实时实证**，揭示回测-实盘巨大差距

### F65 — AlphaFin（详见 alpha 因子类）
- **意义**：股票回测 + 多 Agent 报告生成

### F67 — FLAG-Trader: Fusion LLM-Agent with Gradient-based RL for Financial Trading
- **作者**：Li X. 等
- **来源**：arXiv:2502.11433
- **核心方法**：135M 参数 LLM + PPO 微调；用 Sharpe 变化作为 RL 奖励
- **关键结论**：JNJ SR=3.344（vs B&H 1.343），BTC SR=1.734（vs 0.683）；小模型也能胜过 GPT-4
- **意义**：**LLM+RL 微调**而非纯 prompt 的代表

### F70 — CryptoTrade: Multi-Agent Reflective Framework for Cryptocurrency
- **作者**：Li D. 等
- **来源**：arXiv:2402.18439
- **核心方法**：反射机制分析先验交易结果、改进日级决策
- **关键结论**：在多种加密货币和市场条件下优于传统策略和时间序列基线
- **意义**：**加密货币 LLM Agent** 早期工作

---

## G. 监管科技/合规

### G23 — Enabling Regulatory Multi-Agent Collaboration: Architecture, Challenges, and Solutions
- **作者**：Hu Q., Wang Y., Gao Y., Su Z., Du L.（西安交通大学）
- **来源**：arXiv:2509.09215
- **核心方法**：区块链启用的层次架构（agent / blockchain / regulatory）—行为追溯仲裁 + 动态信誉评估 + 恶意行为预测
- **关键结论**：为大规模 Agent 生态系统提供可信、可扩展的监管机制基础
- **意义**：**少数系统级"agent 监管 agent"**的工作

### G24 — ReguSim: Evaluating LLM Agent Rule Grounding in Financial Compliance
- **作者**：研究团队
- **来源**：arXiv:2608.19974
- **核心方法**：可控环境 + 程序生成基准 ReguBench；trader/monitor/bridge 三任务
- **关键结论**：激励和人格框架改变被拒交易尝试；"提示词级合规"≠"可执行合规"
- **意义**：**合规行为而非合规语言**的实证

### G25 — TRIAG: Tri-reinforced Infused Generative Agents for Financial Risk Compliance
- **作者**：Sheikh R., Miah S. J.
- **来源**：Intelligent Systems with Applications 2026
- **核心方法**：3 个 LLM Agent（Alpha=Orchestrator, Beta=Policy, Gamma=Horizon Scanning）+ 层次 MARL
- **关键结论**：F1=0.93；推理成本降低 96%
- **意义**：**FinTech 合规 Agent** + 设计科学研究方法

### G22 — Agentic AI and Retrieval-Augmented Models in Straight-Through Underwriting
- **作者**：Richardson R., Meyers J., Hartman B., Sandberg D.（BYU）
- **来源**：arXiv:2607.07858
- **核心方法**：Agentic RAG 流水线（目标检索 + 第三方数据检查 + 多步规则评估）；合成环境对比 3 类管线
- **关键结论**：多 Agent 系统在多步和缺失信息场景下提升最大
- **意义**：**直通车核保**（straight-through underwriting）实证

### G65 — AI-Native Insurance for Agentic AI: Pricing, Underwriting, Automation
- **作者**：Zhu Q.（NYU）
- **来源**：arXiv:2607.13230
- **核心方法**：5 维风险状态（自治水平、操作权威、权限暴露、治理成熟度、依赖集中度）+ 约束优化
- **关键结论**：建立"可保险性区域"，揭示治理认证阈值；保险作为部署治理机制
- **意义**：**为 Agentic AI 自身设计保险合约**——开创性

---

## H. 保险科技 InsurTech

### H19 — Insurance of Agentic AI
- **作者**：研究团队
- **来源**：arXiv:2606.05449
- **核心方法**：5 级 Agentic 能力分类（辅助→工具协同→自主数字→多智能体→赛博物理）；精算框架 + 分层保险架构
- **关键结论**：单一产品不可覆盖；需混合 E&O + 网络 + 性能保证 + AI 责任
- **意义**：**保险产品视角**下的 Agentic AI 风险全景

### H20 — AI-Native Insurance for Agentic AI
- （详见 G65）

### H21 — Insurance as AI Risk Infrastructure: A Generative-Agent Simulation
- **作者**：Yuan Y., Wei D. 等
- **来源**：arXiv:2608.15181
- **核心方法**：LLM-driven Agent-based 社会仿真（LABSS）；社会-经济框架通过保险转移 AI 残余金融后果
- **关键结论**：降低企业级金融敞口、加速 AI 工具采纳、提高清偿能力
- **意义**：**LLM Agent 仿真评估 AI 保险影响**

### H22 — Agentic AI in Insurance: Moving Beyond Generative AI to Autonomous Decision-Making
- **作者**：研究团队
- **来源**：AIJCST
- **核心方法**：Agentic Insurance Intelligence Framework（AIIF）——多层结构；定性 + 定量对比
- **关键结论**：风险评估准确率提升 45%，欺诈检测时间降低 30%
- **意义**：**保险业 Agentic AI 综述类**

---

## I. 区块链 / DeFi / 智能合约

### I30 — Agent-to-Agent Finance: Blockchain Payments and Trust Infrastructure
- **作者**：研究团队
- **来源**：arXiv:2607.00245
- **核心方法**：三阶段演进（区块链自动化 → AI 辅助链上交互 → 自主 Agent 链上交互）；委托代理经济学
- **关键结论**：最直接 Agent 间金融实验室是 DeFi 而非传统金融（因后者需 KYC/AML/合规）
- **意义**：**A2A 金融**的理论基础

### I31 — Stablecoin Design with Adversarial-Robust MAS via Trust-Weighted Signal Aggregation
- **作者**：研究团队
- **来源**：arXiv:2601.22168
- **核心方法**：MVF-Composer——信任加权 Mean-Variance Frontier 储备控制器 + Stress Harness
- **关键结论**：黑天鹅冲击下峰值偏离降低 57%，恢复时间缩短 3.1×；信任层贡献 23% 稳定增益
- **意义**：**DeFi 储备管理 + 对抗鲁棒 MAS**

### I32 — Autonomous Agents on Blockchains: Standards, Execution Models, Trust Boundaries
- **作者**：研究团队
- **来源**：arXiv:2601.04583
- **核心方法**：系统性综述（177 篇 + 14 系统文档）；按架构（链上执行/链下代理+链上结算/可验证链下计算/多 Agent 链上交互）分类
- **关键结论**：MAS+区块链+治理三维度相互依赖；MARL + 机制设计在异步区块链中是近期最有价值方向
- **意义**：**自主链上 Agent 系统综述**

### I33 — Multi-Agent Influence Diagrams for DeFi Governance
- （详见 B46；同时归入 I）