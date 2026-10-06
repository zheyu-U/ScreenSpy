using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using ScreenSpy.Collector;
using ScreenSpy.Diagnostics;
using ScreenSpy.Interop;
using ScreenSpy.Scheduling;

namespace ScreenSpy.Demo;

/// <summary>
/// M3 的控制台自检入口（内嵌入口，**长期保留** —— 它仍是回归验证的主要手段）。
///
/// 用法：
///   ScreenSpy.exe --m3-selfcheck [--seconds=20] [--logic-only] [--top=5] [--log=&lt;path&gt;]
///                                [--no-raw-log] [--log-dir=&lt;dir&gt;]
///
/// 分两段：
///  【A】确定性检查 —— 用**脚本化的前台采样**覆盖人工难以复现的场景：过滤规则、规范化、
///       同名合并（跨 pid）、归属累计、未计入拍、跨天清空、UWP 宿主计入、
///       **时间守恒**（已归因 + 已过滤 + 未归因 == 调度器的今日活跃），以及
///       **原始活动日志**（仅变化时记录、原始名保留、标题首末与变更次数、跨天切分、真实落盘与剪枝）。
///  【B】真实观察 —— 真实 <c>GetForegroundWindow</c> + 每秒心跳跑 N 秒。
///       期间请**切换几个软件各停留若干秒**，随后核对“每个软件的时长 ≈ 停留墙钟时间”（§5.4 的验收方式）。
///
/// 与 M2 自检一致：产品是 <c>WinExe</c>（GUI 子系统），从交互式控制台直接运行时标准输出可能不可见，
/// 因此本自检把**全部输出同时写入日志文件**（见末尾打印的路径）。
/// </summary>
internal static class M3SelfCheck
{
    private const string SwitchName = "--m3-selfcheck";

    private static readonly StringBuilder LogBuffer = new();
    private static string? _logPath;

    public static bool IsRequested(string[] args)
    {
        foreach (string a in args)
            if (a.Equals(SwitchName, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static int Run(string[] args)
    {
        bool logicOnly = HasFlag(args, "--logic-only");
        int seconds = Math.Max(5, GetInt(args, "--seconds=", 20));
        int topN = Math.Clamp(GetInt(args, "--top=", 5), 1, 20);
        _logPath = GetString(args, "--log=");

        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 输出被重定向时可能失败，忽略 */ }

        TextWriter original = Console.Out;
        try { Console.SetOut(new TeeWriter(original, LogBuffer)); } catch { /* 忽略 */ }

        int failures;
        try
        {
            Console.WriteLine("============ ScreenSpy · M3 前台进程识别与过滤 自检 ============");
            Console.WriteLine($"真实观察 {seconds}s，Top {topN}，仅确定性检查={logicOnly}");
            Console.WriteLine();

            failures = RunDeterministicChecks();

            if (logicOnly)
            {
                Console.WriteLine(failures == 0 ? "确定性检查：全部通过。" : $"确定性检查：{failures} 项失败。");
            }
            else
            {
                Console.WriteLine();
                failures += RunRealObservation(seconds, topN, args);
                Console.WriteLine();
                Console.WriteLine(failures == 0 ? "M3 自检：全部通过。" : $"M3 自检：{failures} 项失败。");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("!! 自检异常：" + ex);
            failures = 1;
        }
        finally
        {
            try { Console.SetOut(original); } catch { /* 忽略 */ }
        }

        WriteLogFile();
        return failures == 0 ? 0 : 1;
    }

    // ================================================================ A 确定性检查

    private static int RunDeterministicChecks()
    {
        Console.WriteLine("---- A 确定性检查（脚本化前台采样；无需真的切换软件）----");
        int failed = 0;

        IntPtr h = new(0x1234);
        TimeSpan one = TimeSpan.FromSeconds(1);
        DateTime day = new(2026, 10, 5, 12, 0, 0);

        // ---- A1 分类与过滤（§5.4 “要过滤的窗口”清单）----
        failed += Check("分类：类名 Progman → 桌面（过滤）", Kind(h, "explorer", "Progman") == ForegroundAppKind.Desktop);
        failed += Check("分类：类名 WorkerW → 桌面（过滤）", Kind(h, "explorer", "WorkerW") == ForegroundAppKind.Desktop);
        failed += Check("分类：类名 SysListView32 → 桌面（过滤）", Kind(h, "explorer", "SysListView32") == ForegroundAppKind.Desktop);
        failed += Check("分类：类名 Shell_TrayWnd → 系统外壳（任务栏，过滤）",
            Kind(h, "explorer", "Shell_TrayWnd") == ForegroundAppKind.Shell);
        failed += Check("分类：进程 StartMenuExperienceHost → 系统外壳（开始菜单，过滤）",
            Kind(h, "StartMenuExperienceHost", "XamlExplorerHostIslandWindow") == ForegroundAppKind.Shell);
        failed += Check("分类：进程 LogonUI → 锁屏（剔除：完全不计入）", Kind(h, "LogonUI", "LogonUI") == ForegroundAppKind.LockScreen);
        failed += Check("分类：类名 LogonUI（进程名普通）→ 锁屏（剔除：完全不计入）", Kind(h, "foo", "LogonUI") == ForegroundAppKind.LockScreen);
        failed += Check("分类：自身进程 → 自身（优先于其它规则）",
            ForegroundAppRules.Classify(h, "devenv", "CabinetWClass", isSelfProcess: true) == ForegroundAppKind.Self);
        failed += Check("分类：ApplicationFrameHost → UWP 宿主（计入，待 §5.5 修正）",
            Kind(h, "ApplicationFrameHost", "ApplicationFrameWindow") == ForegroundAppKind.UwpHost);
        failed += Check("分类：hwnd=0 → 无前台窗口",
            ForegroundAppRules.Classify(IntPtr.Zero, "devenv", "X", false) == ForegroundAppKind.None);
        failed += Check("分类：进程名为空 → 未知（不猜测）",
            Kind(h, "", "CabinetWClass") == ForegroundAppKind.Unknown);

        // ---- A2 规范化与同名合并键 ----
        ForegroundAppSample devenvSample = ForegroundAppRules.Create(h, 100, "devenv", "HwndWrapper[devenv", "a.cs", false);
        failed += Check("规范化：devenv → 展示名 Visual Studio、键 devenv",
            devenvSample.Kind == ForegroundAppKind.App &&
            devenvSample.DisplayName == "Visual Studio" && devenvSample.MergeKey == "devenv");

        ForegroundAppSample explorerSample = ForegroundAppRules.Create(h, 200, "explorer", "CabinetWClass", "下载", false);
        failed += Check("关键：explorer + CabinetWClass → 应用（不因进程名是 explorer 就被过滤）",
            explorerSample.Kind == ForegroundAppKind.App && explorerSample.DisplayName == "文件资源管理器");

        ForegroundAppSample chromeUpper = ForegroundAppRules.Create(h, 300, "CHROME", "Chrome_WidgetWin_1", "x", false);
        failed += Check("规范化：大小写不敏感（CHROME → Chrome，键 chrome）",
            chromeUpper.DisplayName == "Chrome" && chromeUpper.MergeKey == "chrome");
        failed += Check("规范化：容错传入 devenv.exe → 仍为 Visual Studio",
            ForegroundAppRules.NormalizeDisplayName("devenv.exe") == "Visual Studio");

        ForegroundAppSample unmapped = ForegroundAppRules.Create(h, 400, "FooBar", "SomeClass", "t", false);
        failed += Check("规范化：未映射进程名 FooBar → 展示名 FooBar、键 foobar",
            unmapped.DisplayName == "FooBar" && unmapped.MergeKey == "foobar");
        failed += Check("合并键：标题不同、进程相同 → 键相同（标题不参与合并）",
            ForegroundAppRules.Create(h, 1, "chrome", "C", "页面 A", false).MergeKey ==
            ForegroundAppRules.Create(h, 2, "chrome", "C", "页面 B", false).MergeKey);

        failed += Check("计入判定：App 与 UWP 宿主计入；桌面/外壳/锁屏/自身/未知/无前台 一律不计入",
            ForegroundAppRules.Create(h, 1, "devenv", "C", "", false).CountsAsApp &&
            ForegroundAppRules.Create(h, 1, "ApplicationFrameHost", "C", "", false).CountsAsApp &&
            !ForegroundAppRules.Create(h, 1, "explorer", "Progman", "", false).CountsAsApp &&
            !ForegroundAppRules.Create(h, 1, "explorer", "Shell_TrayWnd", "", false).CountsAsApp &&
            !ForegroundAppRules.Create(h, 1, "LogonUI", "LogonUI", "", false).CountsAsApp &&
            !ForegroundAppRules.Create(h, 1, "ScreenSpy", "C", "", true).CountsAsApp &&
            !ForegroundAppRules.Create(h, 1, "", "C", "", false).CountsAsApp &&
            !ForegroundAppRules.Create(IntPtr.Zero, 0, "", "", "", false).CountsAsApp);

        failed += Check("过滤标志：桌面/外壳/自身 为 IsFiltered（**不含锁屏**）；未知/无前台 不是",
            ForegroundAppRules.Create(h, 1, "explorer", "Progman", "", false).IsFiltered &&
            ForegroundAppRules.Create(h, 1, "explorer", "Shell_TrayWnd", "", false).IsFiltered &&
            ForegroundAppRules.Create(h, 1, "ScreenSpy", "C", "", true).IsFiltered &&
            ForegroundAppRules.Create(h, 1, "devenv", "C", "", false).IsFiltered == false &&
            ForegroundAppRules.Create(h, 1, "", "C", "", false).IsFiltered == false);

        failed += Check("锁屏标志：LogonUI 为 IsLockScreen（完全不计入），且**不再是** IsFiltered",
            ForegroundAppRules.Create(h, 1, "LogonUI", "LogonUI", "", false).IsLockScreen &&
            ForegroundAppRules.Create(h, 1, "LogonUI", "LogonUI", "", false).IsFiltered == false);

        // ---- A3 归属累计 / 同名合并 / 过滤 / 未归因 ----
        var tracker = new AppUsageTracker();
        ForegroundAppSample chromeA = ForegroundAppRules.Create(h, 300, "chrome", "Chrome_WidgetWin_1", "页面 A", false);
        ForegroundAppSample chromeB = ForegroundAppRules.Create(h, 301, "chrome", "Chrome_WidgetWin_1", "页面 B", false);
        ForegroundAppSample desktopSample = ForegroundAppRules.Create(h, 200, "explorer", "Progman", "", false);
        ForegroundAppSample selfSample = ForegroundAppRules.Create(h, 999, "ScreenSpy", "ScreenSpyCard", "", true);
        ForegroundAppSample unknownSample = ForegroundAppRules.Create(h, 500, "", "SomeClass", "", false);
        ForegroundAppSample noneSample = ForegroundAppRules.Create(IntPtr.Zero, 0, null, null, null, false);

        for (int i = 0; i < 3; i++) tracker.Observe(devenvSample, one, true, day);
        tracker.Observe(chromeA, one, true, day);
        tracker.Observe(chromeA, one, true, day);
        tracker.Observe(chromeB, one, true, day);
        tracker.Observe(explorerSample, one, true, day);
        tracker.Observe(desktopSample, one, true, day);
        tracker.Observe(selfSample, one, true, day);
        tracker.Observe(unknownSample, one, true, day);
        tracker.Observe(noneSample, one, true, day);

        failed += Check("累计：devenv 3 拍 → 3.000s", tracker.MillisecondsOf("devenv") == 3000);
        failed += Check("同名合并：chrome 两个 pid 共 3 拍 → 合并为一条 3.000s",
            tracker.MillisecondsOf("chrome") == 3000);
        failed += Check("累计：文件资源管理器 1 拍 → 1.000s", tracker.MillisecondsOf("explorer") == 1000);
        failed += Check("应用条目数 = 3（devenv / chrome / 文件资源管理器）", tracker.AppCount == 3);
        failed += Check("已归因 = 7.000s", Ms(tracker.AttributedTotal) == 7000);
        failed += Check("不计入使用时长 = 2.000s（桌面 + 自身），且分别计数",
            Ms(tracker.FilteredTotal) == 2000 && tracker.DesktopTicks == 1 && tracker.SelfTicks == 1);
        failed += Check("无前台/未知 = 2.000s（未知 + 无前台），且分别计数",
            Ms(tracker.UnattributedTotal) == 2000 && tracker.UnknownTicks == 1 && tracker.NoWindowTicks == 1);
        failed += Check("计数：被计入 11 拍、未计入 0 拍", tracker.CountedTicks == 11 && tracker.SkippedTicks == 0);
        failed += Check("时间守恒：合计 = 11.000s", Ms(tracker.Total) == 11000);
        failed += Check("守恒恒等式：已归因 + 不计入使用时长 + 无前台/未知 == 合计（毫秒级精确）",
            Ms(tracker.AttributedTotal) + Ms(tracker.FilteredTotal) + Ms(tracker.UnattributedTotal) == Ms(tracker.Total));

        // ---- A3b 锁屏：口径“完全不计入”（独立剔除桶，不进展示口径）----
        ForegroundAppSample lockSample = ForegroundAppRules.Create(h, 700, "LogonUI", "LogonUI", "", false);
        tracker.Observe(lockSample, one, true, day);
        failed += Check("锁屏：被计入的锁屏拍进“剔除”桶，**不进**三个展示桶",
            Ms(tracker.LockScreenExcludedTotal) == 1000 && Ms(tracker.FilteredTotal) == 2000 &&
            Ms(tracker.UnattributedTotal) == 2000 && Ms(tracker.AttributedTotal) == 7000 &&
            tracker.LockScreenTicks == 1);
        failed += Check("锁屏：守恒四项闭合（三展示桶 + 锁屏剔除 = 被计入 12 拍）",
            Ms(tracker.AttributedTotal) + Ms(tracker.FilteredTotal) + Ms(tracker.UnattributedTotal) +
            Ms(tracker.LockScreenExcludedTotal) == 12000 && tracker.CountedTicks == 12);

        // ---- A4 排行榜排序与稳定性 ----
        IReadOnlyList<AppUsageEntry> top1 = tracker.Top(2);
        IReadOnlyList<AppUsageEntry> top2 = tracker.Top(2);
        failed += Check("Top：Top(2) 返回 2 条、Top(10) 返回全部 3 条、Top(0) 返回空",
            top1.Count == 2 && tracker.Top(10).Count == 3 && tracker.Top(0).Count == 0);
        failed += Check("Top：前两条都是 3.000s 的 Chrome / Visual Studio",
            top1[0].Milliseconds == 3000 && top1[1].Milliseconds == 3000 &&
            ((top1[0].DisplayName == "Chrome" && top1[1].DisplayName == "Visual Studio") ||
             (top1[0].DisplayName == "Visual Studio" && top1[1].DisplayName == "Chrome")));
        failed += Check("Top：排序稳定（两次调用顺序一致）",
            top1[0].DisplayName == top2[0].DisplayName && top1[1].DisplayName == top2[1].DisplayName);
        failed += Check("Top：占比之和 ≤ 1", top1[0].Share + top1[1].Share <= 1.0000001);

        // ---- A5 UWP 宿主计入 ----
        tracker.Observe(ForegroundAppRules.Create(h, 600, "ApplicationFrameHost", "ApplicationFrameWindow", "计算器", false),
                        one, true, day);
        failed += Check("UWP：ApplicationFrameHost 计入（1.000s）并单独计数",
            tracker.MillisecondsOf("applicationframehost") == 1000 && tracker.UwpTicks == 1);
        failed += Check("UWP：计入后 已归因 = 8.000s、合计 = 12.000s",
            Ms(tracker.AttributedTotal) == 8000 && Ms(tracker.Total) == 12000);

        // ---- A6 未计入的拍：不累计，但“当前软件”仍更新 ----
        long countedBefore = tracker.CountedTicks;
        long totalBefore = Ms(tracker.Total);
        tracker.Observe(chromeA, one, false, day);
        failed += Check("未计入（挂机/暂停/空洞）：时长与计数拍均不变",
            tracker.CountedTicks == countedBefore && Ms(tracker.Total) == totalBefore);
        failed += Check("未计入：跳过拍数 +1", tracker.SkippedTicks == 1);
        failed += Check("未计入：仍更新“当前软件”", tracker.CurrentDisplayName == "Chrome");
        failed += Check("未计入：最近可计入时长归零", tracker.LastCreditedMs == 0);

        // ---- A7 跨天清空（主路径 RollDay + 防御性 localTime）----
        tracker.RollDay(new DateOnly(2026, 10, 6));
        failed += Check("跨天：榜单清空（条目 0、已归因 0、锁屏剔除 0、三项计数清零）",
            tracker.AppCount == 0 && Ms(tracker.AttributedTotal) == 0 &&
            Ms(tracker.LockScreenExcludedTotal) == 0 &&
            tracker.DesktopTicks == 0 && tracker.SelfTicks == 0 && tracker.UnknownTicks == 0);
        failed += Check("跨天：统计日已切换", tracker.Day == new DateOnly(2026, 10, 6));

        tracker.Observe(devenvSample, one, true, new DateTime(2026, 10, 6, 12, 0, 0));
        failed += Check("防御性跨天：同一天继续累计 → 1.000s", Ms(tracker.AttributedTotal) == 1000);

        tracker.Observe(devenvSample, one, true, new DateTime(2026, 10, 7, 0, 0, 1));
        failed += Check("防御性跨天：localTime 跨日 → 自动清空并只含当拍 1.000s、日=10-07",
            Ms(tracker.AttributedTotal) == 1000 && tracker.Day == new DateOnly(2026, 10, 7));

        // ---- A8 端到端：真调度器 + 真桥 + 脚本化前台源 ----
        var clock = new FakeIdleClock { Idle = TimeSpan.FromSeconds(1) };
        var ticks = new FakeTickSource();
        DateTime now = new(2026, 10, 5, 12, 0, 0);

        using var scheduler = new ActivityScheduler(clock, TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(1),
                                                   tickSource: ticks, localNow: () => now);
        var script = new ScriptedSource();
        using var bridge = new AppUsageBridge(scheduler, script);

        script.Push(devenvSample); script.Push(devenvSample); script.Push(chromeA); script.Push(chromeB);
        for (int i = 0; i < 4; i++) { ticks.Milliseconds += 1000; scheduler.Pump(); }

        failed += Check("端到端：4 活跃拍 → 调度器今日 4.000s", Ms(scheduler.TodayActive) == 4000);
        failed += Check("端到端：归属合计与调度器一致（守恒）",
            Ms(bridge.Tracker.Total) == Ms(scheduler.TodayActive));
        failed += Check("端到端：devenv 2 拍 = 2.000s、chrome 合并 2 拍 = 2.000s",
            bridge.Tracker.MillisecondsOf("devenv") == 2000 && bridge.Tracker.MillisecondsOf("chrome") == 2000);

        clock.Idle = TimeSpan.FromSeconds(400);   // 挂机 > 阈值
        script.Push(chromeA); script.Push(devenvSample);
        for (int i = 0; i < 2; i++) { ticks.Milliseconds += 1000; scheduler.Pump(); }

        failed += Check("端到端：挂机 2 拍不计入（今日与合计均仍为 4.000s）",
            Ms(scheduler.TodayActive) == 4000 && Ms(bridge.Tracker.Total) == 4000);
        failed += Check("端到端：挂机期间仍更新“当前软件”", bridge.Tracker.CurrentDisplayName == "Visual Studio");

        clock.Idle = TimeSpan.FromSeconds(1);     // 恢复活跃
        script.Push(chromeA); script.Push(chromeA);
        for (int i = 0; i < 2; i++) { ticks.Milliseconds += 1000; scheduler.Pump(); }

        failed += Check("端到端：恢复 2 拍 → 今日 6.000s、chrome 累计 4.000s",
            Ms(scheduler.TodayActive) == 6000 && bridge.Tracker.MillisecondsOf("chrome") == 4000);
        failed += Check("端到端：Top1 为 Chrome（4.000s）",
            bridge.Tracker.Top(1).Count == 1 && bridge.Tracker.Top(1)[0].DisplayName == "Chrome");
        failed += Check("端到端：最终守恒（归属合计 == 今日活跃）",
            Ms(bridge.Tracker.Total) == Ms(scheduler.TodayActive));
        failed += Check("端到端：无采样异常", bridge.SamplingFailures == 0);

        failed += RawLogSelfCheck.Run();

        Console.WriteLine($"   小计：{(failed == 0 ? "全部通过" : failed + " 项失败")}");
        return failed;
    }

    // ================================================================ B 真实观察

    private static int RunRealObservation(int seconds, int topN, string[] args)
    {
        Console.WriteLine("---- B 真实观察（真实 GetForegroundWindow + 每秒心跳）----");

        var source = new Win32ForegroundAppSource();
        Console.WriteLine($"数据源     ：{(source.IsAvailable ? "可用" : "不可用：" + source.LastError)}");
        Console.WriteLine($"本进程 pid ：{Environment.ProcessId}（本程序的窗口会被过滤，不计入任何软件）");

        ForegroundAppSample probe = source.Sample();
        Console.WriteLine($"即时采样   ：{probe}");
        Console.WriteLine();

        var clock = new Win32IdleClock();
        Console.WriteLine($"空闲数据源 ：{(clock.IsAvailable ? "可用" : "不可用")}（当前空闲 {clock.IdleTime.TotalSeconds:F1}s）");

        EnableDpiAwareness();
        EnvironmentInfo.Print();

        using var scheduler = new ActivityScheduler(clock);
        using var bridge = new AppUsageBridge(scheduler, source);

        // 原始活动日志（旁路，不改动归属逻辑）：默认开启、仅变化时记录；
        // --no-raw-log 关闭，--log-dir=<path> 改目录。开发期默认落在 artifacts 下。
        using RawLogProbe? rawLog = RawLogProbe.Create(bridge, args, "m3-rawlog");

        // 记录软件切换轨迹（用于判断本轮是否真的产生了归属数据）。
        var seen = new List<string>();
        bridge.Observed += o =>
        {
            string name = o.Sample.DisplayName;
            if (seen.Count == 0 || seen[^1] != name) seen.Add(name);
        };

        Console.WriteLine();
        Console.WriteLine($"观察 {seconds}s：请在期间**切换几个软件各停留若干秒**（例如浏览器 / 编辑器 / 记事本）；");
        Console.WriteLine("观察结束后核对：每个软件显示的时长应与它前台的墙钟时间相差几秒以内（§5.4 的验收方式）。");
        Console.WriteLine();

        var sw = Stopwatch.StartNew();
        scheduler.Start();
        int lastBucket = -1;

        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            Thread.Sleep(200);

            int sec = (int)sw.Elapsed.TotalSeconds;
            int bucket = sec / 5;
            if (bucket == lastBucket) continue;
            lastBucket = bucket;

            AppUsageTracker tr = bridge.Tracker;
            Console.WriteLine(
                $"  [t={sec,3}s] 当前={tr.CurrentDisplayName} 今日={Format(scheduler.TodayActive)} " +
                $"归因={Format(tr.AttributedTotal)} 过滤={Format(tr.FilteredTotal)} 未归因={Format(tr.UnattributedTotal)} " +
                $"计入={tr.CountedTicks}/跳过={tr.SkippedTicks}");
        }

        scheduler.Stop();
        sw.Stop();

        AppUsageTracker tracker = bridge.Tracker;
        long conservationDiffMs = Ms(tracker.Total) - Ms(scheduler.TodayActive);

        Console.WriteLine();
        Console.WriteLine("---- 汇总 ----");
        Console.WriteLine($"采样次数     ：{source.Samples}（进程名取样失败 {source.ProcessLookupFailures}）");
        Console.WriteLine($"心跳次数     ：{scheduler.Sequence}");
        Console.WriteLine($"被计入拍数   ：{tracker.CountedTicks}（未计入 {tracker.SkippedTicks}）");
        Console.WriteLine($"已归因时长   ：{Format(tracker.AttributedTotal)}（{Ms(tracker.AttributedTotal)} ms）");
        Console.WriteLine($"已过滤时长   ：{Format(tracker.FilteredTotal)}" +
                          $"（桌面 {tracker.DesktopTicks} / 外壳 {tracker.ShellTicks} / 锁屏 {tracker.LockScreenTicks} / 自身 {tracker.SelfTicks} 拍）");
        Console.WriteLine($"未归因时长   ：{Format(tracker.UnattributedTotal)}" +
                          $"（无前台 {tracker.NoWindowTicks} / 未知 {tracker.UnknownTicks} 拍）");
        Console.WriteLine($"归属合计     ：{Format(tracker.Total)}");
        Console.WriteLine($"今日活跃     ：{Format(scheduler.TodayActive)}（调度器）");
        Console.WriteLine($"守恒差       ：{conservationDiffMs} ms（应为 0）");
        Console.WriteLine();

        Console.WriteLine($"---- 按软件 Top{topN} ----");
        IReadOnlyList<AppUsageEntry> top = tracker.Top(topN);
        if (top.Count == 0)
        {
            Console.WriteLine("  （本轮没有归因到任何软件 —— 可能全程停留在桌面/外壳，或全程挂机）");
        }
        else
        {
            foreach (AppUsageEntry e in top)
                Console.WriteLine($"  {e.DisplayName,-24} {Format(e.Time),10}  {e.Share * 100,5:F1}%");
        }
        Console.WriteLine();

        Console.WriteLine("---- 分类计数 ----");
        Console.WriteLine($"应用 {tracker.AppTicks} 拍 / UWP 宿主 {tracker.UwpTicks} 拍 / 桌面 {tracker.DesktopTicks} 拍 / 外壳 {tracker.ShellTicks} 拍");
        Console.WriteLine($"锁屏 {tracker.LockScreenTicks} 拍 / 自身 {tracker.SelfTicks} 拍 / 无前台 {tracker.NoWindowTicks} 拍 / 未知 {tracker.UnknownTicks} 拍");
        Console.WriteLine($"软件切换     ：{Math.Max(0, seen.Count - 1)} 次");
        Console.WriteLine();

        // 先关闭最后一段状态并等待落盘，再打印证据（否则尾段还在内存里）。
        rawLog?.Dispose();
        if (rawLog is not null) Console.Write(rawLog.Report());

        int rc = 0;

        if (!source.IsAvailable)
        {
            Console.WriteLine("!! 数据源不可用 —— 无法识别前台软件。");
            rc |= 1;
        }
        if (Math.Abs(conservationDiffMs) > 1)
        {
            Console.WriteLine($"!! 时间守恒被破坏（差 {conservationDiffMs} ms）—— 归属链路存在漏记或重复计。");
            rc |= 2;
        }
        if (source.Samples > 3 && source.ProcessLookupFailures > source.Samples / 2)
        {
            Console.WriteLine($"!! 进程名取样失败占比过高（{source.ProcessLookupFailures}/{source.Samples}）。");
            rc |= 8;
        }
        if (scheduler.Sequence == 0)
        {
            Console.WriteLine("!! 观察期间一次心跳都没有。");
            rc |= 16;
        }

        if (source.SelfSamples > 0)
            Console.WriteLine($"提示：采到本程序在前台 {source.SelfSamples} 次（已过滤，未计入任何软件；卡片是 NOACTIVATE 的，正常应≈0）。");
        if (tracker.UwpTicks > 0)
            Console.WriteLine($"提示：本轮有 {tracker.UwpTicks} 拍落在 UWP 宿主 ApplicationFrameHost 上 —— 真实应用名修正属 §5.5 / M11。");
        if (tracker.UnknownTicks > 0)
            Console.WriteLine($"提示：本轮有 {tracker.UnknownTicks} 拍取不到进程名（多为权限受限的系统进程），未计入任何软件。");
        if (tracker.CountedTicks == 0)
            Console.WriteLine("提示：本轮没有被计入的拍（全程挂机 / 锁屏）—— 归属逻辑未被真实数据触发（A 段已覆盖）。");

        if (rc == 0)
            Console.WriteLine("真实观察：通过 —— 归属与过滤正常，时间守恒成立。");

        return rc;
    }

    // ================================================================ 辅助

    private static ForegroundAppKind Kind(IntPtr hwnd, string processName, string className)
        => ForegroundAppRules.Classify(hwnd, processName, className, isSelfProcess: false);

    private static int Check(string label, bool ok)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        return ok ? 0 : 1;
    }

    private static long Ms(TimeSpan t) => (long)Math.Round(t.TotalMilliseconds, MidpointRounding.AwayFromZero);

    private static string Format(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";

    private static void EnableDpiAwareness()
    {
        try
        {
            NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch { /* 忽略：本自检不依赖 DPI 感知 */ }
    }

    private static void WriteLogFile()
    {
        string path = _logPath ?? Path.Combine("artifacts", "m3", "m3-selfcheck.log");

        try
        {
            string full = Path.GetFullPath(path);
            string? dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            LogBuffer.AppendLine($"[日志] 已写入 {full}");
            File.WriteAllText(full, LogBuffer.ToString(), new UTF8Encoding(false));

            AppendConsole($"[日志] 全部输出已写入：{full}");
        }
        catch (Exception ex)
        {
            AppendConsole("写日志文件失败：" + ex.Message + "（控制台输出仍请直接查看）");
        }
    }

    private static void AppendConsole(string line)
    {
        try { Console.WriteLine(line); } catch { }
        try { Console.Error.WriteLine(line); } catch { }
    }

    private static bool HasFlag(string[] args, string flag)
    {
        foreach (string a in args)
            if (a.Equals(flag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static int GetInt(string[] args, string prefix, int fallback)
    {
        foreach (string a in args)
            if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(a[prefix.Length..], out int v))
                return v;
        return fallback;
    }

    private static string? GetString(string[] args, string prefix)
    {
        foreach (string a in args)
            if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return a[prefix.Length..];
        return null;
    }

    private sealed class FakeIdleClock : IIdleClock
    {
        public TimeSpan Idle { get; set; }
        public bool IsAvailable { get; set; } = true;
        public TimeSpan IdleTime => Idle;
    }

    private sealed class FakeTickSource : ITickSource
    {
        public long Milliseconds { get; set; }
    }

    /// <summary>脚本化的前台采样源：按入队顺序返回样本，队列空后重复最后一个。</summary>
    private sealed class ScriptedSource : IForegroundAppSource
    {
        private readonly Queue<ForegroundAppSample> _queue = new();
        private ForegroundAppSample _last = ForegroundAppRules.Create(IntPtr.Zero, 0, null, null, null, false);

        public void Push(ForegroundAppSample sample) => _queue.Enqueue(sample);

        public ForegroundAppSample Sample()
        {
            if (_queue.Count > 0) _last = _queue.Dequeue();
            return _last;
        }

        public bool IsAvailable => true;
        public string? LastError => null;
    }

    /// <summary>把输出同时写到原控制台与内存缓冲区（用于“控制台看不到”时的文件兜底）。</summary>
    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _console;
        private readonly StringBuilder _buffer;

        public TeeWriter(TextWriter console, StringBuilder buffer)
        {
            _console = console;
            _buffer = buffer;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            try { _console.Write(value); } catch { }
            _buffer.Append(value);
        }

        public override void Write(string? value)
        {
            try { _console.Write(value); } catch { }
            if (value is not null) _buffer.Append(value);
        }

        public override void WriteLine(string? value)
        {
            try { _console.WriteLine(value); } catch { }
            _buffer.Append(value).Append(Environment.NewLine);
        }

        public override void WriteLine()
        {
            try { _console.WriteLine(); } catch { }
            _buffer.Append(Environment.NewLine);
        }
    }
}
