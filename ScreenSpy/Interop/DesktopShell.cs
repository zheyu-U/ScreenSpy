using System;
using System.Text;

namespace ScreenSpy.Interop;

/// <summary>
/// 桌面（外壳）窗口的定位与拓扑导出。
///
/// 背景（Windows 11 26H2 / build 26300 实测）：
///   Progman(0x…)
///     ├ SHELLDLL_DefView（图标层，本身带 WS_EX_LAYERED）
///     │   └ SysListView32 "FolderView"（桌面图标）
///     └ WorkerW（壁纸层，全屏可见子窗口）
///   Progman 自身带 WS_EX_NOREDIRECTIONBITMAP，内容由 DirectComposition 呈现。
///
/// 这里**只做定位**，不做嵌入：路线 A（SetParent 到上述窗口）已实测证伪
/// ——第三方 SetParent 进去的子窗口在任何宿主下都不上屏。详见 docs/M0验证结论.md。
/// </summary>
internal static class DesktopShell
{
    private const string DefViewClass = "SHELLDLL_DefView";
    private const string ProgmanClass = "Progman";

    public static IntPtr FindProgman() => NativeMethods.FindWindow(ProgmanClass, null);

    /// <summary>
    /// 桌面图标宿主窗口 = 承载 <c>SHELLDLL_DefView</c> 的那个窗口。
    ///
    /// Windows 11 24H2+（含实测的 26H2）：SHELLDLL_DefView 直接挂在 Progman 之下；
    /// 更早版本则可能是某个顶层 WorkerW。
    /// 该窗口是“显示桌面”检测的 z 序参照物：按 Win+D 时，系统会把它抬到最前。
    /// </summary>
    public static IntPtr FindDesktopIconsHost()
    {
        IntPtr host = IntPtr.Zero;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (NativeMethods.FindWindowEx(hwnd, IntPtr.Zero, DefViewClass, null) != IntPtr.Zero)
            {
                host = hwnd;
                return false;   // 找到即停
            }
            return true;
        }, IntPtr.Zero);

        // 兜底：直接返回 Progman（26H2 上二者其实是同一个窗口）。
        return host != IntPtr.Zero ? host : FindProgman();
    }

    /// <summary>导出当前桌面窗口拓扑，便于排查（不改变任何状态）。</summary>
    public static string CaptureTopology()
    {
        var sb = new StringBuilder();
        IntPtr progman = FindProgman();
        sb.AppendLine($"Progman = 0x{progman.ToInt64():X}（类={NativeMethods.ClassNameOf(progman)}）");
        sb.AppendLine($"桌面图标宿主（含 SHELLDLL_DefView）= 0x{FindDesktopIconsHost().ToInt64():X}");
        sb.AppendLine();

        sb.AppendLine("— 顶层相关窗口（EnumWindows）—");
        int index = 0;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            string cls = NativeMethods.ClassNameOf(hwnd);
            IntPtr defView = NativeMethods.FindWindowEx(hwnd, IntPtr.Zero, DefViewClass, null);
            bool interesting = cls is "WorkerW" or "Progman" || defView != IntPtr.Zero;
            if (!interesting) return true;

            index++;
            NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect);
            sb.AppendLine(
                $"  [{index}] hwnd=0x{hwnd.ToInt64():X} 类={cls} 标题=\"{NativeMethods.TitleOf(hwnd)}\" " +
                $"rect=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom}) " +
                $"含SHELLDLL_DefView={(defView != IntPtr.Zero ? "是" : "否")} 可见={(NativeMethods.IsWindowVisible(hwnd) ? "是" : "否")}");
            return true;
        }, IntPtr.Zero);
        if (index == 0) sb.AppendLine("  （无）");
        sb.AppendLine();

        if (progman != IntPtr.Zero)
        {
            sb.AppendLine("— Progman 的子窗口（EnumChildWindows，含全部后代）—");
            int ci = 0;
            NativeMethods.EnumChildWindows(progman, (hwnd, _) =>
            {
                ci++;
                NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT r);
                sb.AppendLine(
                    $"  c{ci} hwnd=0x{hwnd.ToInt64():X} 类={NativeMethods.ClassNameOf(hwnd)} " +
                    $"rect=({r.Left},{r.Top},{r.Right},{r.Bottom}) 可见={(NativeMethods.IsWindowVisible(hwnd) ? "是" : "否")}");
                return true;
            }, IntPtr.Zero);
            if (ci == 0) sb.AppendLine("  （无）");
        }

        return sb.ToString();
    }
}
