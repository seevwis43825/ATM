// ===== 会话记忆的并发与淘汰单元测试 =====
//
// 为什么要单独测这个：
//   会话记忆是进程内共享可变状态，且位于**每个聊天请求的写路径**上。
//   历史缺陷：淘汰逻辑用 LINQ 的 OrderBy 直接排序 ConcurrentDictionary，
//   走的是 ICollection.CopyTo 批量拷贝路径 —— 该路径在字典被并发修改时会抛
//   "The index is equal to or greater than the length of the array"，
//   随后半填的数组产生 null key，后续 TryRemove 再抛 ArgumentNullException。
//   这个缺陷在低并发（单元测试、E2E 串行脚本）下完全看不出来，
//   只有在真实并发压测下才暴露成 12% 的 HTTP 500。
//
// 所以这里必须有一个**并发**用例，否则修好的代码会被下一个人改回去。

using System.Collections.Concurrent;
using BankingAgent.Base.Agents;

namespace UnitTests;

public class SessionMemoryConcurrencyTests
{
    /// <summary>
    /// 并发写入不得抛异常。
    ///
    /// 容量刻意设得很小，让淘汰逻辑在每次写入时都触发 ——
    /// 淘汰路径才是缺陷所在，写入路径本身不会。
    /// </summary>
    [Fact]
    public async Task Remember_ConcurrentWrites_DoNotThrow()
    {
        const int maxSessions = 16;
        const int threads = 16;
        const int perThread = 300;

        var memory = new InMemorySessionMemory(maxSessions);
        var failures = new ConcurrentBag<Exception>();

        var workers = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < perThread; i++)
                {
                    memory.Remember(
                        $"u_demo{t % 4}",
                        $"sess-{t}-{i}",
                        new SessionSnapshot
                        {
                            LastIntent = "card.list",
                            LastUserInput = "我有哪些卡",
                            UpdatedAt = DateTimeOffset.UtcNow
                        });
                }
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }));

        await Task.WhenAll(workers);

        Assert.True(failures.IsEmpty,
            "并发写入会话记忆时抛异常：" +
            string.Join(" | ", failures.Take(3).Select(e => e.ToString())));

        // 淘汰必须真的在发生：容量远小于写入总量
        Assert.True(memory.Count <= maxSessions,
            $"会话数 {memory.Count} 超过容量上限 {maxSessions}");
    }

    /// <summary>并发读写混合：读路径同样不能被写路径的淘汰干扰。</summary>
    [Fact]
    public async Task Get_ConcurrentWithWrites_DoesNotThrow()
    {
        var memory = new InMemorySessionMemory(32);
        var failures = new ConcurrentBag<Exception>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 2000 && !cts.IsCancellationRequested; i++)
            {
                memory.Remember("u_demo01", $"sess-{i}", new SessionSnapshot
                {
                    LastIntent = "bill.summary",
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
        });

        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    memory.Get("u_demo01", "sess-1");
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                    return;
                }
            }
        }));

        await Task.WhenAll(readers.Append(writer));

        Assert.True(failures.IsEmpty,
            "并发读写会话记忆时抛异常：" +
            string.Join(" | ", failures.Take(3).Select(e => e.ToString())));
    }

    /// <summary>超过容量时淘汰最久未更新的会话，保留最近的。</summary>
    [Fact]
    public async Task Remember_OverCapacity_EvictsOldest()
    {
        var memory = new InMemorySessionMemory(maxSessions: 2);

        memory.Remember("u_demo01", "old", NewSnapshot("card.list"));
        await Task.Delay(30);
        memory.Remember("u_demo01", "mid", NewSnapshot("bill.summary"));
        await Task.Delay(30);
        memory.Remember("u_demo01", "new", NewSnapshot("transfer"));

        // 容量 2 写第 3 条：淘汰最旧的 "old"
        Assert.Null(memory.Get("u_demo01", "old"));
        Assert.NotNull(memory.Get("u_demo01", "mid"));
        Assert.NotNull(memory.Get("u_demo01", "new"));
    }

    /// <summary>最近写入的快照必须能读回来（省略句接续依赖它）。</summary>
    [Fact]
    public void Get_ReturnsLatestSnapshot()
    {
        var memory = new InMemorySessionMemory();

        memory.Remember("u_demo01", "s1", NewSnapshot("transfer", amount: 500m));
        memory.Remember("u_demo01", "s1", NewSnapshot("transfer", amount: 800m));

        var snapshot = memory.Get("u_demo01", "s1");
        Assert.NotNull(snapshot);
        Assert.Equal(800m, snapshot!.LastSlots["amount"]);
    }

    /// <summary>用户号必须参与记忆键：换用户不能读到别人的上下文。</summary>
    [Fact]
    public void Get_DifferentUser_DoesNotSeeOtherSession()
    {
        var memory = new InMemorySessionMemory();

        memory.Remember("u_demo01", "shared", NewSnapshot("transfer", amount: 500m));

        Assert.Null(memory.Get("u_demo02", "shared"));
    }

    private static SessionSnapshot NewSnapshot(string intent, decimal? amount = null) => new()
    {
        LastIntent = intent,
        LastUserInput = "测试",
        LastSlots = amount is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?> { ["amount"] = amount },
        UpdatedAt = DateTimeOffset.UtcNow
    };
}
