// ===== 进程内事件总线 =====
// 插件间解耦通信的唯一通道。发布者不知道订阅者，订阅者不必知道发布者。
// 事件类型采用命名约定：{领域}.{动作}，如 transfer.completed。

namespace BankingAgent.Base.Events;

using System.Collections.Concurrent;
using BankingAgent.PluginSdk;
using Microsoft.Extensions.Logging;

/// <summary>内存事件总线实现。</summary>
public class InMemoryEventBus : IEventPublisher
{
    private readonly ConcurrentDictionary<string, List<IDomainEventHandler>> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<DomainEvent> _deadLetters = new();
    private readonly List<DomainEvent> _history = [];
    private readonly object _gate = new();
    private readonly ILogger<InMemoryEventBus> _logger;

    /// <summary>构造事件总线并建立订阅索引。</summary>
    public InMemoryEventBus(IEnumerable<IDomainEventHandler> handlers, ILogger<InMemoryEventBus> logger)
    {
        _logger = logger;
        foreach (var handler in handlers)
        {
            foreach (var evtType in handler.SubscribedEventTypes)
            {
                var list = _handlers.GetOrAdd(evtType, _ => []);
                lock (_gate) { list.Add(handler); }
            }
        }

        _logger.LogInformation("事件总线初始化完成，订阅关系 {Count} 条", _handlers.Count);
    }

    /// <summary>最近的事件历史，便于演示与调试。</summary>
    public IReadOnlyList<DomainEvent> History
    {
        get { lock (_gate) { return _history.ToList(); } }
    }

    /// <summary>死信队列（处理失败的事件）。</summary>
    public IReadOnlyList<DomainEvent> DeadLetters => _deadLetters.ToList();

    /// <summary>已发布事件总数。并发递增使用 Interlocked 避免丢计数。</summary>
    public int PublishedCount => _publishedCount;

    private int _publishedCount;

    /// <inheritdoc />
    public async Task PublishAsync(DomainEvent evt, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _publishedCount);
        lock (_gate)
        {
            _history.Add(evt);
            if (_history.Count > 500) _history.RemoveAt(0);
        }

        _logger.LogInformation("事件发布: {EventType} from {Source} | {EventId}",
            evt.EventType, evt.Source, evt.EventId);

        var targets = new List<IDomainEventHandler>();
        if (_handlers.TryGetValue(evt.EventType, out var exact))
        {
            lock (_gate) { targets.AddRange(exact); }
        }
        if (_handlers.TryGetValue("*", out var wildcard))
        {
            lock (_gate) { targets.AddRange(wildcard); }
        }

        foreach (var handler in targets.Distinct())
        {
            try
            {
                await handler.HandleAsync(evt, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "事件处理失败，转入死信队列: {EventType} -> {Handler}",
                    evt.EventType, handler.GetType().Name);
                _deadLetters.Enqueue(evt);
            }
        }
    }
}
