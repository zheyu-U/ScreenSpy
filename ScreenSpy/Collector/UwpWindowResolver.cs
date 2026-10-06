using System;
using System.Collections.Generic;
using System.Diagnostics;
using ScreenSpy.Interop;

namespace ScreenSpy.Collector;

/// <summary>
/// UWP 宿主窗口 → 真实应用的 **Win32 侧**（开发文档 §5.5 / M11）。
///
/// 职责只有三件（判断规则在纯逻辑的 <see cref="UwpAppRules"/> 里）：
///  1. <c>EnumChildWindows</c> 枚举宿主的后代（路径 1 用）；
///  2. <c>EnumWindows</c> 收集顶层候选窗口（路径 2 用 —— 26H2 上应用本体是独立顶层窗口）；
///  3. 对**可能是应用本体**的那几个查进程名，再交给 <see cref="UwpAppRules"/> 判定。
///
/// 若干刻意的取舍：
///  * **只对候选查进程名**：绝大多数窗口与"是哪个应用"无关，逐个开进程句柄既没必要、
///    也可能触发权限失败。因此先按类名 / pid / 可见性过滤，再查名。
///  * **任何失败都不抛**：这是心跳线程上的 1Hz 调用，失败只意味着"这次没识别出真实应用"，
///    退回宿主 lump 即可（与 <c>Win32ForegroundAppSource</c> 的"取不到就不猜测"一致）。
///  * **放在 <c>Collector</c> 而不是 <c>Interop</c>**：它产出的是领域类型
///    （<see cref="UwpAppResolution"/>），而 <c>Collector → Interop</c> 是既有的依赖方向；
///    反过来放会让 <c>Interop</c> 依赖 <c>Collector</c>，形成分层倒挂。
/// </summary>
internal static class UwpWindowResolver
{
    /// <summary>
    /// 尝试从宿主窗口解析出真实 UWP 应用。**永不抛异常**；失败返回 <see cref="UwpAppResolution.None"/>。
    ///
    /// 先试路径 1（子窗口），再试路径 2（顶层 + 标题配对）；两条都不成立才算"未识别"。
    /// </summary>
    /// <param name="frameHwnd">宿主窗口（类名 <c>ApplicationFrameWindow</c>）的句柄。</param>
    /// <param name="hostProcessId">宿主进程（<c>ApplicationFrameHost</c>）的 pid。</param>
    public static UwpAppResolution Resolve(IntPtr frameHwnd, int hostProcessId)
    {
        if (frameHwnd == IntPtr.Zero) return UwpAppResolution.None;

        // 路径 1：应用本体作为宿主的子窗口（Windows 10 / 部分构建）。
        List<UwpChildWindow> children = CollectChildren(frameHwnd, hostProcessId);
        UwpAppResolution byChild = UwpAppRules.ResolveInChildren(hostProcessId, children);
        if (byChild.Resolved) return byChild;

        // 路径 2：应用本体是独立顶层窗口，标题与宿主相同（Windows 11 26H2 实测形态）。
        string frameTitle = SafeTitle(frameHwnd);
        List<UwpChildWindow> tops = CollectTopLevelCoreWindows(hostProcessId);
        return UwpAppRules.ResolveByTitle(hostProcessId, frameTitle, tops);
    }

    /// <summary>
    /// 枚举宿主窗口的后代，构造候选列表（对外公开是为了让自检/诊断能拿到"真实枚举"的原始结果）。
    /// </summary>
    internal static List<UwpChildWindow> CollectChildren(IntPtr frameHwnd, int hostProcessId)
    {
        var raw = new List<(string Class, int Pid)>();
        if (frameHwnd == IntPtr.Zero) return new List<UwpChildWindow>();

        try
        {
            NativeMethods.EnumChildWindows(frameHwnd, (child, _) =>
            {
                string cls = NativeMethods.ClassNameOf(child);
                NativeMethods.GetWindowThreadProcessId(child, out int pid);
                raw.Add((cls, pid));
                return true;   // 继续枚举（要看完，才知道有没有 CoreWindow）
            }, IntPtr.Zero);
        }
        catch
        {
            // 枚举失败（窗口在枚举途中销毁等）：当作"没找到候选"，不抛给心跳线程。
            return new List<UwpChildWindow>();
        }

        var result = new List<UwpChildWindow>(raw.Count);
        foreach ((string cls, int pid) in raw)
        {
            // 只给"可能是应用窗口"的候选查进程名 —— 见类注释里的第一条取舍。
            bool candidate = pid > 0
                             && pid != hostProcessId
                             && cls.IndexOf("CoreWindow", StringComparison.OrdinalIgnoreCase) >= 0;

            result.Add(new UwpChildWindow(cls, pid, candidate ? ProcessNameOf(pid) : string.Empty));
        }

        return result;
    }

    /// <summary>
    /// 收集**顶层**窗口里类名含 CoreWindow 的候选（路径 2）。
    /// 只对"可见 + pid 与宿主不同"的候选查进程名。
    /// </summary>
    internal static List<UwpChildWindow> CollectTopLevelCoreWindows(int hostProcessId)
    {
        var raw = new List<(string Class, int Pid, string Title, bool Visible)>();

        try
        {
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                string cls = NativeMethods.ClassNameOf(hwnd);
                if (cls.IndexOf("CoreWindow", StringComparison.OrdinalIgnoreCase) < 0) return true;

                NativeMethods.GetWindowThreadProcessId(hwnd, out int pid);
                bool visible = NativeMethods.IsWindowVisible(hwnd);
                raw.Add((cls, pid, visible ? SafeTitle(hwnd) : string.Empty, visible));
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            return new List<UwpChildWindow>();
        }

        var result = new List<UwpChildWindow>(raw.Count);
        foreach ((string cls, int pid, string title, bool visible) in raw)
        {
            bool candidate = visible && pid > 0 && pid != hostProcessId;
            result.Add(new UwpChildWindow(cls, pid, candidate ? ProcessNameOf(pid) : string.Empty, title, visible));
        }

        return result;
    }

    /// <summary>查进程名；失败返回空串（不抛、不缓存 —— 与 <c>Win32ForegroundAppSource</c> 的理由相同）。</summary>
    private static string ProcessNameOf(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeTitle(IntPtr hwnd)
    {
        try { return NativeMethods.TitleOf(hwnd); } catch { return string.Empty; }
    }
}
