using System;
using System.Threading;
using ScreenSpy.Scheduling;

namespace ScreenSpy.Collector;

/// <summary>一次“心跳 + 前台采样”的组合结果（供自检与日志观察）。</summary>
internal readonly struct AppUsageObservation
{
    public AppUsageObservation(ActivityTick tick, ForegroundAppSample sample, bool counted)
    {
        Tick = tick;
        Sample = sample;
        Counted = counted;
    }

    public ActivityTick Tick { get; }
    public ForegroundAppSample Sample { get; }

    /// <summary>这一拍是否被计入（= 调度器的判定）。</summary>
    public bool Counted { get; }
}

/// <summary>
/// M3 的接线点：把「每秒心跳」（M1/M2 的 <see cref="ActivityScheduler"/>）与
/// 「前台软件采样」合成一条归属链路（开发文档 §5.4）。
///
/// 它**不修改**调度器：只在 <see cref="ActivityScheduler.Ticked"/> 上旁听，
/// 因此 M1（计时）与 M2（暂停）的既有行为完全不受影响 ——
/// 只有当调度器判定这一拍为“活跃”时，才把时长记到当时的软件头上。
///
/// 顺序保证：调度器在跨天时**先**触发 <c>DayRolled</c>、**后**触发 <c>Ticked</c>，
/// 所以这里先清空、再记当拍，不会把新一天的时长混进旧一天的榜上。
/// </summary>
internal sealed class AppUsageBridge : IDisposable
{
    private readonly ActivityScheduler _scheduler;
    private readonly IForegroundAppSource _source;
    private int _disposed;
    private long _samplingFailures;

    public AppUsageBridge(ActivityScheduler scheduler, IForegroundAppSource source)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _source = source ?? throw new ArgumentNullException(nameof(source));

        _scheduler.Ticked += OnTick;
        _scheduler.DayRolled += OnDayRolled;
    }

    /// <summary>按软件累计的结果。</summary>
    public AppUsageTracker Tracker { get; } = new();

    /// <summary>采样源。</summary>
    public IForegroundAppSource Source => _source;

    /// <summary>采样抛异常的次数（实现不应抛异常；>0 说明实现有缺陷）。</summary>
    public long SamplingFailures => Interlocked.Read(ref _samplingFailures);

    /// <summary>每拍之后触发（含未被计入的拍）。回调在心跳线程上，必须短小。</summary>
    public event Action<AppUsageObservation>? Observed;

    private void OnTick(ActivityTick tick)
    {
        ForegroundAppSample sample;
        try
        {
            sample = _source.Sample();
        }
        catch (Exception ex)
        {
            // 兜底：采样源的异常绝不允许打断心跳（那会让计时整体停摆）。
            Interlocked.Increment(ref _samplingFailures);
            sample = ForegroundAppRules.Failed(IntPtr.Zero, 0, null, ex.GetType().Name + ": " + ex.Message);
        }

        Tracker.Observe(sample, tick.Elapsed, tick.IsActive, tick.LocalTime);
        Observed?.Invoke(new AppUsageObservation(tick, sample, tick.IsActive));
    }

    private void OnDayRolled(DateOnly day) => Tracker.RollDay(day);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _scheduler.Ticked -= OnTick;
        _scheduler.DayRolled -= OnDayRolled;
    }
}
