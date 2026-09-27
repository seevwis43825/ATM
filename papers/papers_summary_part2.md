# 银行/金融智能体论文综述 - 续篇

---

## J. 财富管理 / 智能投顾（Wealth Management & Robo-Advisor MAS）

### J58 — Retrieval-Augmented Generation vs. Deterministic Tax Computation in Multi-Agent Financial Advisory
- **作者**：Brar A., Du J., Lor A., Seto K., Taylor E.（Royal Bank of Canada）
- **来源**：arXiv:2608.23908
- **核心方法**：2×2 ANOVA 设计；自定义资本利得计算引擎 + RAG 检索的市场建议库
- **关键结论**：税务优化引擎主效应显著（F(1,29)=9.17，p=.005）；RAG 主效应不显著；纯 RAG 条件描述均值最高（47.7%）
- **意义**：**RBC 实证 + 设计良好的因子实验**；提示 LLM 金融工程未必优于领域引擎

### J61 — DeepFund（详见 F61；本文也归入"实时智能投顾"）

### J62 — Wealth-Voyager: Navigating Intelligent Wealth Management with Multi-Agent Framework
- **作者**：Huang R., Zhao Z., Chen S., Wu X., Zhao J. L.（CityU HK 等）
- **来源**：GAIB 2025；arXiv:2410.18968
- **核心方法**：4 Agent（AssistHub 行为画像 / NewsCrawler 实时情报 / AlphaForge 组合优化 / DualAdvisor BDI 建议）；Monte Carlo 风险建模 + 事件驱动再平衡
- **关键结论**：自适应战术调整将边际收益转为更显著回报，同时降低波动
- **意义**：**BDI + 行为金融**结合的财富管理 MAS

### J68 — Agentic AI: Autonomy, Accountability, and the Algorithmic Society
- **作者**：Mukherjee A., Chang H. H.
- **来源**：arXiv:2502.00289
- **核心方法**：自治性 + 问责 + 算法社会三维框架
- **关键结论**：金融场景下的 Agentic AI 自治需要配套的问责制度设计
- **意义**：**金融 Agentic 治理理论基础**

### J69 — PeterAI: AI Multi-agent Framework for Personalized Investment Consultancy
- **作者**：Gadioli A. V. A. 等（Universidade Federal do Espírito Santo 等）
- **来源**：ICONIP 2025；arXiv: 待补
- **核心方法**：MAS-LLM 架构；专门 Agent（固定收益、股票、REITs、多市场基金）；WhatsApp 接口
- **关键结论**：7 个月、120 客户研究中，PeterAI 收益 8.29% ± 0.30% vs 人工顾问 2.57% ± 0.99%；Sharpe 2.67 vs −0.85
- **意义**：**长期真实部署实证**（不是回测）

### J70 — An end-to-end Multi-Agent AI System for Personal Finance
- **作者**：Pancholi S. S., Jaglan A., Makadia N., Doshi Y., Jafari A.
- **来源**：Neural Computing and Applications 2026（DOI: 10.1007/s00521-025-11749-7）
- **核心方法**：三组件（合成数据生成器 + 预算分析栈 + LLM 投资顾问）
- **关键结论**：预算分类器匹配强基线；顾问价格预测 ~60% 准确率
- **意义**：**个人金融端到端 MAS**

---

## K. 市场模拟 / 系统性风险 / 银行挤兑

### K02 — Modeling Trust and Liquidity Under Payment System Stress
- （详见 A02；同时归入 K）

### K42 — Contagion and Bank Runs in a Multi-Agent Financial System
- **作者**：Provenzano D.（University of Palermo）
- **来源**：多版本，2012/2013（Springer / UniPa）
- **核心方法**：复杂网络（complete/incomplete/random/scale-free/star）+ 蚂蚁群体行为（Kirman 蚂蚁模型）；WTD（挤兑前最大提款数）+ TTD（系统完全崩溃时间）
- **关键结论**：连接度增加 → WTD/TTD 恶化 → 违约风险上升；非对称结构优于对称
- **意义**：**MAS 银行挤兑经典**；是后续众多银行网络模型的引用基础

### K43 — An Agent Based Propagation Model of Bank Failures
- **作者**：Dias A. Â. M.（Universidade do Porto）
- **来源**：Springer 2014（IFIP AICT 2014）
- **核心方法**：三类型 Agent（银行、消费者、央行）；scale-free 银行间网络拓扑；NetLogo 模拟
- **关键结论**：特定配置下存在系统性风险；为监管者提供清偿率下限和市场监管建议
- **意义**：**学位论文级别深度银行挤兑 MAS**

### K44 — Systemic Risk Analysis of Multi-Layer Financial Network System
- **作者**：Gao Q.（Shanghai Lixin University of Accounting and Finance）
- **来源**：Entropy 2022（PMC9498085）
- **核心方法**：三层金融网络（bank-firm credit + asset-bank portfolio + interbank lending）；数值模拟
- **关键结论**：系统性风险最敏感于银行破产风险，其次是 firm credit default；银行集中度可同时起到阻断和传播作用
- **意义**：**多层金融网络 MAS**

### K45 — Bank Resolution Trade-Offs Under Coupled Liquidity and Credit Risks
- **作者**：研究团队（基于 Tedeschi et al.）
- **来源**：MDPI Entropy 2026
- **核心方法**：50 银行 + 500 厂商 + 5000 储户；两种流动性解决方案（负债扩张 vs 资产销售）+ 两种信用解决方案（债务延期 vs 债务减免）
- **关键结论**：资产销售策略在折价受限时优于负债扩张策略
- **意义**：**银行监管救助策略仿真**

---

## L. 加密货币 / Meme Coin 交易

### L37 — MountainLion: Multi-Agent RAG Framework for Cryptocurrency
- **作者**：研究团队
- **来源**：arXiv:2410.22327
- **核心方法**：专门 Agent（新闻、技术分析、链上指标）+ 多 Agent RAG
- **关键结论**：在加密市场优于单 Agent
- **意义**：**加密货币 LLM Agent** 的代表

### L51 — Adaptive Multi-Agent Bitcoin Trading System
- **作者**：Singhi A.
- **来源**：arXiv:2510.08068
- **核心方法**：LLM 专门化 Agent（技术、情绪、决策、Reflection）+ **言语反馈**（verbal feedback）机制
- **关键结论**：BTC 数据 2024.7–2025.4：量化 Agent 在牛市超过 B&H 30%，情绪驱动 Agent 在横盘市场从亏损转为 100% 收益；周反馈再贡献 31%
- **意义**：**言语反馈无须重训**的低成本 LLM 微调范式

### L52 — LLM-Powered Multi-Agent System for Automated Crypto Portfolio Management
- **作者**：Luo Y., Feng Y., Xu J., Tasca P., Liu Y.（UCL, NTU, Exponential Science）
- **来源**：arXiv:2501.00826
- **核心方法**：三模态专门 Agent（Crypto / News / Trading）+ 三种通信架构（hierarchical / collaborative / debate）+ 四种能力配置
- **关键结论**：52 周 2025 年回测：Hierarchical (Skill) 累计回报 +133.52%，Sharpe 1.502；Crypto Agent 是最关键组件（去除后减少 42.57 pp）
- **意义**：**模态融合 + 通信架构消融**最完整的工作

### L53 — Building Crypto Portfolios with Agentic AI
- **作者**：Castelli A., Giudici P., Piergallini A.（University of Pavia）
- **来源**：arXiv:2507.20468
- **核心方法**：Crew AI 协作架构；静态等权重 vs 滚动窗口 Sharpe 最大化
- **关键结论**：动态优化策略风险调整后回报显著优于静态策略（in/out-of-sample 均成立）
- **意义**：**Crew AI + MPT** 的实用加密 MAS

### L54 — Resisting Manipulative Bots in Meme Coin Copy Trading
- **作者**：Luo Y. 等（UCL, HKU）
- **来源**：arXiv:2601.08641
- **核心方法**：多模态 LLM + Chain-of-Thought 推理；针对操纵机器人的防御
- **关键结论**：在 meme coin 投资上平均回报 3%（扣交易摩擦）；优于 zero-shot 和统计基线
- **意义**：**对抗性 MAS**；对抗性金融场景下的 Agent 防御

### L55 — When AI Agents Meet MEV: Cross-Chain Arbitrage in the Agentic Economy
- **作者**：Ye W., Xu J., Wu Y.
- **来源**：arXiv:2609.17897
- **核心方法**：均值-方差效用 + 随机桥延迟 + 信念加权在线学习（Robbins-Monro）
- **关键结论**：ETH-Arbitrum 价差均值 0.044%（10 秒分辨率）；自适应路径选择比标准基线优 11%；中等随机化将 MEV 暴露降低 50%
- **意义**：**AI Agent 作为 MEV 搜索者** 的开创性建模

### L70 — CryptoTrade（详见 F70）

### L57 — Optimizing Market Making using MARL（详见 E57；BTC）

---

## 三、综合分析与趋势报告

### 3.1 论文分类统计

| 分类 | 论文数 | 占比 |
|------|-------|------|
| A. 银行核心与对话 AI | 5 | 9.4% |
| B. 信贷风险 | 5 | 9.4% |
| C. AML/反欺诈 | 5 | 9.4% |
| D. 量化交易/投资组合（MARL） | 6 | 11.3% |
| E. 高频交易/做市 | 4 | 7.5% |
| F. LLM 金融交易 MAS | 17 | 32.1% |
| G. 监管科技/合规 | 5 | 9.4% |
| H. 保险科技 | 4 | 7.5% |
| I. 区块链/DeFi | 4 | 7.5% |
| J. 财富管理/智能投顾 | 5 | 9.4% |
| K. 市场模拟/系统性风险 | 4 | 7.5% |
| L. 加密货币/Meme Coin | 5 | 9.4% |
| **总计** | **53** | **100%** |

> 注：某些论文在多分类下重复统计，因此分类合计 > 53。F 类最大（17 篇）反映 LLM Agent 浪潮；G、I、L 显示新兴前沿方向。

### 3.2 时间趋势

- **2018–2022**（约 5 篇）：JASA/JADE 平台 MARL、Agent-based 银行挤兑经典工作（K42、K43、D11）
- **2023–2024**（约 15 篇）：FinMem、FinAgent、FinCon 等 LLM-Agent 框架集中爆发；TradingAgents 出现
- **2024–2025**（高峰，约 25 篇）：TradingAgents/FinCon 等的扩展、Agentic AI 在银行/保险/合规广泛部署报告
- **2026–至今**（最新趋势）：批判性反思（The Alpha Illusion）、Agentic AI 自身保险（Insurance of Agentic AI）、跨链 MEV 套利

### 3.3 核心研究主题与趋势

#### 趋势 1：从规则引擎到 LLM-Agent 的范式跃迁

**早期**（2018–2023）研究集中于经典 MARL/ABM：
- K42 银行挤兑、E57/E64 做市、D11/D13 MARL 交易
- 共同特点：**有限理性 Agent + 数学严格性** + 仿真器（JADE/JASA/NetLogo）

**当下**（2024–2026）研究集中在 LLM-Agent：
- F05 TradingAgents、F28 FinCon、F38 FinAgent、F40 FinMem、F52 LLM 加密 MAS
- 共同特点：**角色专业化 + 自然语言推理 + 反思/辩论 + 长期记忆**
- 典型架构：Manager-Analyst 层次、Reflection 模块、Risk Team、Fund Manager

#### 趋势 2：金融业成为 Agentic AI 监管落地的"先行场景"

- A01 Ryt Bank 是全球首个监管批准的"对话式 AI 作为主要银行界面"
- A04 Kubam、C49 Atreya 等多家银行实证显示：**LLM 分诊/欺诈/合规 Agent 已可达到工业部署水准**
- G22/G24/G25 同期出现监管框架以应对 Agentic AI 风险
- 但 F29（Alpha Illusion）警示：当前 LLM 交易 Agent 公开证据不能视为部署证据

#### 趋势 3：从单 Agent 到多 Agent 协作的角色分工

- **F05 TradingAgents**：6+ 角色（基本/情绪/技术 + 牛熊研究员 + 风险 + 交易员 + 基金经理）
- **F28 FinCon**：经理-分析师层次 + CVaR 风险控制 + 概念化言语强化
- **F52 Luo**：3 模态 Agent × 3 通信架构 × 4 能力配置，**最完整的消融研究**
- **J62 Wealth-Voyager**：4 Agent（行为画像/新闻/组合/建议）+ BDI 框架

#### 趋势 4：金融基础设施的 MAS 化

- **支付系统**：A02 Amouzgar 跨支付中断信任建模；A18 银行实证
- **信贷**：B04、B10、B66 MASCA 信号博弈；B46 DeFi 治理
- **AML**：C09 AMLNet 知识驱动；C49 LLM 分诊；C63 真实部署
- **合规**：G22-G25 监管框架；H19-H22 保险框架
- **DeFi/链上**：I30-I33 自主链上 Agent；L55 MEV 套利

#### 趋势 5：从"工具"到"自主金融行动者"

- I30（Agent-to-Agent Finance）提出 Agent 进化三阶段：区块链自动化 → AI 辅助 → **自主**
- I32 提出 Agent 链上交互的 4 类架构（链上执行/链下代理/可验证链下计算/多 Agent 链上）
- A01 真正实现"AI Agent 直接执行金融操作"（监管批准）
- G65/H19 出现"为 Agent 自身设计保险"——Agentic AI 作为风险主体的趋势

### 3.4 研究空白（Gaps）

#### 空白 1：长周期/实时部署的证据严重不足

- F29 指出 LLM 交易 Agent 公开回测几乎都是 < 5 年且**未处理交易成本**
- F61 DeepFund 唯一实时（24 天）实证显示：**9 个 LLM 仅 1 个（Grok 3）盈利**；保守现金管理是核心
- J69 PeterAI 是少数 7 个月 + 120 客户的真实部署
- **结论**：行业需要更多长期、低延迟、真实部署的研究

#### 空白 2：跨 Agent 问责与合规审计不成熟

- G23 提出的 MAS 监管框架仍是"立场"性质
- F29 强调"语言自信 ≠ 不可交易概率"
- G24 ReguSim 显示"提示词级合规 ≠ 可执行合规"
- **结论**：需要更严格的审计追踪、可解释决策、跨 Agent 责任分配

#### 空白 3：监管与法律框架滞后

- A01 Ryt Bank 是例外；其他多停留在 PoC / 模拟阶段
- G65/H19 反映**Agentic AI 法律责任**问题尚未解决
- **结论**：金融监管需对 Agentic AI 的自治水平、操作权威、权限暴露有清晰分级（参见 G65 5 维风险状态）

#### 空白 4：跨链 / 跨市场协同尚未充分探索

- I32 综述指出**机制设计 + MARL + 异步区块链**是近期最有价值方向
- L55 跨链 MEV 仅是初步工作
- **结论**：跨链 Agent 协同、跨监管管辖区 Agent 合规是开放问题

#### 空白 5：方法论与工程瓶颈

- D11 ABF 工具链（JASA/JADE）已 15+ 年未更新
- F61 实测显示：**API 成本与推理延迟阻碍实时部署**
- G24 显示 **RAG 仅用作知识检索并不足够**——需要 Agent 工作流 + 工具调用 + 状态机
- **结论**：需要更高效的推理基础设施和工程工具

### 3.5 推荐研究方向

#### 推荐 1：面向部署的"端到端 Agent 系统"

结合多 Agent 协作（角色分工）+ 严格审计追踪 + 人类回环 + 监管友好接口。
> 参考：A01 Ryt Bank（银行）、J69 PeterAI（投顾）、C63 Jano AML

#### 推荐 2：稳健的 LLM-MARL 混合决策（F46 + HRL）

用 LLM 进行高层推理、MARL 做底层决策；FLAG-Trader（F67）已显示小模型+RL 微调胜过 GPT-4。
> 参考：F67 FLAG-Trader、D14 EvoNash-MARL

#### 推荐 3：跨域金融基础设施的 Agent 协同

跨支付 / 跨链 / 跨监管辖区的 Agent 协作模型。
> 参考：I32、I30、L55

#### 推荐 4：Agentic AI 自身的金融风险建模

为 Agent 自身设计保险、定价、监管框架（5 维风险状态）。
> 参考：G65、H19、H20、H21

#### 推荐 5：金融 MAS 的标准化基准

类似 F29 提出的 P1–P6 报告协议、FinBen 等基准。
> 参考：F29、D16 综述、ReguBench

### 3.6 整体洞察

1. **MAS 在金融领域的应用远超传统意义上的"交易机器人"**——已涵盖核心银行、监管、保险、DeFi、市场基础设施
2. **LLM-Agent 是当前主流，但经典 MARL/ABM 在数学严格性上仍不可替代**（如 D14 EvoNash、E27 HFT 理论）
3. **从仿真到部署是最大鸿沟**——F61 DeepFund 提醒：实时 vs 回测差距巨大
4. **Agent 自治与监管的对立统一**——A01 vs G65 展示了 Agent 自治的天花板与地板
5. **金融 MAS 研究正在分化**：
   - **理论派**：博弈论、机制设计、MARL 收敛性
   - **应用派**：LLM 框架、跨域场景、垂直行业
   - **反思派**：批判性工作、评估协议、安全治理

### 3.7 重要参考文献与里程碑

| 里程碑 | 论文 |
|--------|------|
| AML MAS 经典 | C63 Jano（Alexandre 2023，ESA） |
| 银行挤兑 MAS 经典 | K42 Provenzano 2012/2013 |
| LLM 交易 Agent 综述 | F29 Alpha Illusion（2026 立场）+ D16 LLM Trading Survey（2024） |
| TradingAgents | F05（AAAI 2025） |
| FinCon NeurIPS 主会 | F28（2024） |
| Ryt Bank 首个监管批准 | A01（EMNLP 2025 Industry） |
| 跨链 MEV 套利 MAS | L55（2026） |
| Agentic AI 自身保险 | G65/H19/H20（2026） |
| JaxMARL-HFT | E26（ICAIF 2025） |

---

**附录：文件清单**

- `papers/papers_summary.md` —— 本文档前半部分（A-I 类）
- `papers/papers_summary_part2.md` —— 本文档后半部分（J-L 类 + 综合分析）
- `papers/papers_list.txt` —— 所有论文文件清单
- `papers/pdfs/*.pdf` —— 53 篇有效论文 PDF（+ 1 篇重复标记为 .dup）

**总下载量**：约 165 MB
**覆盖年份**：2018–2026（早期 ABM/MARL → 当下 LLM-Agent/Agentic AI）
**arXiv 占比**：约 85%
**期刊/会议占比**：约 15%（Nature, ScienceDirect, IEEE, ACM, NeurIPS, AAAI, AAMAS, ICAIF, EMNLP, WWW 等）