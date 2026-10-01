# 安全加固实施报告

> **日期**：2026-09-28
> **范围**：`src/` 全部代码
> **当前验证**：`dotnet test src/UnitTests/UnitTests.csproj` 实测 **149/149 通过**。旧版“112/201 项”统计已失效；密码自检、E2E、Stress 与 LoadTest 是独立程序，不能并入当前单元测试数量，需按各自最近一次运行证据报告。

> **Current / Target 边界**：本报告中的“已实现”只表示仓库存在代码与相应测试，不表示生产已部署。当前默认仍为 `BankingAgent.Host :5243` + `MockBank.Api :5200`、HTTP、SQLite + `EnsureCreated`；安全门默认开发配置关闭。MFA、KMS/Vault、TDE、WORM、RLS 均未实现。

---

## 0. 先说清楚：关于「绝对安全」

**工程上不存在绝对安全的系统。** 任何声称"绝对安全"的方案都无法验证。

金融级安全的真实标准是三条，本轮工作围绕它们展开：

| 原则 | 含义 | 本轮落实 |
|------|------|---------|
| **失败即停** | 配置不合规不启动，绝不带病运行 | 安全门 `SecurityGate` |
| **纵深防御** | 单点被攻破仍有其他防线 | 密码四层、插件三层、请求五层 |
| **可审计举证** | 每个安全决策都有日志与证据 | 审计链 + 密码自检 + 验证日志 |

**本报告不承诺"绝对安全"，只承诺已实现的控制项和已验证的证据。**

---

## 1. 抗量子密码学组件（已实现；未接生产协议）

### 1.1 实现了什么

| 标准 | 算法 | 用途 | 参数集 | 密钥/签名大小 |
|------|------|------|-------|------------|
| **FIPS 203** | ML-KEM（CRYSTALS-Kyber） | 密钥封装 | 512 / **768** / 1024 | 公钥 800/1184/1568 B |
| **FIPS 204** | ML-DSA（CRYSTALS-Dilithium） | 数字签名 | 44 / **65** / 87 | 签名 2420/3309/4627 B |

参数 768 与 65 为 NIST Level 3，金融场景推荐档位。

### 1.2 三种工作模式

| 模式 | 算法组合 | 抗量子 | 性能 | 建议 |
|------|---------|-------|------|------|
| `Classic` | X25519 + AES-256-GCM | ❌ | 最快 | 仅遗留系统兼容 |
| `PostQuantum` | ML-KEM-768 + ML-DSA-65 | ✅ | 较慢 | 新建高保密系统 |
| **`Hybrid`** | **ML-KEM-768 ⊕ X25519 → AES-256-GCM** | ✅ | 中等 | **金融推荐** |

**混合模式的核心论证**（NIST 立场）：共享密钥由两套独立算法各算一半再拼接，**攻击者必须同时攻破 ML-KEM 和 X25519 才能还原**。这防的是「先窃取密文、将来量子计算机解密」（harvest-now-decrypt-later）。

### 1.3 实测证据

```
ML-KEM 封装/解封一致
  MlKem512    公钥 800B  密文 768B   密钥 32B  耗时 93ms
  MlKem768    公钥 1184B 密文 1088B  密钥 32B  耗时 2ms
  MlKem1024   公钥 1568B 密文 1568B  密钥 32B  耗时 2ms

ML-DSA 签名验证 + 篡改检测
  MlDsa44   签名 2420B  耗时 54ms   篡改检测通过
  MlDsa65   签名 3309B  耗时 23ms   篡改检测通过
  MlDsa87   签名 4627B  耗时 9ms    篡改检测通过

混合密钥交换
  X25519 公钥 32B + ML-KEM-768 公钥 1184B，模式 Hybrid
```

**组件自检示例**（历史运行日志，不代表当前 HTTP 宿主已启用 PQC 传输）：

```
密码模式：混合（抗量子 + 传统）。ML-KEM-MlKem768 ⊕ X25519 → AES-256-GCM。
对抗「先窃取、后解密」威胁。
```

### 1.4 技术选型说明

| 项 | 选择 | 原因 |
|----|------|------|
| 实现库 | BouncyCastle 2.4 | net8.0 兼容；.NET 10 可迁至内置 `MLKem`/`MLDsa` |
| 随机源 | .NET `RandomNumberGenerator` | 显式指定 OS 级 CSPRNG，不用 BC 默认实现 |
| 敏感材料 | `IDisposable` 句柄 + `Array.Clear` | 减少密钥材料驻留内存时间 |

### 1.5 仍然存在的差距

| 差距 | 说明 |
|------|------|
| 传输层未用 PQC | 密钥交换算法已实现，但**TLS 栈未接入**。生产需 TLS 1.3 + X25519MLKEM768 |
| JWT 仍是 HS256 | 对称签名，实测 128 bit 有效强度（Grover 降级后），可接受但非最优。长期应迁 ML-DSA |
| 性能开销未压测 | ML-KEM 密钥生成 ~2-90ms，批量场景需基准测试 |

---

## 2. 密码敏捷框架（已实现）

### 2.1 设计目标

**算法可替换而不改业务代码。** 业务只依赖 `ICryptoService`，不知道底层是 AES 还是 SM4，是 HS256 还是 ML-DSA。

### 2.2 密钥管理

| 能力 | 实现 | 实测 |
|------|------|------|
| 多密钥并存 | `ConcurrentDictionary<string, KeyEntry>` | ✅ |
| 密钥标识透传 | 密文携带 `KeyId`，解密时选对应密钥 | ✅ `test-k1` 正确透传 |
| 定时轮换 | `RotateKey()`，KeyId 含时间戳 | ✅ `test-k1 → k20260928021755` |
| 旧密钥保留 | 保留期内可解密历史数据 | ✅ 实测通过 |
| 过期吊销 | 超过 `RetainOldKeysDays` 自动吊销 + 内存清零 | ✅ |
| 弱密钥拒绝 | 启动时校验 Base64 合法性与 32 字节长度 | ✅ 8 字节密钥被拒 |

### 2.3 密文自描述

```json
{
  "cipherText": "...",
  "nonce": "...",
  "authTag": "...",
  "algorithm": "AES-256-GCM",
  "keyId": "k20260928021755",
  "version": 1
}
```

**意义**：密钥轮换后仍能解密历史数据；算法升级时可平滑迁移。

### 2.4 AAD 绑定

```csharp
crypto.Encrypt(data, aad: "user:u_demo01");
// 换上下文解密 → AuthenticationTagMismatchException
```

**意义**：防止密文被移花接木（同一密钥下把 A 用户的数据解密成 B 用户）。

---

## 3. 生产安全门框架（已实现；默认未启用）

### 3.1 失败即停

`SecurityGate.Enforce()` 在**容器构建前**执行，任一硬性违规直接抛异常阻止启动。

当前 `appsettings.json` 为开发口径：`SecurityGate:Enabled=false`、`RequireHttps=false`。只有在生产配置显式启用并通过检查时，才能作为生产门禁证据。

### 3.2 检查项

| # | 检查项 | 生产环境行为 |
|---|-------|------------|
| 1 | JWT 密钥存在 | ❌ 阻止启动 |
| 2 | JWT 密钥非开发默认 | ❌ 阻止启动 |
| 3 | JWT 密钥 ≥32 字符 | ❌ 阻止启动 |
| 4 | 数据加密密钥存在 | ❌ 阻止启动 |
| 5 | 数据加密密钥为合法 Base64 | ❌ 阻止启动 |
| 6 | 数据加密密钥 32 字节 | ❌ 阻止启动 |
| 7 | 审计签名密钥非开发默认 | ❌ 阻止启动 |
| 8 | 已配置 TLS 证书 | ❌ 阻止启动 |
| 9 | TLS ≥1.2 | ❌ 阻止启动 |
| 10 | HSTS 依赖 HTTPS | ❌ 阻止启动（防浏览器锁死） |
| 11 | 速率限制已启用 | ❌ 阻止启动 |
| 12 | 生产禁用开发者异常页 | ❌ 阻止启动 |
| 13 | 密码模式非 Classic | ⚠️ 警告 |
| 14 | 插件签名验证已启用 | ⚠️ 警告 |

### 3.3 错误输出示例

```
CRITICAL 安全门检查失败，检测到 3 项违规：
  - 使用了开发默认 JWT 密钥，生产环境必须替换
  - 未配置 Crypto:DataEncryptionKey
  - RequireHttps=true 但未配置 TLS 证书
```

### 3.4 启用方式

```json
"SecurityGate": {
  "Enabled": true,
  "Environment": "Production",
  "RequireHttps": true,
  "EnableHsts": true,
  "MinTlsVersion": "TLS12",
  "DisableDeveloperExceptionPage": true
}
```

---

## 4. 传输与响应头安全（响应头已实现；默认 TLS 未部署）

### 4.1 实测的安全响应头

| 响应头 | 值 | 防护 |
|-------|-----|------|
| `X-Content-Type-Options` | `nosniff` | MIME 混淆 XSS |
| `X-Frame-Options` | `DENY` | 点击劫持 |
| `frame-ancestors` | `'none'` | 点击劫持（CSP 补充） |
| `Referrer-Policy` | `no-referrer` | URL 泄漏（含查询参数） |
| `Content-Security-Policy` | `default-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'` | XSS、数据注入 |
| `Permissions-Policy` | 关闭摄像头/麦克风/定位/支付等 | 功能滥用 |
| `Cross-Origin-Resource-Policy` | `same-origin` | 跨域资源泄漏 |
| `Cross-Origin-Opener-Policy` | `same-origin` | 跨窗口攻击 |
| `X-DNS-Prefetch-Control` | `off` | DNS 探测泄漏 |
| `Strict-Transport-Security` | `max-age=31536000; includeSubDomains; preload` | HTTPS 降级 |
| `X-Powered-By` / `Server` | **已移除** | 技术栈信息泄露 |

> **实测注意**：`Server: Kestrel` 仍由 Kestrel 自身下发，需要 `AddServerHeader = false` 才能完全移除。当前已移除 `X-Powered-By`。

### 4.2 HTTPS 强制（Target 配置）

```csharp
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();              // 一年期 HSTS
    app.UseHttpsRedirection(); // HTTP → HTTPS 强制跳转
}
```

---

## 5. 插件供应链安全（已实现）

### 5.1 威胁

**任何能写入 `plugins/` 目录的人，都可以替换业务逻辑 DLL。** 恶意插件可在转账 Agent 里窃取用户身份、篡改金额、绕过合规检查。

### 5.2 三层验证

| 层 | 机制 | 跨平台 | 强度 |
|----|------|-------|------|
| 1 | 文件 SHA-256 哈希白名单 | ✅ | 最强（文件级） |
| 2 | 强名称公钥令牌白名单 | ✅ | 强（发布者级） |
| 3 | Authenticode 签名 + 证书指纹 | ❌ 仅 Windows | 强（含时间戳） |

**跨平台设计考虑**：Authenticode 依赖 Windows 证书存储，Linux 容器环境不可用。因此把**哈希白名单作为主力**，它在任何平台都能工作，且能精确识别「文件被替换」。

### 5.3 加载路径集成

```csharp
foreach (var dll in Directory.EnumerateFiles(directory, "BankingAgent.Plugin.*.dll"))
{
    var verification = _verifier.Verify(dll);
    if (!verification.Allowed)
    {
        _logger.LogError("插件被安全门拒绝加载: {Plugin} — {Reason}", fileName, verification.Reason);
        continue;   // ← 拒绝加载，不降级放行
    }
    var result = await LoadAsync(dll, ct);
}
```

**关键**：验证失败是**拒绝加载**，不是"警告后继续"。

### 5.4 生产配置

```json
"PluginSignature": {
  "RequireSignature": true,
  "AllowUnsignedInDevelopment": false,
  "Environment": "Production",
  "VerifyHashAllowlist": true,
  "TrustedHashes": {
    "BankingAgent.Plugin.Transfer.dll": "<sha256>",
    "BankingAgent.Plugin.BillAnalysis.dll": "<sha256>"
  },
  "TrustedPublicKeyTokens": ["<token-hex>"]
}
```

### 5.5 尚未完成

| 项 | 说明 |
|----|------|
| 证书吊销检查（CRL/OCSP） | 当前只验证证书有效期，未查吊销列表 |
| SBOM 生成 | `dotnet list package --include-transitive` 未接入 CI |
| 依赖锁定 | 未使用 `packages.lock.json` |

---

## 6. 字段级加密（已实现并用于 TransferRecord）

### 6.1 设计

实体属性加 `[Encrypted]` 特性后，EF Core 自动装配值转换器。当前 `TransferRecord` 的用户、付款/收款账号、幂等键和备注已应用；这不等于全数据库 TDE，也不表示所有敏感模型都已覆盖。

```csharp
public class CustomerRecord : EncryptedEntity
{
    public string UserId { get; set; }              // 不加密

    [Encrypted]                                     // ← 自动加密
    public string IdCard { get; set; }              // L3

    [Encrypted]                                     // ← 自动加密
    public string Phone { get; set; }               // L3
}
```

### 6.2 为什么自动扫描

手动逐个配置转换器**必然会漏**。自动扫描意味着：新增敏感字段只要加特性就会被加密，漏不掉。

### 6.3 幂等性

```csharp
if (value.StartsWith("enc:v1:", StringComparison.Ordinal)) return value;
```

已加密的值不会重复加密，兼容数据迁移场景。

### 6.4 审计

`EncryptedFieldLogGuard` 拦截器在保存时记录**哪些字段被变更**（只记字段名，绝不记明文）。

---

## 7. 已有安全能力汇总

| 能力 | 状态 | 验证 |
|------|------|------|
| **抗量子密钥封装** ML-KEM | ✅ | 3 参数集实测 |
| **抗量子签名** ML-DSA | ✅ | 3 参数集 + 篡改检测 |
| **混合模式** | ✅ | 运行日志确认激活 |
| **AES-256-GCM** | ✅ | 加解密 + AAD 绑定 |
| **密钥轮换 + 吊销** | ✅ | 实测轮换 + 历史解密 |
| **弱密钥拒绝** | ✅ | 启动时校验 |
| **安全门** | ✅ | 12 项硬性检查 |
| **安全响应头** | ✅ | 11 个头实测 |
| **插件三层验证** | ✅ | 加载路径集成 |
| **字段级加密** | ✅ | 自动装配 |
| **JWT + RBAC** | ✅ | 8 项端到端断言 |
| **越权防护** | ✅ | userId 强制取自令牌 |
| **速率限制** | ✅ | 令牌桶双维度 |
| **L1-L4 脱敏** | ✅ | 29 项单元测试 |
| **审计链签名** | ✅ | HMAC 链 JSONL |
| **审计独立表双写** | ✅ | `audit_events`；失败记 Critical、不阻断业务 |
| **人工回环** | ✅ | 资金操作强制 |
| **幂等保护** | ✅ | 防重复扣款 |

---

## 8. 仍存在的风险（诚实清单）

| # | 风险 | 严重度 | 说明 |
|---|------|-------|------|
| 1 | **无 MFA / OTP** | 🔴 高 | 金融系统强制要求 |
| 2 | **传输层未接 PQC** | 🔴 高 | 密钥交换算法已实现，TLS 未接入 |
| 3 | **JWT 用 HS256** | 🟡 中 | 实际安全，非最优 |
| 4 | **无证书吊销检查** | 🟡 中 | CRL/OCSP 未接 |
| 5 | **合规规则仅 4/14** | 🔴 高 | 缺高频限流、日累计、黑名单、制裁筛查 |
| 6 | **默认仍用 EnsureCreated** | 🔴 高 | 已有 EF Migration 与 CI pending-model gate，但运行配置仍是 `EnsureCreated` |
| 7 | **审计归档未生产化** | 🟡 中 | 已双写 JSONL + 独立 `audit_events`；无异地 WORM，失败不阻断 |
| 8 | **用户权利接口未实现** | 🔴 高 | 个保法 §46-47 强制 |
| 9 | **无 SBOM / 依赖锁定** | 🟡 中 | 供应链可追溯性不足 |
| 10 | **未做渗透测试** | 🟡 中 | 无第三方安全评估 |
| 11 | **mTLS 未实现** | 🟡 中 | 服务间调用无双向认证 |
| 12 | **LLM 集成后容量未重估** | 🟡 中 | 当前 296 QPS，接 LLM 后降 100-500 倍 |

---

## 9. 生产上线前必做（按优先级）

### P0（阻断上线）

1. **MFA / OTP** 接入
2. **TLS 1.3 + 证书配置**，`RequireHttps=true`
3. **密钥全部改用环境变量注入**，配置文件中清空
4. 将运行配置从 `EnsureCreated` 切换为已具备的 EF Core Migration 流程，并演练回滚
5. **合规规则补齐 4 条**（高频限流 / 日累计 / 黑名单 / 制裁筛查）
6. **用户权利接口**（个保法 §46-47）

### P1（上线后一个月内）

7. 在现有独立 `audit_events` 基础上增加可靠补偿与异地 WORM 归档
8. 证书吊销检查（CRL/OCSP）
9. 依赖锁定 + SBOM 生成
10. 第三方渗透测试

### P2（长期）

11. TLS 混合 PQC 密钥交换（X25519MLKEM768）
12. JWT 签名迁 ML-DSA
13. 敏感数据 KMS 托管

---

## 10. 验证方式

```powershell
# 单元测试
dotnet test src/UnitTests/UnitTests.csproj  # 当前 149 项

# 密码学自检（含抗量子；以本次程序输出为准）
dotnet run --project src/CryptoSelfTest

# 端到端（需先启动两个服务）
dotnet run --project src/E2ETest

# 并发压测（历史数字不并入单测计数）
dotnet run --project src/StressTest

# 容量压测
dotnet run --project src/LoadTest        # 7 级阶梯

# 密码学自检覆盖：ML-KEM 三参数集、ML-DSA 三参数集 + 篡改检测、
#                 AES-GCM + AAD 绑定、密钥轮换 + 历史解密、弱密钥拒绝
```

---

## 11. 参考

- NIST FIPS 203（ML-KEM）：https://csrc.nist.gov/publications/detail/fips/203/final
- NIST FIPS 204（ML-DSA）：https://csrc.nist.gov/publications/detail/fips/204/final
- NIST PQC 项目：https://csrc.nist.gov/projects/post-quantum-cryptography
- Google Willow 芯片（Nature 2024）：量子纠错的现状与距离
- OWASP PQC 迁移建议：https://owasp.org/www-project-post-quantum-cryptography/
- 本项目威胁模型：[`05-security-compliance/01-threat-model.md`](../05-security-compliance/01-threat-model.md)
- 本项目合规矩阵：[`05-security-compliance/02-compliance-matrix.md`](../05-security-compliance/02-compliance-matrix.md)
