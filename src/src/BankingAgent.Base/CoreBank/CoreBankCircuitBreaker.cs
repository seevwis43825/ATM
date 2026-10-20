// ===== 核心银行调用的熔断器 =====
//
// 为什么需要：
//   宿主每个对话请求都要同步调用核心银行（CoreBankClient）。一旦核心系统不可用
//   （网络中断 / 服务挂掉 / 连接池耗尽），每个请求都会阻塞到超时（默认 15 秒）——
//   请求线程被一个个占住，宿主整体被拖垮，**连不依赖核心系统的能力也一起不可用**。
//   熔断器的职责是：核心系统明确不可用时**快速失败**，把伤害圈在核心系统这一侧。
//
// 熔断判据只算「传输层失败」（连接被拒 / 超时 / 读写中断），
// **不算 HTTP 业务错误码**（5xx 业务响应）：服务端明明是活的、只是这笔业务失败，
//   那属于业务语义，不该把熔断器打开（MockBank 的故障注入就靠 503 验证错误处理，
//   若把 503 计入，打开熔断反而会掩盖真正的业务错误）。
//
// 状态机：
//   Closed（正常）──连续失败达阈值──► Open（快速失败）
//   Open ──超过断路时长──► HalfOpen（放一个探针）
//   HalfOpen ──探针成功──► Closed；──探针失败──► Open（重新计时）

namespace BankingAgent.Base.CoreBank;

/// <summary>熔断器状态。</summary>
public enum CircuitState
{
    /// <summary>正常放行。</summary>
    Closed,

    /// <summary>已打开：直接快速失败，不再发起调用。</summary>
    Open,

    /// <summary>半开：只放一个探针调用，验证下游是否恢复。</summary>
    HalfOpen
}

/// <summary>
/// 核心银行调用的熔断器。
/// </summary>
/// <remarks>
/// 线程安全：所有状态变更都在锁内完成。宿主是多线程的，熔断器被所有请求共享。
/// <para>
/// 时钟可注入，便于确定性测试。
/// </para>
/// </remarks>
public sealed class CoreBankCircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _breakDuration;
    private readonly Func<DateTimeOffset> _clock;
    private readonly bool _enabled;
    private readonly object _gate = new();

    private int _consecutiveFailures;
    private DateTimeOffset? _openedAt;
    private bool _probeInFlight;

    /// <summary>构造熔断器。</summary>
    /// <param name="failureThreshold">连续失败多少次后打开熔断；≤0 表示关闭熔断。</param>
    /// <param name="breakDuration">打开后多久允许一次探针（半开）。</param>
    /// <param name="clock">时钟来源，默认系统时钟。</param>
    public CoreBankCircuitBreaker(
        int failureThreshold = 5,
        TimeSpan? breakDuration = null,
        Func<DateTimeOffset>? clock = null)
    {
        _enabled = failureThreshold > 0;
        _failureThreshold = Math.Max(1, failureThreshold);
        _breakDuration = breakDuration ?? TimeSpan.FromSeconds(30);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>熔断是否启用（阈值 ≤0 时关闭）。</summary>
    public bool Enabled => _enabled;

    /// <summary>当前状态。</summary>
    public CircuitState State
    {
        get
        {
            lock (_gate)
            {
                return _openedAt is null
                    ? CircuitState.Closed
                    : (_clock() - _openedAt.Value >= _breakDuration
                        ? CircuitState.HalfOpen
                        : CircuitState.Open);
            }
        }
    }

    /// <summary>连续失败次数（用于诊断与 /health 暴露）。</summary>
    public int ConsecutiveFailures
    {
        get { lock (_gate) return _consecutiveFailures; }
    }

    /// <summary>
    /// 请求是否被放行。
    /// </summary>
    /// <remarks>
    /// 返回 false 表示熔断已打开，调用方应**立即失败**，不要发起网络调用。
    /// </remarks>
    /// <returns>true = 放行；false = 快速失败。</returns>
    public bool TryEnter()
    {
        if (!_enabled) return true;

        lock (_gate)
        {
            if (_openedAt is null) return true;

            if (_clock() - _openedAt.Value < _breakDuration) return false;

            // 断路时长已过 → 半开，只放一个探针
            if (_probeInFlight) return false;

            _probeInFlight = true;
            return true;
        }
    }

    /// <summary>报告一次成功。</summary>
    public void ReportSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _openedAt = null;
            _probeInFlight = false;
        }
    }

    /// <summary>报告一次失败（传输层失败）。</summary>
    public void ReportFailure()
    {
        if (!_enabled) return;

        lock (_gate)
        {
            if (_probeInFlight)
            {
                // 半开探针失败 → 重新打开，重新计时
                _probeInFlight = false;
                _openedAt = _clock();
                return;
            }

            _consecutiveFailures++;
            if (_consecutiveFailures >= _failureThreshold)
            {
                _openedAt = _clock();
            }
        }
    }
}

/// <summary>核心系统不可用（熔断打开或传输层失败）时抛出。</summary>
public sealed class CoreBankUnavailableException : Exception
{
    /// <summary>构造异常。</summary>
    /// <param name="message">错误信息。</param>
    public CoreBankUnavailableException(string message) : base(message) { }
}
