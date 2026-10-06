using System;
using System.Collections.Generic;
using ScreenSpy.Storage;

namespace ScreenSpy.Analytics;

/// <summary>
/// 把库里的行**算成**一张柱状图（M10）。**纯逻辑**：不碰 IO、不碰界面，因此口径可确定性推演。
///
/// ────────────────────────────────────────────────────────────────────────
/// 口径（与主界面 / 卡片 / 托盘同源，不另立一套）
/// ────────────────────────────────────────────────────────────────────────
///  * <b>总量</b> = 该日 <c>app_usage</c> 之和 + 该日 <c>daily_activity</c>（不计入使用时长 + 无前台/未知）。
///    锁屏按既有口径**完全不计入**，而它本来就不在库里（不写 daily_activity），所以这里天然不含 —— 不需要额外减法。
///  * <b>软件</b> = 该日该**归一键**的秒数；非软件那部分**不属于任何软件**，因此不进软件维度。
///  * <b>分类</b> = 该日该分类下各软件之和（分类由调用方以 <c>categoryOf</c> 给出；未分类返回空串）。
///
/// ────────────────────────────────────────────────────────────────────────
/// 两个刻意决定
/// ────────────────────────────────────────────────────────────────────────
///  1. **窗口固定右端是"今天"**：7 天 = 今天 + 前 6 天（含首尾）。左端随今天滚动，
///     因此不会出现"今天有数据但不在图里"这种只有跨天才暴露的偏差。
///  2. **有记录 / 无记录分开**：<see cref="ChartBar.HasData"/> 只看"那天在库里有没有行"。
///     那天程序没运行过 → 空槽；跑了一天但某软件没用过 → 一根 0 高柱 + 值为 0:00。
/// </summary>
internal static class ChartBuilder
{
    /// <summary>默认窗口：近 7 天（含今天）。</summary>
    public const int DefaultDays = 7;

    public static ChartModel Build(
        ChartScope scope,
        DateOnly today,
        int days,
        IReadOnlyList<AppUsageRow>? usageRows,
        IReadOnlyList<DayActivityRow>? activityRows,
        Func<string, string>? displayNameOf = null,
        Func<string, string>? categoryOf = null)
    {
        if (days <= 0) days = DefaultDays;

        DateOnly from = today.AddDays(-(days - 1));

        // 逐日聚合。用字典而不是数组：库里可能缺某些天（那天没运行），缺的就是"无记录"。
        var scopeSeconds = new Dictionary<DateOnly, long>();
        var recorded = new HashSet<DateOnly>();

        if (usageRows is not null)
        {
            foreach (AppUsageRow row in usageRows)
            {
                if (row.Day < from || row.Day > today) continue;   // 防御：范围外的行一律忽略（界面可能传了更宽的查询）

                recorded.Add(row.Day);
                if (!Matches(scope, row.AppName, categoryOf)) continue;

                long add = row.Seconds;
                if (add == 0) continue;
                scopeSeconds[row.Day] = scopeSeconds.TryGetValue(row.Day, out long cur) ? cur + add : add;
            }
        }

        if (activityRows is not null)
        {
            foreach (DayActivityRow row in activityRows)
            {
                if (row.Day < from || row.Day > today) continue;

                recorded.Add(row.Day);

                // 非软件活跃只属于"总量"这一个维度：它没有归到任何软件，因此不该进软件/分类维度。
                if (scope.Dimension != ChartDimension.Total) continue;

                long add = row.FilteredSeconds + row.UnattributedSeconds;
                if (add == 0) continue;
                scopeSeconds[row.Day] = scopeSeconds.TryGetValue(row.Day, out long cur) ? cur + add : add;
            }
        }

        var bars = new List<ChartBar>(days);
        for (int i = 0; i < days; i++)
        {
            DateOnly day = from.AddDays(i);
            scopeSeconds.TryGetValue(day, out long seconds);
            bars.Add(new ChartBar(day, ChartText.DayLabel(day), seconds, recorded.Contains(day)));
        }

        return new ChartModel(TitleOf(scope, days, displayNameOf), bars, today);
    }

    /// <summary>某一行软件是否落在当前 scope 内。</summary>
    private static bool Matches(ChartScope scope, string appKey, Func<string, string>? categoryOf)
    {
        switch (scope.Dimension)
        {
            case ChartDimension.Total:
                return true;

            case ChartDimension.App:
                return string.Equals(appKey, scope.Target, StringComparison.Ordinal);

            case ChartDimension.Category:
                if (categoryOf is null) return false;
                return string.Equals(categoryOf(appKey) ?? string.Empty, scope.Target, StringComparison.Ordinal);

            default:
                return false;
        }
    }

    private static string TitleOf(ChartScope scope, int days, Func<string, string>? displayNameOf)
    {
        switch (scope.Dimension)
        {
            case ChartDimension.App:
                string name = displayNameOf is not null ? displayNameOf(scope.Target) : scope.Target;
                if (string.IsNullOrEmpty(name)) name = scope.Target;
                return $"近 {days} 天 · {name}";

            case ChartDimension.Category:
                return $"近 {days} 天 · 分类「{scope.Target}」";

            default:
                return $"近 {days} 天 · 每天「真实活跃」总量";
        }
    }
}
