using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using ScreenSpy.Collector;
using ScreenSpy.Diagnostics;
using ScreenSpy.Scheduling;

namespace ScreenSpy.Demo;

/// <summary>
/// M1 的控制台自检入口（内嵌入口，**长期保留** —— 它仍是回归验证的主要手段）。
///
/// 用法：
///   ScreenSpy.exe --m1-selfcheck [--seconds=20] [--idle-threshold=300] [--heartbeat=1000] [--logic-only]
///
/// 分两段：
///  【A】确定性检查 —— 注入假时钟 / 假空闲源，覆盖**人工无法复现**的场景：
///       阈值边界、32 位 tick 回绕、挂机不计时、暂停、跨天重置、按差值累计。
///  【B】真实采样 —— 用 GetLastInputInfo + 每秒心跳跑 N 秒，给出**计时漂移**客观数据。
/// </summary>
internal static class M1SelfCheck
{
    private const string SwitchName = "--m1-selfcheck";

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

        bool logicOnly = HasFlag(args, "--logic-only");
        int seconds = Math.Max(3, GetInt(args, "--seconds=", 20));
        int thresholdSec = Math.Max(1, GetInt(args, "--idle-threshold=", ActivityRules.DefaultIdleThresholdSeconds));
        int heartbeatMs = Math.Max(100, GetInt(args, "--heartbeat=", 1000));

        Console.WriteLine("============ ScreenSpy · M1 计时调度器与空闲检测 自检 ============");
        Console.WriteLine($"采样时长 {seconds}s，心跳 {heartbeatMs}ms，空闲阈值 {thresholdSec}s，仅确定性检查={logicOnly}");
        Console.WriteLine();

        int failures = RunDeterministicChecks();
        Console.WriteLine();

        if (logicOnly)
        {
            Console.WriteLine(failures == 0 ? "确定性检查：全部通过。" : $"确定性检查：{failures} 项失败。");
            return failures == 0 ? 0 : 1;
        }

        int rc = RunRealSampling(seconds, thresholdSec, heartbeatMs);

        Console.WriteLine();
        if (failures == 0 && rc == 0)
        {
            Console.WriteLine("M1 自检：全部通过。");
            return 0;
        }

        Console.WriteLine($"M1 自检：失败（确定性检查 {failures} 项，真实采样退出码 {rc}）。");
        return 1;
    }

    // ================================================================ A 确定性检查

    private static int RunDeterministicChecks()
    {
        Console.WriteLine("---- A 确定性检查（假时钟 + 假空闲源；覆盖人工无法复现的场景）----");
        int failed = 0;
        var threeHundred = TimeSpan.FromSeconds(300);

        // 1) 阈值边界
        failed += Check("阈值：idle=299.999s < 300s → 活跃",
            ActivityRules.IsActive(TimeSpan.FromMilliseconds(299999), threeHundred));
        failed += Check("阈值：idle=300s 恰好等于阈值 → 挂机（严格小于才计活跃）",
            !ActivityRules.IsActive(threeHundred, threeHundred));
        failed += Check("阈值：idle=301s > 300s → 挂机",
            !ActivityRules.IsActive(TimeSpan.FromSeconds(301), threeHundred));

        // 2) 32 位 tick 回绕（约 49.7 天）—— 有符号相减会在这里出错
        failed += Check("回绕：now=0x100, last=0xFFFFFF00 → 512ms（不是负数、不是巨值）",
            ActivityRules.IdleFromTickCount32(0x100, 0xFFFF_FF00) == TimeSpan.FromMilliseconds(512));
        failed += Check("常规：now=1000, last=0 → 1000ms",
            ActivityRules.IdleFromTickCount32(1000, 0) == TimeSpan.FromMilliseconds(1000));
        failed += Check("零空闲：now == last → 0ms",
            ActivityRules.IdleFromTickCount32(0xFFFF_FFFF, 0xFFFF_FFFF) == TimeSpan.Zero);

        // 3) 累加行为 / 挂机不计时 / 暂停 / 边界 / 跨天（完全确定）
        var clock = new FakeIdleClock { Idle = TimeSpan.FromSeconds(1) };
        var ticks = new FakeTickSource();
        var localNow = new DateTime(2026, 10, 5, 12, 0, 0);

        // 注意：这里刻意不调用 Start()，避免真实定时器介入；Sample() 以 0 为首拍基准。
        using var s = new ActivityScheduler(clock, threeHundred, TimeSpan.FromSeconds(1),
                                            tickSource: ticks, localNow: () => localNow);

        ticks.Milliseconds += 1000; s.Sample();
        ticks.Milliseconds += 1000; s.Sample();
        failed += Check("累计：活跃 2 拍 → 今日 2.000s", Near(s.TodayActive, 2000));

        clock.Idle = TimeSpan.FromSeconds(400);          // 超过阈值 → 挂机
        ticks.Milliseconds += 1000; var idleTick = s.Sample();
        ticks.Milliseconds += 1000; s.Sample();
        failed += Check("挂机：idle>阈值 → 该拍判定为挂机", !idleTick.IsActive);
        failed += Check("挂机：连续 2 拍不计时 → 今日仍为 2.000s", Near(s.TodayActive, 2000));

        clock.Idle = TimeSpan.FromSeconds(10);           // 恢复活跃
        ticks.Milliseconds += 1000; s.Sample();
        failed += Check("恢复：idle<阈值 1 拍 → 今日 3.000s", Near(s.TodayActive, 3000));

        s.Paused = true;                                  // 暂停（为 M2 锁屏预留）
        ticks.Milliseconds += 1000; s.Sample();
        failed += Check("暂停：Paused=true 1 拍 → 今日仍为 3.000s", Near(s.TodayActive, 3000));
        s.Paused = false;

        clock.Idle = threeHundred;                        // 恰好等于阈值
        ticks.Milliseconds += 1000; s.Sample();
        failed += Check("边界：idle 恰好=阈值 → 不计入（仍 3.000s）", Near(s.TodayActive, 3000));

        bool rolled = false;
        s.DayRolled += _ => rolled = true;
        clock.Idle = TimeSpan.FromSeconds(1);
        localNow = new DateTime(2026, 10, 6, 0, 0, 1);     // 跨天
        ticks.Milliseconds += 1000; s.Sample();
        failed += Check("跨天：DayRolled 已触发", rolled);
        failed += Check("跨天：统计日变为 2026-10-06", s.Day == new DateOnly(2026, 10, 6));
        failed += Check("跨天：今日累计被重置（仅含重置后这 1 拍 = 1.000s）", Near(s.TodayActive, 1000));

        // 4) 按差值累计（而非固定累加）：计量墙钟 == 注入的增量之和
        failed += Check("累计：计量墙钟 == 注入增量之和 8.000s", Near(s.AccountedElapsed, 8000));

        // 5) 回归：**无 Ticked 订阅者**时心跳仍必须累计。
        //    历史 bug：写成 Ticked?.Invoke(ProcessHeartbeat())，Ticked 为 null 时实参不求值 → 一秒都不计。
        //    这里用 Pump()（与定时器同一路径，且刻意不订阅任何事件）复现该路径。
        var quietClock = new FakeIdleClock { Idle = TimeSpan.FromSeconds(1) };
        var quietTicks = new FakeTickSource();
        using var quiet = new ActivityScheduler(quietClock, threeHundred, TimeSpan.FromSeconds(1),
                                               tickSource: quietTicks, localNow: () => new DateTime(2026, 10, 5, 12, 0, 0));

        quietTicks.Milliseconds += 1000; quiet.Pump();
        quietTicks.Milliseconds += 1000; quiet.Pump();
        failed += Check("回归：无订阅者时心跳仍累计 → 今日 2.000s", Near(quiet.TodayActive, 2000));
        failed += Check("回归：定时器路径回调计数 == 2", quiet.TimerFirings == 2);
        failed += Check("回归：无异常记录", quiet.LastError is null);

        // 6) 回归（M5b）：两个暂停来源是**或**关系，且用户暂停不能被 M2 的探测清掉。
        //    背景：M2 的周期探测（默认 5s）会按会话状态重写 Paused；若把用户的手动暂停
        //    也写进 Paused，用户刚点的“暂停统计”会在几秒后被静默清除。
        var pauseClock = new FakeIdleClock { Idle = TimeSpan.FromSeconds(1) };
        var pauseTicks = new FakeTickSource();
        using var paused = new ActivityScheduler(pauseClock, threeHundred, TimeSpan.FromSeconds(1),
                                                tickSource: pauseTicks, localNow: () => new DateTime(2026, 10, 5, 12, 0, 0));

        // 6a) 仅用户暂停 → 不计时
        paused.UserPaused = true;
        pauseTicks.Milliseconds += 1000; var tUser = paused.Sample();
        failed += Check("暂停（M5b）：仅 UserPaused → 不计时且 IsPaused=true", !tUser.IsActive && tUser.IsPaused);
        failed += Check("暂停（M5b）：仅 UserPaused → 今日 0.000s", Near(paused.TodayActive, 0));

        // 6b) 模拟 M2 探测把 Paused 写回 false（会话未锁定）→ 用户暂停必须仍然生效
        paused.Paused = false;
        pauseTicks.Milliseconds += 1000; var tUser2 = paused.Sample();
        failed += Check("暂停（M5b）：M2 探测写入 Paused=false 后，用户暂停仍生效",
            !tUser2.IsActive && tUser2.IsPaused);

        // 6c) 仅会话暂停（锁屏）→ M2 既有语义不变
        paused.UserPaused = false;
        paused.Paused = true;
        pauseTicks.Milliseconds += 1000; var tSession = paused.Sample();
        failed += Check("暂停（M2 语义未变）：仅 Paused（锁屏）→ 不计时", !tSession.IsActive && tSession.IsPaused);

        // 6d) 两个来源都关闭 → 恢复计入
        paused.Paused = false;
        pauseTicks.Milliseconds += 1000; var tResume = paused.Sample();
        failed += Check("暂停（M5b）：两来源都关闭 → 恢复计入（今日 1.000s）",
            tResume.IsActive && Near(paused.TodayActive, 1000));

        Console.WriteLine($"   小计：{(failed == 0 ? "全部通过" : failed + " 项失败")}");
        return failed;
    }

    private static int Check(string label, bool ok)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        return ok ? 0 : 1;
    }

    private static bool Near(TimeSpan actual, double expectedMs, double toleranceMs = 1.0)
        => Math.Abs(actual.TotalMilliseconds - expectedMs) <= toleranceMs;

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

    // ================================================================ B 真实采样

    private static int RunRealSampling(int seconds, int thresholdSec, int heartbeatMs)
    {
        EnableDpiAwareness();
        EnvironmentInfo.Print();

        var clock = new Win32IdleClock();
        Console.WriteLine("---- 空闲数据源 ----");
        Console.WriteLine($"GetLastInputInfo：{(clock.IsAvailable ? "可用" : "不可用")}");
        Console.WriteLine($"当前空闲        ：{clock.IdleTime.TotalSeconds:F1}s");
        Console.WriteLine();

        using var scheduler = new ActivityScheduler(
            clock,
            TimeSpan.FromSeconds(thresholdSec),
            TimeSpan.FromMilliseconds(heartbeatMs));

        scheduler.DayRolled += d => Console.WriteLine($"[跨天] 今日累计已重置，新统计日={d:yyyy-MM-dd}");

        long ticks = 0, activeTicks = 0, idleTicks = 0;
        scheduler.Ticked += t =>
        {
            Interlocked.Increment(ref ticks);
            if (t.IsActive) Interlocked.Increment(ref activeTicks);
            else Interlocked.Increment(ref idleTicks);

            Console.WriteLine(
                $"#{t.Sequence,4}  {t.LocalTime:HH:mm:ss}  " +
                $"idle={t.IdleTime.TotalSeconds,8:F1}s  " +
                $"{(t.IsActive ? "活跃" : "挂机")}  " +
                $"Δ={t.Elapsed.TotalSeconds,5:F2}s  " +
                $"今日={Format(t.TodayActive)}");
        };

        Console.WriteLine("---- B 真实采样（GetLastInputInfo + 每秒心跳）----");
        var sw = Stopwatch.StartNew();
        scheduler.Start();

        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
            Thread.Sleep(100);

        scheduler.Stop();
        sw.Stop();

        double driftMs = scheduler.AccountedElapsed.TotalMilliseconds - sw.Elapsed.TotalMilliseconds;

        Console.WriteLine();
        Console.WriteLine("---- 汇总 ----");
        Console.WriteLine($"心跳次数   ：{Interlocked.Read(ref ticks)}（活跃 {Interlocked.Read(ref activeTicks)}，挂机 {Interlocked.Read(ref idleTicks)}）");
        Console.WriteLine($"今日活跃   ：{Format(scheduler.TodayActive)}（{scheduler.TodayActive.TotalSeconds:F1}s）");
        Console.WriteLine($"计量墙钟   ：{scheduler.AccountedElapsed.TotalSeconds:F3}s（各拍间隔之和）");
        Console.WriteLine($"真实墙钟   ：{sw.Elapsed.TotalSeconds:F3}s");
        Console.WriteLine($"计时漂移   ：{driftMs,8:F1}ms（|漂移| < 心跳间隔 {heartbeatMs}ms 即视为无漂移）");
        Console.WriteLine($"空闲数据源 ：{(scheduler.IdleSourceAvailable ? "全程可用" : "出现过失败")}");

        int rc = 0;
        if (Math.Abs(driftMs) >= heartbeatMs)
        {
            Console.WriteLine("!! 漂移超过一个心跳间隔，累计方式可能有误。");
            rc |= 1;
        }
        if (!scheduler.IdleSourceAvailable)
        {
            Console.WriteLine("!! 空闲数据源出现过失败。");
            rc |= 2;
        }
        if (Interlocked.Read(ref idleTicks) == 0)
        {
            // 不是失败：只是本轮没有出现停手期，挂机分支未被真实触发。
            Console.WriteLine("提示：本轮未出现停手期，挂机分支未被真实触发" +
                              "（该分支已由 A 段确定性检查覆盖）。");
        }

        return rc;
    }

    private static string Format(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";

    private static void EnableDpiAwareness()
    {
        try
        {
            ScreenSpy.Interop.NativeMethods.SetProcessDpiAwarenessContext(
                ScreenSpy.Interop.NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch { /* 忽略：本自检不依赖 DPI 感知 */ }
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
        {
            if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(a[prefix.Length..], out int v))
                return v;
        }
        return fallback;
    }
}
