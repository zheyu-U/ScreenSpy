using System;
using System.Runtime.InteropServices;
using System.Threading;
using ScreenSpy.Collector;
using ScreenSpy.Interop;
using ScreenSpy.Scheduling;

namespace ScreenSpy.Demo;

/// <summary>
/// 一次“睡眠测试”的观测数据（M2 自检 C 段的原始证据）。
///
/// 刻意包含**多个相互独立的度量**，因为“睡眠期间是否被误计时”不能用单一指标判断：
///  * <see cref="WallSeconds"/>：墙钟（<c>DateTime.UtcNow</c>）——无论机器是否睡眠都会推进，是“睡眠了多久”的基准；
///  * <see cref="TickDeltaMs"/>：<c>Environment.TickCount64</c> 增量——用于观察它在睡眠期间**是否推进**（这正是本测试要搞清的事实之一）；
///  * <see cref="SequenceDelta"/>：心跳拍数增量——睡眠期间定时器被挂起，应接近 0；
///  * <see cref="GapDelta"/>：时间空洞计数增量——第二道防线是否触发；
///  * <see cref="TodayDeltaSeconds"/>：今日活跃增量——**最关键的一项**，睡眠时长不应被计入；
///  * <see cref="SawSuspend"/>/<see cref="SawResume"/>：第一道防线（电源事件）是否到达。
/// </summary>
internal readonly struct SleepObservation
{
    public SleepObservation(double wallSeconds,
                            double todayDeltaSeconds,
                            long tickDeltaMs,
                            long sequenceDelta,
                            long gapDelta,
                            bool sawSuspend,
                            bool sawResume)
    {
        WallSeconds = wallSeconds;
        TodayDeltaSeconds = todayDeltaSeconds;
        TickDeltaMs = tickDeltaMs;
        SequenceDelta = sequenceDelta;
        GapDelta = gapDelta;
        SawSuspend = sawSuspend;
        SawResume = sawResume;
    }

    public double WallSeconds { get; }
    public double TodayDeltaSeconds { get; }
    public long TickDeltaMs { get; }
    public long SequenceDelta { get; }
    public long GapDelta { get; }
    public bool SawSuspend { get; }
    public bool SawResume { get; }

    /// <summary>是否观察到电源事件（第一道防线）。</summary>
    public bool SawPowerEvents => SawSuspend || SawResume;
}

/// <summary>
/// 睡眠测试的**判定规则**（纯逻辑，无 IO）。
///
/// 与 <see cref="ActivityRules"/> 同样单独成类，是为了让判定本身也能被确定性推演
/// （由 <c>--m2-selfcheck</c> 的确定性检查覆盖），而不是只能靠“真的睡一觉”来验证。
/// </summary>
internal static class SleepAssessment
{
    /// <summary>
    /// 墙钟增量明显小于计划时长时，判定为“实际没有睡够/没睡” → 本轮测试**无效**（不是产品失败）。
    /// 取 80% 作为容差，容纳唤醒与调度开销。
    /// </summary>
    public static bool LooksLikeRealSleep(SleepObservation o, int requestedSeconds)
        => o.WallSeconds >= requestedSeconds * 0.8;

    /// <summary>
    /// 睡眠是否被误计入活跃。容忍度取 <c>max(5s, 墙钟 × 10%)</c>：
    /// 前者容纳“恢复后到停止心跳之间的正常几拍”，后者容纳极长睡眠下的正常尾段。
    /// </summary>
    public static bool OverCredited(SleepObservation o)
        => o.TodayDeltaSeconds > Math.Max(5.0, o.WallSeconds * 0.10);

    /// <summary>
    /// 是否有任一防线留下证据：时间空洞被记录（第二道防线）**或**电源事件到达（第一道防线）。
    ///
    /// 注意：这只说明“防线有动作”。即使两条都无证据，只要没有被误计入，
    /// 也可能仅仅是因为 <c>TickCount64</c> 在睡眠期间不推进（此时根本没有可计入的时长）。
    /// 因此它只作为**提示**，不作为失败判据。
    /// </summary>
    public static bool HasDefenseEvidence(SleepObservation o)
        => o.GapDelta > 0 || o.SawPowerEvents;
}

/// <summary>睡眠测试的执行结果。<see cref="Error"/> 为 0 表示流程正常结束（不等于判定通过）。</summary>
internal readonly struct SleepRunResult
{
    public SleepRunResult(int error, string? failureReason, SleepObservation observation)
    {
        Error = error;
        FailureReason = failureReason;
        Observation = observation;
    }

    /// <summary>0 = 正常执行；非 0 = 未能进入睡眠或执行出错（应视为测试无效）。</summary>
    public int Error { get; }

    public string? FailureReason { get; }

    public SleepObservation Observation { get; }
}

/// <summary>
/// 自动睡眠测试的执行器（M2 自检 C 段，对应 <c>--sleep-test</c>）。
///
/// 安全设计（顺序至关重要）：
///  1. **先设唤醒定时器**，并校验其返回值。定时器设置失败 → **绝不进入睡眠**（否则可能一睡不醒）；
///  2. 该定时器以 <c>fResume = TRUE</c> 注册，到期会**自动唤醒**系统；
///  3. 尽力启用 <c>SE_SHUTDOWN_NAME</c>（<see cref="NativeMethods.SetSuspendState"/> 的前置条件）；
///  4. 调用前把日志**刷盘**，这样即使中途异常终止，睡眠前的证据也已落盘；
///  5. 失败时只报告、不重试；<c>SetSuspendState</c> 返回后等待定时器信号，确认已真正苏醒。
///
/// 诚实说明：若唤醒定时器被系统策略禁用（“允许唤醒定时器”关闭）而 <c>SetWaitableTimer</c> 仍返回成功，
/// 则机器可能不会自动醒来，需要用户按键盘唤醒。该风险无法在本进程内完全排除。
/// </summary>
internal static class SleepTestRunner
{
    public static SleepRunResult Run(int sleepSeconds,
                                     ActivityScheduler scheduler,
                                     SessionPauseBridge bridge,
                                     Action<string> log,
                                     Action? flushLog = null)
    {
        if (sleepSeconds < 5) sleepSeconds = 5;

        bool sawSuspend = false, sawResume = false;
        Action<SessionStateChange> onChanged = c =>
        {
            if (!c.SuspendChanged) return;
            if (c.SuspendedAfter) sawSuspend = true;
            else sawResume = true;
        };
        bridge.StateChanged += onChanged;

        try
        {
            double todayBefore = scheduler.TodayActive.TotalSeconds;
            long tickBefore = Environment.TickCount64;
            long seqBefore = scheduler.Sequence;
            long gapBefore = scheduler.GapTicks;
            DateTime utcBefore = DateTime.UtcNow;

            // 1) 先设唤醒定时器：拿不到“保命绳”就不睡觉。
            IntPtr timer = NativeMethods.CreateWaitableTimer(IntPtr.Zero, true, null);
            if (timer == IntPtr.Zero)
            {
                int e = Marshal.GetLastWin32Error();
                log($"创建唤醒定时器失败（Win32 错误 {e}）→ 已中止，**未**进入睡眠。");
                return new SleepRunResult(1, $"唤醒定时器创建失败（{e}）", default);
            }

            try
            {
                long due = utcBefore.AddSeconds(sleepSeconds).ToFileTimeUtc();
                if (!NativeMethods.SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, fResume: true))
                {
                    int e = Marshal.GetLastWin32Error();
                    log($"设置唤醒定时器失败（Win32 错误 {e}）→ 已中止，**未**进入睡眠。");
                    return new SleepRunResult(1, $"唤醒定时器设置失败（{e}）", default);
                }

                log($"唤醒定时器已就绪：{sleepSeconds}s 后自动唤醒" +
                    $"（绝对时间 {utcBefore.AddSeconds(sleepSeconds):yyyy-MM-dd HH:mm:ss} UTC，fResume=TRUE）。");

                // 3) 权限（失败不阻断，最终由 SetSuspendState 的返回码定论）。
                bool priv = NativeMethods.TryEnableShutdownPrivilege(out string privMsg);
                log($"SE_SHUTDOWN_NAME 权限：{(priv ? "已启用" : "未能启用")}（{privMsg}）");

                log("即将调用 SetSuspendState(FALSE, FALSE, FALSE) 进入睡眠……");
                flushLog?.Invoke();

                // 4) 睡眠。该调用会在系统恢复后才返回。
                bool ok = NativeMethods.SetSuspendState(false, false, false);
                int err = Marshal.GetLastWin32Error();
                if (!ok)
                {
                    string hint = err == NativeMethods.ERROR_PRIVILEGE_NOT_HELD
                        ? "（1314 = 权限不足，请以管理员身份重试）"
                        : "";
                    log($"SetSuspendState 失败（Win32 错误 {err}）{hint} → **未**进入睡眠。");
                    return new SleepRunResult(2, $"SetSuspendState 失败（{err}）{hint}", default);
                }

                log("已从睡眠返回，等待唤醒定时器确认与电源事件到达……");
                uint waited = NativeMethods.WaitForSingleObject(timer, (uint)((sleepSeconds + 120) * 1000));
                if (waited == NativeMethods.WAIT_TIMEOUT)
                    log("唤醒定时器等待超时（异常，但系统已返回）。");

                // 5) Suspend/Resume 事件可能在本进程恢复后才被投递，留一点时间给它。
                Thread.Sleep(3000);

                var obs = new SleepObservation(
                    wallSeconds: (DateTime.UtcNow - utcBefore).TotalSeconds,
                    todayDeltaSeconds: scheduler.TodayActive.TotalSeconds - todayBefore,
                    tickDeltaMs: Environment.TickCount64 - tickBefore,
                    sequenceDelta: scheduler.Sequence - seqBefore,
                    gapDelta: scheduler.GapTicks - gapBefore,
                    sawSuspend: sawSuspend,
                    sawResume: sawResume);

                return new SleepRunResult(0, null, obs);
            }
            finally
            {
                NativeMethods.CloseHandle(timer);
            }
        }
        catch (Exception ex)
        {
            return new SleepRunResult(3, ex.GetType().Name + ": " + ex.Message, default);
        }
        finally
        {
            bridge.StateChanged -= onChanged;
        }
    }
}
