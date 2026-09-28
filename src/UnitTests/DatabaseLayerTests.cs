// ===== 数据库层单元测试 =====
//
// 这些测试锁死三个「静默失效」缺陷。它们的共同特征是：
// 编译通过、运行不报错、单元测试也不红 —— 只有数据落盘时才发现不对。
//
//   1. 字段级加密链路：工厂没传 ICryptoService 时 ApplyFieldEncryption 直接 return，
//      敏感字段明文落盘，且没有任何日志。
//   2. 多 Schema 隔离：HasDefaultSchema 是全局设置，在贡献器循环里调用只有
//      最后一次生效，第二个插件起所有表都建到同一个 Schema。
//   3. 设计时插件发现：迁移命令下静态注册表为空，生成的迁移里没有插件表。

using BankingAgent.Base.Cryptography;
using BankingAgent.Base.Data;
using BankingAgent.Base.Data.Encryption;
using BankingAgent.Base.Plugins;
using BankingAgent.PluginSdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace UnitTests;

public class DatabaseLayerTests
{
    // ===== 测试替身 =====

    private sealed class AlphaEntity : BaseEntity
    {
        public string Owner { get; set; } = "";

        [Encrypted]
        public string Secret { get; set; } = "";
    }

    private sealed class BetaEntity : BaseEntity
    {
        public string Owner { get; set; } = "";
    }

    private sealed class AlphaContributor : IEntitySetContributor
    {
        public string PartitionName => "plugin_alpha";
        public DataClassification MaxClassification => DataClassification.L3;

        public void ConfigureModel(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<AlphaEntity>(e =>
            {
                e.ToTable("alpha_records");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedNever();
            });
    }

    private sealed class BetaContributor : IEntitySetContributor
    {
        public string PartitionName => "plugin_beta";
        public DataClassification MaxClassification => DataClassification.L2;

        public void ConfigureModel(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<BetaEntity>(e =>
            {
                e.ToTable("beta_records");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedNever();
            });
    }

    private static CryptoService NewCrypto(string keyId = "test-k1") =>
        new(Options.Create(new CryptoOptions
        {
            Mode = CryptoMode.Classic,
            DataEncryptionKey = Convert.ToBase64String(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            KeyId = keyId
        }), NullLogger<CryptoService>.Instance);

    private static BankingDbContext NewContext(
        IEnumerable<IEntitySetContributor> contributors,
        bool npgsql,
        ICryptoService? crypto = null)
    {
        var builder = new DbContextOptionsBuilder<BankingDbContext>();
        if (npgsql)
        {
            // 只构建模型，不建立连接，因此不需要真实数据库
            builder.UseNpgsql("Host=localhost;Database=model_only;Username=x;Password=y");
        }
        else
        {
            builder.UseSqlite("Data Source=:memory:");
        }

        return new BankingDbContext(builder.Options, contributors,
            new CurrentUserAccessor(), new DateTimeOffsetProvider(), crypto);
    }

    // ============================================================
    // 1. 字段级加密链路
    // ============================================================

    [Fact]
    public void FieldCipher_EncryptDecrypt_RoundTrips()
    {
        var cipher = new FieldCipher(NewCrypto());
        const string plain = "110101199001011234";

        var encrypted = cipher.Encrypt(plain);

        Assert.NotEqual(plain, encrypted);
        Assert.DoesNotContain("110101", encrypted, StringComparison.Ordinal);
        Assert.StartsWith(FieldCipher.Prefix, encrypted, StringComparison.Ordinal);
        Assert.Equal(plain, cipher.Decrypt(encrypted));
    }

    [Fact]
    public void FieldCipher_Encrypt_IsIdempotent()
    {
        // 幂等很关键：值转换器可能被重复调用，二次加密会让历史数据无法解密
        var cipher = new FieldCipher(NewCrypto());

        var once = cipher.Encrypt("sensitive");
        var twice = cipher.Encrypt(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void FieldCipher_Decrypt_LeavesLegacyPlaintextUntouched()
    {
        // 迁移前的历史数据是明文，解密必须原样返回而不是抛异常
        var cipher = new FieldCipher(NewCrypto());

        Assert.Equal("legacy-plain", cipher.Decrypt("legacy-plain"));
    }

    [Fact]
    public void FieldCipher_DifferentKey_CannotDecrypt()
    {
        var encrypted = new FieldCipher(NewCrypto("k1")).Encrypt("top-secret");
        var other = new FieldCipher(NewCrypto("k2"));

        Assert.ThrowsAny<Exception>(() => other.Decrypt(encrypted));
    }

    /// <summary>
    /// 核心回归：给了 crypto，[Encrypted] 字段必须带上加密转换器。
    /// 若工厂忘了传 ICryptoService，这条会失败 —— 这正是之前的缺陷。
    /// </summary>
    [Fact]
    public void Model_WithCrypto_AttachesConverterToEncryptedProperty()
    {
        using var db = NewContext([new AlphaContributor()], npgsql: false, crypto: NewCrypto());

        var property = db.Model.FindEntityType(typeof(AlphaEntity))!
            .FindProperty(nameof(AlphaEntity.Secret))!;

        Assert.NotNull(property.GetValueConverter());
    }

    /// <summary>
    /// 反向断言：没有 crypto 时不应装配转换器。
    /// 这条保证上面那条测试真的在测加密，而不是"转换器永远存在"。
    /// </summary>
    [Fact]
    public void Model_WithoutCrypto_HasNoConverter()
    {
        using var db = NewContext([new AlphaContributor()], npgsql: false, crypto: null);

        var property = db.Model.FindEntityType(typeof(AlphaEntity))!
            .FindProperty(nameof(AlphaEntity.Secret))!;

        Assert.Null(property.GetValueConverter());
    }

    [Fact]
    public void Model_UnmarkedStringProperty_IsNotEncrypted()
    {
        // 只加密标了 [Encrypted] 的字段，不能误伤其他列
        using var db = NewContext([new AlphaContributor()], npgsql: false, crypto: NewCrypto());

        var owner = db.Model.FindEntityType(typeof(AlphaEntity))!
            .FindProperty(nameof(AlphaEntity.Owner))!;

        Assert.Null(owner.GetValueConverter());
    }

    // ============================================================
    // 2. 多 Schema 隔离
    // ============================================================

    [Fact]
    public void Model_TwoContributors_ShareNoSchema()
    {
        // 旧实现下两个贡献器都会被塞进最后一次设置的 Schema 里
        using var db = NewContext(
            [new AlphaContributor(), new BetaContributor()], npgsql: true);

        var alphaSchema = db.Model.FindEntityType(typeof(AlphaEntity))!.GetSchema();
        var betaSchema = db.Model.FindEntityType(typeof(BetaEntity))!.GetSchema();

        Assert.Equal("plugin_alpha", alphaSchema);
        Assert.Equal("plugin_beta", betaSchema);
        Assert.NotEqual(alphaSchema, betaSchema);
    }

    [Fact]
    public void Model_TwoContributors_KeepOwnTableNames()
    {
        using var db = NewContext(
            [new AlphaContributor(), new BetaContributor()], npgsql: true);

        Assert.Equal("alpha_records",
            db.Model.FindEntityType(typeof(AlphaEntity))!.GetTableName());
        Assert.Equal("beta_records",
            db.Model.FindEntityType(typeof(BetaEntity))!.GetTableName());
    }

    [Fact]
    public void Model_ContributorOrderDoesNotMatter()
    {
        // 旧缺陷的表现就是"谁最后注册谁赢"，因此必须验证与顺序无关
        using var db1 = NewContext(
            [new AlphaContributor(), new BetaContributor()], npgsql: true);
        using var db2 = NewContext(
            [new BetaContributor(), new AlphaContributor()], npgsql: true);

        Assert.Equal(
            db1.Model.FindEntityType(typeof(AlphaEntity))!.GetSchema(),
            db2.Model.FindEntityType(typeof(AlphaEntity))!.GetSchema());
        Assert.Equal(
            db1.Model.FindEntityType(typeof(BetaEntity))!.GetSchema(),
            db2.Model.FindEntityType(typeof(BetaEntity))!.GetSchema());
    }

    [Fact]
    public void Model_Partitions_AreExposed()
    {
        using var db = NewContext(
            [new AlphaContributor(), new BetaContributor()], npgsql: true);

        Assert.Contains("plugin_alpha", db.Partitions);
        Assert.Contains("plugin_beta", db.Partitions);
    }

    [Fact]
    public void Model_Sqlite_HasNoSchema()
    {
        // SQLite 不支持 Schema，必须退化为无 Schema，而不是抛异常
        using var db = NewContext(
            [new AlphaContributor(), new BetaContributor()], npgsql: false);

        Assert.Null(db.Model.FindEntityType(typeof(AlphaEntity))!.GetSchema());
        Assert.Null(db.Model.FindEntityType(typeof(BetaEntity))!.GetSchema());
    }

    // ============================================================
    // 3. 设计时插件发现
    // ============================================================

    [Fact]
    public void Discover_MissingDirectory_ReturnsEmpty()
    {
        // 目录不存在必须静默跳过，否则 CI 里没有插件目录时迁移会直接崩
        var found = PluginContributionLoader.Discover(
            [Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid().ToString("N"))]);

        Assert.Empty(found);
    }

    [Fact]
    public void Discover_SkipsSdkAssembly()
    {
        // SDK 程序集自己实现了 IPluginEntryPoint，但绝不能被当成插件加载
        var dir = Path.GetDirectoryName(typeof(IPluginEntryPoint).Assembly.Location)!;
        var found = PluginContributionLoader.Discover([dir]);

        Assert.DoesNotContain(found, c =>
            Path.GetFileNameWithoutExtension(c.AssemblyPath)
                .EndsWith(".Plugin.Sdk", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Discover_ReportsErrorInsteadOfThrowing()
    {
        // 坏插件只能变成一条 Error，不能让整个迁移流程挂掉
        var dir = Directory.CreateTempSubdirectory("plugin-discovery-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "BankingAgent.Plugin.Broken.dll"), "not a real dll");

            var found = PluginContributionLoader.Discover([dir]);

            Assert.Single(found);
            Assert.NotNull(found[0].Error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Contribution_DefaultsAreEmptyNotNull()
    {
        var c = new PluginContribution { AssemblyPath = "x.dll" };

        Assert.NotNull(c.Contributors);
        Assert.Empty(c.Contributors);
        Assert.Null(c.Manifest);
        Assert.Null(c.Error);
    }
}