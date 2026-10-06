using System;
using System.Collections.Generic;
using ScreenSpy.Collector;

namespace ScreenSpy.AppHost;

/// <summary>
/// 某一时刻的运行状态快照（供主界面显示，避免 UI 直接触碰各组件、也就不会读到撕裂的中间状态）。
///
/// 这是**只读的数据搬运对象**：由 <see cref="ProductRuntime.Snapshot"/> 逐项填入，
/// 界面只负责展示，不含任何逻辑。
/// </summary>
internal sealed class RuntimeStatus
{
    /// <summary>快照时刻（本地时间）。</summary>
    public DateTime SampledAt { get; set; }

    /// <summary>启动参数摘要（便于确认“数据到底写去哪了”）。</summary>
    public string OptionsDescription { get; set; } = string.Empty;

    // ---------------------------------------------------------------- M1 计时

    public bool Running { get; set; }
    public TimeSpan TodayActive { get; set; }

    /// <summary>
    /// **今天一整天**的“真实活跃”时长
    /// = <see cref="TodayActive"/>（本次运行的全部计入拍，含锁屏剔除）
    /// − <see cref="LockExcluded"/>（锁屏/登录界面：按口径**完全不计入**）
    /// + <see cref="Seeded"/>（库中今日基线，含软件 + 不计入使用时长 + 无前台/未知）。
    ///
    /// 这是**对外展示**口径（主界面大字号、托盘 tooltip、桌面卡片）—— 重启后不会掉回 0:00。
    /// 而 <see cref="TodayActive"/> 保留为“本次运行”口径：M3 的时间守恒等式只描述本次运行
    /// （基线不计入 <c>AttributedTotal</c>），两者混用会让守恒断言失效。
    ///
    /// 刻意做成**计算属性**而不是由组合根填写的字段：派生值一旦变成可写字段，
    /// 谁忘了填、或填的时机不对，界面就会安静地显示 0:00 —— 正是本项目最忌讳的“不报错的错”。
    /// 计算属性让它永远与两个来源一致（自检里只设 TodayActive/Seeded 也能得到正确结果）。
    /// </summary>
    public TimeSpan TodayTotal => TodayActive - LockExcluded + Seeded;

    public TimeSpan AccountedElapsed { get; set; }
    public TimeSpan LastIdle { get; set; }
    public long Sequence { get; set; }
    public long GapTicks { get; set; }
    public long TimerFirings { get; set; }
    public bool LastActive { get; set; }
    public bool IdleSourceAvailable { get; set; }
    public string? SchedulerError { get; set; }
    public TimeSpan IdleThreshold { get; set; }
    public TimeSpan Heartbeat { get; set; }

    // ---------------------------------------------------------------- M2 锁屏 / 睡眠

    /// <summary>会话监听是否已建立（失败时锁屏/睡眠可能被计入）。</summary>
    public bool SessionAttached { get; set; }
    public bool SessionHooked { get; set; }
    public string? SessionHookError { get; set; }
    public bool Locked { get; set; }
    public bool Suspended { get; set; }

    /// <summary>生效暂停（锁屏/睡眠 或 用户手动，任一成立）。</summary>
    public bool Paused { get; set; }

    /// <summary>用户手动暂停（托盘「暂停统计」）。与 <see cref="Paused"/> 分开，便于界面说明原因。</summary>
    public bool UserPaused { get; set; }

    public string SessionProbeText { get; set; } = "—";
    public string? SessionProbeError { get; set; }

    // ---------------------------------------------------------------- M3 归属

    public bool AppSourceAvailable { get; set; }
    public string? AppSourceError { get; set; }
    public string CurrentApp { get; set; } = "—";

    /// <summary>
    /// 是否已至少采样到一次前台软件。
    /// <c>false</c> 时 <see cref="CurrentApp"/> 只是占位文案（"(尚未采样)"），
    /// 界面应显示“启动中”而不是把它当成真实软件名（M6 卡片依赖此判断）。
    /// </summary>
    public bool HasCurrentApp { get; set; }

    public IReadOnlyList<AppUsageEntry> Top { get; set; } = Array.Empty<AppUsageEntry>();
    public TimeSpan Attributed { get; set; }

    /// <summary>**本次运行**的“不计入使用时长”（桌面 / 外壳 / 自身）。</summary>
    public TimeSpan Filtered { get; set; }

    /// <summary>**本次运行**的“无前台 / 未知”。</summary>
    public TimeSpan Unattributed { get; set; }

    /// <summary>**本次运行**被剔除的锁屏 / 登录界面时长（按口径完全不计入）。</summary>
    public TimeSpan LockExcluded { get; set; }

    /// <summary>库中今日基线（软件 + 不计入使用时长 + 无前台 / 未知）。</summary>
    public TimeSpan Seeded { get; set; }

    /// <summary>库中基线的“不计入使用时长”分量。</summary>
    public TimeSpan SeededFiltered { get; set; }

    /// <summary>库中基线的“无前台 / 未知”分量。</summary>
    public TimeSpan SeededUnattributed { get; set; }

    /// <summary>对外展示：**今天一整天**的“不计入使用时长”（本次运行 + 库中基线）。</summary>
    public TimeSpan DayFiltered => Filtered + SeededFiltered;

    /// <summary>对外展示：**今天一整天**的“无前台 / 未知”（本次运行 + 库中基线）。</summary>
    public TimeSpan DayUnattributed => Unattributed + SeededUnattributed;

    public int SeededApps { get; set; }
    public int AppCount { get; set; }
    public long SamplingFailures { get; set; }

    // ---------------------------------------------------------------- M4 落库

    public bool StoreActive { get; set; }
    public string DatabasePath { get; set; } = string.Empty;
    public TimeSpan FlushInterval { get; set; }
    public long FlushedSeconds { get; set; }
    public long PendingMilliseconds { get; set; }
    public long Flushes { get; set; }
    public long ForcedFlushes { get; set; }
    public long StoreDropped { get; set; }
    public long StoreFailures { get; set; }
    public string? StoreError { get; set; }

    // ---------------------------------------------------------------- 原始日志

    public bool LogActive { get; set; }
    public string LogDirectory { get; set; } = string.Empty;
    public string? LogCurrentPath { get; set; }
    public long LogRecords { get; set; }
    public long LogWritten { get; set; }
    public long LogDropped { get; set; }
    public string? LogError { get; set; }

    // ---------------------------------------------------------------- 警告

    /// <summary>启动期与运行期的降级说明（正常为空）。</summary>
    public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();
}
