using System;

namespace ScreenSpy.Collector;

/// <summary>
/// 暂停状态的来源。用于日志，也用于确定性检查里对“是谁改了状态”的断言。
/// </summary>
internal enum PauseSource
{
    /// <summary>无来源（初始状态，尚未发生任何变化）。</summary>
    None = 0,

    /// <summary>来自 <c>SystemEvents</c> 的会话 / 电源事件。</summary>
    Event = 1,

    /// <summary>来自 WTS 锁屏状态探测（启动初值 / 周期自愈）。</summary>
    Probe = 2,
}

/// <summary>会话 / 电源状态信号（开发文档 §5.3）。</summary>
internal enum SessionSignal
{
    /// <summary>工作站被锁定（Win+L、屏保锁定等）。</summary>
    Locked,

    /// <summary>工作站解锁。</summary>
    Unlocked,

    /// <summary>系统进入睡眠 / 休眠。</summary>
    Suspended,

    /// <summary>系统从睡眠 / 休眠恢复。</summary>
    Resumed,
}

/// <summary>
/// 一次状态变化的前后快照。
///
/// 设计要点：把“是否锁定”和“是否睡眠”当作**两个独立的标志位**，而不是一个三态枚举。
/// 原因是二者会**重叠**：例如锁屏后再合盖睡眠，醒来时先收到 Resume 而尚未解锁。
/// 若用单一状态，这类顺序就会互相覆盖，导致“仍在锁屏却恢复了计时”。
/// 只要两者有一个为真，就必须暂停计时。
/// </summary>
internal readonly struct SessionStateChange
{
    public SessionStateChange(PauseSource source,
                              bool lockedBefore, bool suspendedBefore,
                              bool lockedAfter, bool suspendedAfter)
    {
        Source = source;
        LockedBefore = lockedBefore;
        SuspendedBefore = suspendedBefore;
        LockedAfter = lockedAfter;
        SuspendedAfter = suspendedAfter;
    }

    /// <summary>本次变化的来源。</summary>
    public PauseSource Source { get; }

    public bool LockedBefore { get; }
    public bool SuspendedBefore { get; }
    public bool LockedAfter { get; }
    public bool SuspendedAfter { get; }

    public bool ShouldPauseBefore => LockedBefore || SuspendedBefore;
    public bool ShouldPauseAfter => LockedAfter || SuspendedAfter;

    /// <summary>“是否需要暂停”这一点是否发生了变化（唯一需要通知调度器的情况）。</summary>
    public bool PauseChanged => ShouldPauseBefore != ShouldPauseAfter;

    public bool LockChanged => LockedBefore != LockedAfter;
    public bool SuspendChanged => SuspendedBefore != SuspendedAfter;

    /// <summary>两个标志位是否有任一变化（含“暂停结论不变”的内部变化）。</summary>
    public bool Changed => LockChanged || SuspendChanged;

    public override string ToString() =>
        $"锁屏 {Yn(LockedBefore)}→{Yn(LockedAfter)}，睡眠 {Yn(SuspendedBefore)}→{Yn(SuspendedAfter)}，" +
        $"暂停 {Yn(ShouldPauseBefore)}→{Yn(ShouldPauseAfter)}（来源 {Source}）";

    private static string Yn(bool b) => b ? "是" : "否";
}

/// <summary>
/// “锁屏 / 睡眠 → 暂停计时”的状态机（开发文档 §5.3 的逻辑部分）。
///
/// 这是**纯逻辑**：不碰任何 Win32 API、不依赖时钟，因此所有顺序组合都能确定性推演
/// （当前计划不含测试工程，由 <c>--m2-selfcheck</c> 内嵌的确定性检查覆盖）。
///
/// 线程模型：事件源（SystemEvents 专用线程）与探测定时器（线程池）会并发调用，
/// 因此内部加锁；<see cref="Changed"/> 在**锁外**触发，避免调用方回调里再取锁造成死锁。
/// </summary>
internal sealed class SessionStateTracker
{
    private readonly object _gate = new();
    private bool _locked;
    private bool _suspended;

    public bool Locked { get { lock (_gate) return _locked; } }
    public bool Suspended { get { lock (_gate) return _suspended; } }

    /// <summary>是否需要暂停计时：锁屏**或**睡眠（二者重叠时依然为真）。</summary>
    public bool ShouldPause { get { lock (_gate) return _locked || _suspended; } }

    /// <summary>任一标志位发生变化时触发（暂停结论未变时也会触发，便于日志）。</summary>
    public event Action<SessionStateChange>? Changed;

    /// <summary>应用一个会话 / 电源信号。</summary>
    public SessionStateChange Apply(SessionSignal signal, PauseSource source = PauseSource.Event)
        => Update(source, locked: signal switch
        {
            SessionSignal.Locked => true,
            SessionSignal.Unlocked => false,
            _ => (bool?)null,
        }, suspended: signal switch
        {
            SessionSignal.Suspended => true,
            SessionSignal.Resumed => false,
            _ => (bool?)null,
        });

    /// <summary>
    /// 用探测结果校正“是否锁定”这一个标志位（睡眠标志位不受影响）。
    ///
    /// 用途有两个：
    ///  1. **启动初值**：事件源无法回溯“进程启动前就已经锁屏”的情况；
    ///  2. **周期自愈**：万一解锁事件被漏掉（会永久少计），探测可把它纠正回来。
    /// 注意探测**不会**处理睡眠：睡眠由电源事件负责，而“时间空洞”还有第二道防线（见 ActivityRules）。
    /// </summary>
    public SessionStateChange SynchronizeLock(bool locked, PauseSource source = PauseSource.Probe)
        => Update(source, locked, suspended: null);

    private SessionStateChange Update(PauseSource source, bool? locked, bool? suspended)
    {
        SessionStateChange change;

        lock (_gate)
        {
            bool lb = _locked, sb = _suspended;
            if (locked is { } l) _locked = l;
            if (suspended is { } s) _suspended = s;
            change = new SessionStateChange(source, lb, sb, _locked, _suspended);
        }

        if (change.Changed) Changed?.Invoke(change);
        return change;
    }
}
