# 数据分级与脱敏（Data Classification & Masking）

> **状态**：评审中 · **所有者**：安全合规 · **版本**：v1.0
> **最后更新**：2026-09-21

---

## 0. 现状与目标边界

> **Current（源码可验证）**：存在 L1-L4 分类类型、`DataMasker`、`[Encrypted]` 特性和 EF Core 自动值转换器。`TransferRecord` 的 `UserId`、付款/收款账号、幂等键和备注已标注加密，其中需要等值查询的字段使用可搜索加密。默认仍是 SQLite；密钥由配置/环境变量提供。
>
> **未实现 / Target**：TDE、KMS/Vault 托管、PostgreSQL RLS、分 Schema 最小权限账号、备份加密/异地存储、自动留存清理与“所有 L3+ 字段”CI 阻断扫描均无当前落地证据。以下策略与示例保留为生产目标。

## 1. 目的

为不同类型、不同敏感度的数据定义**统一的分级标准**、**保护措施**和**脱敏规则**，确保：

1. 数据保护强度与数据敏感度匹配
2. 团队成员对数据敏感度有共同认知
3. 自动化工具（脱敏中间件、监控、审计）有明确规则可循

---

## 2. 数据分级标准

### 2.1 四级分类

| 级别 | 名称 | 描述 | 示例 |
|------|------|------|------|
| **L4** | **绝密（Top Secret）** | 一旦泄露将对用户/公司造成**严重且不可逆**损害 | 用户明文密码、CVV、生物特征原始数据、私钥 |
| **L3** | **机密（Confidential）** | 一旦泄露将造成**严重**损害，需要严格访问控制 | 用户身份证号、银行卡号全段、账户余额、交易明细 PII 部分 |
| **L2** | **内部（Internal）** | 泄露会对公司造成影响，但不对个人造成直接损害 | 用户手机号（中间段）、哈希 user_id、内部业务日志、内部 IP |
| **L1** | **公开（Public）** | 可以对外公开，无敏感性 | 产品名称、公司信息、公开利率（脱敏后） |

### 2.2 字段级映射示例

| 数据 | 级别 | 理由 |
|------|------|------|
| 用户身份证号 | L3 | 唯一标识 + 可关联其他信息 |
| 用户身份证号（哈希后） | L2 | 哈希不可逆，但仍属个人信息 |
| 用户手机号 | L3 | 可联系 + 可关联账号 |
| 用户手机号（中间 4 位 `*`） | L2 | 弱 PII |
| 用户手机号（后 4 位） | L2 | 仅做核对用途 |
| 用户姓名 | L3 | 个人信息 |
| 用户姓名（首字 + *） | L2 | 弱标识 |
| 银行卡号全段 | L3 | 敏感金融信息 |
| 银行卡号（后 4 位） | L2 | 对照用途 |
| CVV / 密码 / PIN | **L4** | 直接金融危害 |
| 账户余额 | L3 | 财务隐私 |
| 交易时间 + 收款人 + 金额 | L3 | 行为隐私 |
| 交易笔数（聚合） | L2 | 弱隐私 |
| 用户地址 | L3 | 位置隐私 |
| 用户 GPS 定位 | **L4** | 高敏感（需用户单独同意） |
| LLM API Key | **L4** | 系统安全 |
| 数据库密码 | **L4** | 系统安全 |
| 内部 IP（10.x.x.x） | L1 | 公开网络架构内已知 |
| 产品文档 URL | L1 | 公开 |
| 用户操作日志（脱敏后） | L2 | 可用于分析 |

---

## 3. 各级别保护措施（Target 基线）

### 3.1 保护矩阵

| 维度 | L1 公开 | L2 内部 | L3 机密 | L4 绝密 |
|------|--------|--------|--------|--------|
| **传输加密** | 可选 | TLS | TLS 1.2+ | TLS 1.3 + mTLS |
| **存储加密** | 明文 | 明文或加密 | AES-256 加密 | AES-256 + 字段级加密 |
| **访问控制** | 公开 | 内部员工 | 业务最小权限 | 严格 RBAC + 审批 |
| **数据库访问** | 全员只读 | 业务账号 | 业务模块专号 | 临时凭证 + 双人审批 |
| **日志记录** | 可选 | INFO | INFO + 审计 | AUDIT（独立通道） |
| **脱敏展示** | 明文 | 视场景 | 默认脱敏 | **永远不展示** |
| **备份加密** | 可选 | 加密 | 加密 + 异地 | 加密 + 异地 + 离线 |
| **留存期** | 永久 | 5 年 | 5 年（合规） | 5 年（合规）+ 强制清理 |
| **导出审批** | 无 | 无 | 一级审批 | 二级审批 + 合规审批 |
| **跨境** | 允许 | 评估 | 申报 | 禁止 |

---

## 4. 数据库加密（Current + Target）

### 4.1 透明加密（TDE）

> **Target，未实现。**

- PostgreSQL 16 使用 `pg_tde` 扩展（Percona / EDB 版本）
- 或使用文件系统级加密（LUKS）
- 密钥管理：阿里云 KMS / HashiCorp Vault

### 4.2 字段级加密（Field-level）

当前已通过 `[Encrypted]` + EF 值转换器覆盖 `TransferRecord` 的部分 L3 字段。以下按分级选择算法的代码仅为目标示意，不是仓库当前 API：

```csharp
public class EncryptedField
{
    // 加密存储
    public static string Encrypt(string plain, DataClassification level)
    {
        return level switch
        {
            DataClassification.L4 => EncryptAesGcm(plain, KeyManager.GetKey(level)),
            DataClassification.L3 => EncryptAesCbc(plain, KeyManager.GetKey(level)),
            _ => plain  // L1/L2 不加密
        };
    }

    // 脱敏展示
    public static string Mask(string plain, DataClassification level)
    {
        return level switch
        {
            DataClassification.L4 => "****",  // 永不展示
            DataClassification.L3 => MaskMiddle(plain),
            _ => plain
        };
    }
}
```

### 4.3 密钥管理

> **Target，当前未接 KMS/Vault。**

- **L4 密钥**：存储在 Vault / KMS，永不出库
- **L3 密钥**：存储在 Vault，定期轮换（90 天）
- **应用永远拿不到明文密钥**，只能通过 KMS API 加解密

---

## 5. 脱敏规则

### 5.1 脱敏函数库

```csharp
public static class Masking
{
    /// 手机号：138****1234
    public static string Phone(string phone) =>
        phone?.Length == 11 ? phone[..3] + "****" + phone[7..] : "****";

    /// 身份证：110101********1234
    public static string IdCard(string id) =>
        id?.Length == 18 ? id[..6] + "********" + id[14..] : "****";

    /// 银行卡：**** **** **** 1234
    public static string BankCard(string card) =>
        card?.Length >= 4 ? "**** **** **** " + card[^4..] : "****";

    /// 姓名：张*
    public static string Name(string name) =>
        string.IsNullOrEmpty(name) ? "" : name[..1] + (name.Length > 1 ? "*" : "");

    /// 邮箱：a***@example.com
    public static string Email(string email)
    {
        var parts = email?.Split('@');
        return parts?.Length == 2
            ? parts[0][..1] + "***@" + parts[1]
            : "***@***";
    }

    /// 金额：1,234.56 → 1,***.56（保护具体数字）
    public static string Amount(decimal amount, bool showFull = false) =>
        showFull ? amount.ToString("N2") : "***.**";
}
```

### 5.2 脱敏场景

| 场景 | 脱敏策略 |
|------|---------|
| **IM 卡片展示** | 银行卡 `**** **** **** 1234` |
| **对话回复** | 涉及金额默认脱敏，确认后才显示 |
| **日志** | 全字段自动脱敏（见下） |
| **审计查询** | 授权用户可看完整，其他用户看脱敏 |
| **LLM 调用** | L3 字段脱敏后传入 |
| **导出报表** | 默认脱敏，导出需审批 |
| **内部 BI** | 聚合分析，不含 PII |

### 5.3 日志自动脱敏

通过 `Serilog.Destructuring` + 自定义 Filter：

```csharp
public class SensitiveDataFilter : IDestructuringPolicy
{
    public bool TryDestructure(object value, ILogEventPropertyValueFactory factory, out ILogEventPropertyValue result)
    {
        if (value is PhoneNumber p) { result = factory.CreatePropertyValue(Masking.Phone(p.Value)); return true; }
        if (value is IdCard i) { result = factory.CreatePropertyValue(Masking.IdCard(i.Value)); return true; }
        if (value is BankCard b) { result = factory.CreatePropertyValue(Masking.BankCard(b.Value)); return true; }
        // ... 其他敏感类型
        result = null;
        return false;
    }
}
```

---

## 6. 数据生命周期

### 6.1 留存期

| 数据类型 | 留存期 | 法规 |
|---------|--------|------|
| 用户身份资料 | 用户销户后 ≥ 5 年 | 反洗钱法 |
| 交易记录 | ≥ 5 年 | 反洗钱法 + 商业银行法 |
| 审计日志 | ≥ 5 年（不可篡改）| 网络安全法 + 银保监 |
| 操作日志 | ≥ 6 个月 | 网络安全法 §21 |
| 会话记录 | 90 天 | 公司规范 |
| LLM 调用日志 | 180 天（用于审计） | 公司规范 |
| 营销数据 | 用户撤回同意后立即 | 个保法 |
| 临时文件 | 24h 自动清理 | 公司规范 |
| 备份数据 | 1 年（异地 OSS） | 公司规范 |

### 6.2 删除策略

- **软删除**：`deleted_at` 字段，业务查询过滤（适用 L1/L2）
- **硬删除**：`DELETE FROM ...` 后定期清理（适用 L3 用户主动请求）
- **加密擦除**：销毁 L4 数据的密钥，使数据永久不可读
- **定期清理 Job**：每日清理过期数据，每月清理已硬删数据

---

## 7. 数据访问控制（Target）

### 7.1 数据库账号矩阵

| Schema | DB 账号 | 权限 | 用途 |
|--------|---------|------|------|
| `core` | `app_agent_core_rw` | SELECT/INSERT/UPDATE | agent-core 业务 |
| `audit` | `app_audit_insert_only` | INSERT only | 审计日志写入 |
| `audit` | `app_audit_readonly` | SELECT | 审计员查询 |
| `admin` | `app_admin` | 限定表 | admin 后台 |
| `ai` | `app_ai_service_rw` | SELECT（脱敏视图）| ai-service 读取 |

### 7.2 行级安全（RLS）

> **Target，未实现**：当前默认 SQLite 不支持以下 PostgreSQL RLS 配置。

```sql
ALTER TABLE core.accounts ENABLE ROW LEVEL SECURITY;
CREATE POLICY user_isolation ON core.accounts
    USING (user_id = current_setting('app.current_user_id')::uuid);
```

应用层每次连接必须 `SET app.current_user_id = '<uuid>'`。

### 7.3 应用层权限矩阵

| 角色 | 可读 PII 字段 | 可写 PII 字段 |
|------|-------------|-------------|
| **普通用户** | 自己的全部 | 自己的非关键字段 |
| **客服** | 业务需要字段（含敏感但需审批）| 无 |
| **风控** | 业务需要字段 | 无 |
| **运维** | 仅 ID + 哈希 | 无 |
| **开发（预发）** | 测试数据（mock） | 无 |
| **开发（生产）** | 仅 Debug 时一次性，需审批 | 无 |
| **DBA** | 无（强制走审计员视图）| DDL |
| **审计员** | 全部 + 审计表 | 审计表 |

---

## 8. 数据出境

### 8.1 原则：**数据不出境**

- 部署在境内（阿里云华东 Region）
- LLM 主用国产合规（Qwen3 / DeepSeek）
- 不使用境外 CDN（境内用阿里云 CDN）

### 8.2 例外（如必须）

1. 申报网信办《数据出境安全评估》
2. 签订 DPA
3. 加密 + 脱敏 + 最小化
4. 定期审计

### 8.3 目标部署约束

- 生产环境计划不使用境外 LLM
- 生产环境计划不在境外存储数据
- ✅ 可使用境外 LLM 仅用于开发/测试（脱敏样本）

---

## 9. 自动化工具（Target）

### 9.1 分类扫描器（自研）

启动时扫描所有数据模型：

```csharp
[DataClassification(DataClassification.L3)]
public string IdCard { get; set; }

[DataClassification(DataClassification.L4)]
public string PasswordHash { get; set; }  // 即使是 hash 也是 L4
```

目标是在启动时 + CI 时自动校验（当前 CI 尚未完整实现这些阻断规则）：
- 所有 L3+ 字段都有 `[Encrypted]` 或 `[Masked]` 属性
- 没有明文 L3+ 字段出现在日志 sink
- 数据库表都有 RLS（多租户表）

### 9.2 扫描规则

| 规则 | 阻断？ |
|------|--------|
| L3+ 字段未加密 | 阻断 |
| L4 字段未脱敏 | 阻断 |
| L3+ 字段出现在日志 | 阻断 |
| 数据库表无 RLS | 阻断 |
| 新增敏感字段未分级 | 阻断 |
| 数据导出未脱敏 | 阻断 |
| 备份未加密 | 阻断 |

---

## 10. 新数据接入 Checklist

新增数据字段/表时：

- [ ] 已分类（L1/L2/L3/L4）
- [ ] 加密/脱敏策略已应用
- [ ] 访问控制已配置（DB 账号 + RLS + 应用层）
- [ ] 日志脱敏已生效
- [ ] 留存期已明确
- [ ] 删除策略已实现
- [ ] L3+ 字段在隐私政策中告知
- [ ] 同意范围已更新（如新增字段）

详见 `06-product/02-feature-lifecycle.md` 中 PIA 流程。

---

## 11. 参考

- 合规矩阵 — `05-security-compliance/02-compliance-matrix.md`
- 威胁模型 — `05-security-compliance/01-threat-model.md`
- 审计日志 — `05-security-compliance/04-audit-logging.md`
- 域模型（C#）— `01-domain/02-domain-model.md`