# 多银行核心系统与多数据库设计

> **状态**：已实现（可运行、可验证）
> **所有者**：架构师 / 业务开发
> **最后更新**：2026-10-20
> **相关代码**：`src/mock-bank/MockBank.Api/Data/`、`src/mock-bank/MockBank.Api/Domain/Banks.cs`

---

## 1. 为什么要模拟多个银行

真实世界里，一个自然人可以在多家银行开户。每家银行是**独立的法人核心系统**，拥有自己的客户主档、账户表、卡表与流水库，彼此之间只通过清算系统往来。

本项目要演示「跨行卡片」（一个客户在多家银行各有一张卡，一次查询全部返回），就必须照搬这个结构：**一个进程内模拟多个银行核心系统，每个核心系统一个独立数据库**。

如果只在单库里加一个 `bank_code` 字段，那本质还是「一家银行的多个网点」，演示不出跨行：
- 卡片号段无法体现各行独立发行；
- 转账不会产生「行间清算」这回事；
- 数据隔离只是逻辑上的，不是物理上的。

---

## 2. 架构

```text
MockBank.Api  :5200
│
├── MockBankStore                ← 跨行聚合门面（唯一对外的查询入口）
│   ├── BankLedger(ICBC) ──► App_Data/mockbank_icbc.db   （工行：客户/账户/卡/流水/理财/基金）
│   ├── BankLedger(CCB)  ──► App_Data/mockbank_ccb.db    （建行：同上）
│   └── BankLedger(CMB)  ──► App_Data/mockbank_cmb.db    （招行：同上）
│
└── 每行内部：BankLedger → BankDbContext → 自己的 SQLite 文件
```

| 概念 | 类型 | 职责 |
|------|------|------|
| `BankDescriptor` | `Domain/Banks.cs` | 一家银行的静态档案：行代码、全称、简称、卡号 BIN 前缀、网点、客服、库文件名 |
| `BankCatalog` | `Domain/Banks.cs` | 银行目录与查找；`ShortNameOf` / `FullNameOf` 供展示 |
| `BankLedger` | `Data/BankLedger.cs` | **一家银行 = 一个核心系统实例**：自己的库文件、自己的写锁、自己的流水号序列 |
| `BankDbContext` | `Data/BankDbContext.cs` | 一行一个 EF Core 上下文，映射到该行数据库 |
| `BankLedgerFactory` | `Data/BankLedgerFactory.cs` | 建库、首次种子写入、缓存账本实例、并发模式设置 |
| `MockBankStore` | `Data/MockBankStore.cs` | 跨行聚合门面：一次查询汇总所有行；写操作按 `BankCode` 路由到对应库 |

---

## 3. 三个必须记住的规则

### 3.1 写操作必须按 `BankCode` 路由

每个实体（`Account` / `BankCard` / `BankTransaction` / `WealthProduct`）都带 `BankCode`。`MockBankStore.UpdateAccount` 等方法据此找到目标行，**绝不能把 A 行的数据写进 B 行的库**。

### 3.2 跨行加锁按固定顺序获取

跨行转账涉及两行，必须一次性拿到两把锁。获取顺序按**行代码字典序**固定：

```text
A→B 的转账：先拿 A 再拿 B
B→A 的转账：先拿 A 再拿 B   ← 顺序一致
```

若各按自己的方向取锁，就会出现「A 持 A 锁等 B 锁、B 持 B 锁等 A 锁」的死锁。`MockBankStore.AcquireLedgerLocksAsync` 负责这件事，取锁失败时会归还已拿到的锁。

### 3.3 账号与卡号在跨行层面必须唯一

本系统靠**各行使用不同的卡号 BIN 段**保证：

| 银行 | BIN 段 | 示例账号 |
|------|--------|----------|
| 中国工商银行 | `6222` | `6222020200000001` |
| 中国建设银行 | `6217` | `6217000100000001` |
| 招商银行 | `6225` | `6225000100000001` |

因此跨行聚合查询「按账号查账户」「按卡号查卡」都可以直接全局搜索，无需额外限定行。

---

## 4. 数据隔离到底隔离了什么

| 层面 | 隔离情况 |
|------|----------|
| 物理存储 | 完全隔离：三个独立 `.db` 文件，删掉一个不影响另外两个 |
| 表结构 | 同一套模型（`BankDbContext`），各行独立建表 |
| 客户主档 | 隔离：同一个自然人在三家行有**三份独立主档**，只是沿用同一个 `UserId` 以便跨行聚合识别出是同一个人 |
| 理财产品 | 隔离：银行自营理财由本行发行，各行产品代码段不重叠（工行 `WP0xx`、建行 `WP1xx`、招行 `WP2xx`） |
| 公募基金 | 隔离：基金是**代销**产品，各行有自己的代销清单；同一只基金可被多家行代销，各自存在自己的库里 |
| 风险评级 | 隔离：客户评级是**行内**评级，各行可不同（工行 R3、建行 R2） |

**为什么客户主档要按行拆开**：真实系统里客户号是行内主键，跨行不通用。保留同一个 `UserId` 只是演示期的折衷——它让「张明在工行、建行、招行各有一张卡」能被一次跨库聚合查出来，而这正是「跨行卡片」要演示的东西。

### 4.1 读路径走内存快照，写路径落库

跨行聚合每次要问 3 家银行。若读路径直接走 SQLite，每次请求要开 3 个库、建 3 次连接、发 3 条 SQL，实测比内存字典慢约 5 倍。

因此 `BankLedger` 在启动时（建表/种子之后）把本行数据 `LoadSnapshot()` 进内存字典，**读路径走内存**；**写路径仍然落库**，落库后同步更新快照。

> **SQLite 仍是持久化的真相来源**，内存快照只是读加速层。
> 代价：同一个库文件被多个进程打开时，各进程的快照不共享 —— Mock Bank 正常单实例运行，不受影响。
> 由此还引出一个必须记住的顺序约束：**任何"库里是不是空"的判断都不能读快照**（见 §6.5）。

---

## 5. 跨行转账的记账链

```text
付款行 (CCB)                       收款行 (ICBC)
┌────────────────────────┐        ┌────────────────────────┐
│ 取两把锁（按行代码序）  │        │                        │
│ 扣款：余额 -500        │        │                        │
│ 记支出流水             │        │                        │
│   TX...CCB0000001      │        │                        │
│   渠道 = 跨行转账       │        │                        │
│   对手方 = 工行 + 户名   │        │                        │
│                        │  ───► │ 入账：余额 +500        │
│                        │        │ 记收入流水             │
│                        │        │   TX...ICBC0000001     │
│                        │        │   渠道 = 跨行转账       │
└────────────────────────┘        └────────────────────────┘
```

两条流水各自落在本行的库里，各带本行代码，因此**跨行聚合查询后仍然能定位到是哪家行记的账**。

返回体同时给出双边流水号：

```json
{
  "txNo": "TX20261020CCB0000001",
  "toTxNo": "TX20261020ICBC0000001",
  "fromBankCode": "CCB",
  "toBankCode": "ICBC",
  "isInterBank": true
}
```

---

## 6. 落地时踩过的坑（都已修，记录在此避免重犯）

### 6.1 流水号与种子数据重号

流水号里的序号来自内存计数器，**重启后从 0 开始**，而库里已经躺着种子数据写入的流水 —— 两者撞号，表现为
`UNIQUE constraint failed: transactions.TxNo`，也就是「重启后转账接口必然 500」。

修法有两条，缺一不可：

1. `BankLedger.InitializeTransactionSequence()`：启动时解析库里已有流水的序号，取最大值作为计数器起点；
2. `BankLedger.AddTransaction()`：写入前检查重号，撞了就顺延（应对多个进程同时打开同一个库文件）。

### 6.2 SQLite 的时间比较

SQLite 没有原生日期类型，EF 默认按带时区偏移的文本存储，**跨偏移量的字符串比较会得到错误的时间区间结果**（账单按月统计会算错）。

`BankDbContext` 统一把所有 `DateTimeOffset` 经值转换器转成 UTC `DateTime` 存储，保证排序与区间过滤正确。

### 6.3 「库被锁」

SQLite 默认用 rollback journal，一次写事务会**锁住整个库**，另一个进程写入立刻收到 `database is locked`。

演示环境里「宿主调 Mock Bank」与「E2E 脚本直连 Mock Bank」并发访问同一个库文件是常态，因此：

- 连接串加 `Default Timeout=30`（撞上写锁时等待而不是立刻失败）；
- 启动时切到 **WAL 日志模式**（读写不互斥，读者不阻塞写者）。

### 6.4 `EnsureCreated()` 不演进结构

`EnsureCreated()` 只在**整个数据库不存在**时才建表，数据库已存在时它什么都不做——既不演进表结构，也不报错。

于是「模型加了表、旧库里没有」这个最常见的开发期场景，会在第一次查询新表时抛出 `no such table`，而错误信息指向的是某个业务查询，排查方向完全被带偏。

`BankLedgerFactory` 的做法是：检测到缺表 → 重建该行库 → 重新写种子，并显式告警。模拟核心系统的数据全部来自种子、可确定性重建，所以重建是安全的。

### 6.5 种子守卫误读内存快照 → 重启必崩

引入内存快照后踩到的坑，症状极具迷惑性：**首次启动正常，之后每次重启都崩**。

根因：`BankLedgerFactory` 用 `CountAll()` 判断"该行库是不是空的、要不要写种子"。这个计数原本查数据库；改成读内存快照后，由于守卫发生在 `LoadSnapshot()` **之前**（快照尚未载入），计数**永远为 0** → 每次启动都重播种子 → 撞 `UNIQUE constraint failed: accounts.AccountNo` → 服务直接起不来。

修法：`CountAll()` 改回查数据库（EF 计数），把"持久化状态"的判断与"读加速层"彻底分开。

> **通用教训**：**加速层永远不能用来回答"持久化状态是什么"**。
> 凡是"库是否为空 / 是否已初始化 / 是否需要迁移"这类判断，必须查真相来源。
> 验证方式：用**已存在的库文件**重启一次 —— 全新库里这个 bug 根本不会暴露。

---

## 7. 配置

`src/mock-bank/MockBank.Api/appsettings.json`：

```json
"MockBank": {
  "DataDirectory": "App_Data",
  "ResetOnStartup": false,
  "Banks": []
}
```

| 配置项 | 说明 |
|--------|------|
| `DataDirectory` | 各行数据库文件所在目录（相对路径按宿主内容根目录解析）。默认 `App_Data` —— 改名的真实原因是 **Windows 大小写不敏感**：`data` 会命中源码目录 `Data/`，`App_Data` 不会。该目录仍在项目内，但 `.gitignore` 的 `*.db` 规则保证运行期数据不入库 |
| `ResetOnStartup` | 启动时是否重建全部银行数据库。默认 `false`——数据在重启后保留，更像真实核心系统 |
| `Banks` | 启用哪些银行（行代码）。为空表示启用目录中的全部银行；可填 `["ICBC","CCB"]` 只启用部分行 |

需要干净的演示数据时，二选一：

```bash
# 方式一：调用重置端点
curl -X POST http://localhost:5200/api/corebank/v1/_admin/reset

# 方式二：删掉 App_Data/ 目录下的 .db 文件后重启
```

> 注意：WAL 模式会在库文件旁生成 `mockbank_*.db-wal` 与 `mockbank_*.db-shm` 两个附属文件。
> 删库时必须一起删——只删主文件的话，残留的 WAL 会在新库打开时被回放，
> 把旧结构的数据「复活」回来，制造出比缺表更难查的错乱。

---

## 8. 对外接口

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/api/corebank/v1/banks` | 银行目录：每家银行的信息 + 独立库文件名 + 各行数据规模 |
| GET | `/api/corebank/v1/banks/{bankCode}` | 单家银行详情 |
| POST | `/api/corebank/v1/_admin/reset` | 重置全部银行数据库并重写种子 |
| GET | `/api/corebank/v1/customers/{userId}` | 客户档案（含**行内**风险评级，用于理财风险匹配） |
| GET | `/api/corebank/v1/cards?userId=&bankCode=` | 卡片列表（跨行聚合；`bankCode` 可限定发卡行） |
| GET | `/api/corebank/v1/cards/{cardNo}` | 单张卡片 |
| POST | `/api/corebank/v1/cards/{cardNo}/status` | 卡片状态变更（挂失 / 解挂 / 冻结 / 解冻） |
| POST | `/api/corebank/v1/cards/{cardNo}/limit` | 卡片单日限额调整 |
| GET | `/api/corebank/v1/accounts?userId=` | 账户列表（跨行聚合） |
| GET | `/api/corebank/v1/accounts/{accountNo}/balance` | 账户余额 |
| GET | `/api/corebank/v1/statements/{accountNo}` | 月度账单 |
| GET | `/api/corebank/v1/transactions?...` | 交易流水（跨行聚合） |
| GET | `/api/corebank/v1/beneficiaries?keyword=` | 收款人检索（同名跨行客户返回多条，各带开户行） |
| POST | `/api/corebank/v1/transfers` | 转账（行内 / 跨行自动识别） |
| GET | `/api/corebank/v1/products`、`/products/{code}` | 银行自营理财产品 |
| POST | `/api/corebank/v1/wealth/subscribe` | 认购理财产品（校验本行客户评级） |
| GET | `/api/corebank/v1/funds`、`/funds/{code}` | 公募基金代销清单与详情 |
| GET | `/api/corebank/v1/funds/{code}/nav`、`/funds/rankings` | 基金净值序列与排行 |

实测样例（演示用户 `u_demo01` 的跨行卡片）：

```json
[
  { "cardNo": "6217000100000001", "bankCode": "CCB", "bankName": "中国建设银行", "cardType": "借记卡" },
  { "cardNo": "6222020200000001", "bankCode": "ICBC", "bankName": "中国工商银行", "cardType": "借记卡" },
  { "cardNo": "6225000100000001", "bankCode": "CMB",  "bankName": "招商银行",     "cardType": "借记卡" }
]
```
