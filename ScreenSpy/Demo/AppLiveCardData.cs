using System;
using ScreenSpy.Collector;
using ScreenSpy.Rendering;
using ScreenSpy.Scheduling;

namespace ScreenSpy.Demo;

/// <summary>
/// 把 M3 的**真实**按软件统计接到卡片上（用于肉眼确认“切换软件 → 时长归到对应软件”）。
/// M5 完成后随演示入口一起删除 —— 届时卡片数据将由 M4 存储提供。
/// </summary>
internal static class AppLiveCardData
{
    /// <summary>演示用总额限：5 小时（真实限额来自 M9 设置）。</summary>
    private static readonly TimeSpan DemoLimit = TimeSpan.FromHours(5);

    /// <summary>卡片固定高度 360px，y≈168 之后每行 30px → 最多 6 行。</summary>
    private const int MaxRows = 6;

    public static CardModel Build(int tick, AppUsageTracker tracker, ActivityScheduler scheduler, int topN = 5)
    {
        TimeSpan today = scheduler.TodayActive;
        TimeSpan total = tracker.Total;

        var m = new CardModel
        {
            Title = "ScreenSpy（M3 实时数据）",
            TotalTime = Format(today),
            LimitText = $"{Format(today)} / {Format(DemoLimit)}",
            LimitRatio = Math.Min(1.0, today.TotalSeconds / DemoLimit.TotalSeconds),
            CurrentApp = tracker.HasLastSample
                ? tracker.CurrentDisplayName
                : "启动中",
        };

        int rows = 0;
        foreach (AppUsageEntry entry in tracker.Top(topN))
        {
            m.TopApps.Add((entry.DisplayName, Format(entry.Time), entry.Share));
            rows++;
        }

        // 应用不足 5 行时，用“已过滤 / 未归因”补齐 —— 这两个数字是时间守恒的另外两项，
        // 放在卡片上可以一眼看出“活跃时长都去哪了”。
        if (rows < 5)
        {
            double v = total.TotalMilliseconds;
            m.TopApps.Add(("已过滤（桌面/外壳/锁屏/自身）", Format(tracker.FilteredTotal),
                v > 0 ? tracker.FilteredTotal.TotalMilliseconds / v : 0.0));
            rows++;
        }

        if (rows < 5)
        {
            double v = total.TotalMilliseconds;
            m.TopApps.Add(("未归因（无前台/未知）", Format(tracker.UnattributedTotal),
                v > 0 ? tracker.UnattributedTotal.TotalMilliseconds / v : 0.0));
            rows++;
        }

        // 最后一行：证明卡片仍在刷新（并顺便暴露心跳序号）。
        if (rows < MaxRows)
            m.TopApps.Add(("刷新", $"t={tick}", 0.10));

        return m;
    }

    private static string Format(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
