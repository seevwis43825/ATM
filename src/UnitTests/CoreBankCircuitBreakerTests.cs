// ===== 核心银行熔断器单元测试 =====
//
// 为什么必须测：
//   熔断器是**故障时**才起作用的东西 —— 正常路径下它什么都不做，
//   所以它的逻辑在正常测试里几乎不可能被覆盖。
//   而它一旦判错，后果是双向的：
//     误开 → 核心系统好好的却被快速失败，等于自伤；
//     误不开 → 核心系统挂了还在一个请求一个请求地等超时，宿主被拖垮。
//   所以这两条边界都必须有确定性用例。

using System.Net;
using System.Net.Http;
using BankingAgent.Base.CoreBank;
using Microsoft.Extensions.Logging.Abstractions;

namespace UnitTests;

public class CoreBankCircuitBreakerTests
{
    [Fact]
    public void Closed_AllowsRequests()
    {
        var breaker = new CoreBankCircuitBreaker(failureThreshold: 3);

        Assert.True(breaker.TryEnter());
        Assert.True(breaker.TryEnter());
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public void ConsecutiveFailures_OpenCircuitAtThreshold()
    {
        var breaker = new CoreBankCircuitBreaker(failureThreshold: 3);

        breaker.ReportFailure();
        breaker.ReportFailure();
        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.True(breaker.TryEnter(), "未达阈值前不应打开");

        breaker.ReportFailure();
        Assert.Equal(CircuitState.Open, breaker.State);
        Assert.False(breaker.TryEnter(), "熔断打开后必须快速失败");
    }

    [Fact]
    public void Success_ResetsFailureCounter()
    {
        var breaker = new CoreBankCircuitBreaker(failureThreshold: 3);

        breaker.ReportFailure();
        breaker.ReportFailure();
        breaker.ReportSuccess();

        Assert.Equal(0, breaker.ConsecutiveFailures);
        breaker.ReportFailure();
        breaker.ReportFailure();
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public void AfterBreakDuration_HalfOpenAllowsOneProbe()
    {
        var now = DateTimeOffset.UtcNow;
        var breaker = new CoreBankCircuitBreaker(
            failureThreshold: 1, breakDuration: TimeSpan.FromSeconds(30), clock: () => now);

        breaker.ReportFailure();
        Assert.Equal(CircuitState.Open, breaker.State);
        Assert.False(breaker.TryEnter());

        // 断路时长未到
        now = now.AddSeconds(29);
        Assert.False(breaker.TryEnter());

        // 断路时长已过 → 半开，只放一个探针
        now = now.AddSeconds(2);
        Assert.Equal(CircuitState.HalfOpen, breaker.State);
        Assert.True(breaker.TryEnter(), "半开应放一个探针");
        Assert.False(breaker.TryEnter(), "半开期间只允许一个探针");
    }

    [Fact]
    public void HalfOpenProbeSuccess_ClosesCircuit()
    {
        var now = DateTimeOffset.UtcNow;
        var breaker = new CoreBankCircuitBreaker(
            failureThreshold: 1, breakDuration: TimeSpan.FromSeconds(10), clock: () => now);

        breaker.ReportFailure();
        now = now.AddSeconds(11);
        Assert.True(breaker.TryEnter());

        breaker.ReportSuccess();

        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.True(breaker.TryEnter());
    }

    [Fact]
    public void HalfOpenProbeFailure_ReopensCircuit()
    {
        var now = DateTimeOffset.UtcNow;
        var breaker = new CoreBankCircuitBreaker(
            failureThreshold: 1, breakDuration: TimeSpan.FromSeconds(10), clock: () => now);

        breaker.ReportFailure();
        now = now.AddSeconds(11);
        Assert.True(breaker.TryEnter());

        breaker.ReportFailure();

        Assert.Equal(CircuitState.Open, breaker.State);
        Assert.False(breaker.TryEnter(), "探针失败后必须重新打开");
    }

    [Fact]
    public void ThresholdZero_DisablesCircuit()
    {
        var breaker = new CoreBankCircuitBreaker(failureThreshold: 0);

        for (var i = 0; i < 20; i++)
        {
            breaker.ReportFailure();
        }

        Assert.False(breaker.Enabled);
        Assert.True(breaker.TryEnter(), "阈值 ≤0 表示关闭熔断");
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public async Task Handler_OpenCircuit_FailsFastWithoutCallingInner()
    {
        var breaker = new CoreBankCircuitBreaker(failureThreshold: 1);
        var inner = new CountingHandler();
        var handler = new CoreBankCircuitHandler(breaker, NullLogger<CoreBankCircuitHandler>.Instance)
        {
            InnerHandler = inner
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1") };

        // 先让熔断打开
        breaker.ReportFailure();
        Assert.Equal(CircuitState.Open, breaker.State);

        await Assert.ThrowsAsync<CoreBankUnavailableException>(
            () => client.GetAsync("/anything"));

        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Handler_TransportFailure_CountsTowardsCircuit()
    {
        var breaker = new CoreBankCircuitBreaker(failureThreshold: 1);
        var inner = new ThrowingHandler();
        var handler = new CoreBankCircuitHandler(breaker, NullLogger<CoreBankCircuitHandler>.Instance)
        {
            InnerHandler = inner
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1") };

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/anything"));

        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public async Task Handler_BusinessError_DoesNotOpenCircuit()
    {
        // 关键语义：服务是活的、只是这笔业务失败（MockBank 故障注入就返回 503），
        // 这属于业务语义，**不该**把熔断打开 —— 否则会掩盖真正的业务错误。
        var breaker = new CoreBankCircuitBreaker(failureThreshold: 1);
        var inner = new StatusHandler(HttpStatusCode.ServiceUnavailable);
        var handler = new CoreBankCircuitHandler(breaker, NullLogger<CoreBankCircuitHandler>.Instance)
        {
            InnerHandler = inner
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1") };

        var resp = await client.GetAsync("/anything");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("模拟连接被拒");
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }
}
