// ===== 字段级加密的实际生效验证 =====
//
// 这组测试回答一个问题：**加密真的生效了吗，还是只是"看起来配了"？**
//
// 背景：本项目的字段加密曾因两个原因完全失效（工厂没传 crypto、
// [Encrypted] 从未被检测）。修好之后还必须保证"修对了"，
// 尤其是下面这个极易踩中的陷阱：
//
//   随机加密（默认）用于**参与等值查询或唯一索引**的列时，
//   同明文每次密文都不同 —— `WHERE col = @v` 永远匹配不到，
//   唯一索引也形同虚设，而且不报任何错。
//
// 因此必须有 Searchable（确定性）模式，且必须有测试锁死两者语义。

using BankingAgent.Base.Cryptography;
using BankingAgent.Base.Data;
using BankingAgent.Base.Data.Encryption;
using BankingAgent.PluginSdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace UnitTests;

public class FieldEncryptionTests
{
    // ===== 测试实体：一个可搜索字段 + 一个不可搜索字段 =====

    private sealed class VaultEntity : BaseEntity
    {
        /// <summary>需要等值查询的列（例如幂等键）—— 必须确定性加密。</summary>
        [Encrypted(Searchable = true)]
        public string? LookupKey { get; set; }

        /// <summary>普通敏感列（例如账号）—— 使用随机加密。</summary>
        [Encrypted]
        public string? Secret { get; set; }

        /// <summary>未标注的普通列 —— 不得被加密。</summary>
        public string Plain { get; set; } = "";
    }

    private sealed class VaultContributor : IEntitySetContributor
    {
        public string PartitionName => "plugin_vault";
        public DataClassification MaxClassification => DataClassification.L3;

        public void ConfigureModel(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<VaultEntity>(e =>
            {
                e.ToTable("vault_records");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedNever();
                e.HasIndex(x => x.LookupKey).IsUnique();
            });
    }

    private static CryptoService NewCrypto() =>
        new(Options.Create(new CryptoOptions
        {
            Mode = CryptoMode.Classic,
            DataEncryptionKey = Convert.ToBase64String(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            KeyId = "test-k1"
        }), NullLogger<CryptoService>.Instance);

    private static BankingDbContext NewContext(ICryptoService? crypto) =>
        new(new DbContextOptionsBuilder<BankingDbContext>()
                .UseSqlite("Data Source=:memory:").Options,
            [new VaultContributor()],
            new CurrentUserAccessor(),
            new DateTimeOffsetProvider(),
            crypto);

    // ============================================================
    // 1. 转换器装配：必须按字段分别装配正确的那一种
    // ============================================================

    [Fact]
    public void Model_SearchableField_GetsDeterministicConverter()
    {
        using var db = NewContext(NewCrypto());
        var prop = db.Model.FindEntityType(typeof(VaultEntity))!
            .FindProperty(nameof(VaultEntity.LookupKey))!;

        var converter = prop.GetValueConverter();
        Assert.NotNull(converter);

        // 确定性语义：同一明文两次加密必须得到同一密文
        var a = (string)converter!.ConvertToProvider("same-value")!;
        var b = (string)converter.ConvertToProvider("same-value")!;
        Assert.Equal(a, b);

        // 不同明文必须得到不同密文
        var c = (string)converter.ConvertToProvider("other-value")!;
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Model_PlainEncryptedField_GetsRandomizedConverter()
    {
        using var db = NewContext(NewCrypto());
        var prop = db.Model.FindEntityType(typeof(VaultEntity))!
            .FindProperty(nameof(VaultEntity.Secret))!;

        var converter = prop.GetValueConverter();
        Assert.NotNull(converter);

        // 随机语义：同一明文两次加密必须得到不同密文（否则就是可搜索的，会泄露相等性）
        var a = (string)converter!.ConvertToProvider("same-value")!;
        var b = (string)converter.ConvertToProvider("same-value")!;
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Model_UnmarkedField_HasNoConverter()
    {
        using var db = NewContext(NewCrypto());
        var prop = db.Model.FindEntityType(typeof(VaultEntity))!
            .FindProperty(nameof(VaultEntity.Plain))!;

        Assert.Null(prop.GetValueConverter());
    }

    [Fact]
    public void Model_EncryptedFields_CarryAnnotation()
    {
        using var db = NewContext(NewCrypto());

        foreach (var name in new[] { nameof(VaultEntity.LookupKey), nameof(VaultEntity.Secret) })
        {
            var prop = db.Model.FindEntityType(typeof(VaultEntity))!.FindProperty(name)!;
            Assert.NotNull(prop!.FindAnnotation(BankingDbContext.EncryptedAnnotation));
        }

        // 可搜索标志也要落到注解上，便于迁移审阅与运维排查
        var lookup = db.Model.FindEntityType(typeof(VaultEntity))!
            .FindProperty(nameof(VaultEntity.LookupKey))!;
        Assert.Equal(true, lookup.FindAnnotation(BankingDbContext.EncryptedSearchableAnnotation)?.Value);
    }

    // ============================================================
    // 2. 往返：密文里不能出现明文，且能正确还原
    // ============================================================

    [Fact]
    public void Converter_EncryptThenDecrypt_RoundTrips()
    {
        using var db = NewContext(NewCrypto());
        var converter = db.Model.FindEntityType(typeof(VaultEntity))!
            .FindProperty(nameof(VaultEntity.Secret))!.GetValueConverter()!;

        const string plain = "6222020200000001";
        var stored = (string)converter.ConvertToProvider(plain)!;

        Assert.DoesNotContain("62220202", stored, StringComparison.Ordinal);
        Assert.StartsWith(FieldCipher.Prefix, stored, StringComparison.Ordinal);
        Assert.Equal(plain, converter.ConvertFromProvider(stored));
    }

    [Fact]
    public void Converter_IsIdempotent_SoDoubleEncryptionCannotHappen()
    {
        // 值转换器可能被重复调用；二次加密会让历史数据永久无法解密
        using var db = NewContext(NewCrypto());
        var cipher = new FieldCipher(NewCrypto());

        var once = cipher.Encrypt("sensitive");
        Assert.Equal(once, cipher.Encrypt(once));
        Assert.Equal("sensitive", cipher.Decrypt(cipher.Decrypt(once)));
    }

    [Fact]
    public void Converter_WithoutCrypto_LeavesDataPlain()
    {
        // 反向断言：没有 crypto 时不应有转换器 ——
        // 保证上面那些"有转换器"的断言真的在测加密，而不是恒真
        using var db = NewContext(crypto: null);
        var prop = db.Model.FindEntityType(typeof(VaultEntity))!
            .FindProperty(nameof(VaultEntity.Secret))!;

        Assert.Null(prop.GetValueConverter());
    }

    // ============================================================
    // 3. 真实落库：确认磁盘上确实没有明文
    // ============================================================

    [Fact]
    public async Task Persistence_StoresCiphertextOnDisk()
    {
        // 用真实文件库而不是内存库，才能检查"磁盘上到底存了什么"
        var dbPath = Path.Combine(Path.GetTempPath(), $"enc-{Guid.NewGuid():N}.db");
        var crypto = NewCrypto();

        try
        {
            using (var db = new BankingDbContext(
                new DbContextOptionsBuilder<BankingDbContext>()
                    .UseSqlite($"Data Source={dbPath}").Options,
                [new VaultContributor()],
                new CurrentUserAccessor(),
                new DateTimeOffsetProvider(),
                crypto))
            {
                await db.Database.EnsureCreatedAsync();
                db.Add(new VaultEntity
                {
                    LookupKey = "session-abc",
                    Secret = "6222020200000009",
                    Plain = "not-sensitive"
                });
                await db.SaveChangesAsync();
            }

            // 关键校验：查询"可搜索"列必须能命中（确定性加密才做得到）
            // 注意：必须让上下文完全释放再读文件 —— SQLite 连接池会持有
            // 文件句柄，否则读盘会抛 IOException 而不是断言失败。
            {
                using var db = new BankingDbContext(
                    new DbContextOptionsBuilder<BankingDbContext>()
                        .UseSqlite($"Data Source={dbPath}").Options,
                    [new VaultContributor()],
                    new CurrentUserAccessor(),
                    new DateTimeOffsetProvider(),
                    crypto);

                var found = await db.Set<VaultEntity>()
                    .FirstOrDefaultAsync(x => x.LookupKey == "session-abc");

                Assert.NotNull(found);
                // 取出后必须是明文（转换器负责解密）
                Assert.Equal("6222020200000009", found!.Secret);
            }

            // 关闭连接池，确保文件句柄已释放（否则无法读取磁盘内容）
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            // 直接把磁盘上的内容当文本读：明文不得出现。
            // 同时读 .db 与 -wal —— SQLite 默认 WAL 模式下，
            // 未 checkpoint 的数据只存在于 -wal 中（这一点曾让我误判"没落库"）。
            var onDisk = new System.Text.StringBuilder();
            foreach (var f in new[] { dbPath, dbPath + "-wal" })
            {
                if (File.Exists(f))
                {
                    var raw = await File.ReadAllBytesAsync(f);
                    onDisk.Append(System.Text.Encoding.UTF8.GetString(raw));
                }
            }

            Assert.DoesNotContain("6222020200000009", onDisk.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("session-abc", onDisk.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            {
                try { if (File.Exists(f)) File.Delete(f); } catch { /* 清理失败不影响断言 */ }
            }
        }
    }
}