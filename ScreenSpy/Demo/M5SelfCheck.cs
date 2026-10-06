using System;
using System.Text;
using ScreenSpy.AppHost;

namespace ScreenSpy.Demo;

/// <summary>
/// M5b 的控制台自检入口（内嵌入口，**长期保留** —— 它仍是回归验证的主要手段）。
///
/// 用法：ScreenSpy.exe --m5-selfcheck
///
/// 覆盖四类**可以在进程内客观判定**的事实：
///  【图标】程序化生成的图标非空、尺寸正确、可反复生成（句柄管理没问题）；
///  【托盘】真实创建 NotifyIcon 成功；菜单形态正确（含 M8/M9 占位项确实禁用）；
///          「暂停统计」点击真的回调到上层（true → false 往返）；tooltip 文案随暂停原因变化；
///  【降级】快照抛异常时托盘仍能创建、且刷新异常不外泄；
///  【组合根】真实 <see cref="ProductRuntime"/> 的快照必须反映“生效暂停”（含托盘手动暂停）——
///          这条针对的是一个真实存在过、且**不报错的显示口径缺陷**（见 <c>RootPauseChecks</c>）。
///
/// **本自检不覆盖**：通知区域里图标是否真的显示出来（那需要人眼或 UI 自动化），
/// 以及关闭窗口后是否真的继续统计（由实机运行验证，见 docs/M5b-托盘与关闭不退出.md）。
///
/// 注意：运行期间会**短暂出现一个真实托盘图标**（创建后立即释放）。
/// </summary>
internal static class M5SelfCheck
{
    private const string SwitchName = "--m5-selfcheck";

    public static bool IsRequested(string[] args)
    {
        foreach (string a in args)
            if (a.Equals(SwitchName, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static int Run(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 输出被重定向时可能失败，忽略 */ }

        Console.WriteLine("============ ScreenSpy · M5b 托盘与关闭不退出 自检 ============");
        Console.WriteLine();

        int failed = IconChecks();
        failed += TrayChecks();
        failed += DegradeChecks();
        failed += RootPauseChecks();

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "M5b 自检：全部通过。" : $"M5b 自检：{failed} 项失败。");
        return failed == 0 ? 0 : 1;
    }

    private static int IconChecks()
    {
        Console.WriteLine("---- A 图标（程序化生成，不新增二进制资源）----");
        int failed = 0;

        using (var normal = TrayIconFactory.Create(paused: false))
        {
            // 先断言非空再解引用：否则“非空检查”这一项本身就会先抛 NullReferenceException，
            // 把失败伪装成崩溃。
            bool notNull = normal is not null;
            failed += Check("生成「统计中」图标非空", notNull);
            if (normal is not null)
                failed += Check("图标尺寸 32×32", normal.Size.Width == 32 && normal.Size.Height == 32);
        }

        using (var paused = TrayIconFactory.Create(paused: true))
            failed += Check("生成「已暂停」图标非空（两种形态都能画出来）", paused is not null);

        bool repeatable = true;
        try
        {
            for (int i = 0; i < 5; i++)
            {
                using var icon = TrayIconFactory.Create(paused: i % 2 == 0);
            }
        }
        catch
        {
            repeatable = false;
        }
        failed += Check("反复生成 5 次不抛异常（HICON 已销毁，无泄漏/悬空句柄）", repeatable);

        Console.WriteLine($"   小计：{(failed == 0 ? "全部通过" : failed + " 项失败")}");
        return failed;
    }

    private static int TrayChecks()
    {
        Console.WriteLine("---- B 托盘宿主（真实创建 NotifyIcon）----");
        int failed = 0;

        bool pausedState = false;
        int pauseCallbacks = 0;
        int openCallbacks = 0;
        int exitCallbacks = 0;

        TrayIconHost? tray = TrayIconHost.TryCreate(
            snapshot: () => new RuntimeStatus { UserPaused = pausedState, TodayActive = TimeSpan.FromSeconds(1) },
            setUserPaused: value => { pausedState = value; pauseCallbacks++; },
            openMainWindow: () => { openCallbacks++; },
            exit: () => { exitCallbacks++; });

        failed += Check("托盘创建成功（NotifyIcon 在本机可用）", tray is not null);
        if (tray is null)
        {
            Console.WriteLine($"   小计：{failed} 项失败");
            return failed;
        }

        using (tray)
        {
            string[] menu = tray.DescribeMenuForDiagnostics();
            Console.WriteLine("   菜单：" + string.Join(" | ", menu));

            failed += Check("菜单含「打开主界面」", Array.Exists(menu, m => m == "打开主界面"));
            failed += Check("菜单含「暂停统计」（可点）", Array.Exists(menu, m => m == "暂停统计"));
            failed += Check("菜单含「调整组件位置」占位项且**禁用**（M8）",
                Array.Exists(menu, m => m.Contains("调整组件位置") && m.Contains("[禁用]")));
            failed += Check("菜单含「设置」占位项且**禁用**（M8/M9）",
                Array.Exists(menu, m => m.Contains("设置") && m.Contains("[禁用]")));
            failed += Check("菜单含「退出」", Array.Exists(menu, m => m == "退出"));

            failed += Check("未点击时：暂停回调 0 次", pauseCallbacks == 0);

            tray.InvokePauseForDiagnostics();
            failed += Check("点击「暂停统计」→ 上层收到「暂停」= true", pauseCallbacks == 1 && pausedState);
            failed += Check("点击后菜单项变为「继续统计」并显示已勾选",
                Array.Exists(tray.DescribeMenuForDiagnostics(),
                    m => m.Contains("继续统计") && m.Contains("[已勾选]")));

            tray.InvokePauseForDiagnostics();
            failed += Check("再点一次 → 上层收到「继续」= false（可往返）", pauseCallbacks == 2 && !pausedState);

            // 整条菜单的接线都要验：只验「暂停」会留下「打开主界面 / 退出」从不被检验的缺口。
            // 这里走的是 PerformClick（真实 Click 事件），不是直接调回调。
            failed += Check("未点击「打开主界面」时：回调 0 次", openCallbacks == 0);

            tray.InvokeOpenForDiagnostics();
            failed += Check("点击「打开主界面」→ 上层收到打开请求 1 次", openCallbacks == 1);

            failed += Check("未点击「退出」时：回调 0 次", exitCallbacks == 0);

            tray.InvokeExitForDiagnostics();
            failed += Check("点击「退出」→ 上层收到退出请求 1 次", exitCallbacks == 1);
        }

        bool idempotent = true;
        try { tray.Dispose(); } catch { idempotent = false; }
        failed += Check("重复 Dispose 不抛异常（幂等）", idempotent);

        // tooltip 文案：走与托盘刷新**同一个**纯函数，因此这里验的是真实文案规则，
        // 而不是“测试里重新实现一遍”。两个暂停来源必须给出不同的说明。
        failed += Check("tooltip：未暂停 → 不加后缀",
            !TrayIconHost.ComposeTooltip(TimeSpan.FromSeconds(1), paused: false, userPaused: false).Contains("（"));
        failed += Check("tooltip：仅用户手动暂停 → 含「（已暂停）」",
            TrayIconHost.ComposeTooltip(TimeSpan.FromSeconds(1), paused: true, userPaused: true).Contains("（已暂停）"));
        failed += Check("tooltip：仅锁屏/睡眠 → 含「（锁屏/睡眠）」",
            TrayIconHost.ComposeTooltip(TimeSpan.FromSeconds(1), paused: true, userPaused: false).Contains("（锁屏/睡眠）"));

        Console.WriteLine($"   小计：{(failed == 0 ? "全部通过" : failed + " 项失败")}");
        return failed;
    }

    private static int DegradeChecks()
    {
        Console.WriteLine("---- C 降级：托盘刷新异常不得影响统计 ----");
        int failed = 0;

        // 快照每次都抛异常（模拟状态读取失败）：托盘必须仍能创建，且异常不外泄 ——
        // 否则托盘会变成“统计正常但托盘一刷新就崩”的怪状态。
        TrayIconHost? tray = TrayIconHost.TryCreate(
            snapshot: () => throw new InvalidOperationException("模拟状态读取失败"),
            setUserPaused: _ => { },
            openMainWindow: () => { },
            exit: () => { });

        failed += Check("快照抛异常时，托盘仍创建成功且未被异常打断", tray is not null);

        bool disposeOk = true;
        try { tray?.Dispose(); } catch { disposeOk = false; }
        failed += Check("该托盘释放同样不抛异常", disposeOk);

        Console.WriteLine($"   小计：{(failed == 0 ? "全部通过" : failed + " 项失败")}");
        return failed;
    }

    /// <summary>
    /// 组合根口径：<see cref="ProductRuntime.Snapshot"/> 的“生效暂停”必须包含**用户手动暂停**。
    ///
    /// 为什么必须在这里（而不是绕过组合根去拼一个 <see cref="RuntimeStatus"/>）：
    /// 缺陷恰恰在组合根的赋值处 —— 它曾把 <c>status.Paused</c> 赋成 <c>scheduler.Paused</c>
    /// （只含锁屏/睡眠），漏掉了 <c>scheduler.UserPaused</c>。于是托盘点了「暂停统计」后，
    /// 状态面板显示「挂机中」、tooltip 也不显示「已暂停」，而且**全程不报错**。
    /// 若在自检里自己 new 一个 DTO 再填字段，就永远测不到这一行。
    ///
    /// 因此这里启动一个**真实的** <see cref="ProductRuntime"/>，但关掉落库与原始日志
    /// （<c>--no-store --no-raw-log</c>）并关闭周期探测（<c>--probe-ms=0</c>），
    /// 使其不产生任何文件副作用、也不会被会话探测中途改写暂停位，从而结果确定。
    ///
    /// 单实例互斥体已被占用时（产品正在托盘运行）无法启动，此时**跳过而非判失败**：
    /// 那是环境限制，不是缺陷。
    /// </summary>
    private static int RootPauseChecks()
    {
        Console.WriteLine("---- D 组合根暂停口径（真实 ProductRuntime 的 Snapshot）----");
        int failed = 0;

        StartupOptions options = StartupOptions.Parse(
            new[] { "--no-store", "--no-raw-log", "--probe-ms=0" });

        HostStartResult start = ProductRuntime.Start(options);
        if (start.Outcome == HostStartOutcome.AlreadyRunning)
        {
            Console.WriteLine("   [SKIP] 已有 ScreenSpy 实例在运行，无法独占单实例互斥体 —— 跳过。");
            Console.WriteLine("   小计：0 项失败（跳过）");
            return 0;
        }

        bool started = start.Outcome == HostStartOutcome.Started && start.Runtime is not null;
        failed += Check("组合根启动成功（降级开关下）", started);
        if (!started || start.Runtime is null)
        {
            Console.WriteLine($"   小计：{failed} 项失败");
            return failed;
        }

        using (ProductRuntime runtime = start.Runtime)
        {
            failed += Check("初始（会话未暂停、用户未暂停）→ 快照 Paused=false",
                !runtime.Snapshot().Paused);

            // 与托盘「暂停统计」完全同一写入路径：ProductRuntime.UserPaused → scheduler.UserPaused
            runtime.UserPaused = true;
            RuntimeStatus userOnly = runtime.Snapshot();
            failed += Check("仅用户手动暂停 → 快照 Paused=true（本次修复的回归点）", userOnly.Paused);
            failed += Check("仅用户手动暂停 → 快照 UserPaused=true（原因仍可区分）", userOnly.UserPaused);

            runtime.UserPaused = false;
            failed += Check("取消用户暂停 → 快照 Paused=false（可往返）", !runtime.Snapshot().Paused);
        }

        Console.WriteLine($"   小计：{(failed == 0 ? "全部通过" : failed + " 项失败")}");
        return failed;
    }

    private static int Check(string label, bool ok)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        return ok ? 0 : 1;
    }
}
