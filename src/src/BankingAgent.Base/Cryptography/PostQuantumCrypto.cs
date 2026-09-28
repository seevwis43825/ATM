// ===== 抗量子密码算法（Post-Quantum Cryptography）=====
// 实现 NIST 标准化算法（CRYSTALS 后裔）：
//   ML-KEM（原 CRYSTALS-Kyber）—— FIPS 203，密钥封装与密钥交换
//   ML-DSA（原 CRYSTALS-Dilithium）—— FIPS 204，数字签名
//
// BouncyCastle 2.4 仍用 CRYSTALS 原名；.NET 10 起可迁移到内置
// System.Security.Cryptography.MLKem / MLDsa。
//
// 详见 docs/10-security/01-cryptography-and-hardening.md

using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Pqc.Crypto;
using Org.BouncyCastle.Pqc.Crypto.Crystals.Dilithium;using Org.BouncyCastle.Pqc.Crypto.Crystals.Kyber;
using Org.BouncyCastle.Security;

namespace BankingAgent.Base.Cryptography;

/// <summary>ML-KEM 参数集。安全等级越高，密钥与密文越大。</summary>
public enum KemParameterSet
{
    /// <summary>Kyber512，NIST Level 1。</summary>
    MlKem512 = 512,
    /// <summary>Kyber768，NIST Level 3。金融场景推荐。</summary>
    MlKem768 = 768,
    /// <summary>Kyber1024，NIST Level 5。最高安全。</summary>
    MlKem1024 = 1024
}

/// <summary>ML-DSA 参数集。</summary>
public enum DsaParameterSet
{
    /// <summary>Dilithium2，NIST Level 2。</summary>
    MlDsa44 = 44,
    /// <summary>Dilithium3，NIST Level 3。金融场景推荐。</summary>
    MlDsa65 = 65,
    /// <summary>Dilithium5，NIST Level 5。最高安全。</summary>
    MlDsa87 = 87
}

/// <summary>ML-KEM 密钥对。</summary>
public sealed record KemKeyPair(byte[] PublicKey, byte[] SecretKey, KemParameterSet ParameterSet);

/// <summary>ML-KEM 封装结果。</summary>
public sealed record KemSecret(byte[] CipherText, byte[] Secret);

/// <summary>ML-DSA 密钥对。</summary>
public sealed record DsaKeyPair(byte[] PublicKey, byte[] SecretKey, DsaParameterSet ParameterSet);

/// <summary>ML-DSA 签名。</summary>
public sealed record DsaSignature(byte[] Signature, DsaParameterSet ParameterSet);

/// <summary>
/// 抗量子密钥封装（ML-KEM / FIPS 203）。
/// 替代 RSA-ECDHE 密钥交换，抵抗 Shor 算法。
/// </summary>
public static class MlKem
{
    private static KyberParameters KyberParams(KemParameterSet set) => set switch
    {
        KemParameterSet.MlKem512 => KyberParameters.kyber512,
        KemParameterSet.MlKem768 => KyberParameters.kyber768,
        KemParameterSet.MlKem1024 => KyberParameters.kyber1024,
        _ => throw new ArgumentOutOfRangeException(nameof(set))
    };

    /// <summary>生成 ML-KEM 密钥对。</summary>
    public static KemKeyPair GenerateKeyPair(KemParameterSet set = KemParameterSet.MlKem768)
    {
        var p = KyberParams(set);
        var gen = new KyberKeyPairGenerator();
        gen.Init(new KyberKeyGenerationParameters(new CryptoRng(), p));
        var pair = gen.GenerateKeyPair();

        return new KemKeyPair(
            ((KyberPublicKeyParameters)pair.Public).GetEncoded(),
            ((KyberPrivateKeyParameters)pair.Private).GetEncoded(),
            set);
    }

    /// <summary>用接收方公钥封装共享密钥（发送方调用）。</summary>
    public static KemSecret Encapsulate(byte[] publicKey,
        KemParameterSet set = KemParameterSet.MlKem768)
    {
        var p = KyberParams(set);
        var gen = new KyberKemGenerator(new CryptoRng());

        // 返回的句柄持有敏感密钥材料，用 using 确保及时清零
        using var encapsulated = gen.GenerateEncapsulated(
            new KyberPublicKeyParameters(p, publicKey));

        return new KemSecret(
            encapsulated.GetEncapsulation(),
            encapsulated.GetSecret());
    }

    /// <summary>用私钥解封共享密钥（接收方调用）。</summary>
    public static byte[] Decapsulate(byte[] cipherText, byte[] secretKey,
        KemParameterSet set = KemParameterSet.MlKem768)
    {
        var p = KyberParams(set);
        var extractor = new KyberKemExtractor(new KyberPrivateKeyParameters(p, secretKey));
        return extractor.ExtractSecret(cipherText);
    }

    /// <summary>ML-KEM-768 密文长度（字节），用于网络协议预分配。</summary>
    public const int CipherTextLength768 = 1088;

    /// <summary>ML-KEM-768 公钥长度（字节）。</summary>
    public const int PublicKeyLength768 = 1184;
}

/// <summary>抗量子签名（ML-DSA / FIPS 204）。替代 RSA/ECDSA 签名。</summary>
public static class MlDsa
{
    private static DilithiumParameters DsaParams(DsaParameterSet set) => set switch
    {
        DsaParameterSet.MlDsa44 => DilithiumParameters.Dilithium2,
        DsaParameterSet.MlDsa65 => DilithiumParameters.Dilithium3,
        DsaParameterSet.MlDsa87 => DilithiumParameters.Dilithium5,
        _ => throw new ArgumentOutOfRangeException(nameof(set))
    };

    /// <summary>生成 ML-DSA 密钥对。</summary>
    public static DsaKeyPair GenerateKeyPair(DsaParameterSet set = DsaParameterSet.MlDsa65)
    {
        var p = DsaParams(set);
        var gen = new DilithiumKeyPairGenerator();
        gen.Init(new DilithiumKeyGenerationParameters(new CryptoRng(), p));
        var pair = gen.GenerateKeyPair();

        return new DsaKeyPair(
            ((DilithiumPublicKeyParameters)pair.Public).GetEncoded(),
            ((DilithiumPrivateKeyParameters)pair.Private).GetEncoded(),
            set);
    }

    /// <summary>
    /// 用私钥签名消息。
    /// Dilithium 私钥恢复需要公钥（内部含 KDF 反推公钥的机制），
    /// 因此密钥对中同时携带公钥以完成恢复。
    /// </summary>
    public static DsaSignature Sign(byte[] message, DsaKeyPair keyPair)
    {
        var p = DsaParams(keyPair.ParameterSet);
        var pub = new DilithiumPublicKeyParameters(p, keyPair.PublicKey);
        var priv = new DilithiumPrivateKeyParameters(p, keyPair.SecretKey, pub);

        var signer = new DilithiumSigner();
        signer.Init(true, priv);
        return new DsaSignature(signer.GenerateSignature(message), keyPair.ParameterSet);
    }

    /// <summary>用公钥验证签名。</summary>
    public static bool Verify(byte[] message, DsaSignature signature, byte[] publicKey)
    {
        try
        {
            var p = DsaParams(signature.ParameterSet);
            var signer = new DilithiumSigner();
            signer.Init(false, new DilithiumPublicKeyParameters(p, publicKey));
            return signer.VerifySignature(message, signature.Signature);
        }
        catch
        {
            // 签名格式非法同样视为验证失败，不向调用方抛异常
            return false;
        }
    }
}

/// <summary>
/// 桥接到 .NET 的 RandomNumberGenerator。
/// BouncyCastle 默认 SecureRandom 实现并非总是密码学安全，
/// 此处显式指定操作系统级 CSPRNG，符合金融合规要求。
/// </summary>
internal sealed class CryptoRng : SecureRandom
{
    public override void NextBytes(byte[] buffer) =>
        System.Security.Cryptography.RandomNumberGenerator.Fill(buffer);

    public override byte[] GenerateSeed(int length)
    {
        var seed = new byte[length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(seed);
        return seed;
    }
}
