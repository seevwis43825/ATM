// ===== 轨迹日志（Trajectory Log）=====
// 参考 DeepSeek Harness：所有系统提示、推理、工具调用、子 Agent 调度、
// 上下文注入都写入 append-only 事件流，支持回放、分叉与审计。
//
// 与 AuditLog 的区别：
//   AuditLog     —— 合规视角，回答「谁在什么时候做了什么」
//   TrajectoryLog —— 调试视角，回答「Agent 看到了什么、为什么这么做」

namespace BankingAgent.Base.Agents;

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>内存轨迹日志。生产环境应替换为持久化实现（追加写文件或数据库）。</summary>
public sealed class InMemoryTrajectoryLog(ILogger<InMemoryTrajectoryLog> logger) : ITrajectoryLog
{
    private readonly ConcurrentQueue<TrajectoryEvent> _events = new();
    private readonly ConcurrentDictionary<string, List<TrajectoryEvent>> _sessions = new();
    private int _count;

    /// <summary>事件总数。</summary>
    public int Count => _count;

    /// <inheritdoc />
    public int Append(TrajectoryEvent evt)
    {
        _events.Enqueue(evt);
        _sessions.AddOrUpdate(
            evt.SessionId,
            _ => new List<TrajectoryEvent> { evt },
            (_, list) =>
            {
                lock (list) { list.Add(evt); }
                return list;
            });

        var n = Interlocked.Increment(ref _count);
        logger.LogDebug("轨迹事件 #{Seq} {Type} session={Session} step={Step} agent={Agent}",
            evt.Sequence, evt.Type, evt.SessionId, evt.StepId, evt.AgentId);
        return n;
    }

    /// <inheritdoc />
    public IReadOnlyList<TrajectoryEvent> GetSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var list)) return [];
        lock (list) { return list.OrderBy(e => e.Sequence).ToList(); }
    }

    /// <inheritdoc />
    public IReadOnlyList<TrajectoryEvent> GetAll(int limit = 1000) =>
        _events.TakeLast(limit).ToList();

    /// <summary>导出会话为 JSONL，便于离线分析。</summary>
    public string ExportSession(string sessionId)
    {
        var opts = new JsonSerializerOptions { WriteIndented = false };
        return string.Join(Environment.NewLine,
            GetSession(sessionId).Select(e => JsonSerializer.Serialize(e, opts)));
    }

    /// <summary>从轨迹分叉：新会话继承分叉点之前的所有事件。</summary>
    public string Fork(string sessionId, int atSequence, string newSessionId)
    {
        var prefix = GetSession(sessionId).Where(e => e.Sequence <= atSequence).ToList();
        foreach (var e in prefix)
        {
            Append(e with
            {
                EventId = Guid.NewGuid().ToString("N"),
                SessionId = newSessionId,
                Data = new Dictionary<string, object?>(e.Data)
                {
                    ["forkedFrom"] = sessionId,
                    ["forkedAtSequence"] = atSequence
                }
            });
        }
        logger.LogInformation("会话 {Source} 在序号 {Seq} 分叉为 {Target}，继承 {Count} 条事件",
            sessionId, atSequence, newSessionId, prefix.Count);
        return newSessionId;
    }
}

/// <summary>文件轨迹日志。仅追加写，进程重启不丢。</summary>
public sealed class FileTrajectoryLog : ITrajectoryLog
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<TrajectoryEvent> _recent = [];
    private readonly object _recentLock = new();

    /// <summary>构造文件轨迹日志。</summary>
    public FileTrajectoryLog(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    /// <inheritdoc />
    public async Task<int> AppendAsync(TrajectoryEvent evt, CancellationToken ct = default)
    {
        var line = JsonSerializer.Serialize(evt) + Environment.NewLine;
        await _gate.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(_path, line, ct);
        }
        finally
        {
            _gate.Release();
        }

        lock (_recentLock)
        {
            _recent.Add(evt);
            if (_recent.Count > 2000) _recent.RemoveAt(0);
        }

        return evt.Sequence;
    }

    /// <inheritdoc />
    public int Append(TrajectoryEvent evt)
    {
        AppendAsync(evt).GetAwaiter().GetResult();
        return evt.Sequence;
    }

    /// <inheritdoc />
    public IReadOnlyList<TrajectoryEvent> GetSession(string sessionId)
    {
        if (!File.Exists(_path)) return [];
        return File.ReadLines(_path)
            .Select(l =>
            {
                try { return JsonSerializer.Deserialize<TrajectoryEvent>(l); }
                catch { return (TrajectoryEvent?)null; }
            })
            .Where(e => e is not null && e!.SessionId == sessionId)
            .OrderBy(e => e!.Sequence)
            .ToList()!;
    }

    /// <inheritdoc />
    public IReadOnlyList<TrajectoryEvent> GetAll(int limit = 1000)
    {
        lock (_recentLock)
        {
            return _recent.TakeLast(limit).ToList();
        }
    }
}
