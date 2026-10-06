using System;

namespace ScreenSpy.Storage;

/// <summary>
/// 增量的类别 —— 决定它最终落进 <c>app_usage</c> 还是 <c>daily_activity</c>。
///
/// 为什么非软件时长也走同一条队列：只有同一条队列 + 同一次 flush，
/// 才能保证软件行与非软件行**在同一事务里**提交，不会出现半截状态。
/// </summary>
internal enum UsageDeltaKind
{
    /// <summary>可归因到具体软件（App / UWP 宿主）→ <c>app_usage</c>。</summary>
    App = 0,

    /// <summary>“不计入使用时长”（桌面 / 外壳 / 自身）→ <c>daily_activity.filtered_seconds</c>。</summary>
    Filtered = 1,

    /// <summary>“无前台 / 未知”→ <c>daily_activity.unattributed_seconds</c>。</summary>
    Unattributed = 2,
}

/// <summary>
/// 一次“可计入软件”的心跳所产生的时长增量（毫秒）。
///
/// 为什么以**增量**而非“绝对总量”传递：见 <see cref="AppUsageStore"/> 的类注释
/// （增量模型在跨天、重启续算、写失败重试三种情况下都不会把已有数据改小）。
/// </summary>
internal readonly struct UsageDelta
{
    public UsageDelta(DateOnly day, string mergeKey, string displayName, long milliseconds, long sequence,
                      UsageDeltaKind kind = UsageDeltaKind.App)
    {
        Day = day;
        MergeKey = mergeKey ?? string.Empty;
        DisplayName = displayName ?? string.Empty;
        Milliseconds = milliseconds;
        Sequence = sequence;
        Kind = kind;
    }

    /// <summary>
    /// 造一条“非软件”增量（不计入使用时长 / 无前台·未知）。合并键与展示名恒为空 ——
    /// 它们不以软件身份落库，只按天累加到 <c>daily_activity</c>。
    /// </summary>
    public static UsageDelta NonApp(DateOnly day, UsageDeltaKind kind, long milliseconds, long sequence)
        => new(day, string.Empty, string.Empty, milliseconds, sequence, kind);

    /// <summary>该拍归属的**本地日期**（取自 <c>ActivityTick.LocalTime</c>，而非“写盘时刻”）。</summary>
    public DateOnly Day { get; }

    /// <summary>合并键（进程名小写），落库时作为 app_name。</summary>
    public string MergeKey { get; }

    /// <summary>展示名（仅用于诊断，不落库；落库用合并键以避免改名重复建行）。</summary>
    public string DisplayName { get; }

    /// <summary>本拍时长（毫秒）。</summary>
    public long Milliseconds { get; }

    /// <summary>产生它的心跳序号（诊断用，便于与 M1/M3 日志对账）。</summary>
    public long Sequence { get; }

    /// <summary>增量类别（软件 / 不计入使用时长 / 无前台·未知）。</summary>
    public UsageDeltaKind Kind { get; }

    public override string ToString() =>
        $"{Day:yyyy-MM-dd} {Kind} {MergeKey} +{Milliseconds}ms (#{Sequence})";
}
