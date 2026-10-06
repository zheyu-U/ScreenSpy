using System;
using System.Threading;
using ScreenSpy.Collector;

namespace ScreenSpy.Scheduling;

/// <summary>
/// M1 的核心：每秒心跳、以“真实经过时间”累计活跃时长的调度器（开发文档 §5.1 + §5.2）。
///
/// 设计要点：
///  * **按差值累计，而非固定累加**：每拍取 <see cref="ITickSource.Milliseconds"/> 与上一拍求差，
///    累加的是真实间隔。因此定时器抖动、短暂卡顿都不会产生计时漂移（这是 §5.1 的验收项）。
///  * **是否计入由空闲决定**：空闲 ≥ <see cref="IdleThreshold"/>（默认 300s）即视为挂机，该拍不计入。
///  * **跨天自动重置**：本地日期变化时把今日累计清零并触发 <see cref="DayRolled"/>。
///  * **两个独立的暂停来源**：<see cref="Paused"/>（M2：锁屏 / 睡眠）与 <see cref="UserPaused"/>
///    （M5b：托盘「暂停统计」）。生效暂停是两者的**或** —— 必须分开成两个字段，
///    因为 M2 的周期探测会按会话状态**重写** <see cref="Paused"/>（详见 <see cref="UserPaused"/>）。
///
/// 可测性：时间源（<see cref="ITickSource"/>）、本地时间（<c>localNow</c>）与空闲源
/// （<see cref="IIdleClock"/>）均可注入，因此上述全部行为都能确定性地复现。
///
/// 线程模型：心跳在**线程池**上执行，属性读取加锁；事件在锁外触发，避免调用方回调里再取锁造成死锁。
/// </summary>
internal sealed class ActivityScheduler : IDisposable
{
    private readonly IIdleClock _clock;
    private readonly ITickSource _ticks;
    private readonly Func<DateTime> _localNow;
    private readonly object _gate = new();

    private Timer? _timer;
    private int _inTick;
    private long _timerFirings;
    private long _lastTickMs;
    private long _sequence;
    private long _activeMsToday;
    private long _accountedMs;
    private DateOnly _day;

    public ActivityScheduler(IIdleClock clock,
                             TimeSpan? idleThreshold = null,
                             TimeSpan? heartbeat = null,
                             ITickSource? tickSource = null,
                             Func<DateTime>? localNow = null,
                             TimeSpan? maxCreditedInterval = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ticks = tickSource ?? SystemTickSource.Instance;
        _localNow = localNow ?? (() => DateTime.Now);
        IdleThreshold = idleThreshold ?? ActivityRules.DefaultIdleThreshold;
        Heartbeat = heartbeat ?? TimeSpan.FromSeconds(1);
        MaxCreditedInterval = maxCreditedInterval ?? ActivityRules.DefaultMaxCreditedInterval;

        if (IdleThreshold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleThreshold), "空闲阈值必须大于 0。");
        if (Heartbeat <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(heartbeat), "心跳间隔必须大于 0。");
        if (MaxCreditedInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxCreditedInterval), "可信任间隔上限必须大于 0。");
    }

    /// <summary>空闲多久算挂机（达到即不计时）。</summary>
    public TimeSpan IdleThreshold { get; }

    /// <summary>心跳间隔。</summary>
    public TimeSpan Heartbeat { get; }

    /// <summary>
    /// 单拍可被计入的最大间隔（M2 第二道防线）。超过它的拍被判为“时间空洞”，一律不计入，
    /// 用于兜住睡眠 / 休眠 / 进程长时间挂起 —— 即使电源事件缺失也不会把整段睡眠算成活跃。
    /// </summary>
    public TimeSpan MaxCreditedInterval { get; }

    /// <summary>今日累计活跃时长（以本地日期为界）。</summary>
    public TimeSpan TodayActive { get { lock (_gate) return TimeSpan.FromMilliseconds(_activeMsToday); } }

    /// <summary>被计量的墙钟时长（所有心跳间隔之和），用于与真实耗时对照、检查漂移。</summary>
    public TimeSpan AccountedElapsed { get { lock (_gate) return TimeSpan.FromMilliseconds(_accountedMs); } }

    /// <summary>当前统计日（本地）。</summary>
    public DateOnly Day { get { lock (_gate) return _day; } }

    /// <summary>最近一拍测得的空闲时长。</summary>
    public TimeSpan LastIdle { get; private set; }

    /// <summary>最近一拍是否被判为活跃。</summary>
    public bool LastActive { get; private set; }

    /// <summary>空闲数据源是否可用。</summary>
    public bool IdleSourceAvailable => _clock.IsAvailable;

    /// <summary>是否正在运行。</summary>
    public bool IsRunning => _timer is not null;

    /// <summary>
    /// 会话暂停（锁屏 / 睡眠）。由 M2 的 <c>SessionPauseBridge</c> 驱动。
    ///
    /// 用 <c>volatile</c> 支撑：写入者可能是 SystemEvents 的专用线程或探测定时器的线程池线程，
    /// 读取者是心跳线程，必须保证可见性。
    ///
    /// **不要把用户的手动暂停写进这里**：M2 的周期探测（默认 5s）会用会话状态重写本属性，
    /// 用户暂停会在下一次探测时被静默清掉。用户暂停请写 <see cref="UserPaused"/>。
    /// </summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    private volatile bool _paused;

    /// <summary>
    /// 用户手动暂停（M5b 托盘「暂停统计」）。与 <see cref="Paused"/> 是**或**关系：
    /// 生效暂停 = <see cref="Paused"/> || <see cref="UserPaused"/>。
    ///
    /// 之所以必须与 <see cref="Paused"/> 分开：M2 的周期探测每 5 秒会把
    /// <see cref="Paused"/> 重写成“当前会话是否需要暂停”，两者若共用同一字段，
    /// 用户刚点的暂停会在几秒后被无声清除（M1SelfCheck 里有对应的回归检查）。
    ///
    /// 语义（本次确认）：暂停期间**只不计时**，心跳 / 前台采样 / 原始日志照常，
    /// 因此日志里会留下 reason=paused 的段落，事后可追溯。
    /// </summary>
    public bool UserPaused
    {
        get => _userPaused;
        set => _userPaused = value;
    }

    private volatile bool _userPaused;

    /// <summary>当前是否处于暂停态（锁屏/睡眠 或 用户手动，任一成立即为 true）。</summary>
    public bool IsPaused => Paused || UserPaused;

    /// <summary>被判定为“时间空洞”（间隔超过 <see cref="MaxCreditedInterval"/>）的拍数。</summary>
    public long GapTicks => Interlocked.Read(ref _gapTicks);

    private long _gapTicks;

    /// <summary>已完成的心跳次数。</summary>
    public long Sequence => Interlocked.Read(ref _sequence);

    /// <summary>定时器实际回调次数。与 <see cref="Sequence"/> 的差值即“回调了但处理失败”的次数。</summary>
    public long TimerFirings => Interlocked.Read(ref _timerFirings);

    /// <summary>心跳处理中最近一次异常（仅记录首条，用于诊断；正常运行时为 null）。</summary>
    public string? LastError { get; private set; }

    /// <summary>每拍之后触发（线程池线程上；异常不会传播到心跳线程之外）。</summary>
    public event Action<ActivityTick>? Ticked;

    /// <summary>本地日期翻页、今日累计被重置之后触发。</summary>
    public event Action<DateOnly>? DayRolled;

    public void Start()
    {
        if (_timer is not null) throw new InvalidOperationException("ActivityScheduler 已启动。");

        lock (_gate)
        {
            _day = DateOnly.FromDateTime(_localNow());
            _activeMsToday = 0;
            _accountedMs = 0;
            _sequence = 0;
            _gapTicks = 0;
            _lastTickMs = _ticks.Milliseconds;
            LastIdle = TimeSpan.Zero;
            LastActive = false;
        }

        _timer = new Timer(OnHeartbeat, null, Heartbeat, Heartbeat);
    }

    public void Stop()
    {
        Timer? timer = _timer;
        _timer = null;
        timer?.Dispose();
    }

    /// <summary>
    /// 同步采一次样并返回结果（不触发 <see cref="Ticked"/>，也不依赖定时器）。
    /// 可**不调用 <see cref="Start"/>** 直接使用（届时以 0 作为首拍基准），
    /// 便于确定性检查；正式运行则先 <see cref="Start"/>。
    /// </summary>
    public ActivityTick Sample()
    {
        lock (_gate)
        {
            if (_day == default) _day = DateOnly.FromDateTime(_localNow());
        }

        return ProcessHeartbeat();
    }

    private ActivityTick ProcessHeartbeat()
    {
        ActivityTick tick;
        DateOnly? rolled = null;

        lock (_gate)
        {
            DateTime now = _localNow();
            long nowMs = _ticks.Milliseconds;
            long elapsedMs = ActivityRules.ElapsedMs(nowMs, _lastTickMs);
            _lastTickMs = nowMs;

            var today = DateOnly.FromDateTime(now);
            if (today != _day)
            {
                _day = today;
                _activeMsToday = 0;
                rolled = today;
            }

            TimeSpan elapsed = TimeSpan.FromMilliseconds(elapsedMs);
            TimeSpan idle = _clock.IdleTime;

            // M2 第二道防线：间隔超过可信任上限 → 时间空洞（睡眠 / 休眠 / 进程被长挂起）。
            // 它与电源事件无关，因此在事件缺失的场景下同样有效。
            bool isGap = !ActivityRules.IsCreditable(elapsed, MaxCreditedInterval);

            // 暂停：M2 的锁屏/睡眠 与 M5b 的用户手动暂停，任一成立即不计入。
            bool paused = IsPaused;

            bool isActive = !paused &&
                            ActivityRules.IsActive(idle, IdleThreshold, elapsed, MaxCreditedInterval);

            _accountedMs += elapsedMs;
            if (isActive) _activeMsToday += elapsedMs;
            if (isGap) Interlocked.Increment(ref _gapTicks);

            LastIdle = idle;
            LastActive = isActive;

            tick = new ActivityTick(
                sequence: ++_sequence,
                localTime: now,
                idleTime: idle,
                idleSourceAvailable: _clock.IsAvailable,
                isActive: isActive,
                isPaused: paused,
                isGap: isGap,
                elapsed: elapsed,
                todayActive: TimeSpan.FromMilliseconds(_activeMsToday));
        }

        if (rolled is { } day) DayRolled?.Invoke(day);
        return tick;
    }

    /// <summary>
    /// 手动触发一次心跳（与定时器**完全同一路径**）。便于确定性验证，也可用于“立刻刷新一次”。
    /// </summary>
    public void Pump() => HeartbeatOnce();

    private void OnHeartbeat(object? state) => HeartbeatOnce();

    private void HeartbeatOnce()
    {
        Interlocked.Increment(ref _timerFirings);

        // 防止上一拍未完成时重入（正常情况下 1 秒内必然完成）。
        if (Interlocked.Exchange(ref _inTick, 1) == 1) return;

        try
        {
            // 关键：必须**先求值**再抛事件。
            // 若写成 Ticked?.Invoke(ProcessHeartbeat())，则当 Ticked 为 null（无人订阅）时，
            // 实参根本不会被求值 —— 心跳将完全不累计。这是本项目实际踩到过的 bug，
            // self-check 里已加回归检查（见 M1SelfCheck 的“无订阅者”用例）。
            ActivityTick tick = ProcessHeartbeat();
            Ticked?.Invoke(tick);
        }
        catch (Exception ex)
        {
            // 心跳异常不得让进程崩溃；具体错误记录在 LastError 供诊断。
            LastError ??= ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            Interlocked.Exchange(ref _inTick, 0);
        }
    }

    public void Dispose() => Stop();
}
