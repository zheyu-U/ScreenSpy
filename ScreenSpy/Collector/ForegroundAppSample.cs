using System;

namespace ScreenSpy.Collector;

/// <summary>
/// 一次前台窗口采样的分类（开发文档 §5.4 的“要过滤的窗口”清单）。
///
/// 刻意把“过滤”的几种原因分开，而不是笼统地丢弃：这样日志与自检能给出
/// “到底过滤掉了什么”的客观计数，日后若发现过滤过度/不足也有据可查。
/// </summary>
internal enum ForegroundAppKind
{
    /// <summary>此刻没有前台窗口（<c>GetForegroundWindow</c> 返回 NULL）。不计入任何软件。</summary>
    None = 0,

    /// <summary>普通应用 —— **唯一会被计入“按软件统计”的一类**。</summary>
    App = 1,

    /// <summary>本程序自身（ScreenSpy）。过滤，避免自己的卡片把自己算成使用中。</summary>
    Self = 2,

    /// <summary>桌面（<c>Progman</c> / <c>WorkerW</c> / <c>SHELLDLL_DefView</c> / <c>SysListView32</c>）。</summary>
    Desktop = 3,

    /// <summary>系统外壳（任务栏、开始菜单、搜索、输入法候选等）。</summary>
    Shell = 4,

    /// <summary>
    /// 锁屏 / 登录界面（<c>LogonUI</c> / <c>LockApp</c> / <c>Winlogon</c>）。
    /// **完全不计入**：不归属软件，也不算进“今日真实活跃”（口径，本次确认）。
    /// </summary>
    LockScreen = 5,

    /// <summary>
    /// UWP 宿主进程 <c>ApplicationFrameHost</c>。§5.5（M11）会把它修正成真实应用名；
    /// 在此之前**仍按应用计入**（归到“UWP 应用（待修正）”名下），
    /// 否则这段时间会被整段丢掉 —— 少计比暂不精确更糟。
    /// </summary>
    UwpHost = 6,

    /// <summary>取不到进程名（进程已退出 / 拒绝访问 / 系统进程）。不计入任何软件，但单独计数。</summary>
    Unknown = 7,
}

/// <summary>
/// 一次前台采样的结果（不可变）。
///
/// 这是**纯数据**：字段全部由 <see cref="ForegroundAppRules"/> 从原始句柄信息推导，
/// 不持有任何句柄（<see cref="Hwnd"/> 仅作日志用，不保留所有权）。
/// 设计为 <c>readonly struct</c>：每秒一次、长期运行，避免堆分配。
/// </summary>
internal readonly struct ForegroundAppSample
{
    public ForegroundAppSample(IntPtr hwnd,
                               int processId,
                               string processName,
                               string className,
                               string title,
                               ForegroundAppKind kind,
                               string displayName,
                               string mergeKey,
                               string rawKey,
                               string? diagnostic = null)
    {
        Hwnd = hwnd;
        ProcessId = processId;
        // 强制非 null：本类型是 struct，`default` 会给出 null 字符串；
        // 调用方（心跳线程）不应因一个坏样本而抛异常。
        ProcessName = processName ?? string.Empty;
        ClassName = className ?? string.Empty;
        Title = title ?? string.Empty;
        Kind = kind;
        DisplayName = displayName ?? ForegroundAppRules.DisplayUnknown;
        MergeKey = mergeKey ?? string.Empty;
        RawKey = rawKey ?? string.Empty;
        Diagnostic = diagnostic;
    }

    /// <summary>前台窗口句柄（仅用于日志/诊断，不拥有所有权）。</summary>
    public IntPtr Hwnd { get; }

    /// <summary>前台窗口所属进程 ID（0 表示未取到）。</summary>
    public int ProcessId { get; }

    /// <summary>进程名（**不含** <c>.exe</c>，如 <c>devenv</c>）；取不到时为空串（不可是 null）。</summary>
    public string ProcessName { get; }

    /// <summary>窗口类名（如 <c>Progman</c>、<c>CabinetWClass</c>）。</summary>
    public string ClassName { get; }

    /// <summary>窗口标题（仅诊断用；标题变化频繁，**不**作为合并键）。</summary>
    public string Title { get; }

    /// <summary>分类结果。</summary>
    public ForegroundAppKind Kind { get; }

    /// <summary>展示名（规范化后的“软件名”）。</summary>
    public string DisplayName { get; }

    /// <summary>
    /// 合并键（同名合并：按进程名，忽略大小写；§5.4 要求）。
    /// M9-1 起这里是**归一键**：若用户把某个进程名合并到了别的软件，它给出的是合并后的键。
    /// 原始进程名见 <see cref="RawKey"/>（原始活动日志仍记原始进程名，因此合并后依然可追溯）。
    /// </summary>
    public string MergeKey { get; }

    /// <summary>
    /// **原始**合并键（进程名小写，未经 M9-1 的身份层解析）。等于 <see cref="MergeKey"/> 表示"未合并"。
    /// 保留它是为了：① 界面能说明"这个软件由哪些进程名合并而来"；② 自检能断言解析确实发生了。
    /// </summary>
    public string RawKey { get; }

    /// <summary>采样过程中遇到的问题（正常为 null）。</summary>
    public string? Diagnostic { get; }

    /// <summary>
    /// 复制一份、只替换展示名（M9-1b）。
    ///
    /// 用途：用户改了软件名之后，要把**"当前软件"**（卡片上那一行）也立刻换成新名 ——
    /// 否则它会一直显示旧名、直到那个软件下一次成为前台（可能几分钟后），
    /// 表现为"改了名但没完全生效"（不报错）。
    /// 除展示名之外的一切（分类、键、pid、句柄）都原样保留：改名是**纯展示层**动作。
    /// </summary>
    public ForegroundAppSample WithDisplayName(string? displayName)
        => new(Hwnd, ProcessId, ProcessName, ClassName, Title, Kind,
               displayName ?? string.Empty, MergeKey, RawKey, Diagnostic);

    /// <summary>是否应计入“按软件统计”。</summary>
    public bool CountsAsApp => Kind is ForegroundAppKind.App or ForegroundAppKind.UwpHost;

    /// <summary>
    /// **不计入使用时长**（不归属到任何软件，但**仍占用活跃时长**）：桌面 / 外壳 / 自身。
    /// 注意 **不含锁屏** —— 锁屏按口径“完全不计入”，既不归属软件、也不算进“今日真实活跃”，
    /// 见 <see cref="IsLockScreen"/>。
    /// </summary>
    public bool IsFiltered => Kind is ForegroundAppKind.Self
                                   or ForegroundAppKind.Desktop
                                   or ForegroundAppKind.Shell;

    /// <summary>锁屏 / 登录界面：**完全不计入**（既不归属软件，也不算进“今日真实活跃”）。</summary>
    public bool IsLockScreen => Kind is ForegroundAppKind.LockScreen;

    public override string ToString() =>
        $"{ForegroundAppRules.Describe(Kind)} '{DisplayName}'" +
        (ProcessName.Length > 0 ? $" (pid={ProcessId}, proc={ProcessName}, class={ClassName})" : "") +
        (string.Equals(RawKey, MergeKey, StringComparison.Ordinal) ? "" : $" [merge {RawKey}→{MergeKey}]") +
        (Diagnostic is null ? "" : $" [{Diagnostic}]");
}
