using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ScreenSpy.Collector;
using ScreenSpy.Desktop;
using ScreenSpy.Diagnostics;
using ScreenSpy.Interop;
using ScreenSpy.Rendering;
using ScreenSpy.Scheduling;
using ScreenSpy.Widget;

namespace ScreenSpy.Demo;

/// <summary>
/// 桌面卡片的演示入口（随 M0 结论一并落地，**长期保留**）。
///
/// 用途：把「B+ 桌面层确实能工作」这件事**随时可复现地**验证一遍（含截屏像素判定），
/// 也是 M7 双形态中“嵌入形态”唯一的独立观测手段 —— 产品形态由组合根与托盘驱动，
/// 不便反复重放 Win+D 这类场景。
///
/// 判定原则（沿用 M0 最重要的教训）：只认**截屏像素**，不认 API 返回值。
/// </summary>
internal static class DesktopCardDemo
{
    private const string SwitchName = "--demo-card";

    private const byte VK_LWIN = 0x5B;
    private const byte VK_D = 0x44;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public static bool IsRequested(string[] args)
    {
        foreach (string a in args)
            if (a.Equals(SwitchName, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static int Run(string[] args)
    {
        // WinExe 默认控制台编码可能不是 UTF-8，先设为 UTF-8 以免中文输出乱码。
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 输出被重定向时可能失败，忽略 */ }

        int seconds = GetInt(args, "--seconds=", 8);
        int x = GetInt(args, "--x=", 200);
        int y = GetInt(args, "--y=", 700);
        int settleMs = Math.Max(0, GetInt(args, "--settle-ms=", 1500));   // 等首帧/首拍稳定（供客观取证）
        bool marker = !HasFlag(args, "--no-marker");
        bool testWinD = HasFlag(args, "--win-d");
        bool useM1 = HasFlag(args, "--m1");
        bool useM3 = HasFlag(args, "--m3");   // M3：按软件统计（隐含启动 M1 调度器）
        string outDir = GetString(args, "--out=") ?? "artifacts";
        string name = GetString(args, "--name=") ?? "product-demo";

        Console.WriteLine("============ ScreenSpy 桌面卡片 · 产品工程演示（演示入口）============");
        Console.WriteLine($"卡片位置 ({x},{y})，尺寸 {CardWindow.DefaultWidth}×{CardWindow.DefaultHeight}（嵌入形态），运行 {seconds} 秒");
        Console.WriteLine($"标记方块={marker}，Win+D 检测={testWinD}，M1 实时数据={useM1}，M3 按软件统计={useM3}");
        Console.WriteLine();

        EnableDpiAwareness();
        EnvironmentInfo.Print();

        Console.WriteLine("---- 桌面窗口拓扑 ----");
        Console.Write(DesktopShell.CaptureTopology());
        Console.WriteLine("----------------------");
        Console.WriteLine();

        // --m1：把 M1 调度器采集到的**真实**活跃时长接到卡片上；
        // --m3：再加上 M3 的按软件统计（隐含启动 M1 调度器）；都不给则用演示假数据。
        ActivityScheduler? scheduler = null;
        AppUsageBridge? usageBridge = null;
        Win32ForegroundAppSource? appSource = null;

        if (useM1 || useM3)
        {
            int idleThresholdSec = GetInt(args, "--idle-threshold=", ActivityRules.DefaultIdleThresholdSeconds);
            scheduler = new ActivityScheduler(new Win32IdleClock(), TimeSpan.FromSeconds(idleThresholdSec));
            scheduler.Start();
            Console.WriteLine($"M1 调度器  ：已启动（空闲阈值 {idleThresholdSec}s，心跳 {scheduler.Heartbeat.TotalMilliseconds:F0}ms）");
        }

        Func<CardModel> modelFactory = () => DemoCardData.Build(0);

        if (useM3)
        {
            appSource = new Win32ForegroundAppSource();
            usageBridge = new AppUsageBridge(scheduler!, appSource);
            Console.WriteLine($"M3 前台源  ：{(appSource.IsAvailable ? "可用" : "不可用")}（即时采样：{appSource.Sample()}）");
            AppUsageBridge bridge = usageBridge;
            ActivityScheduler sched = scheduler!;
            modelFactory = () => AppLiveCardData.Build(0, bridge.Tracker, sched);
        }
        else if (useM1)
        {
            modelFactory = () => LiveCardData.Build(0, scheduler!);
        }

        // 卡片：**嵌入形态**（M7 的默认形态，与产品一致）。
        // 取数函数在构造时传入 —— 卡片每秒拉一次，正是本项目“绑定”的做法。
        using var card = new CardWindow(modelFactory, WidgetMode.Embedded, x, y,
                                        debugMarker: marker ? CardWindow.DefaultDebugMarker : null);

        // 原始活动日志（旁路，不改动归属逻辑）：默认开启、仅变化时记录；
        // --no-raw-log 关闭，--log-dir=<path> 改目录。开发期默认落在 artifacts 下。
        RawLogProbe? rawLog = usageBridge is null ? null : RawLogProbe.Create(usageBridge, args, "demo-rawlog");

        // CardWindow.Start() 自己拉起带消息循环的 STA 线程，并在首帧画完后才返回。
        if (!card.Start())
        {
            Console.WriteLine("!! 卡片启动失败：" + (card.LastError ?? "未知原因"));
            return 4;
        }

        // 兜底看门狗：无论中途哪一步卡住，最多 seconds + 15 秒就自行关闭。
        // （否则一个卡死的卡片会让演示进程留在后台，而看起来像“还在正常跑”。）
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(seconds + 15));
            try { card.RequestClose(); } catch { /* 忽略 */ }
        })
        { IsBackground = true, Name = "screenspy-demo-watchdog" };
        watchdog.Start();

        Console.WriteLine($"卡片窗口已创建：hwnd=0x{card.Handle.ToInt64():X}");
        Console.WriteLine($"扩展样式      ：{card.DescribeExStyle()}");
        Console.WriteLine($"显示桌面钩子  ：{(card.HookInstalled ? "已安装（SetWinEventHook）" : "未安装（仅定时器兜底）")}");
        Console.WriteLine();

        Thread.Sleep(settleMs);   // 让定时器、首帧与首拍稳定

        if (scheduler is not null)
        {
            Console.WriteLine($"M1 调度器快照：定时器回调 {scheduler.TimerFirings} 次，" +
                              $"成功心跳 {scheduler.Sequence} 拍，" +
                              $"最近空闲 {scheduler.LastIdle.TotalSeconds:F1}s，" +
                              $"判定 {(scheduler.LastActive ? "活跃" : "挂机")}，" +
                              $"今日 {scheduler.TodayActive.TotalSeconds:F1}s");
            if (scheduler.LastError is { } err)
                Console.WriteLine($"M1 调度器错误：{err}");

            if (scheduler.TimerFirings > scheduler.Sequence)
                Console.WriteLine($"!! 有 {scheduler.TimerFirings - scheduler.Sequence} 次心跳未生效（计时会偏低）。");

            if (scheduler.Sequence == 0)
            {
                Console.WriteLine("M1 诊断：心跳从未成功，尝试在主线程同步采样一次…");
                try
                {
                    Console.WriteLine($"  同步采样成功：{scheduler.Sample()}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  同步采样抛异常：{ex}");
                }
            }
            Console.WriteLine();
        }

        string dir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(dir);

        int rc = 0;
        rc |= Probe("S1 基线（正常桌面）", card, Path.Combine(dir, name + "-S1.png"), marker, decisive: false);

        if (testWinD)
        {
            Console.WriteLine("正在模拟 Win+D（显示桌面）…");
            SendShowDesktop();
            Thread.Sleep(1200);
            rc |= Probe("S2 显示桌面（Win+D）", card, Path.Combine(dir, name + "-S2.png"), marker, decisive: true);

            Console.WriteLine("正在再按一次 Win+D（还原）…");
            SendShowDesktop();
            Thread.Sleep(1200);
            rc |= Probe("S3 还原", card, Path.Combine(dir, name + "-S3.png"), marker, decisive: false);
        }

        Console.WriteLine();
        Console.WriteLine($"累计：采样 {card.Polls} 次，状态切换 {card.Transitions} 次，绘制 {card.Frames} 帧，Elevated={card.Elevated}");

        card.RequestClose();
        if (!card.WaitForExit(10000))
        {
            Console.WriteLine("!! 卡片线程未在 10 秒内退出。");
            rc |= 8;
        }

        if (usageBridge is not null)
        {
            AppUsageTracker tr = usageBridge.Tracker;
            Console.WriteLine($"M3 归属合计 ：{tr.Total.TotalSeconds:F1}s " +
                              $"(已归因 {tr.AttributedTotal.TotalSeconds:F1}s / 已过滤 {tr.FilteredTotal.TotalSeconds:F1}s / 未归因 {tr.UnattributedTotal.TotalSeconds:F1}s)");
            foreach (AppUsageEntry e in tr.Top(5))
                Console.WriteLine($"   {e.DisplayName,-24} {e.Time.TotalSeconds,8:F1}s");
            if (appSource is not null)
                Console.WriteLine($"M3 采样     ：{appSource.Samples} 次（进程名取样失败 {appSource.ProcessLookupFailures}，自身前台 {appSource.SelfSamples}）");
            if (scheduler is not null)
            {
                long diff = (long)Math.Round(tr.Total.TotalMilliseconds) - (long)Math.Round(scheduler.TodayActive.TotalMilliseconds);
                Console.WriteLine($"M3 守恒差   ：{diff} ms（应为 0）");
                if (Math.Abs(diff) > 1) rc |= 16;
            }
        }

        // 先关闭最后一段状态并等待落盘，再打印证据（否则尾段还在内存里）。
        rawLog?.Dispose();
        if (rawLog is not null) Console.Write(rawLog.Report());

        usageBridge?.Dispose();
        scheduler?.Dispose();

        Console.WriteLine();
        Console.WriteLine(rc == 0
            ? "演示判定：通过。"
            : $"演示判定：失败（位掩码 {rc}）。");
        return rc;
    }

    /// <summary>截屏 + 像素判定。返回非 0 表示失败。</summary>
    private static int Probe(string label, CardWindow card, string png, bool marker, bool decisive)
    {
        string path = ScreenCapture.Capture(png);

        Console.WriteLine($"[{label}]");
        Console.WriteLine($"   截图         ：{path}");
        Console.WriteLine($"   卡片状态     ：ShowDesktop={card.ShowDesktop} Elevated={card.Elevated} 帧={card.Frames}");

        if (!marker)
        {
            Console.WriteLine("   像素判定     ：未启用标记方块，跳过（只看截图）。");
            return 0;
        }

        var hit = ScreenCapture.Analyze(path, CardWindow.DebugMarkerColor);
        Console.WriteLine($"   标记像素     ：{hit.Count}  包围盒={(hit.Count > 0 ? hit.Bounds.ToString() : "(无)")}");

        if (hit.Visible)
        {
            Console.WriteLine("   判定         ：卡片确实上屏 ✅");
            return 0;
        }

        var center = new Point(card.X + 22, card.Y + 22);
        Console.WriteLine($"   该点最上层外部窗口：{ScreenCapture.TopWindowOver(center)}");

        if (decisive)
        {
            Console.WriteLine("   判定         ：卡片不可见 ❌（决定性检查失败）");
            return 1;
        }

        Console.WriteLine("   判定        ：卡片不可见 —— 若该点被其它窗口遮挡则属预期（B+ 已知局限），不计失败。");
        return 0;
    }

    private static void SendShowDesktop()
    {
        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
        keybd_event(VK_D, 0, 0, UIntPtr.Zero);
        keybd_event(VK_D, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static void EnableDpiAwareness()
    {
        try
        {
            if (NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
            {
                Console.WriteLine("DPI 感知：已设为 Per-Monitor V2");
                return;
            }
            Console.WriteLine($"DPI 感知：Per-Monitor V2 未生效（错误码 {Marshal.GetLastWin32Error()}），尝试旧接口…");
        }
        catch (Exception ex)
        {
            Console.WriteLine("DPI 感知：SetProcessDpiAwarenessContext 调用失败，" + ex.Message);
        }

        try
        {
            int hr = NativeMethods.SetProcessDpiAwareness(2); // PROCESS_PER_MONITOR_DPI_AWARE
            Console.WriteLine($"DPI 感知：SetProcessDpiAwareness 返回 HRESULT=0x{hr:X8}（0 表示成功）");
        }
        catch (Exception ex)
        {
            Console.WriteLine("DPI 感知：设置失败，使用系统默认。" + ex.Message);
        }
    }

    // ------------------------------------------------------------ 参数解析

    private static bool HasFlag(string[] args, string flag)
    {
        foreach (string a in args)
            if (a.Equals(flag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string? GetString(string[] args, string prefix)
    {
        foreach (string a in args)
            if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return a[prefix.Length..];
        return null;
    }

    private static int GetInt(string[] args, string prefix, int fallback)
    {
        string? s = GetString(args, prefix);
        return int.TryParse(s, out int v) ? v : fallback;
    }
}
