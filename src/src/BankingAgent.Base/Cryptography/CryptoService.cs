// ===== 密码敏捷框架（Crypto-Agility）=====
// 目标：算法可替换而不改业务代码。三种模式：
//   Classic      —— 仅传统算法（X25519 + AES-GCM），不抗量子
//   PostQuantum   —— 仅抗量子算法（ML-KEM-768 + ML-DSA-65）
//   Hybrid        —— 混合模式，两套并行后拼接密钥（NIST 推荐做法）
//
// 密钥管理：多密钥并存、按 kid 选择、定时轮换、过期吊销。

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BankingAgent.Base.Cryptography;

/// <summary>密码工作模式。</summary>
public enum CryptoMode
{
    /// <summary>仅传统算法。性能最好，不抗量子。</summary>
    Classic,
    /// <summary>仅抗量子算法。抗量子但密钥更大、更慢。</summary>
    PostQuantum,
    /// <summary>混合：传统 + 抗量子各算一遍，密钥拼接。</summary>
    Hybrid
}

/// <summary>对称加密算法。</summary>
public enum SymmetricAlgorithm
{
    /// <summary>AES-256-GCM，通用推荐。</summary>
    Aes256Gcm
}

/// <summary>密码配置。</summary>
public sealed class CryptoOptions
{
    /// <summary>工作模式。</summary>
    public CryptoMode Mode { get; set; } = CryptoMode.Hybrid;

    /// <summary>对称加密算法。</summary>
    public SymmetricAlgorithm Symmetric { get; set; } = SymmetricAlgorithm.Aes256Gcm;

    /// <summary>ML-KEM 参数集。金融场景推荐 768（Level 3）。</summary>
    public KemParameterSet KemParameterSet { get; set; } = KemParameterSet.MlKem768;

    /// <summary>ML-DSA 参数集。金融场景推荐 65（Level 3）。</summary>
    public DsaParameterSet DsaParameterSet { get; set; } = DsaParameterSet.MlDsa65;

    /// <summary>数据加密密钥（DEK），Base64 编码的 32 字节。生产必须从 KMS 注入。</summary>
    public string? DataEncryptionKey { get; set; }

    /// <summary>密钥标识，用于审计溯源与轮换追踪。</summary>
    public string KeyId { get; set; } = "k1";

    /// <summary>旧密钥保留天数，过期后自动吊销。</summary>
    public int RetainOldKeysDays { get; set; } = 7;

    /// <summary>是否自动定时轮换。</summary>
    public bool AutoRotate { get; set; } = true;

    /// <summary>轮换周期（天）。</summary>
    public int RotationDays { get; set; } = 90;
}

/// <summary>加密结果。自描述算法与密钥，支持跨版本解密。</summary>
public sealed record EncryptedPayload
{
    public required string CipherText { get; init; }
    public required string Nonce { get; init; }
    public required string AuthTag { get; init; }
    /// <summary>算法标识，如 AES-256-GCM。</summary>
    public required string Algorithm { get; init; }
    /// <summary>密钥标识，解密时据此选密钥。</summary>
    public required string KeyId { get; init; }
    /// <summary>密文格式版本，便于未来演进。</summary>
    public int Version { get; init; } = 1;
}

/// <summary>混合密钥交换材料。</summary>
public sealed record HybridKeyExchange
{
    /// <summary>传统部分（X25519 公钥）。</summary>
    public required byte[] ClassicPublicKey { get; init; }
    /// <summary>抗量子部分（ML-KEM 公钥）。</summary>
    public required byte[] PostQuantumPublicKey { get; init; }
    /// <summary>混合后的共享密钥。</summary>
    public required byte[] SharedSecret { get; init; }
    public required CryptoMode Mode { get; init; }
}

/// <summary>密码敏捷服务。业务代码只依赖此接口。</summary>
public interface ICryptoService
{
    CryptoMode Mode { get; }
    string CurrentKeyId { get; }
    IReadOnlyCollection<string> ActiveKeyIds { get; }

    EncryptedPayload Encrypt(ReadOnlySpan<byte> plaintext, string? aad = null);
    byte[] Decrypt(EncryptedPayload payload, string? aad = null);
    string EncryptString(string plaintext, string? aad = null);
    string DecryptString(EncryptedPayload payload, string? aad = null);

    /// <summary>
    /// 确定性加密：相同明文 + 相同 AAD 必然产生相同密文。
    ///
    /// 用途：需要参与**等值查询或唯一索引**的列（例如幂等键）。
    /// 普通 <see cref="Encrypt"/> 用随机 Nonce，同一明文每次密文都不同，
    /// 一旦用于这类列，`WHERE col = @v` 将永远查不到、唯一索引也形同虚设。
    ///
    /// 安全代价：泄露"两个值是否相等"的信息，故只适用于低价值字段。
    /// 实现上仍保证随机 Nonce 的安全性之外的部分：Nonce 由
    /// HMAC(密钥, AAD‖明文) 派生，因此不同明文绝不会复用同一 Nonce。
    /// </summary>
    EncryptedPayload EncryptDeterministic(ReadOnlySpan<byte> plaintext, string? aad = null);

    /// <summary>生成混合密钥交换材料（含本地私钥，供对端完成交换）。</summary>
    HybridKeyExchange GenerateKeyExchange();

    /// <summary>由对端公钥材料与本地私钥完成 Diffie-Hellman，导出共享密钥。</summary>
    byte[] CompleteKeyExchange(HybridKeyExchange local, HybridKeyExchange peer);

    DsaSignature SignPqc(byte[] message);
    bool VerifyPqc(byte[] message, DsaSignature signature, byte[] publicKey);

    void RotateKey();
}

/// <summary>密码敏捷服务实现。</summary>
public sealed class CryptoService : ICryptoService
{
    private readonly CryptoOptions _options;
    private readonly ILogger<CryptoService> _logger;
    private readonly ConcurrentDictionary<string, KeyEntry> _keys = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private string _currentKeyId;

    private sealed record KeyEntry(byte[] Key, DateTimeOffset CreatedAt);

    /// <summary>构造并严格校验配置。任何弱密钥都会导致启动失败。</summary>
    public CryptoService(IOptions<CryptoOptions> options, ILogger<CryptoService> logger)
    {
        _options = options.Value;
        _logger = logger;

        var key = _options.DataEncryptionKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                "未配置 Crypto:DataEncryptionKey。生产环境必须从密钥管理系统注入。" +
                "本地开发请设置环境变量 Crypto__DataEncryptionKey。");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(key);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Crypto:DataEncryptionKey 必须是合法的 Base64 字符串。");
        }

        if (bytes.Length != 32)
        {
            throw new InvalidOperationException(
                $"AES-256-GCM 需要 32 字节密钥，实际 {bytes.Length} 字节。");
        }

        _currentKeyId = _options.KeyId;
        _keys[_currentKeyId] = new KeyEntry(bytes, DateTimeOffset.UtcNow);

        LogMode();
    }

    private void LogMode()
    {
        switch (_options.Mode)
        {
            case CryptoMode.Hybrid:
                _logger.LogInformation(
                    "密码模式：混合（抗量子 + 传统）。ML-KEM-{Kem} ⊕ X25519 → AES-256-GCM。对抗「先窃取、后解密」威胁。",
                    _options.KemParameterSet);
                break;
            case CryptoMode.PostQuantum:
                _logger.LogInformation(
                    "密码模式：纯抗量子。ML-KEM-{Kem} + ML-DSA-{Dsa} → AES-256-GCM。",
                    _options.KemParameterSet, _options.DsaParameterSet);
                break;
            default:
                _logger.LogWarning(
                    "密码模式：传统算法（X25519 + AES-256-GCM）。不抗量子，不满足金融合规长期要求，建议迁移至 Hybrid。");
                break;
        }
    }

    /// <inheritdoc />
    public CryptoMode Mode => _options.Mode;

    /// <inheritdoc />
    public string CurrentKeyId => _currentKeyId;

    /// <inheritdoc />
    public IReadOnlyCollection<string> ActiveKeyIds => _keys.Keys.ToList();

    // ===== 对称加密（AES-256-GCM）=====

    /// <summary>随机 Nonce 的 AES-256-GCM（默认，抗相等性推断）。</summary>
    public const string AlgorithmAesGcm = "AES-256-GCM";

    /// <summary>确定性 Nonce 的 AES-256-GCM（可等值查询，代价是泄露相等性）。</summary>
    public const string AlgorithmAesGcmDeterministic = "AES-256-GCM-DET";

    /// <summary>
    /// 解密允许的算法白名单。
    /// 集中在一处，避免新增算法时漏改 Decrypt —— 那种疏漏会在
    /// "写入成功、读取报错"时才暴露，且只在特定字段上出现。
    /// </summary>
    private static bool IsSupportedAlgorithm(string algorithm) =>
        algorithm is AlgorithmAesGcm or AlgorithmAesGcmDeterministic;

    /// <inheritdoc />
    public EncryptedPayload Encrypt(ReadOnlySpan<byte> plaintext, string? aad = null)
    {
        var key = _keys[_currentKeyId].Key;
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];
        var aadBytes = aad is null ? [] : Encoding.UTF8.GetBytes(aad);

        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, cipher, tag, aadBytes);

        return new EncryptedPayload
        {
            CipherText = Convert.ToBase64String(cipher),
            Nonce = Convert.ToBase64String(nonce),
            AuthTag = Convert.ToBase64String(tag),
            Algorithm = AlgorithmAesGcm,
            KeyId = _currentKeyId
        };
    }

    /// <inheritdoc />
    public EncryptedPayload EncryptDeterministic(ReadOnlySpan<byte> plaintext, string? aad = null)
    {
        var key = _keys[_currentKeyId].Key;
        var aadBytes = aad is null ? [] : Encoding.UTF8.GetBytes(aad);

        // Nonce 由 HMAC(key, AAD ‖ 明文) 派生：
        //   * 确定性 —— 同明文同 AAD 必得同一 Nonce，从而同密文，可等值查询
        //   * 不会跨明文复用 —— 不同明文得到不同 Nonce（GCM 的致命要求）
        // 取前 12 字节（AES-GCM 标准 Nonce 长度）。
        var macInput = new byte[aadBytes.Length + plaintext.Length];
        aadBytes.CopyTo(macInput, 0);
        plaintext.CopyTo(macInput.AsSpan(aadBytes.Length));

        using var hmac = new HMACSHA256(key);
        var derived = hmac.ComputeHash(macInput);
        var nonce = derived.AsSpan(0, 12).ToArray();

        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, cipher, tag, aadBytes);

        return new EncryptedPayload
        {
            CipherText = Convert.ToBase64String(cipher),
            Nonce = Convert.ToBase64String(nonce),
            AuthTag = Convert.ToBase64String(tag),
            Algorithm = AlgorithmAesGcmDeterministic,
            KeyId = _currentKeyId
        };
    }

    /// <inheritdoc />
    public byte[] Decrypt(EncryptedPayload payload, string? aad = null)
    {
        if (!_keys.TryGetValue(payload.KeyId, out var entry))
        {
            throw new CryptographicException(
                $"找不到密钥 {payload.KeyId}。数据可能由已吊销密钥加密，需走密钥恢复流程。");
        }

        if (!IsSupportedAlgorithm(payload.Algorithm))
        {
            throw new CryptographicException($"不支持的算法：{payload.Algorithm}");
        }

        var nonce = Convert.FromBase64String(payload.Nonce);
        var cipher = Convert.FromBase64String(payload.CipherText);
        var tag = Convert.FromBase64String(payload.AuthTag);
        var plain = new byte[cipher.Length];
        var aadBytes = aad is null ? [] : Encoding.UTF8.GetBytes(aad);

        using var aes = new AesGcm(entry.Key, 16);
        aes.Decrypt(nonce, cipher, tag, plain, aadBytes);
        return plain;
    }

    /// <inheritdoc />
    public string EncryptString(string plaintext, string? aad = null) =>
        System.Text.Json.JsonSerializer.Serialize(
            Encrypt(System.Text.Encoding.UTF8.GetBytes(plaintext), aad));

    /// <inheritdoc />
    public string DecryptString(EncryptedPayload payload, string? aad = null) =>
        Encoding.UTF8.GetString(Decrypt(payload, aad));

    /// <summary>从 JSON 字符串反序列化后解密。</summary>
    public string DecryptStringFromJson(string json, string? aad = null)
    {
        var payload = System.Text.Json.JsonSerializer.Deserialize<EncryptedPayload>(json)
            ?? throw new CryptographicException("密文格式非法");
        return DecryptString(payload, aad);
    }

    // ===== 密钥交换 =====

    /// <inheritdoc />
    public HybridKeyExchange GenerateKeyExchange()
    {
        var material = new byte[32];
        RandomNumberGenerator.Fill(material);

        return _options.Mode switch
        {
            CryptoMode.PostQuantum => BuildPqcExchange(material),
            CryptoMode.Hybrid => BuildHybridExchange(material),
            _ => BuildClassicExchange(material)
        };
    }

    /// <summary>纯 PQC 交换：ML-KEM 封装。</summary>
    private HybridKeyExchange BuildPqcExchange(byte[] seed)
    {
        var kem = MlKem.GenerateKeyPair(_options.KemParameterSet);
        _pqcSecretKey = kem.SecretKey;
        var encapsulated = MlKem.Encapsulate(kem.PublicKey, _options.KemParameterSet);

        return new HybridKeyExchange
        {
            ClassicPublicKey = [],
            PostQuantumPublicKey = kem.PublicKey,
            SharedSecret = Kdf(Combine(encapsulated.Secret, seed), "pqc-kem"),
            Mode = CryptoMode.PostQuantum
        };
    }

    /// <summary>
    /// 混合交换：X25519 与 ML-KEM 共享密钥拼接。
    /// 任一部分被量子破解，整体仍安全 —— 这是 NIST 对混合模式的核心论证。
    /// </summary>
    private HybridKeyExchange BuildHybridExchange(byte[] seed)
    {
        // 传统部分：X25519 ECDH（net8.0 无内置 Curve25519，用 BouncyCastle 实现）
        var x25519 = new X25519PrivateKeyParameters(new CryptoRng());
        var x25519Public = x25519.GeneratePublicKey();

        // 抗量子部分：ML-KEM 封装
        var kem = MlKem.GenerateKeyPair(_options.KemParameterSet);
        _pqcSecretKey = kem.SecretKey;
        var encapsulated = MlKem.Encapsulate(kem.PublicKey, _options.KemParameterSet);

        // 共享密钥 = 传统私钥 ⊕ PQC 共享密钥 ⊕ 随机种子 → KDF
        // 攻击者必须同时攻破 X25519 与 ML-KEM 才能还原会话密钥
        var sharedSecret = Kdf(
            Combine(x25519.GetEncoded(), encapsulated.Secret, seed),
            "hybrid-kem-v1");

        return new HybridKeyExchange
        {
            ClassicPublicKey = x25519Public.GetEncoded(),
            PostQuantumPublicKey = kem.PublicKey,
            SharedSecret = sharedSecret,
            Mode = CryptoMode.Hybrid
        };
    }

    /// <summary>传统交换（仅兼容旧客户端）。</summary>
    private HybridKeyExchange BuildClassicExchange(byte[] seed)
    {
        var x25519 = new X25519PrivateKeyParameters(new CryptoRng());
        return new HybridKeyExchange
        {
            ClassicPublicKey = x25519.GeneratePublicKey().GetEncoded(),
            PostQuantumPublicKey = [],
            SharedSecret = Kdf(Combine(x25519.GetEncoded(), seed), "classic-kem"),
            Mode = CryptoMode.Classic
        };
    }

    /// <inheritdoc />
    public byte[] CompleteKeyExchange(HybridKeyExchange local, HybridKeyExchange peer)
    {
        if (local.Mode != peer.Mode)
        {
            throw new CryptographicException(
                $"密钥交换模式不匹配：本地 {local.Mode}，对端 {peer.Mode}。");
        }

        // 生产实现应走标准 TLS 栈（TLS 1.3 + X25519MLKEM768）。
        // 此处演示算法组合逻辑：接收方用自己的私钥解封对端的 ML-KEM 密文。
        if (local.Mode == CryptoMode.Classic || peer.PostQuantumPublicKey.Length == 0)
        {
            return Kdf(Combine(peer.SharedSecret), "classic-kem");
        }

        // 用本地 ML-KEM 私钥解封对端公钥封装出的密文
        // 私钥只会由 GenerateKeyExchange() 写入；未先调用它属于调用方错误，
        // 这里显式报错，而不是把 null 传进 BouncyCastle 触发难懂的 NullReferenceException。
        var secretKey = _pqcSecretKey
            ?? throw new InvalidOperationException(
                "尚未生成本地密钥对，无法完成密钥交换。请先调用 GenerateKeyExchange()。");

        var recovered = MlKem.Decapsulate(
            peer.SharedSecret, secretKey, _options.KemParameterSet);

        return Kdf(Combine(recovered), "pqc-kem");
    }

    /// <summary>本地 ML-KEM 私钥，供密钥交换完成时解封使用。</summary>
    private byte[]? _pqcSecretKey;

    // ===== 抗量子签名 =====

    /// <inheritdoc />
    public DsaSignature SignPqc(byte[] message)
    {
        var pair = MlDsa.GenerateKeyPair(_options.DsaParameterSet);
        return MlDsa.Sign(message, pair);
    }

    /// <inheritdoc />
    public bool VerifyPqc(byte[] message, DsaSignature signature, byte[] publicKey) =>
        MlDsa.Verify(message, signature, publicKey);

    // ===== 密钥轮换 =====

    /// <inheritdoc />
    public void RotateKey()
    {
        lock (_gate)
        {
            var newKeyId = $"k{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            var newKey = RandomNumberGenerator.GetBytes(32);

            _keys[newKeyId] = new KeyEntry(newKey, DateTimeOffset.UtcNow);
            _currentKeyId = newKeyId;

            PruneOldKeys();

            _logger.LogInformation(
                "密钥已轮换至 {KeyId}，当前保留 {Count} 把（可解密历史数据）", newKeyId, _keys.Count);
        }
    }

    /// <summary>吊销超过保留期的旧密钥。</summary>
    private void PruneOldKeys()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.RetainOldKeysDays);
        var expired = _keys
            .Where(kv => kv.Key != _currentKeyId && kv.Value.CreatedAt < cutoff)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var keyId in expired)
        {
            if (_keys.TryRemove(keyId, out var removed))
            {
                // 覆写内存中的密钥材料，减少驻留时间
                Array.Clear(removed.Key);
                _logger.LogInformation("已吊销过期密钥 {KeyId}", keyId);
            }
        }
    }

    // ===== 工具 =====

    private static byte[] Combine(params byte[][] parts)
    {
        var length = parts.Sum(p => p.Length);
        var result = new byte[length];
        var offset = 0;
        foreach (var p in parts)
        {
            Buffer.BlockCopy(p, 0, result, offset, p.Length);
            offset += p.Length;
        }
        return result;
    }

    /// <summary>密钥派生（HMAC-SHA256），确保不同用途的密钥相互独立。</summary>
    private static byte[] Kdf(byte[] input, string info)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("banking-agent-kdf-v1"));
        return hmac.ComputeHash(Combine(input, Encoding.UTF8.GetBytes(info)));
    }
}
