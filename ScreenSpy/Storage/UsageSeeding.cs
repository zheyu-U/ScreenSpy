using System;
using System.Collections.Generic;
using ScreenSpy.Collector;

namespace ScreenSpy.Storage;

/// <summary>
/// 启动续算的接线：把数据库里“今日 × 每软件”的秒数转成内存榜单的基线（开发文档 §5.6 验收）。
///
/// 一个刻意的取舍：数据库只存**合并键**（进程名小写），不存展示名 ——
/// 展示名会随版本升级而变（例如以后把 <c>devenv</c> 改成别的友好名），
/// 存进库里就会与代码里的映射表**分叉**。
/// 因此这里在读取时用 <see cref="ForegroundAppRules.NormalizeDisplayName"/> **现算**展示名：
/// 对已知软件能立刻恢复成“Visual Studio”这样的友好名，
/// 对未知软件则退回进程名本身（与运行期行为完全一致）。
/// </summary>
internal static class UsageSeeding
{
    /// <summary>
    /// 读出某天数据并灌入榜单；返回实际灌入的条目数。
    ///
    /// <paramref name="filteredSeconds"/> / <paramref name="unattributedSeconds"/> 是库中今日的
    /// “不计入使用时长 / 无前台·未知”秒数（来自 <c>daily_activity</c>）。
    /// **必须一起灌入**：否则重启后“今日真实活跃”会漏掉这两部分而变小 ——
    /// 这正是本次修复的口径缺陷。
    ///
    /// <paramref name="identity"/>（M9-1）决定展示名与归一键：
    ///  * 展示名优先取用户的设置（库里只存归一键与用户的覆盖名，不存系统友好名 —— 否则代码里的
    ///    映射表升级后会被库里的旧名字压住）；
    ///  * 归一键再解析一次是**幂等**的，且能兜住"先设了合并、而某天的行还是旧键"这类残留。
    /// </summary>
    public static int SeedTracker(AppUsageTracker tracker, IReadOnlyList<AppUsageRow> rows, DateOnly day,
                                  long filteredSeconds = 0, long unattributedSeconds = 0,
                                  AppIdentity? identity = null)
    {
        if (tracker is null) throw new ArgumentNullException(nameof(tracker));

        var seeds = new List<AppUsageSeed>(rows?.Count ?? 0);

        if (rows is not null)
        {
            foreach (AppUsageRow row in rows)
            {
                if (row.Seconds <= 0 || string.IsNullOrEmpty(row.AppName)) continue;

                string key = identity?.Resolve(row.AppName) ?? row.AppName;
                string display = identity?.DisplayNameOverride(key)
                                 ?? ForegroundAppRules.NormalizeDisplayName(key);

                seeds.Add(new AppUsageSeed(
                    key,
                    display,
                    row.Seconds * 1000));
            }
        }

        tracker.Seed(day, seeds,
                     TimeSpan.FromSeconds(filteredSeconds),
                     TimeSpan.FromSeconds(unattributedSeconds));
        return seeds.Count;
    }
}
