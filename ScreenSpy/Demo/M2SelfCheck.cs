using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ScreenSpy.Collector;
using ScreenSpy.Diagnostics;
using ScreenSpy.Interop;
using ScreenSpy.Scheduling;

namespace ScreenSpy.Demo;

/// <summary>
/// M2 的控制台自检入口（内嵌入口，**长期保留** —— 它仍是回归验证的主要手段）。
///
/// 用法：
///   ScreenSpy.exe --m2-selfcheck [--seconds=20] [--logic-only] [--probe-interval=5000]
///                                [--lock-test] [--sleep-test] [--sleep-seconds=20] [--log=&lt;path&gt;]
///
/// 分三段：
///  【A】确定性检查 —— 注入假信号源 / 假探针 / 假时钟，覆盖**人工难以复现**的场景：
///       锁屏、睡眠、二者重叠（睡眠中解锁 / 锁屏中恢复）、探测自愈、时间空洞、
///       WTS 头部自定位（含“会话号为 1”的错位陷阱回归）、睡眠测试的判定规则。
///  【B】真实观察 —— 真实 WTS 探测 + 真实 SystemEvents 订阅，观察 N 秒，
///       期间可自行按 Win+L 锁屏 / 解锁来验证完整链路。
///       加 <c>--lock-test</c> 则由程序调用 <c>LockWorkStation()</c> 自动发起锁屏
///       （**会真的锁屏，解锁需你本人输入凭据**；因此默认关闭，建议配合 <c>--seconds=120</c>）。
///  【C】睡眠测试（<c>--sleep-test</c>）—— 程序**真实**让系统睡眠，并靠一个以
///       <c>fResume=TRUE</c> 注册的唤醒定时器自动唤醒，随后用“睡眠期间今日活跃增量”做客观判定。
///       安全前提：**唤醒定时器注册失败就绝不入睡**。默认关闭；建议先设短时长（如 <c>--sleep-seconds=20</c>）。
///
/// 另有一处针对本项目的特殊处理：产品是 <c>WinExe</c>（GUI 子系统），
/// 从交互式控制台直接运行时标准输出可能不可见。因此本自检把**全部输出同时写入日志文件**，
/// 控制台看不到时直接看文件即可（见末尾打印的路径）。
/// </summary>
internal static class M2SelfCheck
{
    private const string SwitchName = "--m2-selfcheck";

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
        bool lockTest = HasFlag(args, "--lock-test");
        bool sleepTest = HasFlag(args, "--sleep-test");
        int seconds = Math.Max(5, GetInt(args, "--seconds=", 20));
        int probeMs = GetInt(args, "--probe-interval=", 5000);
        int sleepSeconds = Math.Max(5, GetInt(args, "--sleep-seconds=", 20));
        _logPath = GetString(args, "--log=");

        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 输出被重定向时可能失败，忽略 */ }

        // 把标准输出镜像进缓冲区：既能让控制台正常显示，也能在任何情况下留下文件证据。
        TextWriter original = Console.Out;
        try { Console.SetOut(new TeeWriter(original, LogBuffer)); } catch { /* 忽略 */ }

        int failures;
        try
        {
            Console.WriteLine("============ ScreenSpy · M2 锁屏/睡眠监听 自检 ============");
            Console.WriteLine($"真实观察 {seconds}s，周期探测 {probeMs}ms，仅确定性检查={logicOnly}");
            Console.WriteLine();

            failures = RunDeterministicChecks();
            Console.WriteLine();

            if (logicOnly)
            {
                Console.WriteLine(failures == 0 ? "确定性检查：全部通过。" : $"确定性检查：{failures} 项失败。");
            }
            else
            {
                failures += RunRealObservation(seconds, probeMs, lockTest);
                if (sleepTest)
                {
                    Console.WriteLine();
                    failures += RunSleepTest(sleepSeconds);
                }
                Console.WriteLine();
                Console.WriteLine(failures == 0 ? "M2 自检：全部通过。" : $"M2 自检：{failures} 项失败。");
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
        Console.WriteLine("---- A 确定性检查（假信号源 + 假探针 + 假时钟；无需真的锁屏或睡眠）----");
        int failed = 0;

        var threshold = TimeSpan.FromSeconds(300);
        DateTime now = new(2026, 10, 5, 12, 0, 0);

        var signals = new FakeSignalSource();
        var probe = new FakeProbe();
        var clock = new FakeIdleClock { Idle = TimeSpan.FromSeconds(1) };
        var ticks = new FakeTickSource();

        using var scheduler = new ActivityScheduler(clock, threshold, TimeSpan.FromSeconds(1),
                                                    tickSource: ticks, localNow: () => now);
        using var bridge = new SessionPauseBridge(scheduler, signals, probe, probeInterval: null);

        int logLines = 0;
        bridge.Log += _ => logLines++;

        // 1) 初始状态与“不猜测”原则
        failed += Check("初始：未锁屏、未睡眠 → 不暂停", !bridge.State.ShouldPause && !scheduler.Paused);
        failed += Check("初始：探测结果 Unknown 时来源仍为 None（未做任何改动）",
            bridge.LastSource == PauseSource.None && bridge.LastProbeResult == SessionLockProbeResult.Unknown);

        // 2) 锁屏 / 解锁（含重复事件去重）
        int t0 = bridge.PauseTransitions;
        signals.Emit(SessionSignal.Locked);
        failed += Check("锁屏事件 → 需要暂停，且调度器 Paused 已置位",
            bridge.State.ShouldPause && scheduler.Paused);
        failed += Check("锁屏事件：来源=Event", bridge.LastSource == PauseSource.Event);
        failed += Check("锁屏事件：暂停切换计数 +1", bridge.PauseTransitions == t0 + 1);

        signals.Emit(SessionSignal.Locked);
        failed += Check("重复锁屏事件 → 暂停切换计数不再增加（去重）", bridge.PauseTransitions == t0 + 1);

        signals.Emit(SessionSignal.Unlocked);
        failed += Check("解锁事件 → 恢复计时（Paused=false）", !bridge.State.ShouldPause && !scheduler.Paused);

        // 3) 睡眠 / 恢复
        signals.Emit(SessionSignal.Suspended);
        failed += Check("睡眠事件 → 需要暂停", bridge.State.ShouldPause && scheduler.Paused);

        // 4) 重叠场景（关键：两个标志位必须独立）
        int t1 = bridge.PauseTransitions;
        signals.Emit(SessionSignal.Unlocked);
        failed += Check("重叠：睡眠期间收到解锁 → 仍须暂停（不能因此恢复计时）",
            bridge.State.ShouldPause && scheduler.Paused);
        failed += Check("重叠：暂停结论未变 → 切换计数不增加", bridge.PauseTransitions == t1);

        signals.Emit(SessionSignal.Resumed);
        failed += Check("恢复事件（此时已解锁）→ 结束暂停", !bridge.State.ShouldPause && !scheduler.Paused);

        signals.Emit(SessionSignal.Locked);
        signals.Emit(SessionSignal.Resumed);
        failed += Check("重叠：锁屏期间收到恢复 → 仍须暂停（不能因此恢复计时）",
            bridge.State.ShouldPause && scheduler.Paused);

        signals.Emit(SessionSignal.Unlocked);
        failed += Check("其后解锁 → 结束暂停", !bridge.State.ShouldPause);

        failed += Check("日志：状态变化已产生可读输出", logLines > 0);

        // 5) 计时正确性：暂停期间一律不累计
        clock.Idle = TimeSpan.FromSeconds(1);
        ticks.Milliseconds += 1000; scheduler.Sample();
        ticks.Milliseconds += 1000; scheduler.Sample();
        failed += Check("未暂停：2 拍 → 今日 2.000s", Near(scheduler.TodayActive, 2000));

        signals.Emit(SessionSignal.Locked);
        ticks.Milliseconds += 1000; scheduler.Sample();
        ticks.Milliseconds += 1000; scheduler.Sample();
        ticks.Milliseconds += 1000; ActivityTick pausedTick = scheduler.Sample();
        failed += Check("锁屏：3 拍全部不计入 → 今日仍为 2.000s", Near(scheduler.TodayActive, 2000));
        failed += Check("锁屏：该拍 IsPaused=true、IsActive=false", pausedTick.IsPaused && !pausedTick.IsActive);

        signals.Emit(SessionSignal.Unlocked);   // 锁屏解除
        signals.Emit(SessionSignal.Suspended);  // 但立刻进入睡眠
        ticks.Milliseconds += 1000; scheduler.Sample();
        ticks.Milliseconds += 1000; ActivityTick sleepTick = scheduler.Sample();
        failed += Check("睡眠（已解锁）：2 拍仍不计入 → 今日仍为 2.000s", Near(scheduler.TodayActive, 2000));
        failed += Check("睡眠：该拍 IsPaused=true", sleepTick.IsPaused);

        signals.Emit(SessionSignal.Resumed);
        ticks.Milliseconds += 1000; scheduler.Sample();
        failed += Check("恢复：1 拍 → 今日 3.000s（此前 5 拍暂停全部未计入）", Near(scheduler.TodayActive, 3000));

        // 6) 探测自愈：补上漏报的锁屏、纠正漏报的解锁、不确定时不动
        signals.Emit(SessionSignal.Unlocked);
        signals.Emit(SessionSignal.Resumed);
        failed += Check("自愈前置：已回到不暂停", !bridge.State.ShouldPause);

        probe.Result = SessionLockProbeResult.Locked;
        SessionStateChange heal1 = bridge.SyncFromProbe();
        failed += Check("自愈：探测=已锁定 → 补上锁屏并暂停（覆盖漏报的锁屏事件）",
            bridge.State.ShouldPause && scheduler.Paused);
        failed += Check("自愈：来源=Probe", heal1.Source == PauseSource.Probe);

        probe.Result = SessionLockProbeResult.Unlocked;
        bridge.SyncFromProbe();
        failed += Check("自愈：探测=未锁定 → 清除卡住的锁屏标志（避免永久少计，这是最危险的方向）",
            !bridge.State.ShouldPause && !scheduler.Paused);

        probe.Result = SessionLockProbeResult.Locked;
        bridge.SyncFromProbe();
        int t2 = bridge.PauseTransitions;
        probe.Result = SessionLockProbeResult.Unknown;
        SessionStateChange heal3 = bridge.SyncFromProbe();
        failed += Check("自愈：探测=不确定 → 不作任何改动（不猜测）",
            !heal3.Changed && bridge.State.ShouldPause && bridge.PauseTransitions == t2);

        signals.Emit(SessionSignal.Unlocked);
        failed += Check("自愈后：事件仍能正常解除暂停", !bridge.State.ShouldPause);

        // 7) 启动初值：进程启动时工作站已锁定（事件不会补发）
        var bootSignals = new FakeSignalSource();
        var bootProbe = new FakeProbe { Result = SessionLockProbeResult.Locked };
        using var bootScheduler = new ActivityScheduler(new FakeIdleClock { Idle = TimeSpan.FromSeconds(1) },
                                                        threshold, TimeSpan.FromSeconds(1),
                                                        tickSource: new FakeTickSource(),
                                                        localNow: () => now);
        using var bootBridge = new SessionPauseBridge(bootScheduler, bootSignals, bootProbe, probeInterval: null);
        failed += Check("启动初值：启动即已锁屏 → 直接进入暂停（事件不会补发）",
            bootBridge.State.ShouldPause && bootScheduler.Paused);
        failed += Check("启动初值：来源=Probe", bootBridge.LastSource == PauseSource.Probe);

        // 8) 时间空洞（第二道防线）：不用任何事件，仅凭巨大间隔就排除睡眠
        var gapTicks = new FakeTickSource();
        var gapClock = new FakeIdleClock { Idle = TimeSpan.FromSeconds(1) };   // 空闲极小，理应判为活跃
        using var gapScheduler = new ActivityScheduler(gapClock, threshold, TimeSpan.FromSeconds(1),
                                                       tickSource: gapTicks, localNow: () => now);

        gapTicks.Milliseconds += 1000; gapScheduler.Sample();
        failed += Check("空洞前置：正常 1 拍 → 今日 1.000s", Near(gapScheduler.TodayActive, 1000));

        gapTicks.Milliseconds += 2 * 60 * 60 * 1000;   // 模拟睡眠 2 小时
        ActivityTick gapTick = gapScheduler.Sample();
        failed += Check("空洞：2 小时间隔（尽管空闲很小）→ 判为时间空洞", gapTick.IsGap);
        failed += Check("空洞：该拍不计入 → 今日仍为 1.000s", Near(gapScheduler.TodayActive, 1000));
        failed += Check("空洞：GapTicks 计数 = 1", gapScheduler.GapTicks == 1);

        gapTicks.Milliseconds += 1000; gapScheduler.Sample();
        failed += Check("空洞后恢复正常：1 拍 → 今日 2.000s", Near(gapScheduler.TodayActive, 2000));

        gapTicks.Milliseconds += 60 * 1000;   // 恰好等于默认上限 60s
        ActivityTick edgeTick = gapScheduler.Sample();
        failed += Check("空洞边界：恰好等于上限 60s → 仍计入（只在“超过”时判为空洞）", !edgeTick.IsGap);

        // 9) WTS 头部自定位（纯函数）：覆盖结构体对齐差异与“会话号=1”的错位陷阱
        byte[] realLayout = BuildInfoEx(withPadding: true, sessionId: 2, sessionState: 0, sessionFlags: 1);
        bool okReal = Win32SessionStateProbe.TryLocateHeader(realLayout, 2, 0,
            out int offReal, out int stateReal, out int flagsReal);
        failed += Check("自定位：真实布局（Level 后有 4 字节填充）→ 命中偏移 8、SessionFlags=1",
            okReal && offReal == 8 && flagsReal == 1);
        failed += Check("分类：SessionState=0(Active) + SessionFlags=1 → 未锁定",
            Win32SessionStateProbe.Classify(stateReal, flagsReal) == SessionLockProbeResult.Unlocked);

        byte[] hazard = BuildInfoEx(withPadding: true, sessionId: 1, sessionState: 0, sessionFlags: 1);
        bool okHazard = Win32SessionStateProbe.TryLocateHeader(hazard, 1, 0,
            out int offHazard, out _, out int flagsHazard);
        failed += Check("回归：会话号=1（与 Level 同为 1）→ 仍命中偏移 8 而非错位的 0，不误判",
            okHazard && offHazard == 8 && flagsHazard == 1);
        failed += Check("回归：会话号=1 且已锁定时，不会因错位读出“未锁定”",
            Win32SessionStateProbe.Classify(0,
                LocateFlags(BuildInfoEx(true, 1, 0, 0), 1)) == SessionLockProbeResult.Locked);

        byte[] noPad = BuildInfoEx(withPadding: false, sessionId: 1, sessionState: 0, sessionFlags: 0);
        bool okNoPad = Win32SessionStateProbe.TryLocateHeader(noPad, 1, 0,
            out int offNoPad, out _, out int flagsNoPad);
        failed += Check("自定位：无填充布局 → 命中偏移 4，SessionFlags=0 → 已锁定",
            okNoPad && offNoPad == 4 && flagsNoPad == 0 &&
            Win32SessionStateProbe.Classify(0, flagsNoPad) == SessionLockProbeResult.Locked);

        failed += Check("自定位：SessionFlags 非法（7）→ 拒绝命中（不猜测）",
            !Win32SessionStateProbe.TryLocateHeader(
                BuildInfoEx(true, 2, 0, 7), 2, 0, out _, out _, out _));
        failed += Check("自定位：交叉校验不符（期望连接状态=4）→ 拒绝命中",
            !Win32SessionStateProbe.TryLocateHeader(
                BuildInfoEx(true, 2, 0, 1), 2, 4, out _, out _, out _));
        failed += Check("分类：非活动会话（SessionState=4）→ 不确定，绝不覆盖事件状态",
            Win32SessionStateProbe.Classify(4, 1) == SessionLockProbeResult.Unknown);

        // 10) 睡眠测试的判定规则（纯逻辑）：先确保“睡没睡 / 是否被误计 / 有无防线证据”的尺子本身正确，
        //     否则真要睡一觉时也读不懂结果。
        failed += Check("睡眠判定：墙钟 20s（计划 20s）→ 视为真实睡眠",
            SleepAssessment.LooksLikeRealSleep(Obs(wall: 20.0, today: 0.9, gap: 1), 20));
        failed += Check("睡眠判定：墙钟 0.3s → 未真正睡眠（本轮结论无效）",
            !SleepAssessment.LooksLikeRealSleep(Obs(wall: 0.3), 20));
        failed += Check("睡眠判定：边界（16s = 计划 20s 的 80%）→ 仍视为真实睡眠",
            SleepAssessment.LooksLikeRealSleep(Obs(wall: 16.0), 20));

        failed += Check("误计判定：睡眠 20s 仅计入 0.9s → 未误计（这是期望结果）",
            !SleepAssessment.OverCredited(Obs(wall: 20.0, today: 0.9, gap: 1)));
        failed += Check("误计判定：睡眠 20s 计入 19.5s → 判定为误计（防护失效）",
            SleepAssessment.OverCredited(Obs(wall: 20.0, today: 19.5)));
        failed += Check("误计判定：边界（恰好 5.0s）→ 未误计（容忍度取 max(5s, 墙钟×10%)）",
            !SleepAssessment.OverCredited(Obs(wall: 20.0, today: 5.0, gap: 1)));

        failed += Check("防线证据：仅有时间空洞（电源事件缺失）→ 有证据",
            SleepAssessment.HasDefenseEvidence(Obs(wall: 20.0, gap: 1)));
        failed += Check("防线证据：仅有电源事件（空洞为 0）→ 有证据",
            SleepAssessment.HasDefenseEvidence(Obs(wall: 20.0, sawResume: true)));
        failed += Check("防线证据：两者皆无 → 无证据（仅提示，不判失败）",
            !SleepAssessment.HasDefenseEvidence(Obs(wall: 20.0)));

        Console.WriteLine($"   小计：{(failed == 0 ? "全部通过" : failed + " 项失败")}");
        return failed;
    }

    /// <summary>构造观测数据的简写（只填关心的字段）。</summary>
    private static SleepObservation Obs(double wall, double today = 0.0, long gap = 0,
                                        bool sawSuspend = false, bool sawResume = false)
        => new(wall, today, tickDeltaMs: (long)(wall * 1000), sequenceDelta: 0,
               gapDelta: gap, sawSuspend: sawSuspend, sawResume: sawResume);

    /// <summary>从合成缓冲区里取 SessionFlags（仅用于回归断言）。</summary>
    private static int LocateFlags(byte[] buffer, uint sessionId)
        => Win32SessionStateProbe.TryLocateHeader(buffer, sessionId, 0, out _, out _, out int flags)
            ? flags
            : int.MinValue;

    /// <summary>
    /// 合成一个 WTSINFOEX 缓冲区。<paramref name="withPadding"/>=true 表示复现本机实测的布局
    /// （<c>Level</c> 在 0，4 字节填充，<c>SessionId</c> 在 8）；false 表示无填充（SessionId 在 4）。
    /// </summary>
    private static byte[] BuildInfoEx(bool withPadding, uint sessionId, int sessionState, int sessionFlags)
    {
        var buffer = new byte[64];
        BitConverter.GetBytes(1).CopyTo(buffer, 0);                 // Level = 1
        int off = withPadding ? 8 : 4;
        BitConverter.GetBytes(sessionId).CopyTo(buffer, off);
        BitConverter.GetBytes(sessionState).CopyTo(buffer, off + 4);
        BitConverter.GetBytes(sessionFlags).CopyTo(buffer, off + 8);
        return buffer;
    }

    // ================================================================ B 真实观察

    private static int RunRealObservation(int seconds, int probeMs, bool lockTest)
    {
        Console.WriteLine("---- B 真实观察（真实 WTS 探测 + 真实 SystemEvents 订阅）----");

        var probe = new Win32SessionStateProbe();
        SessionLockProbeResult real = probe.ProbeLocked();
        Console.WriteLine($"WTS 探测   ：{Describe(real)}");
        Console.WriteLine($"探测诊断   ：{probe.LastDiagnostic}");

        var signals = new SystemSessionSignalSource();
        Console.WriteLine($"事件订阅   ：{(signals.IsHooked
            ? "成功（SessionSwitch + PowerModeChanged）"
            : "失败：" + signals.HookError)}");

        var clock = new Win32IdleClock();
        Console.WriteLine($"空闲数据源 ：{(clock.IsAvailable ? "可用" : "不可用")}（当前空闲 {clock.IdleTime.TotalSeconds:F1}s）");

        EnvironmentInfo.Print();

        using var scheduler = new ActivityScheduler(clock);
        using var bridge = new SessionPauseBridge(scheduler, signals, probe,
            probeMs > 0 ? TimeSpan.FromMilliseconds(probeMs) : null);

        int events = 0;
        bridge.Log += msg =>
        {
            Interlocked.Increment(ref events);
            Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] {msg}");
        };

        // 用于量化“锁屏期间是否真的停止计时”（客观指标，不依赖肉眼）。
        bool sawLocked = false, sawUnlocked = false;
        TimeSpan todayAtLock = TimeSpan.Zero, todayAtUnlock = TimeSpan.Zero;
        bridge.StateChanged += c =>
        {
            if (!c.LockChanged) return;
            if (c.LockedAfter) { sawLocked = true; todayAtLock = scheduler.TodayActive; }
            else { sawUnlocked = true; todayAtUnlock = scheduler.TodayActive; }
        };

        Console.WriteLine();
        if (lockTest)
        {
            Console.WriteLine($"观察 {seconds}s：将在约 3s 后自动调用 LockWorkStation() 发起锁屏；");
            Console.WriteLine("锁屏后请自行输入凭据解锁 —— 程序会等到“锁屏→解锁”闭环完成或观察窗口结束。");
        }
        else
        {
            Console.WriteLine($"观察 {seconds}s：期间可随时按 Win+L 锁屏再解锁（或合盖睡眠），");
            Console.WriteLine("观察事件是否到达、暂停是否生效、锁屏期间“今日活跃”是否停止增长。");
            Console.WriteLine("（如希望程序自动发起锁屏，可加 --lock-test，它会真的锁屏。）");
        }
        Console.WriteLine();

        bool lockIssued = false;
        var sw = Stopwatch.StartNew();
        scheduler.Start();
        int lastBucket = -1;

        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (lockTest && !lockIssued && sw.Elapsed >= TimeSpan.FromSeconds(3))
            {
                lockIssued = true;
                bool ok = NativeMethods.LockWorkStation();
                Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] 已调用 LockWorkStation() → " +
                                  (ok ? "成功（请稍后自行解锁）" : $"失败，Win32 错误 {Marshal.GetLastWin32Error()}"));
            }

            // 闭环完成（锁屏与解锁事件都观察到）即提前结束，不必等满观察窗口。
            if (lockIssued && sawLocked && sawUnlocked) break;

            Thread.Sleep(200);

            int sec = (int)sw.Elapsed.TotalSeconds;
            int bucket = sec / 5;
            if (bucket == lastBucket) continue;
            lastBucket = bucket;

            SessionStateTracker st = bridge.State;
            Console.WriteLine(
                $"  [t={sec,3}s] 锁屏={Yn(st.Locked)} 睡眠={Yn(st.Suspended)} 暂停={Yn(st.ShouldPause)} " +
                $"今日={Format(scheduler.TodayActive)} 心跳={scheduler.Sequence} 空洞={scheduler.GapTicks} " +
                $"最近探测={Describe(bridge.LastProbeResult)}");
        }

        long sequenceBefore = scheduler.Sequence;
        scheduler.Stop();
        sw.Stop();

        Console.WriteLine();
        Console.WriteLine("---- 汇总 ----");
        Console.WriteLine($"事件条数   ：{events}（0 表示本轮未发生锁屏/睡眠事件；锁屏再解锁应至少 2 条）");
        Console.WriteLine($"暂停切换   ：{bridge.PauseTransitions} 次");
        Console.WriteLine($"事件订阅   ：{(bridge.IsHooked ? "正常" : "失败：" + bridge.HookError)}");
        Console.WriteLine($"周期探测   ：{bridge.ProbeIntervalText}");
        Console.WriteLine($"心跳次数   ：{sequenceBefore}");
        Console.WriteLine($"今日活跃   ：{Format(scheduler.TodayActive)}");
        Console.WriteLine($"时间空洞   ：{scheduler.GapTicks} 拍（>0 说明期间发生过睡眠 / 长时间挂起）");
        Console.WriteLine($"最近探测   ：{Describe(bridge.LastProbeResult)}");

        if (lockTest)
        {
            Console.WriteLine();
            Console.WriteLine("---- 真实锁屏链路 ----");
            Console.WriteLine($"已发起锁屏 ：{Yn(lockIssued)}");
            Console.WriteLine($"观察到锁定 ：{Yn(sawLocked)}");
            Console.WriteLine($"观察到解锁 ：{Yn(sawUnlocked)}");
            if (sawLocked && sawUnlocked)
            {
                TimeSpan delta = todayAtUnlock - todayAtLock;
                Console.WriteLine($"锁屏期间今日活跃增量：{delta.TotalSeconds:F1}s（应约为 0，即锁屏期间未计时）");
            }
        }

        int rc = 0;
        if (real == SessionLockProbeResult.Unknown)
        {
            Console.WriteLine("!! 真实探测不可用：锁屏检测将只剩事件与时间空洞两道防线，且失去自愈能力。");
            rc |= 1;
        }
        if (!bridge.IsHooked)
        {
            Console.WriteLine("!! 会话/电源事件订阅失败：请检查 SystemEvents 是否可用。");
            rc |= 2;
        }
        if (lockTest && !sawLocked)
        {
            Console.WriteLine("!! 未观察到锁屏事件 —— 锁屏链路未打通。");
            rc |= 4;
        }
        if (lockTest && sawLocked && !sawUnlocked)
        {
            Console.WriteLine("!! 未观察到解锁事件（观察窗口可能太短；建议 --seconds=120）。");
            rc |= 8;
        }
        if (events == 0)
        {
            // 不是失败：只是本轮没有锁屏/睡眠。
            Console.WriteLine("提示：本轮未收到任何锁屏/睡眠事件（如需验证真实链路，可在观察期内按 Win+L，" +
                              "或加 --lock-test 让程序自动发起锁屏）。");
        }

        return rc;
    }

    // ================================================================ C 睡眠测试

    private static int RunSleepTest(int sleepSeconds)
    {
        Console.WriteLine("---- C 睡眠测试（真实进入睡眠；唤醒定时器保证自动唤醒）----");
        Console.WriteLine($"  计划睡眠 {sleepSeconds}s。程序会**先**注册 {sleepSeconds}s 的唤醒定时器（fResume=TRUE），");
        Console.WriteLine("  只有注册成功才会真正入睡；注册失败则立即中止，不会睡眠。");
        Console.WriteLine("  ⚠ 本段会真实挂起本机（VS、下载、播放都会暂停）。若系统策略禁用了唤醒定时器，");
        Console.WriteLine("    可能需要你按一下键盘才能唤醒。");
        Console.WriteLine();

        var probe = new Win32SessionStateProbe();
        var signals = new SystemSessionSignalSource();
        var clock = new Win32IdleClock();

        using var scheduler = new ActivityScheduler(clock);
        using var bridge = new SessionPauseBridge(scheduler, signals, probe, TimeSpan.FromSeconds(5));

        bridge.Log += msg => Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] {msg}");
        scheduler.Start();

        Console.WriteLine($"  基线：今日活跃 {Format(scheduler.TodayActive)}，" +
                          $"心跳 {scheduler.Sequence}，空洞 {scheduler.GapTicks}");
        Console.WriteLine();

        // FlushLogFile 在入睡前把已有输出落盘：万一睡眠期间异常终止，证据仍在文件里。
        SleepRunResult result = SleepTestRunner.Run(sleepSeconds, scheduler, bridge,
            msg => Console.WriteLine("  " + msg), FlushLogFile);

        scheduler.Stop();

        Console.WriteLine();
        Console.WriteLine("---- 睡眠测试结果 ----");

        if (result.Error != 0)
        {
            Console.WriteLine($"执行失败：{result.FailureReason}");
            Console.WriteLine("!! 本轮未取得有效数据（测试无效，并非产品缺陷）。");
            return 64;
        }

        SleepObservation o = result.Observation;
        Console.WriteLine($"墙钟时长     ：{o.WallSeconds:F1}s（计划 {sleepSeconds}s）");
        Console.WriteLine($"今日活跃增量 ：{o.TodayDeltaSeconds:F1}s   ← 关键指标，应远小于墙钟时长");
        Console.WriteLine($"TickCount64  ：{o.TickDeltaMs} ms  ← 用于观察该计时器在睡眠期间是否推进");
        Console.WriteLine($"心跳增量     ：{o.SequenceDelta} 拍  ← 睡眠期间定时器被挂起，应接近 0");
        Console.WriteLine($"时间空洞增量 ：{o.GapDelta} 拍  ← 第二道防线（与事件无关）");
        Console.WriteLine($"电源事件     ：Suspend={Yn(o.SawSuspend)} Resume={Yn(o.SawResume)}  ← 第一道防线");
        Console.WriteLine();

        int rc = 0;

        if (!SleepAssessment.LooksLikeRealSleep(o, sleepSeconds))
        {
            Console.WriteLine($"!! 墙钟增量仅 {o.WallSeconds:F1}s，未达计划的 80% —— 机器可能并未真正睡眠，" +
                              "本轮结论不可用。");
            rc |= 16;
        }

        if (SleepAssessment.OverCredited(o))
        {
            Console.WriteLine($"!! 睡眠被计入活跃：增量 {o.TodayDeltaSeconds:F1}s 超过容差 —— 防护失效。");
            rc |= 32;
        }

        if (!SleepAssessment.HasDefenseEvidence(o))
        {
            Console.WriteLine("提示：既无时间空洞、也无电源事件 —— 可能是 TickCount64 在睡眠期间不推进");
            Console.WriteLine("      （此时本就没有可计入的时长，故上面的“未被计入活跃”依然成立）。");
        }

        if (rc == 0)
            Console.WriteLine("睡眠测试：通过 —— 睡眠期间未被计入活跃。");

        return rc;
    }

    // ================================================================ 辅助

    private static int Check(string label, bool ok)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        return ok ? 0 : 1;
    }

    private static bool Near(TimeSpan actual, double expectedMs, double toleranceMs = 1.0)
        => Math.Abs(actual.TotalMilliseconds - expectedMs) <= toleranceMs;

    private static string Describe(SessionLockProbeResult r) => r switch
    {
        SessionLockProbeResult.Locked => "已锁定",
        SessionLockProbeResult.Unlocked => "未锁定",
        _ => "不确定",
    };

    private static string Yn(bool b) => b ? "是" : "否";

    private static string Format(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";

    private static void WriteLogFile()
    {
        string path = _logPath ?? Path.Combine("artifacts", "m2", "m2-selfcheck.log");

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

    /// <summary>
    /// 把当前缓冲区落盘（供睡眠测试在**入睡前**留证：万一睡眠期间异常终止，之前的输出仍在文件里）。
    /// 失败不抛异常，也不中断测试 —— 控制台输出仍然有效。
    /// </summary>
    private static void FlushLogFile()
    {
        string path = _logPath ?? Path.Combine("artifacts", "m2", "m2-selfcheck.log");

        try
        {
            string full = Path.GetFullPath(path);
            string? dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(full, LogBuffer.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 留证失败不应阻断睡眠测试。
        }
    }

    private static void AppendConsole(string line)
    {
        try { Console.WriteLine(line); } catch { }
        try { Console.Error.WriteLine(line); } catch { }
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

    private sealed class FakeProbe : ISessionStateProbe
    {
        public SessionLockProbeResult Result { get; set; } = SessionLockProbeResult.Unknown;
        public SessionLockProbeResult ProbeLocked() => Result;
    }

    private sealed class FakeSignalSource : ISessionSignalSource
    {
        public event Action<SessionSignal, string>? Signal;
        public bool IsHooked => true;
        public string? HookError => null;

        public void Emit(SessionSignal signal) => Signal?.Invoke(signal, "手动注入 " + signal);

        public void Dispose() { }
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
}
