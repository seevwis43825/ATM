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
    public async Task ResetAll_AcquiresLocksInBankCodeOrdinalOrder()
    {
        var (factory, dataDir) = BuildFactory();
        try
        {
            var ledgers = factory.CreateAll();
            var byCode = ledgers.ToDictionary(l => l.Bank.BankCode, l => l);

            // 行代码字典序：CCB < CMB < ICBC。
            // 这条顺序必须与 MockBankStore.AcquireLedgerLocksAsync 用的是同一个。
            Assert.Equal(
                new[] { "CCB", "CMB", "ICBC" },
                byCode.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

            // 占住中间那档（CMB）的写锁，重置就应当先拿到 CCB、再卡在 CMB 上。
            // 若顺序被改成别的（例如先 ICBC），它会在到达 CMB 之前就先去抢别的锁。
            var cmb = byCode["CMB"];
            var ccb = byCode["CCB"];
            cmb.Gate.Wait();

            var reset = Task.Run(() => factory.ResetAll());
            try
            {
                // 观测：阻塞期间 CCB 的锁应当**已被重置持有** ——
                // 这正是「按字典序先取 CCB」的证据。
                Assert.True(
                    WaitUntil(() => IsHeldByOther(ccb.Gate), TimeSpan.FromSeconds(15)),
                    "重置应当已持有 CCB 的写锁（说明它按字典序先取了 CCB，再卡在 CMB 上）");

                cmb.Gate.Release();
                var finished = await Task.WhenAny(reset, Task.Delay(ResetTimeout));
                Assert.True(finished == reset, "释放 CMB 的锁后重置应当完成");
            }
            finally
            {
                if (!reset.IsCompleted)
                {
                    cmb.Gate.Release();
                    await reset.WaitAsync(ResetTimeout);
                }
            }
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task ResetAll_WhileCrossBankLocksHeld_DoesNotDeadlock()
    {
        var (factory, dataDir) = BuildFactory();
        try
        {
            var ledgers = factory.CreateAll();
            var store = new MockBankStore(ledgers, NullLogger<MockBankStore>.Instance);

            // 模拟一笔跨行转账：按 MockBankStore 自己的规则拿两行的锁并持有。
            var transfer = Task.Run(async () =>
            {
                await using var handles = await store.AcquireLedgerLocksAsync(["ICBC", "CCB"]);
                await Task.Delay(400);
            });

            // 等转账确实拿到锁后再发起重置
            await Task.Delay(100);

            var reset = Task.Run(() => factory.ResetAll());

            var all = Task.WhenAll(transfer, reset);
            var finished = await Task.WhenAny(all, Task.Delay(ResetTimeout));
            Assert.True(finished == all,
                "跨行转账与重置并发时必须在超时前全部完成 —— 若加锁顺序不一致，这里会死锁");

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

    /// <summary>轮询等待条件成立。</summary>
    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(20);
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
