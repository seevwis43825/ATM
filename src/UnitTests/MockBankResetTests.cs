// ===== MockBank 演示数据重置的并发与语义回归测试 =====
//
// 为什么需要这个测试：
//   重置端点（POST /api/corebank/v1/_admin/reset）要**同时**拿各行的写锁（Gate），
//   而跨行转账（MockBankStore.AcquireLedgerLocksAsync）也要一次拿多行的锁。
//   两者的**加锁顺序必须一致**，否则就是经典的
//   「A 持 A 锁等 B 锁、B 持 B 锁等 A 锁」死锁 —— 而且只在并发下才复现。
//
//   这段逻辑此前只有「能跑通」的人工验证，**没有自动化护栏**。
//   而它恰恰是最容易被后续重构悄悄改坏的地方：
//   谁调整一下排序，谁就埋下一个单线程测试永远发现不了、只在压测时才炸的死锁。
//
// 另外两条同样容易回归的语义，也在这里钉住：
//   - 重置必须是**原地**的。账本实例注册为容器单例，而 MockBankStore 在构造时就把
//     账本列表固化了；换新实例会让存储层继续读旧实例的内存快照，
//     表现为「接口返回 200，但数据根本没重置」。
//   - 内存快照必须**清干净**。LoadSnapshot 只做覆盖、不做删除，
//     不显式清空的话，重置前的账户/卡片会继续被读出去。

using MockBank.Api.Data;
using MockBank.Api.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace UnitTests;

public class MockBankResetTests
{
    /// <summary>重置时等待各行写锁的上限（与 BankLedgerFactory 内部一致）。</summary>
    private static readonly TimeSpan ResetTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ResetAll_WhileCrossBankTransferHoldsFirstLock_DoesNotDeadlock()
    {
        var (factory, dataDir) = BuildFactory();
        try
        {
            var ledgers = factory.CreateAll();
            var byCode = ledgers.ToDictionary(l => l.Bank.BankCode, l => l);
            var ccb = byCode["CCB"];
            var icbc = byCode["ICBC"];

            // ===== 跨行转账的关键结构：持第一把锁去要第二把 =====
            // 死锁的成因是「甲持 A 等 B、乙持 B 等 A」这个环。
            // 所以这里**刻意**分两步取锁：先拿 CCB（字典序第一把），
            // 停一下，再去拿 ICBC —— 复刻 MockBankStore.AcquireLedgerLocksAsync
            // 「一把一把拿」的行为。
            //
            // ⚠️ 一次性把两把锁都拿齐的写法**构不成环**（拿齐之后就不再需要任何锁），
            // 那种写法对死锁是空转的：无论重置的顺序怎么改，它都测不出问题。
            var holdingCcb = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var transfer = Task.Run(async () =>
            {
                // 顺序与 MockBankStore 一致：行代码字典序 ⇒ CCB 先、ICBC 后
                await ccb.Gate.WaitAsync();
                try
                {
                    holdingCcb.TrySetResult();
                    await proceed.Task;            // 模拟「持着第一把锁去做别的事」
                    await icbc.Gate.WaitAsync();   // ← 顺序不一致时，死锁就发生在这里
                    try
                    {
                        // 跨行转账的其余动作（此处无）
                    }
                    finally
                    {
                        icbc.Gate.Release();
                    }
                }
                finally
                {
                    ccb.Gate.Release();
                }
            });

            await holdingCcb.Task.WaitAsync(TimeSpan.FromSeconds(15));

            // 重置与转账并发
            var reset = Task.Run(() => factory.ResetAll());

            // 观测：重置有没有抢在 CCB 之前拿走 ICBC？
            //   正确顺序（与转账同序，CCB 优先）⇒ 重置会卡在 CCB 上，什么也没拿到；
            //   错误顺序（ICBC 优先）⇒ 重置拿走 ICBC，随后与转账形成等待环。
            var resetGrabbedIcbcFirst = await WaitUntilAsync(
                () => IsHeldByOther(icbc.Gate), TimeSpan.FromSeconds(3));

            // 放行转账去要第二把锁
            proceed.TrySetResult();

            var all = Task.WhenAll(transfer, reset);
            var finished = await Task.WhenAny(all, Task.Delay(ResetTimeout));
            Assert.True(
                finished == all,
                "跨行转账与重置并发时必须在超时前全部完成 —— 若两边的加锁顺序不一致，" +
                "就会形成「转账持 CCB 等 ICBC、重置持 ICBC 等 CCB」的等待环而死锁。" +
                $"（本次观测：重置是否抢先拿走 ICBC = {resetGrabbedIcbcFirst}；正确顺序下应为 False）");

            await all;
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public void ResetAll_ReusesSameLedgerInstance_AndStoreSeesFreshData()
    {
        var (factory, dataDir) = BuildFactory();
        try
        {
            var ledgers = factory.CreateAll();
            var store = new MockBankStore(ledgers, NullLogger<MockBankStore>.Instance);
            var icbc = ledgers.First(l => l.Bank.BankCode == "ICBC");

            var account = icbc.GetAllAccounts().First();
            var seedBalance = account.Balance;

            // 改一笔余额，模拟重置前发生过业务写入
            icbc.Gate.Wait();
            try
            {
                icbc.UpdateAccount(account with { Balance = 12345.67m });
            }
            finally
            {
                icbc.Gate.Release();
            }

            Assert.Equal(12345.67m, icbc.GetAccount(account.AccountNo)!.Balance);

            factory.ResetAll();

            // 账本实例必须**是同一个**：换实例的话，容器单例与固化的 store 会继续读旧快照，
            // 表现为「重置返回成功，但数据根本没重置」。
            Assert.Same(icbc, factory.Get(icbc.Bank));

            // 存储层看到的必须是重置后的数据（种子余额），不是重置前的那个
            Assert.Equal(seedBalance, icbc.GetAccount(account.AccountNo)!.Balance);
            Assert.Equal(seedBalance, store.GetAccount(account.AccountNo)!.Balance);
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public void ClearSnapshot_EmptiesEveryCollection()
    {
        var (factory, dataDir) = BuildFactory();
        try
        {
            var icbc = factory.CreateAll().First(l => l.Bank.BankCode == "ICBC");

            // 清空前：七个集合都应有种子数据（否则下面的断言是空的）
            Assert.NotEmpty(icbc.GetCustomers());
            Assert.NotEmpty(icbc.GetAllAccounts());
            Assert.NotEmpty(icbc.GetAllCards());
            Assert.NotEmpty(icbc.QueryTransactions(null));
            Assert.NotEmpty(icbc.QueryProducts(null, null));
            Assert.NotEmpty(icbc.QueryFunds(null, null, null, null));

            var fundCode = icbc.QueryFunds(null, null, null, null)[0].FundCode;
            Assert.NotEmpty(icbc.GetFundNav(fundCode, 90));

            icbc.ClearSnapshot();

            // 必须**全部**清空。漏掉任何一个集合，重置后旧数据仍会被读出去 ——
            // 而 LoadSnapshot 只做覆盖、不做删除，所以清不干净 = 永远残留。
            Assert.Empty(icbc.GetCustomers());
            Assert.Empty(icbc.GetAllAccounts());
            Assert.Empty(icbc.GetAllCards());
            Assert.Empty(icbc.QueryTransactions(null));
            Assert.Empty(icbc.QueryProducts(null, null));
            Assert.Empty(icbc.QueryFunds(null, null, null, null));
            Assert.Empty(icbc.GetFundNav(fundCode, 90));
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public void ResetAll_RestoresSeedDataIntoTheSnapshot()
    {
        var (factory, dataDir) = BuildFactory();
        try
        {
            var ledgers = factory.CreateAll();
            var icbc = ledgers.First(l => l.Bank.BankCode == "ICBC");

            var account = icbc.GetAllAccounts().First();
            var seedBalance = account.Balance;

            // 清掉快照（模拟"只剩库、快照没了"的中间态），再重置 ——
            // 重置必须把种子数据重新灌回快照，否则重置后读接口全空。
            icbc.ClearSnapshot();
            Assert.Empty(icbc.GetAllAccounts());

            factory.ResetAll();

            Assert.NotEmpty(icbc.GetAllAccounts());
            Assert.Equal(seedBalance, icbc.GetAccount(account.AccountNo)!.Balance);
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    /// <summary>
    /// 构建一个指向临时数据目录的账本工厂（三行全开）。
    /// </summary>
    private static (BankLedgerFactory Factory, string DataDirectory) BuildFactory()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"mbk-reset-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);

        var options = Options.Create(new MockBankOptions
        {
            Banks = ["ICBC", "CCB", "CMB"],
            ResetOnStartup = false
        });

        var factory = new BankLedgerFactory(options, NullLoggerFactory.Instance, dataDir);
        return (factory, dataDir);
    }

    /// <summary>判断信号量是否已被别的持有者占住。</summary>
    private static bool IsHeldByOther(SemaphoreSlim gate)
    {
        if (gate.Wait(0))
        {
            // 抢到了说明当时没人持有 —— 立刻还回去，别把锁状态搞乱
            gate.Release();
            return false;
        }

        return true;
    }

    /// <summary>轮询等待条件成立（异步版）。</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    /// <summary>清理临时数据目录（best-effort，文件可能仍被 SQLite 池占用）。</summary>
    private static void Cleanup(string dataDir)
    {
        try
        {
            if (Directory.Exists(dataDir))
            {
                Directory.Delete(dataDir, true);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响断言
        }
        catch (UnauthorizedAccessException)
        {
            // 清理失败不影响断言
        }
    }
}
