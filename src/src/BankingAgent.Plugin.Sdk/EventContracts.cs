// ===== 事件契约 =====
// 插件之间通过事件解耦通信，禁止直接互相引用对方的类型。

namespace BankingAgent.PluginSdk;

/// <summary>领域事件信封。遵循 CloudEvents 字段命名习惯。</summary>
public sealed record DomainEvent
{
    public required string EventId { get; init; }
    public required string EventType { get; init; }
    public required string Source { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public string? CorrelationId { get; init; }
    public string? UserId { get; init; }
    public IReadOnlyDictionary<string, object?> Payload { get; init; }
        = new Dictionary<string, object?>();
}

/// <summary>事件处理器契约。插件注册实现即可收到事件。</summary>
public interface IDomainEventHandler
{
    /// <summary>订阅的事件类型。返回空集合表示订阅通配所有事件。</summary>
    IReadOnlyCollection<string> SubscribedEventTypes { get; }
    Task HandleAsync(DomainEvent evt, CancellationToken ct = default);
}

/// <summary>事件发布契约。由 Base 提供实现，插件注入使用。</summary>
public interface IEventPublisher
{
    Task PublishAsync(DomainEvent evt, CancellationToken ct = default);
}
