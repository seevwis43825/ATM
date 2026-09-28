// ===== 生产环境安全门 =====
// 金融系统的关键原则：开发环境可以用弱配置跑通，生产环境必须「失败即停」。
// 本文件把安全要求固化为启动时检查，一旦不满足直接抛异常阻止启动。
//
// 依据：
//   《网络安全法》第 21 条   网络安全等级保护
//   《数据安全法》第 21 条   数据分类分级保护
//   《个人信息保护法》第 51 条  技术措施
//   商业银行信息安全通用要求

namespace BankingAgent.Base.Security.Hardening;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

/// <summary>安全门配置。</summary>
public sealed class SecurityGateOptions
{
    /// <summary>是否启用安全门（生产必开，开发可关）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>运行环境名（Production / Staging / Development）。</summary>
    public string Environment { get; set; } = "Development";

    /// <summary>是否强制 HTTPS。</summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>HSTS 有效期（秒）。默认一年。</summary>
    public int HstsMaxAgeSeconds { get; set; } = 31_536_000;

    /// <summary>是否移除服务器标识（防技术栈信息泄露）。</summary>
    public bool RemoveServerHeader { get; set; } = true;

    /// <summary>最低 TLS 版本。金融合规要求禁用 TLS 1.0/1.1。</summary>
    public string MinTlsVersion { get; set; } = "TLS12";

    /// <summary>是否强制启用 HSTS（禁用 HTTPS 时必须关掉，否则浏览器锁死）。</summary>
    public bool EnableHsts { get; set; } = true;

    /// <summary>是否要求插件签名验证（防供应链投毒）。</summary>
    public bool RequirePluginSignature { get; set; }

    /// <summary>是否禁用开发者异常页（生产禁止，会泄露堆栈与内部路径）。</summary>
    public bool DisableDeveloperExceptionPage { get; set; }

    /// <summary>可信代理 IP 列表（逗号分隔），用于正确解析客户端 IP 与协议。</summary>
    public string TrustedProxies { get; set; } = "";
}

/// <summary>安全门检查结果。</summary>
public sealed record SecurityGateResult
{
    public required bool Passed { get; init; }
    public IReadOnlyList<string> Violations { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>安全门违规异常。抛出即阻止启动。</summary>
public sealed class SecurityGateViolationException(string message) : Exception(message);

/// <summary>
/// 安全门校验器。启动时检查所有安全配置，任一硬性违规则阻止启动。
/// </summary>
public sealed class SecurityGate
{
    private readonly IConfiguration _config;
    private readonly ILogger<SecurityGate> _logger;

    /// <summary>构造安全门。</summary>
    public SecurityGate(IConfiguration config, ILogger<SecurityGate> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>是否生产环境。</summary>
    public bool IsProduction =>
        (Str("SecurityGate:Environment") ?? "Development")
            .Equals("Production", StringComparison.OrdinalIgnoreCase);

    /// <summary>读取布尔配置，缺省时返回 fallback。</summary>
    private bool Flag(string key, bool fallback)
    {
        var raw = Str($"SecurityGate:{key}");
        return raw is null ? fallback : Convert.ToBoolean(raw);
    }

    /// <summary>读取布尔配置（无前缀）。</summary>
    private bool FlagRaw(string key, bool fallback)
    {
        var raw = Str(key);
        return raw is null ? fallback : Convert.ToBoolean(raw);
    }

    private string? Str(string key) => _config[key];

    /// <summary>执行安全检查，返回逐项结果。</summary>
    public SecurityGateResult Validate()
    {
        var violations = new List<string>();
        var warnings = new List<string>();

        if (!Flag("Enabled", IsProduction))
        {
            _logger.LogWarning("安全门已关闭（SecurityGate:Enabled=false），仅允许在开发环境使用");
            return new SecurityGateResult { Passed = true, Warnings = ["安全门未启用"] };
        }

        // ===== 1. JWT 密钥强度 =====
        var jwtKey = Str("Jwt:SigningKey");
        if (string.IsNullOrWhiteSpace(jwtKey))
        {
            violations.Add("未配置 Jwt:SigningKey");
        }
        else if (jwtKey.StartsWith("dev-only-", StringComparison.Ordinal))
        {
            violations.Add("使用了开发默认 JWT 密钥，生产环境必须替换");
        }
        else if (jwtKey.Length < 32)
        {
            violations.Add($"JWT 密钥长度不足（{jwtKey.Length} < 32 字符）");
        }

        // ===== 2. 数据加密密钥 =====
        var dek = Str("Crypto:DataEncryptionKey");
        if (string.IsNullOrWhiteSpace(dek))
        {
            violations.Add("未配置 Crypto:DataEncryptionKey");
        }
        else
        {
            try
            {
                var bytes = Convert.FromBase64String(dek);
                if (bytes.Length != 32)
                {
                    violations.Add($"AES-256 密钥长度错误（{bytes.Length} 字节，应为 32）");
                }
            }
            catch (FormatException)
            {
                violations.Add("Crypto:DataEncryptionKey 不是合法 Base64");
            }
        }

        // ===== 3. 审计链签名密钥 =====
        var auditKey = Str("Audit:SigningKey");
        if (string.IsNullOrWhiteSpace(auditKey))
        {
            violations.Add("未配置 Audit:SigningKey");
        }
        else if (auditKey.StartsWith("dev-only-", StringComparison.Ordinal))
        {
            violations.Add("使用了开发默认审计密钥");
        }

        // ===== 4. 传输加密 =====
        if (Flag("RequireHttps", true) && !HasTlsConfigured())
        {
            violations.Add("RequireHttps=true 但未配置 TLS 证书");
        }

        // ===== 5. TLS 版本 =====
        var minTls = Str("SecurityGate:MinTlsVersion") ?? "TLS12";
        if (minTls is "TLS10" or "TLS11")
        {
            violations.Add($"TLS 版本 {minTls} 不符合金融合规要求，最低 TLS 1.2");
        }

        // ===== 6. HSTS 依赖 HTTPS =====
        if (Flag("EnableHsts", true) && !Flag("RequireHttps", true))
        {
            violations.Add("启用 HSTS 但未强制 HTTPS，会导致浏览器永久锁定 HTTP");
        }

        // ===== 7. 密码模式 =====
        if ((Str("Crypto:Mode") ?? "Hybrid") == "Classic")
        {
            warnings.Add("密码模式为 Classic，不抗量子。金融合规长期要求应使用 Hybrid");
        }

        // ===== 8. 速率限制 =====
        if (!FlagRaw("RateLimit:Enabled", true))
        {
            violations.Add("速率限制已关闭，存在暴力破解风险");
        }

        // ===== 9. 插件签名 =====
        if (!Flag("RequirePluginSignature", false))
        {
            warnings.Add("未强制插件签名验证，存在供应链投毒风险");
        }

        // ===== 10. 开发者异常页 =====
        if (IsProduction && !Flag("DisableDeveloperExceptionPage", true))
        {
            violations.Add("生产环境不应启用开发者异常页（泄露堆栈与内部路径）");
        }

        var result = new SecurityGateResult
        {
            Passed = violations.Count == 0,
            Violations = violations,
            Warnings = warnings
        };

        if (result.Passed)
        {
            _logger.LogInformation("安全门检查通过。密码模式={Mode}，警告 {Count} 条",
                Str("Crypto:Mode"), warnings.Count);
        }
        else
        {
            _logger.LogCritical("安全门检查失败，检测到 {Count} 项违规：{Detail}",
                violations.Count, string.Join(" | ", violations));
        }

        foreach (var w in warnings)
        {
            _logger.LogWarning("安全门警告: {Warning}", w);
        }

        return result;
    }

    /// <summary>检查并强制执行。不通过直接抛异常阻止应用启动。</summary>
    public void Enforce()
    {
        var result = Validate();
        if (result.Passed) return;

        var detail = string.Join(
            Environment.NewLine,
            result.Violations.Select(v => "  - " + v));

        throw new SecurityGateViolationException(
            "应用启动被安全门阻止：" + Environment.NewLine + detail +
            Environment.NewLine + "请修正配置或通过环境变量注入密钥后重试。");
    }

    /// <summary>判断是否已配置 TLS（证书文件、环境变量、或 https 监听地址）。</summary>
    private bool HasTlsConfigured()
    {
        if (!string.IsNullOrWhiteSpace(Str("Kestrel:Certificates:Default:Path"))) return true;
        if (Environment.GetEnvironmentVariable("ASPNETCORE_KTLS_CERT") is not null) return true;

        var urls = Str("Urls") ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "";
        return urls.Contains("https://", StringComparison.OrdinalIgnoreCase);
    }
}
