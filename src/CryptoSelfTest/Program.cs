// ===== 密码学自检 =====
// 验证 ML-KEM / ML-DSA / AES-256-GCM / 密钥轮换 全部真实可用
using System.Diagnostics;
using BankingAgent.Base.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var pass = 0;
var fail = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  [PASS] {name}  {detail}"); }
    else { fail++; Console.WriteLine($"  [FAIL] {name}  {detail}"); }
}

Console.WriteLine(new string('=', 72));
Console.WriteLine(" 密码学自检（含抗量子算法）");
Console.WriteLine(new string('=', 72));

// ---------- 1. ML-KEM 密钥封装 ----------
Console.WriteLine();
Console.WriteLine("1. ML-KEM（抗量子密钥封装）");

foreach (var ps in new[] { KemParameterSet.MlKem512, KemParameterSet.MlKem768, KemParameterSet.MlKem1024 })
{
    var sw = Stopwatch.StartNew();
    var kp = MlKem.GenerateKeyPair(ps);
    var enc = MlKem.Encapsulate(kp.PublicKey, ps);
    var dec = MlKem.Decapsulate(enc.CipherText, kp.SecretKey, ps);
    sw.Stop();

    var match = dec.SequenceEqual(enc.Secret);
    Check($"{ps} 封装/解封一致", match,
        $"公钥 {kp.PublicKey.Length}B 密文 {enc.CipherText.Length}B 密钥 {enc.Secret.Length}B 耗时 {sw.ElapsedMilliseconds}ms");
}

// ---------- 2. ML-DSA 签名 ----------
Console.WriteLine();
Console.WriteLine("2. ML-DSA（抗量子签名）");

foreach (var ps in new[] { DsaParameterSet.MlDsa44, DsaParameterSet.MlDsa65, DsaParameterSet.MlDsa87 })
{
    var sw = Stopwatch.StartNew();
    var kp = MlDsa.GenerateKeyPair(ps);
    var msg = System.Text.Encoding.UTF8.GetBytes("转账金额 30000 元，账户 ****-1234");

    var sig = MlDsa.Sign(msg, kp);
    var okValid = MlDsa.Verify(msg, sig, kp.PublicKey);
    var okTamper = MlDsa.Verify(
        System.Text.Encoding.UTF8.GetBytes("转账金额 99999 元，账户 ****-1234"), sig, kp.PublicKey);
    sw.Stop();

    Check($"{ps} 签名验证", okValid, $"签名 {sig.Signature.Length}B 耗时 {sw.ElapsedMilliseconds}ms");
    Check($"{ps} 篡改检测", !okTamper, "被篡改的消息验证失败");
}

// ---------- 3. 对称加密 ----------
Console.WriteLine();
Console.WriteLine("3. AES-256-GCM（对称加密）");

var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
var opts = Options.Create(new CryptoOptions
{
    Mode = CryptoMode.Hybrid,
    DataEncryptionKey = key,
    KeyId = "test-k1"
});
var crypto = new CryptoService(opts, NullLogger<CryptoService>.Instance);

var sensitive = "110101199001011234";
var payload = crypto.Encrypt(System.Text.Encoding.UTF8.GetBytes(sensitive), "user:u_demo01");
var recovered = System.Text.Encoding.UTF8.GetString(
    crypto.Decrypt(payload, "user:u_demo01"));

Check("加解密一致", recovered == sensitive, "");
Check("密文不含原文", !payload.CipherText.Contains("110101"), "");
Check("密钥标识透传", payload.KeyId == "test-k1", $"kid={payload.KeyId}");
Check("算法标识", payload.Algorithm == "AES-256-GCM", payload.Algorithm);

// AAD 绑定：换 AAD 应解密失败
var aadFail = false;
try { crypto.Decrypt(payload, "user:other"); }
catch (System.Security.Cryptography.AuthenticationTagMismatchException) { aadFail = true; }
catch { aadFail = true; }
Check("AAD 绑定生效（换上下文解密失败）", aadFail, "");

// ---------- 4. 密钥轮换 ----------
Console.WriteLine();
Console.WriteLine("4. 密钥轮换与吊销");

var before = crypto.Encrypt(System.Text.Encoding.UTF8.GetBytes("轮换前数据"));
crypto.RotateKey();
var after = crypto.Encrypt(System.Text.Encoding.UTF8.GetBytes("轮换后数据"));

Check("轮换后密钥 ID 变化", before.KeyId != after.KeyId, $"{before.KeyId} → {after.KeyId}");
Check("旧密钥仍可解密历史数据",
    System.Text.Encoding.UTF8.GetString(crypto.Decrypt(before)) == "轮换前数据", "");

// ---------- 5. 混合密钥交换 ----------
Console.WriteLine();
Console.WriteLine("5. 混合密钥交换（X25519 + ML-KEM-768）");

var swKex = Stopwatch.StartNew();
var alice = crypto.GenerateKeyExchange();
var bob = crypto.GenerateKeyExchange();
swKex.Stop();

Check("双方密钥交换完成", alice.SharedSecret.Length == 32 && bob.SharedSecret.Length == 32,
    $"耗时 {swKex.ElapsedMilliseconds}ms");
Check("含 X25519 公钥", alice.ClassicPublicKey.Length == 32, $"{alice.ClassicPublicKey.Length}B");
Check("含 ML-KEM-768 公钥", alice.PostQuantumPublicKey.Length == 1184, $"{alice.PostQuantumPublicKey.Length}B");
Check("模式标识正确", alice.Mode == CryptoMode.Hybrid, alice.Mode.ToString());

// ---------- 6. 弱密钥拒绝 ----------
Console.WriteLine();
Console.WriteLine("6. 弱密钥拒绝");

try
{
    var weak = Options.Create(new CryptoOptions { DataEncryptionKey = "dG9vc2hvcnQ=" });
    _ = new CryptoService(weak, NullLogger<CryptoService>.Instance);
    Check("拒绝短密钥", false, "未抛异常");
}
catch (InvalidOperationException) { Check("拒绝短密钥", true, "已拒绝 8 字节密钥"); }

try
{
    var empty = Options.Create(new CryptoOptions { DataEncryptionKey = null });
    _ = new CryptoService(empty, NullLogger<CryptoService>.Instance);
    Check("拒绝空密钥", false, "未抛异常");
}
catch (InvalidOperationException) { Check("拒绝空密钥", true, "已拒绝"); }

// ---------- 汇总 ----------
Console.WriteLine();
Console.WriteLine(new string('=', 72));
Console.WriteLine($" 通过: {pass}    失败: {fail}");
Console.WriteLine(fail == 0 ? " *** 密码学自检全部通过 ***" : $" *** {fail} 项失败 ***");
Console.WriteLine(new string('=', 72));
return fail == 0 ? 0 : 1;
