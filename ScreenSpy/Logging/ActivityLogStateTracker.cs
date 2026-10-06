using System;
using ScreenSpy.Collector;
using ScreenSpy.Scheduling;

namespace ScreenSpy.Logging;

/// <summary>
/// 把逐拍观测流**折叠**成“仅变化时”的记录（纯逻辑：无 IO、无线程、无 Win32）。
///
/// 状态界定键 = <c>(是否计入, 原因, 分类, 合并键)</c>。键不变就一直累加到同一个状态里，
/// 键一变就把上一段**关闭**并交还给调用方去落盘。因此：
///  * 同软件连续使用 → 只有一行（含总时长与拍数）；
///  * 进程名不同 / 分类不同 / 计入与否不同 / 不计入的原因不同 → 各切一行。
///
/// 标题**不参与**状态界定（否则浏览器每翻一页就切一行）；但会额外记录
/// 首标题、末标题与变更次数，做到“既不刷屏，也不骗人”。
///
/// 跨天：一行绝不跨天。日期一变就先关闭上一段、再开新段；触发跨天的那一拍
/// **整拍归入新的一天** —— 这与 <c>ActivityScheduler</c> 的跨天处理
/// （先重置今日累计、再累加本拍间隔）保持一致，否则日志与统计会对不上。
/// </summary>
internal sealed class ActivityLogStateTracker
{
    private readonly bool _trackTitles;
    private Open? _open;

    public ActivityLogStateTracker(bool trackTitles = true) => _trackTitles = trackTitles;

    /// <summary>当前是否有一段未关闭的状态（Dispose 前应 <see cref="Flush"/>，否则尾段会丢）。</summary>
    public bool HasOpenState => _open is not null;

    /// <summary>
    /// 记录一次观测。返回**非 null** 表示“上一段状态已结束”，调用方应把它写盘。
    /// </summary>
    public ActivityLogRecord? Observe(in AppUsageObservation observation)
    {
        ActivityTick tick = observation.Tick;
        ForegroundAppSample sample = observation.Sample;
        ActivityLogReason reason = ReasonOf(tick, observation.Counted);
        DateOnly day = DateOnly.FromDateTime(tick.LocalTime);

        Open? open = _open;
        if (open is not null && open.Matches(day, observation.Counted, reason, sample))
        {
            open.Accumulate(tick, sample, _trackTitles);
            return null;
        }

        ActivityLogRecord? closed = open?.Close();
        _open = Open.Start(day, tick, sample, observation.Counted, reason, _trackTitles);
        return closed;
    }

    /// <summary>关闭当前状态（进程退出 / 停止记录时调用，避免丢掉最后一段）。</summary>
    public ActivityLogRecord? Flush()
    {
        Open? open = _open;
        _open = null;
        return open?.Close();
    }

    private static ActivityLogReason ReasonOf(in ActivityTick tick, bool counted)
    {
        if (counted) return ActivityLogReason.Active;
        if (tick.IsPaused) return ActivityLogReason.Paused;
        if (tick.IsGap) return ActivityLogReason.Gap;
        return ActivityLogReason.Idle;
    }

    private static long Ms(TimeSpan t)
        => (long)Math.Round(t.TotalMilliseconds, MidpointRounding.AwayFromZero);

    /// <summary>一段进行中的状态（可变）。</summary>
    private sealed class Open
    {
        public DateOnly Day;
        public DateTime Started;
        public long DurationMs;
        public long Ticks;
        public long SequenceFrom;
        public long SequenceTo;
        public bool Counted;
        public ActivityLogReason Reason;
        public ForegroundAppKind Kind;

        public string ProcessName = string.Empty;
        public string ClassName = string.Empty;     // 首次观测
        public string Title = string.Empty;         // 首次观测
        public string TitleLast = string.Empty;     // 最近观测
        public int TitleChanges;

        public string DisplayName = string.Empty;
        public string MergeKey = string.Empty;
        public int ProcessId;                       // 最近观测
        public IntPtr Hwnd;                         // 最近观测
        public string? Diagnostic;                  // 最近观测

        public static Open Start(DateOnly day, in ActivityTick tick, in ForegroundAppSample sample,
                                 bool counted, ActivityLogReason reason, bool trackTitles)
        {
            var o = new Open
            {
                Day = day,
                Started = tick.LocalTime,
                DurationMs = Math.Max(0, Ms(tick.Elapsed)),
                Ticks = 1,
                SequenceFrom = tick.Sequence,
                SequenceTo = tick.Sequence,
                Counted = counted,
                Reason = reason,
                Kind = sample.Kind,
                ProcessName = sample.ProcessName ?? string.Empty,
                ClassName = sample.ClassName ?? string.Empty,
                DisplayName = sample.DisplayName ?? string.Empty,
                MergeKey = sample.MergeKey ?? string.Empty,
                ProcessId = sample.ProcessId,
                Hwnd = sample.Hwnd,
                Diagnostic = sample.Diagnostic,
            };

            if (trackTitles)
            {
                o.Title = sample.Title ?? string.Empty;
                o.TitleLast = o.Title;
            }

            return o;
        }

        public bool Matches(DateOnly day, bool counted, ActivityLogReason reason, in ForegroundAppSample sample)
            => Day == day
               && Counted == counted
               && Reason == reason
               && Kind == sample.Kind
               && string.Equals(MergeKey, sample.MergeKey ?? string.Empty, StringComparison.Ordinal);

        public void Accumulate(in ActivityTick tick, in ForegroundAppSample sample, bool trackTitles)
        {
            DurationMs += Math.Max(0, Ms(tick.Elapsed));
            Ticks++;
            SequenceTo = tick.Sequence;

            // 只更新“最近值”，首值保持不动（首值说明“切到了什么”，末值说明“离开时是什么”）。
            ProcessId = sample.ProcessId;
            Hwnd = sample.Hwnd;
            Diagnostic = sample.Diagnostic;

            if (!trackTitles) return;

            string title = sample.Title ?? string.Empty;
            if (!string.Equals(title, TitleLast, StringComparison.Ordinal))
            {
                TitleChanges++;
                TitleLast = title;
            }
        }

        public ActivityLogRecord Close()
            => new(Started, Day, DurationMs, Ticks, SequenceFrom, SequenceTo, Counted, Reason, Kind,
                   ProcessName, ClassName, Title, TitleLast, TitleChanges,
                   DisplayName, MergeKey, ProcessId, Hwnd, Diagnostic);
    }
}
