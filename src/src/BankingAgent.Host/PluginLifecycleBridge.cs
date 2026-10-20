// ===== 插件生命周期桥接 =====
//
// 插件通过 IPluginStartupHook / IPluginShutdownHook 声明自己的生命周期回调，
// 但插件程序集里拿不到宿主容器。这个桥接器把回调转成「从宿主容器里解析插件」，
// 让插件能在启动与停止时做自己的初始化与清理，而不必认识宿主的 DI 实现。

using BankingAgent.Base.Plugins;
using BankingAgent.PluginSdk;

namespace BankingAgent.Host;

/// <summary>桥接插件生命周期钩子到容器中的 IPlugin 实现。</summary>
internal sealed class PluginLifecycleBridge(IServiceProvider sp) : IPluginStartupHook, IPluginShutdownHook
{
    /// <inheritdoc />
    public async Task StartAsync(PluginId id, CancellationToken ct)
    {
        foreach (var hook in sp.GetServices<IPlugin>())
        {
            await hook.OnStartingAsync(new SimplePluginContext(id, sp), ct);
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(PluginId id, CancellationToken ct)
    {
        foreach (var hook in sp.GetServices<IPlugin>())
        {
            await hook.OnStoppingAsync(new SimplePluginContext(id, sp), ct);
        }
    }

    /// <summary>传给插件的最小上下文：给插件一个 ID、一个服务提供者与一个日志器。</summary>
    private sealed class SimplePluginContext(PluginId id, IServiceProvider services) : IPluginContext
    {
        public PluginManifest Manifest { get; } = new()
        {
            Id = id,
            Name = id.Value,
            Description = "",
            Version = PluginVersion.Parse("1.0.0")
        };

        public IServiceProvider Services { get; } = services;

        /// <summary>数据分区名：小写并把点换成下划线，与 EF 的 Schema 命名一致。</summary>
        public string PartitionName => id.Value.ToLowerInvariant().Replace('.', '_');

        public ILogger Logger { get; } =
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }
}
