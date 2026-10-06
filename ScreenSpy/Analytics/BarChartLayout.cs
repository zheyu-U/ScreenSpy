using System;

namespace ScreenSpy.Analytics;

/// <summary>
/// 柱状图的**几何**（M10）。纯函数，不碰任何控件 —— 这样"柱子高度是否成比例"可以被确定性断言，
/// 而不是"看起来差不多"。
///
/// 与绘制层（<c>Charts/BarChartView</c>）的分工：这里只算数（高度），那里只摆控件。
/// 用户已说明以后要重做 UI；届时只需换掉绘制层，本文件的规则与断言原样保留。
/// </summary>
internal static class BarChartLayout
{
    /// <summary>
    /// 最小可见高度：秒数 &gt; 0 但比例极小时，也至少画这么高。
    /// 否则"用了 30 秒"与"完全没用"在 150px 的图里肉眼无法区分。
    /// </summary>
    public const double MinVisibleHeight = 2.0;

    /// <summary>
    /// 柱高（像素）。
    ///
    /// 三种返回 0 的情况都是**刻意**的：
    ///  * <paramref name="plotHeight"/> ≤ 0（还没布局 / 窗口被压扁）；
    ///  * <paramref name="maxSeconds"/> ≤ 0（这段时间没有任何时长 —— 不除零）；
    ///  * <paramref name="seconds"/> ≤ 0（真的没用过）。
    /// </summary>
    public static double HeightOf(long seconds, long maxSeconds, double plotHeight)
    {
        if (plotHeight <= 0) return 0;
        if (maxSeconds <= 0 || seconds <= 0) return 0;

        double h = plotHeight * seconds / maxSeconds;

        if (h > plotHeight) h = plotHeight;                     // 防御：秒数超过 maxSeconds 不该溢出
        return h < MinVisibleHeight ? MinVisibleHeight : h;
    }
}
