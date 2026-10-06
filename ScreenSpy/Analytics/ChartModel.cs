using System;
using System.Collections.Generic;

namespace ScreenSpy.Analytics;

/// <summary>
/// 「近 7 天柱状图」（M10）的统计维度。
///
/// 三种维度的**口径都由 <see cref="ChartBuilder"/> 一处定义**，界面只负责选维度、不参与算数 ——
/// 否则换个界面（用户已说明以后要重做 UI）就会冒出第二份口径。
/// </summary>
internal enum ChartDimension
{
    /// <summary>
    /// 每天一整天的「真实活跃」总量 = 各软件 + 不计入使用时长 + 无前台/未知（锁屏按口径完全不计入）。
    /// 与主界面大字号、卡片、托盘 tooltip 是同一个口径。
    /// </summary>
    Total = 0,

    /// <summary>某一个软件（**归一键**）的每日时长。</summary>
    App = 1,

    /// <summary>某一个分类下各软件之和（分类口径：一个软件只属于一个分类，故分类之和 == 各软件之和）。</summary>
    Category = 2,
}

/// <summary>图表要画"谁"（<see cref="ChartDimension"/> + 目标键）。</summary>
internal readonly struct ChartScope
{
    public ChartScope(ChartDimension dimension, string? target)
    {
        Dimension = dimension;
        Target = target ?? string.Empty;
    }

    public ChartDimension Dimension { get; }

    /// <summary>
    /// <see cref="ChartDimension.Total"/> 时为**空串**；
    /// <see cref="ChartDimension.App"/> 时为**归一键**；
    /// <see cref="ChartDimension.Category"/> 时为**分类名**。
    /// </summary>
    public string Target { get; }

    /// <summary>默认维度：总量（"今天在电脑前多久"）。</summary>
    public static ChartScope Total => new(ChartDimension.Total, string.Empty);

    /// <summary>
    /// 这个 scope 是否"什么都不画"。
    /// App / Category 维度若为空目标，就是**没选目标**，此时不该拿它去查库（否则会画出一堆 0）。
    /// </summary>
    public bool IsUsable => Dimension == ChartDimension.Total || Target.Length > 0;

    public override string ToString() => Dimension == ChartDimension.Total
        ? "total"
        : $"{Dimension.ToString().ToLowerInvariant()}:{Target}";
}

/// <summary>图表里的一根柱子（一天）。</summary>
internal readonly struct ChartBar
{
    public ChartBar(DateOnly day, string dayLabel, long seconds, bool hasData)
    {
        Day = day;
        DayLabel = dayLabel ?? string.Empty;
        Seconds = seconds;
        HasData = hasData;
    }

    public DateOnly Day { get; }

    /// <summary>柱下的日期标签（如 <c>10-06</c>）。</summary>
    public string DayLabel { get; }

    /// <summary>这一天的秒数（可能为 0 —— 那天有记录但这个软件没用过）。</summary>
    public long Seconds { get; }

    /// <summary>
    /// 这一天**在库里有没有记录**。
    ///
    /// 刻意与"秒数是否为 0"分开：<c>false</c> 表示"那天程序没运行过 / 没有任何记录"，
    /// 界面上画成**空槽**（并显示 <c>—</c>）而不是一根 0 高的柱子 ——
    /// "那天没开机"与"那天开了一天但从没用过这个软件"是两件事，不该长得一样。
    /// </summary>
    public bool HasData { get; }

    /// <summary>显示给用户的数值文本：无记录时是 <c>—</c>，否则是时长。</summary>
    public string ValueText => HasData ? ChartText.Duration(Seconds) : ChartText.NoDataText;

    public override string ToString() =>
        $"{DayLabel} {(HasData ? Seconds + "s" : "—")}";
}

/// <summary>一张已经算好的柱状图：界面只负责画，不再算数。</summary>
internal sealed class ChartModel
{
    public ChartModel(string title, IReadOnlyList<ChartBar>? bars, DateOnly? today)
    {
        Title = title ?? string.Empty;
        Bars = bars ?? Array.Empty<ChartBar>();
        Today = today;
    }

    /// <summary>图表标题（含维度与目标，界面直接显示）。</summary>
    public string Title { get; }

    /// <summary>按**日期升序**排列（左旧右新）。</summary>
    public IReadOnlyList<ChartBar> Bars { get; }

    /// <summary>今天（用于把最后一根柱高亮）。</summary>
    public DateOnly? Today { get; }

    /// <summary>空图（无数据 / 存储不可用时用；界面据此显示占位文案）。</summary>
    public static ChartModel Empty(string title) => new(title, Array.Empty<ChartBar>(), null);

    /// <summary>柱高比例的分母。全程 0 表示这段时间没有任何记录（不是"有记录但都是 0"）。</summary>
    public long MaxSeconds
    {
        get
        {
            long max = 0;
            for (int i = 0; i < Bars.Count; i++)
            {
                if (Bars[i].Seconds > max) max = Bars[i].Seconds;
            }
            return max;
        }
    }

    /// <summary>是否至少有一天有记录（否则整张图都是空槽，界面该说"没有历史数据"）。</summary>
    public bool HasAnyData
    {
        get
        {
            for (int i = 0; i < Bars.Count; i++)
            {
                if (Bars[i].HasData) return true;
            }
            return false;
        }
    }

    /// <summary>所有柱子秒数之和（界面显示"7 天合计"）。</summary>
    public long TotalSeconds
    {
        get
        {
            long sum = 0;
            for (int i = 0; i < Bars.Count; i++) sum += Bars[i].Seconds;
            return sum;
        }
    }
}

/// <summary>图表的文本契约（做成一处，界面与自检用的是**同一份**字面量）。</summary>
internal static class ChartText
{
    /// <summary>无记录那一天的数值占位。</summary>
    public const string NoDataText = "—";

    /// <summary>时长文本（<c>h:mm</c>）—— 柱顶标签空间有限，不显示秒。</summary>
    public static string Duration(long seconds)
    {
        if (seconds < 0) seconds = 0;
        long totalMinutes = seconds / 60;
        return $"{totalMinutes / 60}:{totalMinutes % 60:D2}";
    }

    /// <summary>柱下的日期标签（<c>MM-dd</c>，固定不变文化，避免跟随系统区域变化）。</summary>
    public static string DayLabel(DateOnly day)
        => day.ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
