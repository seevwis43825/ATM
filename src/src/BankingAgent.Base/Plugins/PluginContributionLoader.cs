// ===== 插件的设计时发现（供 EF Core 迁移使用）=====
//
// 为什么需要这个文件：
//   PluginRegistry 是「运行期」加载器，它依赖宿主已经 Build 出来的 IServiceProvider、
//   签名验证器与日志工厂。而 `dotnet ef migrations add` 走的是设计时路径，
//   没有任何宿主容器。结果是 DesignTimeFactory 里的
//   PluginContributorRegistry.ResolveAll() 永远返回空集合 ——
//   生成的迁移里一个插件表都没有，而且不报错。
//
//   本加载器用最小依赖复刻同一套发现逻辑：
//   扫目录 → 载入程序集 → 找到 IPluginEntryPoint → 调用 ConfigureServices
//   → 从临时容器里取出 IEntitySetContributor。
//
// 保持与运行期一致的点：
//   * 同样按 "BankingAgent.Plugin.*.dll" 匹配，同样跳过 SDK 程序集
//   * 同样使用可回收的 AssemblyLoadContext 隔离

using System.Reflection;
using System.Runtime.Loader;
using BankingAgent.Base.Data;
using BankingAgent.PluginSdk;
using Microsoft.Extensions.DependencyInjection;

namespace BankingAgent.Base.Plugins;

/// <summary>一次设计时发现的结果。</summary>
public sealed record PluginContribution
{
    /// <summary>插件程序集路径。</summary>
    public required string AssemblyPath { get; init; }

    /// <summary>插件清单，解析失败时为 null。</summary>
    public PluginManifest? Manifest { get; init; }

    /// <summary>该插件声明的数据分区贡献器。</summary>
    public IReadOnlyList<IEntitySetContributor> Contributors { get; init; } = [];

    /// <summary>失败原因（成功时为 null）。</summary>
    public string? Error { get; init; }
}

/// <summary>
/// 设计时插件贡献器加载器。
/// 运行期不使用本类（运行期走 PluginRegistry，能力更完整）。
/// </summary>
public static class PluginContributionLoader
{
    /// <summary>
    /// 在给定目录集合中查找插件并取出全部数据分区贡献器。
    /// 目录不存在会被静默跳过，便于在 CI 与本地都直接运行。
    /// </summary>
    public static IReadOnlyList<PluginContribution> Discover(IEnumerable<string> directories)
    {
        var results = new List<PluginContribution>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in directories)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;

            foreach (var dll in Directory.EnumerateFiles(dir, "BankingAgent.Plugin.*.dll"))
            {
                var fileName = Path.GetFileNameWithoutExtension(dll);

                // 与 PluginRegistry.LoadFromDirectoryAsync 保持一致
                if (fileName.EndsWith(".Plugin.Sdk", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(Path.GetFullPath(dll))) continue;

                results.Add(LoadSingle(dll));
            }
        }

        return results;
    }

    /// <summary>
    /// 探测仓库内可能存放插件 DLL 的位置。
    /// 迁移命令通常在 src/BankingAgent.Base 或仓库根目录下执行，因此同时覆盖几种布局。
    /// </summary>
    public static IReadOnlyList<string> ProbeDefaultDirectories(string? explicitDirectory = null)
    {
        var dirs = new List<string>();

        if (!string.IsNullOrWhiteSpace(explicitDirectory)) dirs.Add(explicitDirectory);

        // 环境变量优先，便于 CI 显式指定
        var fromEnv = Environment.GetEnvironmentVariable("BANKING_PLUGINS_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv)) dirs.Add(fromEnv);

        // 宿主输出目录（构建后插件会被复制到这里）
        dirs.Add(Path.Combine(AppContext.BaseDirectory, "plugins"));

        // 源代码树布局：从当前工作目录与程序集目录向上回溯，找到含插件 DLL 的 plugins/ 目录
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var probe = new DirectoryInfo(start);
            for (var depth = 0; probe is not null && depth < 8; depth++, probe = probe.Parent)
            {
                var candidate = Path.Combine(probe.FullName, "plugins");
                if (Directory.Exists(candidate) &&
                    Directory.EnumerateFiles(candidate, "BankingAgent.Plugin.*.dll").Any())
                {
                    dirs.Add(candidate);
                    break;
                }
            }
        }

        return dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static PluginContribution LoadSingle(string assemblyPath)
    {
        AssemblyLoadContext? alc = null;
        try
        {
            var sdkAssemblyName = typeof(IPluginEntryPoint).Assembly.GetName().Name!;
            alc = new DesignTimeLoadContext(assemblyPath, sdkAssemblyName);
            var asm = alc.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));

            var entryPointType = asm.GetTypes()
                .FirstOrDefault(t => typeof(IPluginEntryPoint).IsAssignableFrom(t) && !t.IsAbstract);

            if (entryPointType is null)
            {
                return new PluginContribution
                {
                    AssemblyPath = assemblyPath,
                    Error = "未实现 IPluginEntryPoint"
                };
            }

            var entryPoint = (IPluginEntryPoint)Activator.CreateInstance(entryPointType)!;
            var manifest = entryPoint.GetManifest();

            // 用临时容器承接插件注册的服务，只取贡献器，不启动任何后台任务
            var services = new ServiceCollection();
            entryPoint.ConfigureServices(services, new DesignTimePluginContext(manifest));

            using var provider = services.BuildServiceProvider();
            var contributors = provider.GetServices<IEntitySetContributor>().ToList();

            return new PluginContribution
            {
                AssemblyPath = assemblyPath,
                Manifest = manifest,
                Contributors = contributors
            };
        }
        catch (Exception ex)
        {
            // 设计时发现失败不应该让迁移命令崩溃，但要留下明确原因
            TryUnload(alc);
            return new PluginContribution
            {
                AssemblyPath = assemblyPath,
                Error = $"{ex.GetType().Name}: {ex.Message}"
            };
        }

        // 成功路径刻意不 Unload：贡献器实例与 manifest 都来自该 ALC，
        // 卸载会让后续模型构建拿到已失效的类型。调用方需在整个迁移过程中持有引用。
    }

    private static void TryUnload(AssemblyLoadContext? alc)
    {
        try { alc?.Unload(); } catch { /* 卸载失败不影响迁移 */ }
    }
}

/// <summary>
/// 设计时加载上下文。
/// SDK 程序集必须共享，否则插件里的 IPluginEntryPoint 与宿主侧不是同一个类型，
/// IsAssignableFrom 会全部失败。
/// </summary>
internal sealed class DesignTimeLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _sharedAssemblyName;

    public DesignTimeLoadContext(string pluginPath, string sharedAssemblyName)
        : base($"DesignTime:{Path.GetFileNameWithoutExtension(pluginPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
        _sharedAssemblyName = sharedAssemblyName;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, _sharedAssemblyName, StringComparison.OrdinalIgnoreCase))
        {
            return null;   // 回退到默认上下文，保证契约类型同一
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}

/// <summary>设计时插件上下文。不提供服务定位，插件只能注册服务。</summary>
internal sealed class DesignTimePluginContext(PluginManifest manifest) : IPluginContext
{
    public PluginManifest Manifest { get; } = manifest;

    public IServiceProvider Services { get; } =
        new ServiceCollection().BuildServiceProvider();

    public string PartitionName => Manifest.Id.Value.ToLowerInvariant().Replace('.', '_');

    // 注意：必须写全 Microsoft.Extensions.Logging.ILogger（非泛型）。
    // 直接写 ILogger 会被解析成 Microsoft.Extensions.Logging.ILogger<T>，
    // 与 IPluginContext.Logger 的签名不匹配。
    public Microsoft.Extensions.Logging.ILogger Logger { get; } =
        Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
}