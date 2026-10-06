using System;
using System.Collections.Generic;
using ScreenSpy.AppHost;
using ScreenSpy.Collector;
using ScreenSpy.Rendering;

namespace ScreenSpy.Widget;

/// <summary>
/// 卡片的**产品级取数**：把组合根的一份 <see cref="RuntimeStatus"/> 快照映射成
/// <see cref="CardModel"/>（纯函数，无状态、无副作用，因此可确定性自检）。
///
/// 为什么读“快照”而不是直接读调度器/榜单：
///  * <see cref="ProductRuntime.Snapshot"/> 已经把所有跨线程读取收敛成一次平面拷贝，
///    且**永不抛异常**——卡片线程每秒调一次，正好复用这条“UI 只消费快照”的既有约定；
///  * 于是卡片与主界面、托盘看到的是**同一份口径**，不会出现三处数字互相打架。
/// </summary>
internal static class CardData
{
    /// <summary>卡片上最多列几行软件（400×360 的排版实测可容纳 5 行 + 每行的小进度条）。</summary>
    public const int MaxRows = 5;

    /// <summary>
    /// 快照 → 卡片模型。
    ///
    /// 三处刻意的口径选择：
    ///  1. **“今日”用 <see cref="RuntimeStatus.TodayTotal"/>**（今天一整天 = 本次运行 + 库中基线），
    ///     而不是 <c>TodayActive</c>（本次运行）—— 否则重启后卡片会掉回 0:00 而榜单单仍有数据。
    ///  2. <see cref="CardModel.LimitText"/> 直接透传快照（M9-2 起由限额引擎填）：
    ///     为空 → 渲染器整块不画限额。**没有限额时不显示一个并不存在的约束**这一点仍然成立，
    ///     只是“有没有限额”现在由真实规则决定。
    ///  3. 尚未采样到前台软件时显示“启动中”，而不是把占位文案“(尚未采样)”当成软件名。
    ///  4. **“不计入使用时长 / 无前台·未知”也显示在卡片上**（用 <c>DayFiltered/DayUnattributed</c>，
    ///     即今天一整天口径）—— 它们同样计入“今日”，标出来才不会被误以为漏算；为 0 时隐藏。
    /// </summary>
    public static CardModel FromStatus(RuntimeStatus status)
    {
        if (status is null) throw new ArgumentNullException(nameof(status));

        var model = new CardModel
        {
            Title = "ScreenSpy",
            TotalTime = Format(status.TodayTotal),
            LimitText = status.LimitText ?? string.Empty,
            LimitRatio = Math.Clamp(status.LimitRatio, 0.0, 1.0),
            CurrentApp = status.HasCurrentApp ? status.CurrentApp : "启动中",
            // 非软件活跃两行：都计入“今日真实活跃”，标出来免得被误以为漏掉。
            // 为 0 时给空串 → 渲染器整行不画。
            NonAppTime = FormatNonZero(status.DayFiltered),
            NoWindowTime = FormatNonZero(status.DayUnattributed),
        };

        IReadOnlyList<AppUsageEntry> top = status.Top;
        if (top is not null)
        {
            int rows = top.Count < MaxRows ? top.Count : MaxRows;
            for (int i = 0; i < rows; i++)
            {
                AppUsageEntry entry = top[i];
                model.TopApps.Add((entry.DisplayName, Format(entry.Time), Math.Clamp(entry.Share, 0.0, 1.0)));
            }
        }

        return model;
    }

    /// <summary>
    /// 卡片上的时长文案（§5.9 的 “今日 3:42” 风格）：
    /// 满 1 小时用 <c>h:mm</c>，不足 1 小时用 <c>m:ss</c>（否则一小时内会一直是 “0:0x”，看不出在动）。
    ///
    /// 刻意 <c>internal</c>：自检要断言的正是**这个函数**，而不是在测试里重写一遍格式化规则。
    /// </summary>
    internal static string Format(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}"
            : $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    }

    /// <summary>为 0（或负）时返回空串 —— 渲染器据此隐藏整行；否则用 <see cref="Format"/>。</summary>
    private static string FormatNonZero(TimeSpan value)
        => value > TimeSpan.Zero ? Format(value) : string.Empty;
}
