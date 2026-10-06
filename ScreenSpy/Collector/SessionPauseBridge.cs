using System;
using System.Threading;
using ScreenSpy.Scheduling;

namespace ScreenSpy.Collector;

/// <summary>
/// M2 的接线点：把「锁屏 / 睡眠事件」（主路径）与「WTS 锁屏探测」（初值 + 自愈）
/// 合成一个暂停信号，驱动 <see cref="ActivityScheduler.Paused"/>（开发文档 §5.3）。
///
/// 三条防线（层层递进，任一失效都不至于把睡眠算成活跃）：
///  1. **事件**：<c>SessionSwitch</c> 锁屏/解锁、<c>PowerModeChanged</c> 睡眠/恢复 —— 及时，但可能漏发；
///  2. **探测**：启动初值 + 周期自愈，修正“启动即锁屏”“解锁事件丢失”这类缺口；
///  3. **时间空洞**：间隔异常巨大时不计入（在 <see cref="ActivityScheduler"/> 内，与事件无关）。
///
/// 精度说明：暂停是**按拍生效**的（心跳 1s）。因此边界上最多有 **±1 拍（约 ±1s）** 的误差：
/// 锁屏侧偏“少计”，解锁侧偏“多计”，方向与幅度都有界。
/// </summary>
internal sealed class SessionPauseBridge : IDisposable
{
    private readonly ActivityScheduler _scheduler;
    private readonly ISessionSignalSource _signals;
    private readonly ISessionStateProbe _probe;
    private readonly SessionStateTracker _state = new();
    private readonly Timer? _probeTimer;
    private readonly string _probeIntervalText;

    public SessionPauseBridge(ActivityScheduler scheduler,
                              ISessionSignalSource signals,
                              ISessionStateProbe probe,
                              TimeSpan? probeInterval = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));

        _state.Changed += OnStateChanged;
        _signals.Signal += OnSignal;

        // 事件源无法回溯“进程启动前工作站就已经锁定”，所以启动时先探测一次初值。
        SyncFromProbe();

        if (probeInterval is { } interval && interval > TimeSpan.Zero)
        {
            // 周期自愈：哪怕解锁事件被漏掉（会永久少计），最多一个周期后也会被纠正。
            _probeTimer = new Timer(_ => SyncFromProbe(), null, interval, interval);
            _probeIntervalText = $"{interval.TotalSeconds:F0}s";
        }
        else
        {
            _probeIntervalText = "关闭";
        }

        // 订阅失败时探测是唯一来源，必须让调用方知道（否则会安静地失去锁屏保护）。
        if (!_signals.IsHooked)
            Log?.Invoke($"[警告] 会话/电源事件订阅失败（{_signals.HookError}），将仅依赖周期探测。");
    }

    public SessionStateTracker State => _state;

    /// <summary>“是否需要暂停”发生切换的次数。</summary>
    public int PauseTransitions { get; private set; }

    /// <summary>最近一次状态变化（或校正）的来源。</summary>
    public PauseSource LastSource { get; private set; } = PauseSource.None;

    /// <summary>最近一次探测结果。</summary>
    public SessionLockProbeResult LastProbeResult { get; private set; } = SessionLockProbeResult.Unknown;

    /// <summary>最近一次探测失败的描述（正常为 null）。</summary>
    public string? LastProbeError { get; private set; }

    public bool IsHooked => _signals.IsHooked;

    public string? HookError => _signals.HookError;

    public string ProbeIntervalText => _probeIntervalText;

    /// <summary>状态切换 / 自愈时的可读日志（在触发线程上同步回调，实现不得阻塞）。</summary>
    public event Action<string>? Log;

    /// <summary>
    /// 锁屏 / 睡眠标志位任一变化时触发（含“暂停结论未变”的内部变化）。
    /// 供上层观察与记录（例如日后写入数据库的暂停区间）。
    /// </summary>
    public event Action<SessionStateChange>? StateChanged;

    private void OnSignal(SessionSignal signal, string reason)
    {
        SessionStateChange change = _state.Apply(signal, PauseSource.Event);

        if (change.Changed)
            Log?.Invoke($"[事件] {reason} → {Describe(change)}");
    }

    private void OnStateChanged(SessionStateChange change)
    {
        LastSource = change.Source;

        // Paused 是幂等的简单标志位，直接同步即可（比“仅当 PauseChanged 才写”更不易出错）。
        _scheduler.Paused = change.ShouldPauseAfter;

        if (change.PauseChanged) PauseTransitions++;

        StateChanged?.Invoke(change);
    }

    /// <summary>
    /// 主动探测一次并校正“是否锁定”（启动初值 / 周期自愈）。
    /// 探测结果不确定时**不作任何改动** —— 绝不用猜测覆盖事件给出的状态。
    /// </summary>
    public SessionStateChange SyncFromProbe()
    {
        SessionLockProbeResult result;
        try
        {
            result = _probe.ProbeLocked();
            LastProbeError = null;
        }
        catch (Exception ex)
        {
            result = SessionLockProbeResult.Unknown;
            LastProbeError = ex.GetType().Name + ": " + ex.Message;
        }

        LastProbeResult = result;
        if (result == SessionLockProbeResult.Unknown) return default;

        SessionStateChange change = _state.SynchronizeLock(
            result == SessionLockProbeResult.Locked, PauseSource.Probe);

        if (change.LockChanged)
            Log?.Invoke($"[探测] 校正为「{(result == SessionLockProbeResult.Locked ? "已锁定" : "未锁定")}」" +
                        $"（覆盖事件源漏报）→ {Describe(change)}");

        return change;
    }

    private static string Describe(SessionStateChange c) =>
        $"锁屏={Yn(c.LockedAfter)} 睡眠={Yn(c.SuspendedAfter)} 暂停={Yn(c.ShouldPauseAfter)}";

    private static string Yn(bool b) => b ? "是" : "否";

    public void Dispose()
    {
        _state.Changed -= OnStateChanged;
        _signals.Signal -= OnSignal;
        _probeTimer?.Dispose();
        _signals.Dispose();
    }
}
