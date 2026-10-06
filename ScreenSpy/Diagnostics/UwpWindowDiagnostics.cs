using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using ScreenSpy.Interop;

namespace ScreenSpy.Diagnostics;

/// <summary>
/// UWP 宿主窗口的**实地勘察**工具（M11 §5.5 的取证手段）：
///   ScreenSpy.exe --diag-uwp
///
/// 为什么需要它：M11 的第一版按"教科书写法"假设 <c>ApplicationFrameWindow</c> 的子窗口里
/// 会有一个 <c>Windows.UI.Core.CoreWindow</c>（应用本体）。**实测（Windows 11 26H2）发现根本没有** ——
/// 三个子窗口全是宿主自己的（标题栏 ×2 + 输入汇）。若只按假设写代码，就会"编译通过、自检通过、
/// 实际永远识别不出任何 UWP 应用"，且不报错。
///
/// 本工具把真实的窗口关系（类名 / pid / 进程名 / 父子与 owner 关系 / 样式）打印出来，
/// 让"应用本体到底在哪"这个问题有**客观依据**，而不是靠记忆或猜测。
/// </summary>
internal static class UwpWindowDiagnostics
{
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_CHILD = 0x40000000L;
    private const uint GA_PARENT = 1;
    private const uint GA_ROOT = 2;
    private const uint GA_ROOTOWNER = 3;

    public static bool IsRequested(string[]? args)
    {
        if (args is null) return false;
        foreach (string a in args)
            if (a.Equals("--diag-uwp", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static int Run()
    {
        var sb = new StringBuilder();

        sb.AppendLine("UWP 窗口实地勘察（--diag-uwp）");
        sb.AppendLine("================================================================");

        // ---- 1. 顶层窗口里所有"像 UWP 应用本体"的窗口 ----
        sb.AppendLine();
        sb.AppendLine("【1】顶层窗口中类名含 CoreWindow 的（应用本体通常长这样）");
        int coreCount = 0;
        var frames = new List<IntPtr>();
        var all = new List<(IntPtr Hwnd, string Class, int Pid, string Title)>();

        try
        {
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                string cls = NativeMethods.ClassNameOf(hwnd);
                NativeMethods.GetWindowThreadProcessId(hwnd, out int pid);
                string title = NativeMethods.TitleOf(hwnd);
                all.Add((hwnd, cls, pid, title));

                if (string.Equals(cls, "ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase))
                    frames.Add(hwnd);

                if (cls.IndexOf("CoreWindow", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    coreCount++;
                    sb.AppendLine("  · " + Describe(hwnd, cls, pid, title));
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            sb.AppendLine("  枚举失败：" + ex.Message);
        }
        sb.AppendLine($"  共 {coreCount} 个。");

        // ---- 2. 每个 ApplicationFrameWindow 的完整后代 ----
        sb.AppendLine();
        sb.AppendLine($"【2】ApplicationFrameWindow（共 {frames.Count} 个）及其全部后代");
        foreach (IntPtr frame in frames)
        {
            NativeMethods.GetWindowThreadProcessId(frame, out int fpid);
            sb.AppendLine("  ● " + Describe(frame, NativeMethods.ClassNameOf(frame), fpid, NativeMethods.TitleOf(frame)));

            int n = 0;
            try
            {
                NativeMethods.EnumChildWindows(frame, (child, _) =>
                {
                    n++;
                    NativeMethods.GetWindowThreadProcessId(child, out int cpid);
                    sb.AppendLine("      └ " + Describe(child, NativeMethods.ClassNameOf(child), cpid, NativeMethods.TitleOf(child)));
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                sb.AppendLine("      枚举后代失败：" + ex.Message);
            }
            sb.AppendLine($"      后代共 {n} 个；rootOwner={H(NativeMethods.GetAncestor(frame, GA_ROOTOWNER))}");
        }

        // ---- 3. 属于这些宿主的"非子窗口"（parent / root owner 指向宿主） ----
        sb.AppendLine();
        sb.AppendLine("【3】parent 或 root owner 指向 UWP 宿主窗口的顶层窗口（应用本体常以这种关系存在）");
        int owned = 0;
        if (frames.Count > 0)
        {
            var fset = new HashSet<IntPtr>(frames);
            foreach ((IntPtr hwnd, string cls, int pid, string title) in all)
            {
                if (fset.Contains(hwnd)) continue;

                IntPtr parent = NativeMethods.GetAncestor(hwnd, GA_PARENT);
                IntPtr rootOwner = NativeMethods.GetAncestor(hwnd, GA_ROOTOWNER);
                if (!fset.Contains(parent) && !fset.Contains(rootOwner)) continue;

                owned++;
                sb.AppendLine("  · " + Describe(hwnd, cls, pid, title) +
                              $" parent={H(parent)} rootOwner={H(rootOwner)}");
            }
        }
        sb.AppendLine($"  共 {owned} 个。");

        // ---- 4. 常见 UWP 应用的进程与它们的（主）窗口 ----
        sb.AppendLine();
        sb.AppendLine("【4】进程视角：这些进程名各自持有哪些顶层窗口");
        string[] interesting = { "SystemSettings", "Calculator", "ApplicationFrameHost" };
        foreach (string name in interesting)
        {
            int shown = 0;
            foreach (Process p in Process.GetProcessesByName(name))
            {
                int pid = p.Id;
                var wins = new List<string>();
                foreach ((IntPtr hwnd, string cls, int wpid, string title) in all)
                {
                    if (wpid != pid) continue;
                    wins.Add($"{cls}\"{title}\"");
                }
                sb.AppendLine($"  · {name} pid={pid} 顶层窗口 {wins.Count} 个：{string.Join(" | ", wins)}");
                shown++;
                try { p.Dispose(); } catch { }
            }
            if (shown == 0) sb.AppendLine($"  · {name}：没有正在运行的实例");
        }

        // ---- 5. 当前前台 ----
        sb.AppendLine();
        sb.AppendLine("【5】当前前台窗口");
        IntPtr fg = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(fg, out int fgpid);
        sb.AppendLine("  " + Describe(fg, NativeMethods.ClassNameOf(fg), fgpid, NativeMethods.TitleOf(fg)));

        sb.AppendLine();
        sb.AppendLine("================================================================");

        string text = sb.ToString();
        Console.WriteLine(text);

        try
        {
            string path = System.IO.Path.Combine("artifacts", "diag-uwp", "diag-uwp.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
            System.IO.File.WriteAllText(path, text, new UTF8Encoding(false));
            Console.WriteLine("[诊断] 已写入：" + System.IO.Path.GetFullPath(path));
        }
        catch { /* 忽略 */ }

        return 0;
    }

    private static string H(IntPtr p) => p == IntPtr.Zero ? "0" : $"0x{p.ToInt64():X}";

    private static string Describe(IntPtr hwnd, string cls, int pid, string title)
    {
        string proc = ProcessNameOf(pid);
        int style = SafeStyle(hwnd, GWL_STYLE);
        int ex = SafeStyle(hwnd, GWL_EXSTYLE);
        bool isChild = ((long)(uint)style & WS_CHILD) != 0;

        return $"hwnd={H(hwnd)} pid={pid} proc={proc} class={cls} " +
               $"title=\"{title}\" {(isChild ? "CHILD" : "POPUP")} style=0x{(uint)style:X8} ex=0x{(uint)ex:X8} " +
               $"visible={(NativeMethods.IsWindowVisible(hwnd) ? "是" : "否")}";
    }

    private static int SafeStyle(IntPtr hwnd, int index)
    {
        try { return NativeMethods.GetWindowLong(hwnd, index); } catch { return 0; }
    }

    private static string ProcessNameOf(int pid)
    {
        if (pid <= 0) return "?";
        try
        {
            using Process p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch { return "?"; }
    }
}
