using System;
using ScreenSpy.Rendering;
using ScreenSpy.Scheduling;

namespace ScreenSpy.Demo;

/// <summary>
/// 把 M1 调度器的**真实**活跃时长接到卡片上（用于肉眼确认 M1 效果）。
/// M5 完成后随演示入口一起删除 —— 届时卡片数据将由 M4 存储提供。
/// </summary>
internal static class LiveCardData
{
    /// <summary>演示用总额限：5 小时（真实限额来自 M9 设置）。</summary>
    private static readonly TimeSpan DemoLimit = TimeSpan.FromHours(5);

    public static CardModel Build(int tick, ActivityScheduler scheduler)
    {
        TimeSpan today = scheduler.TodayActive;
        TimeSpan idle = scheduler.LastIdle;

        // 首拍尚未发生时 LastActive 仍是默认值 false，直接展示会被误读成“挂机”，
        // 因此这里显式区分“还没采样过”与“确实在挂机”。
        bool started = scheduler.Sequence > 0;
        bool active = started && scheduler.LastActive;

        var m = new CardModel
        {
            Title = "ScreenSpy（M1 实时数据）",
            TotalTime = Format(today),
            LimitText = $"{Format(today)} / {Format(DemoLimit)}",
            LimitRatio = Math.Min(1.0, today.TotalSeconds / DemoLimit.TotalSeconds),
            CurrentApp = !started ? "启动中" : active ? "活跃中" : $"挂机 {idle.TotalSeconds:F0}s",
        };

        double idleRatio = Math.Min(1.0, idle.TotalSeconds / Math.Max(1.0, scheduler.IdleThreshold.TotalSeconds));
        string state = !started ? "启动中" : active ? "活跃" : "挂机";

        m.TopApps.Add(($"状态：{state}", Format(today), Math.Min(1.0, m.LimitRatio)));
        // 空闲用“秒”显示（H:mm 在这个量级上永远是 0:00，看不出在动）。
        m.TopApps.Add(("当前空闲", $"{idle.TotalSeconds:F0}s", idleRatio));
        m.TopApps.Add(("空闲阈值", Format(scheduler.IdleThreshold), 0.30));
        m.TopApps.Add(("心跳", $"{scheduler.Heartbeat.TotalMilliseconds:F0}ms", 0.15));
        m.TopApps.Add(("刷新", $"t={tick}", 0.10));
        return m;
    }

    private static string Format(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}";
}
