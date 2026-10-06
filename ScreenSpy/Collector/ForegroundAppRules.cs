using System;
using System.Collections.Generic;

namespace ScreenSpy.Collector;

/// <summary>
/// 前台软件识别的**纯逻辑**（开发文档 §5.4）：分类、过滤、规范化、同名合并键。
///
/// 无 IO、无状态、不碰 Win32 —— 因此“桌面/任务栏/锁屏/自身被过滤”“进程名规范化”
/// 这类规则都能确定性推演（当前计划不含测试工程，由 <c>--m3-selfcheck</c> 覆盖）。
///
/// 分类策略：**优先按窗口类名**（稳定、由 shell 定义），
/// 辅以**进程名黑名单**（开始菜单 / 搜索 / 输入法等外壳宿主，它们没有固定的窗口类名）。
/// 之所以不按“进程名 == explorer 就过滤”，是因为 `explorer.exe` 也可能是
/// 前台的文件资源管理器窗口（类名 <c>CabinetWClass</c>）—— 那是**真实应用**，必须计入。
/// </summary>
internal static class ForegroundAppRules
{
    // ---------------------------------------------------------------- 常量

    /// <summary>UWP 宿主进程名（小写，不含 .exe）。真实名修正见 <see cref="UwpAppRules"/>（§5.5 / M11）。</summary>
    public const string UwpHostProcess = "applicationframehost";

    public const string DisplayDesktop = "桌面";
    public const string DisplayShell = "系统外壳";
    public const string DisplayLockScreen = "锁屏界面";
    public const string DisplaySelf = "(ScreenSpy 自身)";
    public const string DisplayNoWindow = "(无前台窗口)";
    public const string DisplayUnknown = "(未知应用)";
    /// <summary>
    /// UWP 应用**没能识别出真实名**时的展示名（§5.5 / M11）。
    ///
    /// ⚠️ 它同时是键 <c>applicationframehost</c> 的展示名 —— 也就是说，M11 之前累积下来的
    /// "旧合并数据"也显示成这个名字。**同一个键只能有一个名字**（教训 16/20：展示口径与持久化口径
    /// 必须同源），所以这里用了一个同时诚实的措辞：
    ///  * "未能识别" —— 当前这一刻的 UWP 应用没认出来（子窗口里没有 CoreWindow / 进程名取不到）；
    ///  * "旧合并" —— 历史上所有 UWP 应用曾被记在同一个键下的那段数据（无法再拆分）。
    /// </summary>
    public const string DisplayUwpUnresolved = "UWP 应用（未能识别/旧合并）";

    // ---------------------------------------------------------------- 规则表

    /// <summary>窗口类名 → 桌面。</summary>
    private static readonly HashSet<string> DesktopClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "SHELLDLL_DefView", "SysListView32", "FolderView",
    };

    /// <summary>窗口类名 → 系统外壳（任务栏等）。</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
        "TaskListThumbnailWnd", "Windows.UI.Core.CoreWindow",
    };

    /// <summary>窗口类名 → 锁屏 / 登录界面。</summary>
    private static readonly HashSet<string> LockScreenClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "LogonUI", "LockScreenFrameWindow", "Windows.UI.Input.InputSite.WindowClass",
    };

    /// <summary>进程名 → 系统外壳宿主（开始菜单 / 搜索 / 输入法等）。</summary>
    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "StartMenuExperienceHost", "SearchHost", "SearchApp", "ShellExperienceHost",
        "TextInputHost", "LockApp", "dwm", "csrss",
    };

    /// <summary>进程名 → 锁屏 / 登录界面。</summary>
    private static readonly HashSet<string> LockScreenProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "LogonUI", "Winlogon", "LockApp",
    };

    /// <summary>
    /// 进程名 → 更友好的“软件名”。**小规模、可增长**的表面映射：
    /// 未命中时退回进程名本身（去 <c>.exe</c>），因此不会漏统计。
    /// 真正的“从文件版本信息取 FileDescription”留待后续（那需要读取独立进程的模块路径，
    /// 在 1Hz 频率下不划算，且对权限受限进程会失败）。
    /// </summary>
    private static readonly Dictionary<string, string> FriendlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["devenv"] = "Visual Studio",
        ["chrome"] = "Chrome",
        ["msedge"] = "Edge",
        ["firefox"] = "Firefox",
        ["Code"] = "VS Code",
        ["WindowsTerminal"] = "Windows Terminal",
        ["powershell"] = "PowerShell",
        ["pwsh"] = "PowerShell",
        ["cmd"] = "命令提示符",
        ["notepad"] = "记事本",
        ["calc"] = "计算器",
        ["CalculatorApp"] = "计算器",
        ["mspaint"] = "画图",
        ["Taskmgr"] = "任务管理器",
        ["explorer"] = "文件资源管理器",

        // ---------------------------------------------------------------- UWP / 商店应用（M11 §5.5）
        // 这些是**进程名 → 友好名**，与桌面软件共用同一张表、同一套解析；
        // 也就是说 UWP 应用与桌面软件在统计里完全平权（键都是"进程名小写"）。
        // 未收录的 UWP 应用会显示进程名本身（例如 microsoft.windowsstore）——
        // 不会漏统计，且可用 M9-1b 的「重命名」改成你想要的名字。
        ["ApplicationFrameHost"] = DisplayUwpUnresolved,   // 未能识别时的宿主 lump（同时代表旧合并数据）
        ["Calculator"] = "计算器",
        ["SystemSettings"] = "设置",
        ["Photos"] = "照片",
        ["PhotosApp"] = "照片",
        ["WindowsCamera"] = "相机",
        ["Microsoft.WindowsStore"] = "Microsoft Store",
        ["WinStore.App"] = "Microsoft Store",
        ["Microsoft.WindowsAlarms"] = "闹钟和时钟",
        ["Microsoft.WindowsSoundRecorder"] = "录音机",
        ["Microsoft.WindowsMaps"] = "地图",
        ["Microsoft.BingWeather"] = "天气",
        ["Microsoft.MicrosoftStickyNotes"] = "便笺",
        ["Microsoft.ZuneMusic"] = "媒体播放器",
        ["Microsoft.ZuneVideo"] = "电影和电视",
        ["Microsoft.People"] = "联系人",
        ["Microsoft.MicrosoftSolitaireCollection"] = "纸牌",
        ["Microsoft.ScreenSketch"] = "截图工具",
        ["Microsoft.WindowsCalculator"] = "计算器",
        ["Microsoft.Windows.Photos"] = "照片",
        ["Microsoft.WindowsTerminal"] = "Windows Terminal",
        ["Microsoft.Paint"] = "画图",
        ["Microsoft.YourPhone"] = "手机连接",
        ["Microsoft.GamingApp"] = "Xbox",
        ["Microsoft.MicrosoftOfficeHub"] = "Office",
        ["Microsoft.WindowsFeedbackHub"] = "反馈中心",
        ["Microsoft.GetHelp"] = "获取帮助",
        ["Microsoft.WindowsNotepad"] = "记事本",
    };

    /// <summary>
    /// 友好名表里登记的进程名（原样大小写）。
    /// M9-1b 的「禁止重名」要遍历它：即使某个软件今天一次都没运行过，
    /// 它的**默认名**也占着一个位置 —— 否则用户可以把 chrome 改名叫 "Visual Studio"，
    /// 等 VS 某天运行时，界面上就出现两行一模一样的名字。
    /// </summary>
    public static IReadOnlyCollection<string> MappedProcessNames => FriendlyNames.Keys;

    // ---------------------------------------------------------------- 分类

    /// <summary>分类一次前台采样。</summary>
    /// <param name="hwnd">前台窗口句柄（<see cref="IntPtr.Zero"/> = 无前台窗口）。</param>
    /// <param name="processName">进程名（不含 .exe；取不到时传空串）。</param>
    /// <param name="className">窗口类名（取不到时传空串）。</param>
    /// <param name="isSelfProcess">该窗口是否属于本程序。</param>
    public static ForegroundAppKind Classify(IntPtr hwnd, string? processName, string? className, bool isSelfProcess)
    {
        if (hwnd == IntPtr.Zero) return ForegroundAppKind.None;

        className ??= string.Empty;
        processName ??= string.Empty;

        // 自身优先级最高：即便窗口类名恰好落在别的表里，也不该把自己算成外部软件。
        if (isSelfProcess) return ForegroundAppKind.Self;

        // 进程名都取不到 → 不猜测（宁可记为未知，也不按类名硬凑一个软件名）。
        if (processName.Length == 0) return ForegroundAppKind.Unknown;

        if (LockScreenProcesses.Contains(processName) || LockScreenClasses.Contains(className))
            return ForegroundAppKind.LockScreen;

        if (DesktopClasses.Contains(className))
            return ForegroundAppKind.Desktop;

        if (ShellProcesses.Contains(processName) || ShellClasses.Contains(className))
            return ForegroundAppKind.Shell;

        if (IsUwpHost(processName)) return ForegroundAppKind.UwpHost;

        return ForegroundAppKind.App;
    }

    public static bool IsUwpHost(string processName)
        => string.Equals(processName, UwpHostProcess, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 合并键（§5.4 的“同名合并”）：进程名小写。
    /// 刻意**不用**窗口标题 —— 标题变化频繁（浏览器每个页面都不同），会把一个软件拆成成百上千条。
    /// 也不带 pid —— Chrome 会开很多子进程，但前台通常只有一个，必须合并。
    /// </summary>
    public static string MergeKeyOf(string? processName)
        => (processName ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>规范化“软件名”：命中友好名则用之，否则退回进程名本身。</summary>
    public static string NormalizeDisplayName(string? processName)
    {
        string name = (processName ?? string.Empty).Trim();

        // 容错：万一调用方传了带扩展名的名字（如 "devenv.exe"）。
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        if (name.Length == 0) return DisplayUnknown;
        if (FriendlyNames.TryGetValue(name, out string? friendly)) return friendly;
        return name;
    }

    /// <summary>按分类给出展示名（用于卡片“当前软件”与日志）。</summary>
    public static string DisplayNameFor(ForegroundAppKind kind, string? processName) => kind switch
    {
        ForegroundAppKind.None => DisplayNoWindow,
        ForegroundAppKind.Self => DisplaySelf,
        ForegroundAppKind.Desktop => DisplayDesktop,
        ForegroundAppKind.Shell => DisplayShell,
        ForegroundAppKind.LockScreen => DisplayLockScreen,
        ForegroundAppKind.UwpHost => DisplayUwpUnresolved,
        ForegroundAppKind.Unknown => DisplayUnknown,
        _ => NormalizeDisplayName(processName),
    };

    /// <summary>按分类给出合并键（过滤类用分类名作键，便于计数与日志）。</summary>
    public static string MergeKeyFor(ForegroundAppKind kind, string? processName) => kind switch
    {
        ForegroundAppKind.App => MergeKeyOf(processName),
        ForegroundAppKind.UwpHost => MergeKeyOf(processName),
        _ => "#" + kind.ToString().ToLowerInvariant(),
    };

    public static string Describe(ForegroundAppKind kind) => kind switch
    {
        ForegroundAppKind.None => "无前台窗口",
        ForegroundAppKind.App => "应用",
        ForegroundAppKind.Self => "本程序（已过滤）",
        ForegroundAppKind.Desktop => "桌面（已过滤）",
        ForegroundAppKind.Shell => "系统外壳（已过滤）",
        ForegroundAppKind.LockScreen => "锁屏（已过滤）",
        ForegroundAppKind.UwpHost => "UWP 宿主（计入；M11 起按真实应用名归类，认不出才退回宿主）",
        _ => "未知（未计入）",
    };

    /// <summary>
    /// 从原始字段构造一个样本（分类 + 规范化 + **身份解析**一次完成，保证各处一致）。
    ///
    /// <paramref name="identity"/> 是 M9-1 的软件身份层：它把「原始进程名」解析成「软件（归一键）」，
    /// 并可能覆盖显示名。**这是整个项目里唯一的解析点** —— 多一处解析就多一处可能与别处分叉。
    /// 传 null（或 <see cref="AppIdentity.Empty"/>）时行为与 M9-1 之前完全一致。
    ///
    /// 身份层**只作用于会被归属到软件的两类**（<c>App</c> / <c>UwpHost</c>）：
    /// 桌面 / 外壳 / 锁屏用 <c>#desktop</c> 这类伪键，它们不是"软件"，不该被用户改名或分配分类。
    ///
    /// <paramref name="uwp"/> 是 M11（§5.5）的**真实应用解析结果**：宿主进程
    /// <c>ApplicationFrameHost</c> 下真正跑的是哪个 UWP 应用。给了（且 <c>Resolved</c>）就用它的键与名，
    /// 从而让"计算器"不再被记成 <c>applicationframehost</c>。**未识别时它必须是 None** ——
    /// 那样本函数行为与 M11 之前完全一致（退回宿主 lump），绝不猜测。
    /// </summary>
    public static ForegroundAppSample Create(IntPtr hwnd, int processId, string? processName, string? className,
                                            string? title, bool isSelfProcess, string? diagnostic = null,
                                            AppIdentity? identity = null, UwpAppResolution? uwp = null)
    {
        string proc = (processName ?? string.Empty).Trim();
        string cls = (className ?? string.Empty).Trim();

        ForegroundAppKind kind = Classify(hwnd, proc, cls, isSelfProcess);

        string rawKey = MergeKeyFor(kind, proc);
        string display = DisplayNameFor(kind, proc);

        // M11（§5.5）：UWP 宿主已解析出真实应用 → 用**真实应用的键与名**取代宿主 lump。
        // Kind 仍保持 UwpHost：它表示"这一拍来自 UWP 宿主"（诊断与 UWP 计数用），
        // 而不再表示"名字未知"。识别失败时 uwp 为 None，此处什么都不做，行为退回 M11 之前。
        if (kind == ForegroundAppKind.UwpHost && uwp is { Resolved: true } resolved)
        {
            rawKey = resolved.Key;
            display = resolved.DisplayName;
        }

        string key = rawKey;

        if (identity is not null && kind is ForegroundAppKind.App or ForegroundAppKind.UwpHost)
        {
            key = identity.Resolve(rawKey);

            string? over = identity.DisplayNameOverride(key);
            if (!string.IsNullOrEmpty(over))
            {
                display = over!;
            }
            else if (!string.Equals(key, rawKey, StringComparison.Ordinal))
            {
                // 合并后不再是原始进程名，友好名映射表按进程名查不到 —— 按**归一键**再查一次，
                // 否则界面上会把"VS Code"显示成一个生硬的 vscode。
                display = NormalizeDisplayName(key);
            }
        }

        return new ForegroundAppSample(
            hwnd, processId, proc, cls, title ?? string.Empty,
            kind,
            display,
            key,
            rawKey,
            diagnostic);
    }

    /// <summary>采样失败时的样本（进程已退出 / 拒绝访问）。归为“未知”，**不猜测**。</summary>
    public static ForegroundAppSample Failed(IntPtr hwnd, int processId, string? className, string diagnostic)
        => new(hwnd, processId, string.Empty, (className ?? string.Empty).Trim(), string.Empty,
               ForegroundAppKind.Unknown, DisplayUnknown, MergeKeyFor(ForegroundAppKind.Unknown, null),
               MergeKeyFor(ForegroundAppKind.Unknown, null), diagnostic);
}
