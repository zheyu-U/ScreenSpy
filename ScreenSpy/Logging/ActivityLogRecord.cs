using System;
using ScreenSpy.Collector;

namespace ScreenSpy.Logging;

/// <summary>
/// 一段状态**不计入**的原因。顺序即优先级：暂停 &gt; 时间空洞 &gt; 挂机。
/// （暂停是最强的解释：锁屏/睡眠期间谈“挂机”没有意义。）
/// </summary>
internal enum ActivityLogReason
{
    /// <summary>被计入（活跃）。</summary>
    Active = 0,

    /// <summary>挂机：空闲达到阈值。</summary>
    Idle = 1,

    /// <summary>暂停：锁屏 / 睡眠（M2）。</summary>
    Paused = 2,

    /// <summary>时间空洞：单拍间隔超过可信任上限（睡眠 / 进程长时间挂起，M2 第二道防线）。</summary>
    Gap = 3,
}

/// <summary>
/// 原始活动日志的一行（一段**连续同状态**的汇总）。
///
/// 语义要点（“仅变化时记录”的前提，务必理解）：
///  * 一行代表**一段状态**，不是一拍。状态由 <c>(是否计入, 原因, 分类, 合并键)</c> 界定；
///    因此同一软件连续使用 2 小时只会产生**一行**，<see cref="DurationMs"/> = 2 小时。
///  * <see cref="Title"/> 是状态下**首次**观测到的标题，<see cref="TitleLast"/> 是**最后一次**；
///    两者不同说明期间标题变过 —— 变更**次数**记在 <see cref="TitleChanges"/>。
///    （标题不参与状态界定，否则浏览器每翻一页就会切出一行。）
///  * <see cref="ProcessName"/> 是**未规范化**的原始进程名（如 <c>devenv</c>），
///    与 <see cref="DisplayName"/>（如 <c>Visual Studio</c>）刻意分开保存 ——
///    这正是本功能存在的意义：聚合榜单只留显示名，原始名只有这里才有。
/// </summary>
internal readonly struct ActivityLogRecord
{
    public ActivityLogRecord(DateTime startedLocal,
                             DateOnly day,
                             long durationMs,
                             long ticks,
                             long sequenceFrom,
                             long sequenceTo,
                             bool counted,
                             ActivityLogReason reason,
                             ForegroundAppKind kind,
                             string processName,
                             string className,
                             string title,
                             string titleLast,
                             int titleChanges,
                             string displayName,
                             string mergeKey,
                             int processId,
                             IntPtr hwnd,
                             string? diagnostic)
    {
        StartedLocal = startedLocal;
        Day = day;
        DurationMs = durationMs;
        Ticks = ticks;
        SequenceFrom = sequenceFrom;
        SequenceTo = sequenceTo;
        Counted = counted;
        Reason = reason;
        Kind = kind;
        ProcessName = processName ?? string.Empty;
        ClassName = className ?? string.Empty;
        Title = title ?? string.Empty;
        TitleLast = titleLast ?? string.Empty;
        TitleChanges = titleChanges;
        DisplayName = displayName ?? string.Empty;
        MergeKey = mergeKey ?? string.Empty;
        ProcessId = processId;
        Hwnd = hwnd;
        Diagnostic = diagnostic;
    }

    /// <summary>状态开始的本地时间（含时区偏移，如 <c>2026-10-05T12:00:00.000+08:00</c>）。</summary>
    public DateTime StartedLocal { get; }

    /// <summary>状态开始时的本地日期（= 分片文件名中的日期；跨天会被切开，故一行不跨天）。</summary>
    public DateOnly Day { get; }

    /// <summary>本状态累计时长（毫秒，= 各拍真实间隔之和）。</summary>
    public long DurationMs { get; }

    /// <summary>本状态包含的拍数。</summary>
    public long Ticks { get; }

    /// <summary>本状态首拍的心跳序号。</summary>
    public long SequenceFrom { get; }

    /// <summary>本状态末拍的心跳序号。</summary>
    public long SequenceTo { get; }

    /// <summary>本状态是否被计入活跃时长。</summary>
    public bool Counted { get; }

    /// <summary>计入/不计入的原因。</summary>
    public ActivityLogReason Reason { get; }

    /// <summary>前台分类（§5.4 的过滤规则结果）。</summary>
    public ForegroundAppKind Kind { get; }

    /// <summary>**原始**进程名（未规范化、不含 .exe，如 <c>devenv</c>）。</summary>
    public string ProcessName { get; }

    /// <summary>窗口类名（状态下首次观测，如 <c>CabinetWClass</c>）。</summary>
    public string ClassName { get; }

    /// <summary>窗口标题（状态下首次观测；关闭标题记录时为空串）。</summary>
    public string Title { get; }

    /// <summary>窗口标题（状态下最后一次观测）。</summary>
    public string TitleLast { get; }

    /// <summary>状态内标题变更次数（0 = 标题始终未变）。</summary>
    public int TitleChanges { get; }

    /// <summary>规范化显示名（如 <c>Visual Studio</c>）。</summary>
    public string DisplayName { get; }

    /// <summary>合并键（同名合并用，如 <c>devenv</c>；过滤类为 <c>#desktop</c> 等）。</summary>
    public string MergeKey { get; }

    /// <summary>进程 ID（状态下最后一次观测）。</summary>
    public int ProcessId { get; }

    /// <summary>窗口句柄（状态下最后一次观测；仅用于跨日志关联，不保留所有权）。</summary>
    public IntPtr Hwnd { get; }

    /// <summary>采样过程中遇到的问题（取不到进程名等），正常为 null。</summary>
    public string? Diagnostic { get; }

    public override string ToString()
        => $"{StartedLocal:HH:mm:ss} {Reason} {Kind} '{ProcessName}' {DurationMs}ms/{Ticks}拍";
}
