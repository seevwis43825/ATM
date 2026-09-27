// ===== 插件加载器（热插拔）=====
// 使用可回收的 AssemblyLoadContext 实现真正的插件隔离：
// 1. 每个插件独立 ALC，类型不冲突
// 2. 卸载时整棵程序集树可回收，支持热更新
// 3. 依赖 SDK 契约程序集走 Default 上下文共享，避免类型重复

namespace BankingAgent.Base.Plugins;

using System.Reflection;
using System.Runtime.Loader;
using BankingAgent.PluginSdk;
using BankingAgent.PluginSdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>插件加载结果。</summary>
public sealed record PluginLoadResult
{
    public required PluginManifest Manifest { get; init; }
    public required string AssemblyPath { get; init; }
    public DateTimeOffset LoadedAt { get; init; }
    public IReadOnlyList<string> RegisteredServices { get; init; } = [];
    public IReadOnlyList<string> RegisteredAgents { get; init; } = [];
    public bool IsActive { get; init; } = true;
}

/// <summary>可回收的插件加载上下文。</summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly HashSet<string> _sharedAssemblies;

    public PluginLoadContext(string pluginPath, string sdkAssemblyName)
        : base(name: "PluginALC::" + Path.GetFileNameWithoutExtension(pluginPath), isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
        _sharedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sdkAssemblyName };
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (_sharedAssemblies.Contains(assemblyName.Name ?? string.Empty))
        {
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }

    /// <inheritdoc />
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}

/// <summary>插件启动钩子。由 Host 实现，桥接到插件的 OnStartingAsync。</summary>
public interface IPluginStartupHook
{
    Task StartAsync(PluginId id, CancellationToken ct);
}

/// <summary>插件停用钩子。由 Host 实现。</summary>
public interface IPluginShutdownHook
{
    Task StopAsync(PluginId id, CancellationToken ct);
}

/// <summary>插件注册表。负责插件的加载、激活、停用与查询。</summary>
public sealed class PluginRegistry
{
    private readonly IServiceProvider _rootServices;
    private readonly ILogger<PluginRegistry> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Dictionary<string, PluginLoadResult> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PluginLoadContext> _loadContexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly IServiceCollection _serviceCollection = new ServiceCollection();
    private readonly object _gate = new();
    private readonly string _sdkAssemblyName = typeof(IPluginEntryPoint).Assembly.GetName().Name!;
    private IServiceProvider? _buildProvider;

    /// <summary>构造插件注册表。</summary>
    public PluginRegistry(
        IServiceProvider rootServices,
        ILogger<PluginRegistry> logger,
        ILoggerFactory loggerFactory)
    {
        _rootServices = rootServices;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <summary>当前已加载插件。</summary>
    public IReadOnlyCollection<PluginLoadResult> LoadedPlugins
    {
        get { lock (_gate) { return _plugins.Values.ToList(); } }
    }

    /// <summary>插件注册使用的服务集合。</summary>
    public IServiceCollection Services => _serviceCollection;

    /// <summary>注入已构建的服务提供器。</summary>
    public void AttachProvider(IServiceProvider provider) => _buildProvider = provider;

    /// <summary>从指定目录扫描并加载全部插件程序集，随后按依赖顺序启动。</summary>
    public async Task<IReadOnlyList<PluginLoadResult>> LoadFromDirectoryAsync(string directory, CancellationToken ct = default)
    {
        if (!Directory.Exists(directory))
        {
            _logger.LogWarning("插件目录不存在: {Directory}", directory);
            return [];
        }

        var loaded = new List<PluginLoadResult>();

        foreach (var dll in Directory.EnumerateFiles(directory, "BankingAgent.Plugin.*.dll"))
        {
            var fileName = Path.GetFileNameWithoutExtension(dll);
            if (fileName.EndsWith(".Plugin.Sdk", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var result = await LoadAsync(dll, ct);
                if (result is not null) loaded.Add(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "插件加载失败: {Plugin}", dll);
            }
        }

        var ordered = TopologicalSort(loaded);
        foreach (var plugin in ordered)
        {
            await StartPluginAsync(plugin.Manifest.Id, ct);
        }

        return ordered;
    }

    /// <summary>加载单个插件程序集，但暂不启动。</summary>
    public Task<PluginLoadResult?> LoadAsync(string assemblyPath, CancellationToken ct = default)
    {
        var alc = new PluginLoadContext(assemblyPath, _sdkAssemblyName);
        var asm = alc.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));

        var entryPointType = asm.GetTypes()
            .FirstOrDefault(t => typeof(IPluginEntryPoint).IsAssignableFrom(t) && !t.IsAbstract);

        if (entryPointType is null)
        {
            _logger.LogWarning("插件未实现 IPluginEntryPoint，已跳过: {Plugin}", assemblyPath);
            alc.Unload();
            return Task.FromResult<PluginLoadResult?>(null);
        }

        var entryPoint = (IPluginEntryPoint)Activator.CreateInstance(entryPointType)!;
        var manifest = entryPoint.GetManifest();

        lock (_gate)
        {
            if (_plugins.TryGetValue(manifest.Id.Value, out var existing))
            {
                _logger.LogWarning("插件 {Id} 已加载，跳过重复加载", manifest.Id);
                alc.Unload();
                return Task.FromResult<PluginLoadResult?>(existing);
            }
        }

        var context = new PluginContext(manifest, _rootServices, _loggerFactory);
        entryPoint.ConfigureServices(_serviceCollection, context);

        var result = new PluginLoadResult
        {
            Manifest = manifest,
            AssemblyPath = Path.GetFullPath(assemblyPath),
            LoadedAt = DateTimeOffset.UtcNow,
            RegisteredAgents = asm.GetTypes()
                .Where(t => typeof(IBankingAgent).IsAssignableFrom(t) && !t.IsAbstract)
                .Select(t => t.Name)
                .ToList()
        };

        lock (_gate)
        {
            _plugins[manifest.Id.Value] = result;
            _loadContexts[manifest.Id.Value] = alc;
        }

        _logger.LogInformation("插件已加载: {Id} v{Version} - {Name}（Agent {Count} 个）",
            manifest.Id, manifest.Version, manifest.Name, result.RegisteredAgents.Count);

        return Task.FromResult<PluginLoadResult?>(result);
    }

    /// <summary>启动插件。需先调用 AttachProvider。</summary>
    public async Task<bool> StartPluginAsync(PluginId id, CancellationToken ct = default)
    {
        PluginLoadResult plugin;
        lock (_gate)
        {
            if (!_plugins.TryGetValue(id.Value, out var found)) return false;
            plugin = found;
        }

        if (plugin.IsActive) return true;

        if (_buildProvider?.GetService<IPluginStartupHook>() is { } hook)
        {
            await hook.StartAsync(id, ct);
        }

        lock (_gate)
        {
            if (_plugins.TryGetValue(id.Value, out var current))
            {
                _plugins[id.Value] = current with { IsActive = true };
            }
        }

        _logger.LogInformation("插件已启动: {Id}", id);
        return true;
    }

    /// <summary>停用插件。程序集仍驻留内存，可再次启动。</summary>
    public async Task<bool> StopPluginAsync(PluginId id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_plugins.TryGetValue(id.Value, out var plugin)) return false;
            if (!plugin.IsActive) return true;
            _plugins[id.Value] = plugin with { IsActive = false };
        }

        if (_buildProvider?.GetService<IPluginShutdownHook>() is { } hook)
        {
            await hook.StopAsync(id, ct);
        }

        _logger.LogInformation("插件已停用: {Id}", id);
        return true;
    }

    /// <summary>卸载插件程序集。需先停用。</summary>
    public bool UnloadPlugin(PluginId id)
    {
        PluginLoadContext? alc;
        lock (_gate)
        {
            if (!_plugins.TryGetValue(id.Value, out var plugin)) return false;
            if (plugin.IsActive)
            {
                _logger.LogWarning("插件 {Id} 仍在运行，请先停用再卸载", id);
                return false;
            }
            _plugins.Remove(id.Value);
            _loadContexts.Remove(id.Value, out alc);
        }

        alc?.Unload();
        _logger.LogInformation("插件已卸载: {Id}", id);
        return true;
    }

    /// <summary>按依赖关系拓扑排序，检测循环依赖与版本不兼容。</summary>
    public static IReadOnlyList<PluginLoadResult> TopologicalSort(IReadOnlyList<PluginLoadResult> plugins)
    {
        var byId = plugins.ToDictionary(p => p.Manifest.Id.Value, StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<PluginLoadResult>();

        void Visit(PluginLoadResult plugin)
        {
            if (state.TryGetValue(plugin.Manifest.Id.Value, out var visited))
            {
                if (visited == 1) throw new InvalidOperationException("检测到插件循环依赖: " + plugin.Manifest.Id);
                if (visited == 2) return;
            }

            state[plugin.Manifest.Id.Value] = 1;

            foreach (var dep in plugin.Manifest.Dependencies.Where(d => !d.IsOptional))
            {
                if (!byId.TryGetValue(dep.PluginId.Value, out var dependency))
                {
                    throw new InvalidOperationException("插件 " + plugin.Manifest.Id + " 依赖的 " + dep.PluginId + " 未找到");
                }

                if (!dependency.Manifest.Version.IsCompatible(dep.MinimumVersion))
                {
                    throw new InvalidOperationException(
                        "插件 " + plugin.Manifest.Id + " 依赖 " + dep.PluginId + " >= " + dep.MinimumVersion +
                        "，实际为 " + dependency.Manifest.Version);
                }

                Visit(dependency);
            }

            state[plugin.Manifest.Id.Value] = 2;
            ordered.Add(plugin);
        }

        foreach (var p in plugins) Visit(p);
        return ordered;
    }
}

/// <summary>插件运行上下文实现。</summary>
internal sealed class PluginContext : IPluginContext
{
    private readonly ILoggerFactory _loggerFactory;

    public PluginContext(PluginManifest manifest, IServiceProvider services, ILoggerFactory loggerFactory)
    {
        Manifest = manifest;
        Services = services;
        _loggerFactory = loggerFactory;
    }

    public PluginManifest Manifest { get; }

    public IServiceProvider Services { get; }

    /// <summary>插件数据分区名：小写加下划线，保证 SQL 标识符合法。</summary>
    public string PartitionName => Manifest.Id.Value.ToLowerInvariant().Replace('.', '_');

    public ILogger Logger => _loggerFactory.CreateLogger("Plugin:" + Manifest.Id.Value);
}
