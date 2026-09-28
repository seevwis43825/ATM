// ===== DbContext 工厂与设计时工厂 =====
// 1. BankingDbContextFactory —— 运行时用，参数化工厂（EF 标准写法）
// 2. DesignTimeFactory     —— 迁移时用，能访问 DI（收集插件贡献器与加密服务）

using BankingAgent.Base.Cryptography;
using BankingAgent.Base.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Base.Data;

/// <summary>运行时 DbContext 工厂。EF 的参数化工厂标准写法。</summary>
/// <remarks>
/// 三个必须显式传入的依赖，缺一个都会静默出错：
/// <list type="bullet">
/// <item><c>contributors</c> —— 插件贡献器集合。为空则插件实体不进模型，
/// 建表时不会创建任何插件表（表"凭空消失"，且不报错）。</item>
/// <item><c>crypto</c> —— 字段级加密服务。为空则 <see cref="BankingDbContext"/>
/// 直接跳过 <c>[Encrypted]</c> 转换器装配，敏感字段明文落盘。</item>
/// <item><c>diagnostics</c> —— 慢查询与命令计数拦截器。</item>
/// </list>
/// </remarks>
public sealed class BankingDbContextFactory(
    IServiceProvider rootProvider,
    DatabaseOptions options,
    DatabaseDiagnosticsInterceptor? diagnostics = null) : IDbContextFactory<BankingDbContext>
{
    /// <inheritdoc />
    public BankingDbContext CreateDbContext()
    {
        var builder = new DbContextOptionsBuilder<BankingDbContext>();
        DatabaseConfigurator.Configure(builder, options);
        if (diagnostics is not null)
        {
            builder.AddInterceptors(diagnostics);
        }

        // 插件贡献器来自静态注册表：插件在宿主 Build 之前加载并注册，
        // 工厂是单例，创建时机晚于注册，因此每次取快照是安全的。
        var contributors = PluginContributorRegistry.ResolveAll();

        // ICryptoService 是可选依赖：未配置密钥时 CryptoService 构造即抛异常，
        // 此时按"未启用字段加密"处理，由 SecurityGate 在生产环境拦截启动。
        var crypto = rootProvider.GetService<BankingAgent.Base.Cryptography.ICryptoService>();

        return new BankingDbContext(
            builder.Options,
            contributors,
            rootProvider.GetService<ICurrentUserAccessor>() ?? CurrentUserAccessor.Static,
            rootProvider.GetService<DateTimeOffsetProvider>() ?? DateTimeOffsetProvider.Static,
            crypto);
    }

    /// <inheritdoc />
    public Task<BankingDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());
}

/// <summary>
/// 设计时工厂。`dotnet ef migrations add` 时使用。
/// 与运行时工厂的关键差异：能注入插件贡献器与加密服务，
/// 否则迁移生成的表结构会缺少插件实体与加密转换器。
/// </summary>
/// <remarks>
/// 为什么默认目标是 PostgreSQL 而不是 SQLite：
/// 迁移文件里固化的是 <b>Provider 相关的列类型与 Schema</b>（uuid / numeric(18,2) /
/// CreateSchema 等）。生产环境是 PostgreSQL，若用 SQLite 生成迁移，
/// 迁移里就不含 Schema 隔离，也不会创建 plugin_transfer 等 Schema，
/// 到生产环境会直接失败。因此迁移一律以生产 Provider 为准，
/// SQLite 走 DatabaseInitMode.EnsureCreated。
/// </remarks>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<BankingDbContext>
{
    /// <summary>创建用于迁移生成的上下文。</summary>
    public BankingDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<BankingDbContext>();

        var provider = ResolveProvider(args);
        var connectionString = ResolveConnectionString(provider);

        // 迁移只需要 Provider 做类型映射，不需要真实连上数据库，
        // 所以这里的连接串是占位值，不会发起连接。
        Console.WriteLine($"[迁移] 目标 Provider = {provider}（可用 --provider 或 BANKING_DB_PROVIDER 覆盖）");

        switch (provider)
        {
            case DatabaseProvider.PostgreSql:
                options.UseNpgsql(connectionString,
                    npgsql => npgsql.MigrationsAssembly(
                        typeof(BankingDbContext).Assembly.GetName().Name));
                break;
            case DatabaseProvider.SqlServer:
                options.UseSqlServer(connectionString,
                    sql => sql.MigrationsAssembly(
                        typeof(BankingDbContext).Assembly.GetName().Name));
                break;
            default:
                options.UseSqlite(connectionString,
                    sqlite => sqlite.MigrationsAssembly(
                        typeof(BankingDbContext).Assembly.GetName().Name));
                break;
        }

        // 收集全部已注册的插件贡献器。
        // 关键：迁移命令下静态注册表通常是空的（没有宿主容器去填充它），
        // 因此这里必须主动做一次设计时发现，否则生成的迁移里不会包含任何插件表。
        var contributors = new List<IEntitySetContributor>(PluginContributorRegistry.ResolveAll());

        foreach (var dir in PluginContributionLoader.ProbeDefaultDirectories())
        {
            foreach (var contribution in PluginContributionLoader.Discover([dir]))
            {
                if (contribution.Error is not null)
                {
                    Console.Error.WriteLine(
                        $"[迁移] 插件发现失败 {Path.GetFileName(contribution.AssemblyPath)}: {contribution.Error}");
                    continue;
                }

                contributors.AddRange(contribution.Contributors);
                Console.WriteLine(
                    $"[迁移] 已纳入插件 {contribution.Manifest?.Id.Value} " +
                    $"（贡献器 {contribution.Contributors.Count} 个）");
            }
        }

        if (contributors.Count == 0)
        {
            Console.Error.WriteLine(
                "[迁移] 警告：未发现任何插件数据分区贡献器。生成的迁移将不包含插件表。\n" +
                "        请先构建解决方案（dotnet build BankingAgent.slnx），" +
                "或用 BANKING_PLUGINS_DIR 环境变量指定插件 DLL 目录。");
        }
        else
        {
            // 让同一次进程内后续的模型构建也能看到同一批贡献器
            PluginContributorRegistry.Register(contributors);
        }

        // 迁移阶段使用占位加密服务（真实密钥不在设计时环境）
        var crypto = new MigrationOnlyCrypto();

        return new BankingDbContext(options.Options, contributors,
            new CurrentUserAccessor(), new DateTimeOffsetProvider(), crypto);
    }

    private static DatabaseProvider ResolveProvider(string[] args)
    {
        // 命令行参数优先：--provider PostgreSql
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--provider" or "-p" &&
                Enum.TryParse<DatabaseProvider>(args[i + 1], true, out var p))
            {
                return p;
            }
        }

        // 环境变量其次
        var env = Environment.GetEnvironmentVariable("BANKING_DB_PROVIDER");
        if (!string.IsNullOrEmpty(env) &&
            Enum.TryParse<DatabaseProvider>(env, true, out var ep))
        {
            return ep;
        }

        // 默认 PostgreSQL：迁移必须反映生产 Provider 的列类型与 Schema 隔离。
        // 需要 SQLite 版迁移时才显式指定 --provider Sqlite。
        return DatabaseProvider.PostgreSql;
    }

    private static string ResolveConnectionString(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.PostgreSql =>
            Environment.GetEnvironmentVariable("BANKING_DB_POSTGRES")
            ?? "Host=localhost;Database=banking;Username=postgres;Password=postgres",

        DatabaseProvider.SqlServer =>
            Environment.GetEnvironmentVariable("BANKING_DB_SQLSERVER")
            ?? "Server=localhost;Database=banking;User Id=sa;Password=Pass@word;TrustServerCertificate=True",

        _ => "Data Source=bankingagent-migration.db"
    };
}

/// <summary>
/// 迁移专用加密服务。
/// 设计时生成迁移只需要一个"能把密文字段映射成字符串列"的转换器，
/// 不需要真实密钥。真实加密发生在运行时（ICryptoService）。
/// </summary>
internal sealed class MigrationOnlyCrypto : ICryptoService
{
    public CryptoMode Mode => CryptoMode.Classic;

    public string CurrentKeyId => "migration";

    public IReadOnlyCollection<string> ActiveKeyIds => ["migration"];

    public EncryptedPayload Encrypt(ReadOnlySpan<byte> plaintext, string? aad = null)
    {
        // 迁移阶段输出可读的占位符，最终列类型是 string
        return new EncryptedPayload
        {
            CipherText = System.Text.Encoding.UTF8.GetString(plaintext),
            Nonce = "",
            AuthTag = "",
            Algorithm = "PLAINTEXT-MIGRATION",
            KeyId = "migration"
        };
    }

    /// <summary>
    /// 迁移阶段的确定性加密。
    /// 设计时不需要真实密码学：列类型仍是 string，且迁移只关心表结构。
    /// 保持"同输入同输出"即可，与运行期语义一致。
    /// </summary>
    public EncryptedPayload EncryptDeterministic(ReadOnlySpan<byte> plaintext, string? aad = null) =>
        new()
        {
            CipherText = System.Text.Encoding.UTF8.GetString(plaintext),
            Nonce = "",
            AuthTag = "",
            Algorithm = "PLAINTEXT-MIGRATION-DET",
            KeyId = "migration"
        };

    public byte[] Decrypt(EncryptedPayload payload, string? aad = null) =>
        System.Text.Encoding.UTF8.GetBytes(payload.CipherText);

    public string EncryptString(string plaintext, string? aad = null) => plaintext;

    public string DecryptString(EncryptedPayload payload, string? aad = null) => payload.CipherText;

    public HybridKeyExchange GenerateKeyExchange() => new()
    {
        ClassicPublicKey = [],
        PostQuantumPublicKey = [],
        SharedSecret = [],
        Mode = CryptoMode.Classic
    };

    public byte[] CompleteKeyExchange(HybridKeyExchange local, HybridKeyExchange peer) => [];

    public DsaSignature SignPqc(byte[] message) => new([], DsaParameterSet.MlDsa44);

    public bool VerifyPqc(byte[] message, DsaSignature signature, byte[] publicKey) => true;

    public void RotateKey() { }
}
