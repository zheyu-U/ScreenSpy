using System;

namespace ScreenSpy.Collector;

/// <summary>
/// 启动续算时灌入内存榜单的一条“基线”（开发文档 §5.6 验收：重启后今天的时长仍在）。
///
/// 与“本次运行观测到的时长”分别存放（<see cref="AppUsageTracker"/> 内部是两个字段），
/// 这样两件事都成立：
///   * 卡片/榜单显示的是**今天一整天**（含重启前）；
///   * 时间守恒不变量（已归因 + 已过滤 + 未归因 == 调度器今日活跃）仍然只针对**本次运行**，
///     不会被历史数据污染。
/// </summary>
internal readonly struct AppUsageSeed
{
    public AppUsageSeed(string mergeKey, string displayName, long milliseconds)
    {
        MergeKey = mergeKey ?? string.Empty;
        DisplayName = displayName ?? string.Empty;
        Milliseconds = milliseconds;
    }

    /// <summary>合并键（进程名小写），与运行期观测使用同一套键。</summary>
    public string MergeKey { get; }

    /// <summary>展示名。</summary>
    public string DisplayName { get; }

    /// <summary>基线时长（毫秒）。</summary>
    public long Milliseconds { get; }

    public override string ToString() => $"{DisplayName}({MergeKey}) {Milliseconds}ms";
}
