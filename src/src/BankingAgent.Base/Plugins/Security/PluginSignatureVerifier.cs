// ===== 插件签名验证（防供应链投毒）=====
// 威胁模型：任何能写入 plugins/ 目录的人，都可以替换业务逻辑 DLL。
// 后果：恶意插件可在转账 Agent 里窃取用户身份、篡改金额、绕过合规检查。
//
// 缓解：加载前验证程序集签名，只接受受信发布者签发的 DLL。
// 对应 docs/05-security-compliance/01-threat-model.md 的攻击面「插件投毒」。

using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Base.Plugins.Security;

/// <summary>插件验证配置。</summary>
public sealed class PluginSignatureOptions
{
    /// <summary>是否强制要求签名。</summary>
    public bool RequireSignature { get; set; }

    /// <summary>是否允许开发环境跳过签名检查（仅限本地调试）。</summary>
    public bool AllowUnsignedInDevelopment { get; set; } = true;

    /// <summary>当前环境名。</summary>
    public string Environment { get; set; } = "Development";

    /// <summary>受信证书指纹（SHA-256 十六进制，小写，去冒号）。</summary>
    public IReadOnlyList<string> TrustedThumbprints { get; set; } = [];

    /// <summary>是否同时校验文件哈希白名单（更强的第二道防线）。</summary>
    public bool VerifyHashAllowlist { get; set; }

    /// <summary>受信文件哈希（SHA-256 十六进制，小写）。</summary>
    public IReadOnlyDictionary<string, string> TrustedHashes { get; set; }
        = new Dictionary<string, string>();

    /// <summary>受信强名称公钥令牌（十六进制，不含空格）。跨平台可验证。</summary>
    public IReadOnlyList<string> TrustedPublicKeyTokens { get; set; } = [];

    /// <summary>是否要求 Authenticode 签名（仅 Windows 平台生效）。</summary>
    public bool RequireAuthenticode { get; set; }
}

/// <summary>验证结果。</summary>
public sealed record PluginVerificationResult
{
    public required bool Allowed { get; init; }
    public string PluginPath { get; init; } = "";
    public string? Reason { get; init; }
    public string? SubjectName { get; init; }
    public string? Thumbprint { get; init; }
    public string? FileHash { get; init; }
    public string? PublicKeyToken { get; init; }
}

/// <summary>
/// 插件签名验证器。
/// 三层防线：Authenticode 签名 → 证书指纹白名单 → 文件哈希白名单。
/// </summary>
public sealed class PluginSignatureVerifier : IDisposable
{
    private readonly PluginSignatureOptions _options;
    private readonly ILogger<PluginSignatureVerifier> _logger;

    /// <summary>构造验证器。</summary>
    public PluginSignatureVerifier(
        Microsoft.Extensions.Options.IOptions<PluginSignatureOptions> options,
        ILogger<PluginSignatureVerifier> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public void Dispose() { }

    private bool IsDevelopment =>
        _options.Environment.Equals("Development", StringComparison.OrdinalIgnoreCase);

    /// <summary>验证单个插件文件。返回是否允许加载。</summary>
    public PluginVerificationResult Verify(string pluginPath)
    {
        if (!File.Exists(pluginPath))
        {
            return Deny(pluginPath, "文件不存在");
        }

        // 开发环境可跳过
        if (!_options.RequireSignature && _options.AllowUnsignedInDevelopment && IsDevelopment)
        {
            _logger.LogDebug("开发环境跳过签名校验: {Plugin}", Path.GetFileName(pluginPath));
            return new PluginVerificationResult { Allowed = true, PluginPath = pluginPath };
        }

        // ===== 第 1 层：文件哈希白名单 =====
        string fileHash;
        try
        {
            fileHash = ComputeFileHash(pluginPath);
        }
        catch (Exception ex)
        {
            return Deny(pluginPath, $"无法计算文件哈希: {ex.Message}");
        }

        if (_options.VerifyHashAllowlist && _options.TrustedHashes.Count > 0)
        {
            var fileName = Path.GetFileName(pluginPath);
            if (!_options.TrustedHashes.TryGetValue(fileName, out var expected))
            {
                return Deny(pluginPath, "文件不在哈希白名单中", fileHash: fileHash);
            }
            if (!string.Equals(expected, fileHash, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogCritical(
                    "插件哈希不匹配（疑似篡改）: {Plugin} 期望 {Expected} 实际 {Actual}",
                    fileName, expected, fileHash);
                return Deny(pluginPath, "文件哈希与白名单不符（疑似篡改）", fileHash: fileHash);
            }
        }

        // ===== 第 2 层：强名称公钥令牌 =====
        // 强名称是跨平台可验证的（不依赖 Windows 证书存储），
        // 能防止插件被替换成第三方构建的同名程序集。
        byte[]? publicKeyToken;
        string assemblyName;
        try
        {
            var asmName = AssemblyName.GetAssemblyName(pluginPath);
            assemblyName = asmName.Name ?? "";
            publicKeyToken = asmName.GetPublicKeyToken();
        }
        catch (Exception ex)
        {
            return Deny(pluginPath, $"无法读取程序集元数据: {ex.Message}", fileHash: fileHash);
        }

        if (publicKeyToken is { Length: > 0 } && _options.TrustedPublicKeyTokens.Count > 0)
        {
            var tokenHex = Convert.ToHexString(publicKeyToken);
            var trusted = _options.TrustedPublicKeyTokens
                .Select(t => t.Replace(" ", "").ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!trusted.Contains(tokenHex.ToLowerInvariant()))
            {
                _logger.LogCritical(
                    "插件强名称不受信: {Plugin} asm={Assembly} token={Token}",
                    Path.GetFileName(pluginPath), assemblyName, tokenHex);
                return Deny(pluginPath, "强名称公钥令牌不在受信列表中", token: tokenHex, fileHash: fileHash);
            }
        }

        // ===== 第 3 层：Authenticode 签名（仅 Windows 可用）=====
        // Authenticode 依赖 Windows 证书存储，非 Windows 平台跳过此层。
        if (OperatingSystem.IsWindows() && _options.RequireAuthenticode)
        {
            X509Certificate2? cert;
            try
            {
                cert = new X509Certificate2(
                    X509Certificate.CreateFromSignedFile(pluginPath));
            }
            catch (CryptographicException)
            {
                return Deny(pluginPath, "插件未签名，但当前环境要求 Authenticode 签名",
                    fileHash: fileHash);
            }

            var thumbprint = Normalize(cert.Thumbprint);
            var subject = cert.Subject;
            var now = DateTimeOffset.Now;

            if (now < cert.NotBefore || now > cert.NotAfter)
            {
                return Deny(pluginPath,
                    $"签名证书已过期（{cert.NotBefore:yyyy-MM-dd} ~ {cert.NotAfter:yyyy-MM-dd}）",
                    subject, thumbprint, fileHash);
            }

            if (_options.TrustedThumbprints.Count > 0)
            {
                var trustedCerts = _options.TrustedThumbprints
                    .Select(Normalize)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (!trustedCerts.Contains(thumbprint))
                {
                    _logger.LogCritical(
                        "插件签名证书不受信: {Plugin} subject={Subject} thumbprint={Thumbprint}",
                        Path.GetFileName(pluginPath), subject, thumbprint);
                    return Deny(pluginPath, "签名证书不在受信列表中", subject, thumbprint, fileHash);
                }
            }

            _logger.LogInformation(
                "插件 Authenticode 验证通过: {Plugin} subject={Subject} hash={Hash}",
                Path.GetFileName(pluginPath), subject, fileHash[..16]);

            return new PluginVerificationResult
            {
                Allowed = true,
                PluginPath = pluginPath,
                SubjectName = subject,
                Thumbprint = thumbprint,
                FileHash = fileHash
            };
        }

        _logger.LogInformation(
            "插件验证通过（哈希{0}强名称{1}）: {Plugin} hash={Hash}",
            _options.VerifyHashAllowlist ? "+" : "-",
            _options.TrustedPublicKeyTokens.Count > 0 ? "+" : "-",
            Path.GetFileName(pluginPath), fileHash[..16]);

        return new PluginVerificationResult
        {
            Allowed = true,
            PluginPath = pluginPath,
            FileHash = fileHash
        };
    }

    /// <summary>批量验证目录下的所有插件。</summary>
    public IReadOnlyList<PluginVerificationResult> VerifyDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return [];

        return Directory.EnumerateFiles(directory, "BankingAgent.Plugin.*.dll")
            .Where(f => !Path.GetFileNameWithoutExtension(f)
                .EndsWith(".Plugin.Sdk", StringComparison.OrdinalIgnoreCase))
            .Select(Verify)
            .ToList();
    }

    private PluginVerificationResult Deny(
        string path, string reason,
        string? subject = null, string? thumbprint = null,
        string? fileHash = null, string? token = null)
    {
        _logger.LogError("插件验证失败: {Plugin} — {Reason}",
            Path.GetFileName(path), reason);
        return new PluginVerificationResult
        {
            Allowed = false,
            PluginPath = path,
            Reason = reason,
            SubjectName = subject,
            Thumbprint = thumbprint,
            FileHash = fileHash,
            PublicKeyToken = token
        };
    }

    /// <summary>计算文件 SHA-256。</summary>
    private static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>规范化指纹：去冒号、小写。</summary>
    private static string Normalize(string thumbprint) =>
        thumbprint.Replace(":", "").Replace(" ", "").ToLowerInvariant();
}
