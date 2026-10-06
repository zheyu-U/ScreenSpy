using System;

namespace ScreenSpy.Scheduling;

/// <summary>
/// 一次心跳的采样结果。刻意设计为 <c>readonly struct</c>：
/// 心跳每秒一次、长期运行，避免每拍产生堆分配（10 分钟长跑的内存验收会盯这一点）。
/// </summary>
internal readonly struct ActivityTick
{
    public ActivityTick(long sequence, DateTime localTime, TimeSpan idleTime, bool idleSourceAvailable,
                        bool isActive, bool isPaused, bool isGap, TimeSpan elapsed, TimeSpan todayActive)
    {
        Sequence = sequence;
        LocalTime = localTime;
        IdleTime = idleTime;
        IdleSourceAvailable = idleSourceAvailable;
        IsActive = isActive;
        IsPaused = isPaused;
        IsGap = isGap;
        Elapsed = elapsed;
        TodayActive = todayActive;
    }

    /// <summary>自启动起的心跳序号（从 1 开始）。</summary>
    public long Sequence { get; }

    public DateTime LocalTime { get; }

    /// <summary>本拍测得的空闲时长。</summary>
    public TimeSpan IdleTime { get; }

    /// <summary>本拍的空闲数据是否可用（false 表示 GetLastInputInfo 失败）。</summary>
    public bool IdleSourceAvailable { get; }

    /// <summary>本拍是否被判定为活跃（因而被计入）。</summary>
    public bool IsActive { get; }

    /// <summary>本拍是否处于暂停态（锁屏 / 睡眠，见 M2）。</summary>
    public bool IsPaused { get; }

    /// <summary>
    /// 本拍是否被判定为“时间空洞”（间隔超过可信任上限，见 M2 的第二道防线）：
    /// 典型来源是机器睡眠 / 休眠，或进程被长时间挂起。此类拍一律不计入。
    /// </summary>
    public bool IsGap { get; }

    /// <summary>本拍实际计量的墙钟间隔（上拍到此拍）。</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>本拍结束时的今日累计活跃时长。</summary>
    public TimeSpan TodayActive { get; }

    public override string ToString() =>
        $"#{Sequence} {LocalTime:HH:mm:ss} idle={IdleTime.TotalSeconds:F1}s " +
        $"{(IsActive ? "active" : "idle")} Δ={Elapsed.TotalSeconds:F2}s today={TodayActive:hh\\:mm\\:ss}" +
        (IsPaused ? " [paused]" : "") + (IsGap ? " [gap]" : "");
}
