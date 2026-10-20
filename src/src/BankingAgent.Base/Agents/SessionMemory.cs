// ===== 会话记忆 =====
//
// 为什么需要它：
//   真实对话里，用户的第二句话常常是省略句 ——
//     第一句「给李华转 500」
//     第二句「改成 800」
//   第二句本身没有任何业务关键词，靠规则表或模型都可能识别不出来，
//   于是「识别不同语义 → 触发不同功能」在多轮场景下就断了。
//   记住上一轮的场景意图与已抽取槽位，第二句就能接上。
//
// 边界（有意收得很紧）：
//   - 只存**意图与槽位**，不存用户的原始敏感数据；
//   - 按会话隔离，一个会话只属于一个用户；
//   - 进程内存 + 过期时间，进程重启即清空，不落库；
//   - 有容量上限，防止长时间运行后内存无限增长。

namespace BankingAgent.Base.Agents;

using System.Collections.Concurrent;

/// <summary>一个会话在某一轮结束后的记忆快照。</summary>
public sealed record SessionSnapshot
{
    /// <summary>上一轮识别出的意图。</summary>
    public required string LastIntent { get; init; }

    /// <summary>上一轮的用户原话（仅供排障，长度受限）。</summary>
    public string LastUserInput { get; init; } = "";

    /// <summary>上一轮已抽取的槽位。</summary>
    public IReadOnlyDictionary<string, object?> LastSlots { get; init; }
        = new Dictionary<string, object?>();

    /// <summary>快照写入时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>会话记忆契约。</summary>
public interface ISessionMemory
{
    /// <summary>取会话的最近快照。</summary>
    /// <param name="userId">用户号。</param>
    /// <param name="sessionId">会话标识。</param>
    /// <returns>快照；不存在或已过期时返回 null。</returns>
    SessionSnapshot? Get(string? userId, string? sessionId);

    /// <summary>写入会话快照。</summary>
    /// <param name="userId">用户号。</param>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="snapshot">快照。</param>
    void Remember(string? userId, string? sessionId, SessionSnapshot snapshot);

    /// <summary>清除会话记忆。</summary>
    /// <param name="userId">用户号。</param>
    /// <param name="sessionId">会话标识。</param>
    void Forget(string? userId, string? sessionId);
}

/// <summary>进程内存实现的会话记忆。</summary>
public sealed class InMemorySessionMemory : ISessionMemory
{
    /// <summary>默认容量上限：超出后淘汰最久未更新的会话。</summary>
    public const int DefaultMaxSessions = 1000;

    /// <summary>默认过期时长：30 分钟内没有新对话就忘掉。</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, SessionSnapshot> _sessions = new(StringComparer.Ordinal);
    private readonly int _maxSessions;
    private readonly TimeSpan _ttl;

    /// <summary>构造会话记忆。</summary>
    /// <param name="maxSessions">容量上限。</param>
    /// <param name="ttl">过期时长。</param>
    public InMemorySessionMemory(int maxSessions = DefaultMaxSessions, TimeSpan? ttl = null)
    {
        _maxSessions = Math.Max(1, maxSessions);
        _ttl = ttl ?? DefaultTtl;
    }

    /// <summary>当前保留的会话数。用于容量自检与诊断。</summary>
    public int Count => _sessions.Count;

    /// <inheritdoc />
    public SessionSnapshot? Get(string? userId, string? sessionId)
    {
        var key = BuildKey(userId, sessionId);
        if (key is null) return null;
        if (!_sessions.TryGetValue(key, out var snapshot)) return null;

        if (DateTimeOffset.UtcNow - snapshot.UpdatedAt > _ttl)
        {
            _sessions.TryRemove(key, out _);
            return null;
        }

        return snapshot;
    }

    /// <inheritdoc />
    public void Remember(string? userId, string? sessionId, SessionSnapshot snapshot)
    {
        var key = BuildKey(userId, sessionId);
        if (key is null) return;

        _sessions[key] = snapshot with { UpdatedAt = DateTimeOffset.UtcNow };
        EvictIfNeeded();
    }

    /// <inheritdoc />
    public void Forget(string? userId, string? sessionId)
    {
        var key = BuildKey(userId, sessionId);
        if (key is null) return;
        _sessions.TryRemove(key, out _);
    }

    /// <summary>
    /// 拼装记忆键。
    ///
    /// 必须带上用户号：会话标识是客户端提供的，如果不与用户绑定，
    /// 换个 sessionId 就能读到别人的上下文。
    /// </summary>
    /// <param name="userId">用户号。</param>
    /// <param name="sessionId">会话标识。</param>
    /// <returns>记忆键；任一为空时返回 null。</returns>
    private static string? BuildKey(string? userId, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(sessionId)) return null;
        return $"{userId}::{sessionId.Trim()}";
    }

    /// <summary>
    /// 容量超限时淘汰最久未更新的会话。
    ///
    /// 两个坑都在这里：
    ///
    /// 1. **不能用 LINQ 直接排序 ConcurrentDictionary**。
    ///    ConcurrentDictionary 实现了 <c>ICollection&lt;KeyValuePair&lt;,&gt;&gt;</c>，
    ///    而 LINQ 的 OrderBy 走的是 <c>ICollection.CopyTo</c> 这条批量拷贝路径 ——
    ///    该路径在字典被并发修改时会抛
    ///    "The index is equal to or greater than the length of the array"，
    ///    随后半填的数组又引发 NullReferenceException。
    ///    直接用枚举器遍历才是并发安全的（它逐桶无锁读取，不会因扩容而失效）。
    ///
    /// 2. **只淘汰超出容量的那几条**。
    ///    淘汰到某个"水位"（比如容量的 90%）看似能减少排序次数，
    ///    但容量本身就小的时候会把有效会话砍掉一半 ——
    ///    容量 2 写第 3 条就会只剩 1 条。
    ///
    /// 排序成本另作处理：只删一条时用一次线性扫描找最旧的，
    /// 避免每次写入都付 O(n log n)。
    /// </summary>
    private void EvictIfNeeded()
    {
        var count = _sessions.Count;
        if (count <= _maxSessions) return;

        var toRemove = count - _maxSessions;

        var entries = new List<KeyValuePair<string, SessionSnapshot>>(count);
        foreach (var pair in _sessions)
        {
            entries.Add(pair);
        }

        if (entries.Count == 0) return;

        if (toRemove == 1)
        {
            // 常态路径：线性扫描找最旧的一条，O(n) 而非 O(n log n)
            var oldest = entries[0];
            for (var i = 1; i < entries.Count; i++)
            {
                if (entries[i].Value.UpdatedAt < oldest.Value.UpdatedAt)
                {
                    oldest = entries[i];
                }
            }

            _sessions.TryRemove(oldest.Key, out _);
            return;
        }

        foreach (var oldest in entries
                     .OrderBy(pair => pair.Value.UpdatedAt)
                     .Take(toRemove)
                     .Select(pair => pair.Key))
        {
            _sessions.TryRemove(oldest, out _);
        }
    }
}
