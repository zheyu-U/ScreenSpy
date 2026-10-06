using System;
using System.Collections.Generic;

namespace ScreenSpy.Collector;

/// <summary>
/// 一个候选窗口（§5.5 / M11）：既可以是 UWP 宿主的**子窗口**，也可以是**顶层窗口**。
///
/// 由 <c>UwpWindowResolver</c> 从真实的 <c>EnumChildWindows</c> / <c>EnumWindows</c> 结果构造，
/// 也可以由自检直接构造 —— 因此解析规则可以**确定性推演**，不依赖"此刻恰好有个 UWP 应用开着"。
/// </summary>
internal readonly struct UwpChildWindow
{
    // 刻意用显式字段 + 防御式取值：本类型是 struct，`default(UwpChildWindow)` 会绕过构造函数、
    // 让自动属性变成 null。调用方（心跳线程）绝不该因为一个坏样本而抛异常。
    private readonly string? _className;
    private readonly string? _processName;
    private readonly string? _title;

    public UwpChildWindow(string? className, int processId, string? processName,
                          string? title = null, bool visible = true)
    {
        _className = className ?? string.Empty;
        _processName = processName ?? string.Empty;
        _title = title ?? string.Empty;
        ProcessId = processId;
        Visible = visible;
    }

    /// <summary>窗口类名（UWP 应用本体通常是 <c>Windows.UI.Core.CoreWindow</c>）。</summary>
    public string ClassName => _className ?? string.Empty;

    /// <summary>窗口所属进程 ID（0 表示取不到）。</summary>
    public int ProcessId { get; }

    /// <summary>窗口所属进程名（取不到时为空串）。</summary>
    public string ProcessName => _processName ?? string.Empty;

    /// <summary>窗口标题（**只用于"配对"**，绝不作为软件身份 —— 见 <see cref="UwpAppRules.ResolveByTitle"/>）。</summary>
    public string Title => _title ?? string.Empty;

    /// <summary>是否可见。</summary>
    public bool Visible { get; }

    public override string ToString()
        => $"{ClassName}(pid={ProcessId}, proc={ProcessName}, title=\"{Title}\", visible={(Visible ? "是" : "否")})";
}

/// <summary>
/// UWP 真实应用的解析结果。
///
/// <see cref="Resolved"/> 为 false 时表示**没能识别**：此时调用方必须保持 M11 之前的行为
/// （归到宿主 <c>applicationframehost</c> 这个 lump），而**不是**猜一个名字 ——
/// 本项目一贯的取向是"宁可少计/暂不精确，不可错计"。
/// </summary>
internal readonly struct UwpAppResolution
{
    /// <summary>
    /// 未识别。刻意就是 <c>default</c> —— 本类型用**显式字段 + 防御式取值**，
    /// 因此 <c>default</c> 也是安全的（自动属性在 default 实例里会是 null）。
    /// </summary>
    public static readonly UwpAppResolution None = default;

    private readonly string? _processName;
    private readonly string? _key;
    private readonly string? _displayName;

    public UwpAppResolution(bool resolved, string? processName, string? key, string? displayName)
    {
        Resolved = resolved;
        _processName = processName ?? string.Empty;
        _key = key ?? string.Empty;
        _displayName = displayName ?? string.Empty;
    }

    /// <summary>是否成功识别出真实应用。</summary>
    public bool Resolved { get; }

    /// <summary>真实应用的进程名（原始大小写，如 <c>Calculator</c>）。</summary>
    public string ProcessName => _processName ?? string.Empty;

    /// <summary>归一键（进程名小写，与桌面软件同一命名空间）。</summary>
    public string Key => _key ?? string.Empty;

    /// <summary>
    /// 展示名。**刻意由 <see cref="Key"/> 推出**（见 <see cref="UwpAppRules.IdentityOf"/>）：
    /// 显示名必须能只由键算出来，否则"采样时叫一个名字、重启续算后叫另一个名字" —— 那正是本项目
    /// 反复消灭的那类不一致（教训 16/20：展示口径与持久化口径必须同源）。
    /// </summary>
    public string DisplayName => _displayName ?? string.Empty;

    public override string ToString()
        => Resolved ? $"{ProcessName} → {Key} / {DisplayName}" : "(未识别)";
}

/// <summary>
/// UWP 真实应用名的**纯逻辑**（开发文档 §5.5 / M11）。
///
/// 无 IO、无状态、不碰 Win32 —— Win32 那部分（枚举窗口、查进程名）在 <c>UwpWindowResolver</c> 里，
/// 本类只做判断，因此规则可确定性推演（由 <c>--m11-selfcheck</c> 覆盖）。
///
/// ────────────────────────────────────────────────────────────────────────
/// 为什么有**两条**解析路径（这是实测逼出来的，不是设计洁癖）
/// ────────────────────────────────────────────────────────────────────────
/// 教科书写法是"枚举宿主的子窗口，里面有一个 <c>Windows.UI.Core.CoreWindow</c>"。
/// **实测 Windows 11 26H2：这句话不成立** —— <c>ApplicationFrameWindow</c> 的子窗口只有
/// <c>ApplicationFrameTitleBarWindow</c> ×2 与 <c>ApplicationFrameInputSinkWindow</c>，全是宿主自己的；
/// 应用本体是一个**独立的顶层窗口**（类名仍是 <c>Windows.UI.Core.CoreWindow</c>，属于真实应用进程），
/// 而且它与宿主之间**没有父子 / owner 关系**（parent 与 root owner 都不是宿主，已用 --diag-uwp 取证）。
///
/// 于是：
///  * <b>路径 1（子窗口）</b>：<see cref="ResolveInChildren"/> —— 覆盖 Windows 10 与部分构建；
///  * <b>路径 2（顶层 + 标题配对）</b>：<see cref="ResolveByTitle"/> —— 覆盖 26H2 实测形态。
///
/// ────────────────────────────────────────────────────────────────────────
/// 路径 2 为什么用"标题相等"配对，而不违"标题不参与身份"这条红线
/// ────────────────────────────────────────────────────────────────────────
/// 标题在这里**只用来在两个窗口之间配对**（哪个应用窗口属于这个宿主），
/// 真正的软件身份仍然是**进程名**（键 = 进程名小写，展示名 = 由键推出的名字）。
/// 也就是说：标题参与的是"找对窗口"，不是"叫什么名字" —— 因此重启后名字不会因为拿不到标题而跳变。
///
/// 配对还刻意加了三条保守约束（宁可认不出，也不认错）：
///  1. 宿主标题**为空则不做配对**（一堆窗口标题都是空的，配上必是错的）；
///  2. 候选必须**可见**、pid 与宿主不同、进程名非空；
///  3. 匹配上的候选必须**恰好一个** —— 0 个或 ≥2 个都判为"不识别"。
/// </summary>
internal static class UwpAppRules
{
    /// <summary>UWP 应用本体的窗口类名。</summary>
    public const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

    /// <summary>
    /// 明确"不是应用"的宿主进程：即使类名与标题对上了也不能算作被使用的应用。
    /// 这是**保险**（正常情况它们不会与宿主标题相同），用来挡住"输入体验 / 外壳 XAML 宿主"这类窗口。
    /// </summary>
    private static readonly HashSet<string> NonAppProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "TextInputHost", "ShellHost", "explorer", "dwm", "csrss", "ApplicationFrameHost",
    };

    /// <summary>
    /// 路径 1：从宿主窗口的**子窗口**里解析（Windows 10 及部分构建的形态）。
    ///
    /// 只认 CoreWindow 族（而不是"任取一个 pid 不同的子窗口"）：子窗口里可能嵌套输入法候选窗、
    /// 浮层等，若任取一个就可能把输入法当成"正在使用的应用" —— 那是**错计**，比不识别更糟。
    /// </summary>
    public static UwpAppResolution ResolveInChildren(int hostProcessId, IReadOnlyList<UwpChildWindow>? children)
    {
        if (children is null || children.Count == 0) return UwpAppResolution.None;

        // 首取：类名恰为 CoreWindow。
        for (int i = 0; i < children.Count; i++)
        {
            UwpChildWindow c = children[i];
            if (IsExactCoreWindow(c.ClassName) && IsUsable(c, hostProcessId)) return IdentityOf(c.ProcessName);
        }

        // 次取：类名含 CoreWindow（防将来带前后缀），仍限定在这个族里。
        for (int i = 0; i < children.Count; i++)
        {
            UwpChildWindow c = children[i];
            if (ContainsCoreWindow(c.ClassName) && IsUsable(c, hostProcessId)) return IdentityOf(c.ProcessName);
        }

        return UwpAppResolution.None;
    }

    /// <summary>
    /// 路径 2：从**顶层窗口**里解析 —— 应用本体是独立顶层窗口、其标题与宿主标题相同
    /// （Windows 11 26H2 实测形态，见类注释）。
    /// </summary>
    /// <param name="hostProcessId">宿主（<c>ApplicationFrameHost</c>）的 pid。</param>
    /// <param name="frameTitle">宿主窗口的标题。</param>
    /// <param name="candidates">顶层候选窗口（应已由 Win32 侧按类名粗筛过）。</param>
    public static UwpAppResolution ResolveByTitle(int hostProcessId, string? frameTitle,
                                                  IReadOnlyList<UwpChildWindow>? candidates)
    {
        string title = (frameTitle ?? string.Empty).Trim();

        // 约束 1：宿主标题为空 ⇒ 不配对（空标题的窗口太多，配上必是错的）。
        if (title.Length == 0) return UwpAppResolution.None;
        if (candidates is null || candidates.Count == 0) return UwpAppResolution.None;

        UwpAppResolution best = UwpAppResolution.None;
        int matches = 0;

        for (int i = 0; i < candidates.Count; i++)
        {
            UwpChildWindow c = candidates[i];

            if (!c.Visible) continue;                                     // 约束 2
            if (!ContainsCoreWindow(c.ClassName)) continue;
            if (!IsUsable(c, hostProcessId)) continue;                     // 约束 2
            if (NonAppProcesses.Contains(c.ProcessName)) continue;
            if (!string.Equals(c.Title.Trim(), title, StringComparison.OrdinalIgnoreCase)) continue;

            matches++;
            best = IdentityOf(c.ProcessName);
        }

        // 约束 3：必须恰好一个。0 个（认不出）或 ≥2 个（分不清）都不猜。
        return matches == 1 ? best : UwpAppResolution.None;
    }

    /// <summary>
    /// 由真实应用进程名推出（键、展示名）。
    ///
    /// 展示名**由键推出**（<c>NormalizeDisplayName(key)</c>，而不是 <c>NormalizeDisplayName(进程名)</c>）：
    /// 这样它与重启续算时走的 <c>AppNamingRules.EffectiveName(identity, key)</c> **逐字一致**。
    /// 若用进程名原始大小写（如未收录的 <c>FooBar</c>），采样时会显示 <c>FooBar</c>、
    /// 重启后会显示 <c>foobar</c> —— 名字会跳变。
    /// </summary>
    public static UwpAppResolution IdentityOf(string? appProcessName)
    {
        string proc = (appProcessName ?? string.Empty).Trim();
        if (proc.Length == 0) return UwpAppResolution.None;

        string key = ForegroundAppRules.MergeKeyOf(proc);
        if (key.Length == 0) return UwpAppResolution.None;

        return new UwpAppResolution(true, proc, key, ForegroundAppRules.NormalizeDisplayName(key));
    }

    private static bool IsExactCoreWindow(string? className)
        => string.Equals(className, CoreWindowClass, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsCoreWindow(string? className)
        => (className ?? string.Empty).IndexOf("CoreWindow", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsUsable(UwpChildWindow c, int hostProcessId)
        => c.ProcessId > 0 && c.ProcessId != hostProcessId && !string.IsNullOrWhiteSpace(c.ProcessName);
}
