// ===== 速率限制核心算法 =====
// 当前系统最大的实际安全风险：任何人都可无限调接口，导致暴力破解与资源耗尽。
// 采用双维度令牌桶：按 IP（防扫描）+ 按用户（防单账号爆破）。
// 本文件为纯算法，不依赖 ASP.NET Core；HTTP 接入在 Host/Middleware/。

namespace BankingAgent.Base.Security.RateLimit;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

/// <summary>速率限制配置。按端点敏感度分档。</summary>
public sealed class RateLimitOptions
{
    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>认证类端点的严格配额（每分钟）。</summary>
    public int AuthPermitsPerMinute { get; set; } = 5;
    /// <summary>资金类端点的配额（每分钟）。</summary>
    public int TransferPermitsPerMinute { get; set; } = 20;
    /// <summary>普通业务端点配额（每分钟）。</summary>
    public int ApiPermitsPerMinute { get; set; } = 120;
    /// <summary>只读端点配额（每分钟）。</summary>
    public int ReadPermitsPerMinute { get; set; } = 300;

    /// <summary>按 IP 的全局兜底配额（每分钟）。防止单 IP 打满所有维度。</summary>
    public int IpPermitsPerMinute { get; set; } = 600;

    /// <summary>令牌桶容量（允许的突发量）。</summary>
    public int BurstCapacity { get; set; } = 30;
}

/// <summary>端点限流档位。</summary>
public enum RateLimitTier
{
    /// <summary>免限流（健康检查等）。</summary>
    None,
    /// <summary>严格（认证）。</summary>
    Strict,
    /// <summary>中等（资金操作）。</summary>
    Standard,
    /// <summary>宽松（普通业务）。</summary>
    Relaxed,
    /// <summary>只读。</summary>
    ReadOnly
}

/// <summary>限流结果。</summary>
public sealed record RateLimitDecision(bool Allowed, int Limit, int Remaining, TimeSpan RetryAfter)
{
    public static RateLimitDecision Allow(int limit, int remaining) =>
        new(true, limit, remaining, TimeSpan.Zero);

    public static RateLimitDecision Reject(int limit, TimeSpan retryAfter) =>
        new(false, limit, 0, retryAfter);
}

/// <summary>令牌桶算法实现（线程安全，无外部依赖）。</summary>
public sealed class TokenBucket
{
    private readonly double _capacity;
    private readonly double _refillPerSecond;
    private double _tokens;
    private long _lastRefillTicks;

    /// <summary>构造令牌桶。</summary>
    public TokenBucket(double capacity, double permitsPerMinute)
    {
        _capacity = capacity;
        _refillPerSecond = permitsPerMinute / 60.0;
        _tokens = capacity;
        _lastRefillTicks = DateTime.UtcNow.Ticks;
    }

    /// <summary>尝试获取一个令牌，返回是否成功及建议重试时间。</summary>
    public (bool Allowed, TimeSpan RetryAfter) TryAcquire()
    {
        lock (this)
        {
            Refill();

            if (_tokens >= 1.0)
            {
                _tokens -= 1.0;
                return (true, TimeSpan.Zero);
            }

            var deficit = 1.0 - _tokens;
            var waitSeconds = deficit / _refillPerSecond;
            return (false, TimeSpan.FromSeconds(Math.Ceiling(waitSeconds)));
        }
    }

    /// <summary>桶的本地线程，回收时判断是否长时间未使用。</summary>
    private long _lastAccessTicks = DateTime.UtcNow.Ticks;

    /// <summary>当前剩余令牌数（供响应头展示）。</summary>
    public double Remaining
    {
        get
        {
            lock (this)
            {
                Refill();
                Interlocked.Exchange(ref _lastAccessTicks, DateTime.UtcNow.Ticks);
                return Math.Floor(_tokens);
            }
        }
    }

    /// <summary>距离上次访问的时长。用于回收判断。</summary>
    public TimeSpan IdleFor
    {
        get
        {
            var last = Interlocked.Read(ref _lastAccessTicks);
            return TimeSpan.FromTicks(Math.Max(0, DateTime.UtcNow.Ticks - last));
        }
    }

    private void Refill()
    {
        var now = DateTime.UtcNow.Ticks;
        var elapsed = (now - _lastRefillTicks) / (double)TimeSpan.TicksPerSecond;
        if (elapsed <= 0) return;

        _tokens = Math.Min(_capacity, _tokens + elapsed * _refillPerSecond);
        _lastRefillTicks = now;
    }
}

/// <summary>限流器。按维度维护令牌桶，自动回收闲置条目。</summary>
public sealed class RateLimiter(ILogger<RateLimiter> logger)
{
    private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new();
    private readonly ConcurrentDictionary<string, byte> _blocked = new();
    private long _lastSweepTicks = DateTime.UtcNow.Ticks;

    /// <summary>尝试获取令牌。</summary>
    public RateLimitDecision TryAcquire(string dimension, string key, int permitsPerMinute, int burst)
    {
        Sweep();

        var bucketKey = $"{dimension}:{key}";
        var bucket = _buckets.GetOrAdd(
            bucketKey,
            _ => new TokenBucket(burst, permitsPerMinute));

        var (allowed, retryAfter) = bucket.TryAcquire();
        return allowed
            ? RateLimitDecision.Allow(permitsPerMinute, (int)bucket.Remaining)
            : RateLimitDecision.Reject(permitsPerMinute, retryAfter);
    }

    /// <summary>当前桶数量（供监控与测试）。</summary>
    public int BucketCount => _buckets.Count;

    /// <summary>定期清理长时间未使用的桶，防止内存无限增长（防扫描攻击撑爆内存）。</summary>
    private void Sweep()
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (now - last < TimeSpan.TicksPerSecond * 60) return;
        if (Interlocked.CompareExchange(ref _lastSweepTicks, now, last) != last) return;

        // 回收闲置超过 10 分钟的桶
        foreach (var kv in _buckets)
        {
            if (kv.Value.IdleFor > TimeSpan.FromMinutes(10))
            {
                _buckets.TryRemove(kv.Key, out _);
            }
        }

        if (_buckets.Count > 10000)
        {
            logger.LogWarning("限流桶数量过多: {Count}，可能存在扫描攻击", _buckets.Count);
        }
    }
}

