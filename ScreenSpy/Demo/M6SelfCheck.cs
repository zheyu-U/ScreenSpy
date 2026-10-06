using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using ScreenSpy.AppHost;
using ScreenSpy.Collector;
using ScreenSpy.Interop;
using ScreenSpy.Rendering;
using ScreenSpy.Widget;

namespace ScreenSpy.Demo;

/// <summary>
/// M6 的控制台自检入口（内嵌入口，**长期保留**）。
///
/// 用法：ScreenSpy.exe --m6-selfcheck
///
/// 覆盖四类**可以在进程内客观判定**的事实：
///  【数据】快照 → <see cref="CardModel"/> 的映射：**“今日”必须是今天一整天**（本次运行 + 库中基线）、
///         限额整块隐藏、行数上限、占比夹紧、时长格式化边界；
///  【窗口】真实创建 <see cref="CardWindow"/> 并验证：扩展样式含 LAYERED 但**不含** TRANSPARENT/NOACTIVATE
///         （这正是 M6“可交互”与 M7“鼠标穿透”的分界）、<c>WM_NCHITTEST</c> 返回 <c>HTCAPTION</c>（可拖动）、
///         默认坐标在屏内、显示/隐藏往返、重绘持续推进、Dispose 后窗口真的销毁；
///  【托盘】真实 <see cref="TrayIconHost"/> 的卡片开关：菜单文案与勾选**回读真实可见状态**、
///         点击真的回调到上层且可往返、不接线卡片时菜单形态与 M5b 完全一致；
///  【主界面】榜单末尾那条「不计入统计」行：时长用“今天一整天”口径（重启后**不许**掉）、
///         只含非软件活跃（**不含**锁屏剔除）、**占比列留空**且行名仍与软件行同列对齐、为 0 时整行不插。
///
/// **本自检不覆盖**（需要人眼）：
///  * 卡片看起来是否好看、半透明是否正确；
///  * 用鼠标真的拖一下窗口（自检只能验“命中测试对”，不能验“手感”）；
///  * 榜单末行在真实窗口里的排版观感（自检只能验文本口径，验不了“看起来对不对”）。
///
/// 注意：运行期间会**短暂出现一张真实卡片窗口**（创建后立即销毁）。
/// </summary>
internal static class M6SelfCheck
{
    private const string SwitchName = "--m6-selfcheck";

    public static bool IsRequested(string[] args)
    {
        foreach (string a in args)
            if (a.Equals(SwitchName, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static int Run(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 输出被重定向时可能失败，忽略 */ }

        Console.WriteLine("============ ScreenSpy · M6 组件卡片 UI 自检 ============");
        Console.WriteLine();

        int failed = DataChecks();
        failed += WindowChecks();
        failed += TrayCardChecks();
        failed += MainWindowChecks();

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "M6 自检：全部通过。" : $"M6 自检：{failed} 项失败。");
        return failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ A 数据

    private static int DataChecks()
    {
        Console.WriteLine("---- A 数据映射（RuntimeStatus → CardModel，纯逻辑）----");
        int failed = 0;

        var status = new RuntimeStatus
        {
            // 本次运行 1:12，库中基线 0:30 → 今天一整天应为 1:42。
            TodayActive = TimeSpan.FromHours(1) + TimeSpan.FromMinutes(12),
            Seeded = TimeSpan.FromMinutes(30),
            HasCurrentApp = true,
            CurrentApp = "Visual Studio",
        };
        status.Top = new[]
        {
            new AppUsageEntry("devenv", "Visual Studio", 3_600_000, 0.60),
            new AppUsageEntry("chrome", "Google Chrome", 1_800_000, 0.30),
            new AppUsageEntry("code", "Visual Studio Code", 600_000, 0.10),
        };

        CardModel model = CardData.FromStatus(status);

        failed += Check("今日口径 = 本次运行 + 库中基线（1:12 + 0:30 → \"1:42\"）", model.TotalTime == "1:42");
        failed += Check("今日口径**不是**本次运行（写成 TodayActive 会得到 \"1:12\"）", model.TotalTime != "1:12");
        failed += Check("限额整块隐藏（LimitText 为空；M9 之前不显示假限额）", model.LimitText.Length == 0);
        failed += Check("当前软件透传（已采样 → 真实软件名）", model.CurrentApp == "Visual Studio");
        failed += Check("Top 行数与快照一致（3 行）", model.TopApps.Count == 3);
        failed += Check("Top 首行为最长者且名称/时长正确",
            model.TopApps.Count > 0 && model.TopApps[0].Name == "Visual Studio" && model.TopApps[0].Time == "1:00");
        failed += Check("Top 占比透传（0.60）",
            model.TopApps.Count > 0 && Math.Abs(model.TopApps[0].Ratio - 0.60) < 1e-9);

        // 未采样：不能把占位文案当成软件名。
        var cold = new RuntimeStatus { HasCurrentApp = false, CurrentApp = "(尚未采样)" };
        failed += Check("未采样 → 显示「启动中」而不是占位文案",
            CardData.FromStatus(cold).CurrentApp == "启动中");

        // ---- 非软件活跃（本次统一口径）：计入“今日”，标出来；为 0 时隐藏 ----
        failed += Check("非软件活跃：为 0 时隐藏（空串 → 渲染器整行不画）",
            model.NonAppTime.Length == 0 && model.NoWindowTime.Length == 0);

        var nonApp = new RuntimeStatus
        {
            TodayActive = TimeSpan.FromMinutes(20),
            Filtered = TimeSpan.FromMinutes(5),
            SeededFiltered = TimeSpan.FromMinutes(55),         // 今日合计 60 分
            Unattributed = TimeSpan.FromSeconds(5),
            SeededUnattributed = TimeSpan.FromSeconds(10),     // 今日合计 15 秒
        };
        CardModel mNon = CardData.FromStatus(nonApp);
        failed += Check("不计入使用时长：用“今天一整天”口径（5 + 55 = 60 分 → \"1:00\"）", mNon.NonAppTime == "1:00");
        failed += Check("无前台/未知：用今日合计（5 + 10 = 15 秒 → \"0:15\"）", mNon.NoWindowTime == "0:15");
        failed += Check("非软件活跃：**不**混进软件榜（Top 仍为空）", mNon.TopApps.Count == 0);

        // 锁屏：按口径完全不计入 → 主数字必须扣掉它（写成 TodayActive + Seeded 会多出 2 分钟）。
        var lockStatus = new RuntimeStatus
        {
            TodayActive = TimeSpan.FromMinutes(10),
            LockExcluded = TimeSpan.FromMinutes(2),
            Seeded = TimeSpan.FromMinutes(30),
        };
        failed += Check("今日真实活跃：扣除锁屏剔除（10 − 2 + 30 = 38 分 → \"38:00\"）",
            CardData.FromStatus(lockStatus).TotalTime == "38:00");

        // 行数上限：卡片排版只放得下 MaxRows 行，多给也不能越界。
        var many = new RuntimeStatus();
        var rows = new List<AppUsageEntry>();
        for (int i = 0; i < 12; i++) rows.Add(new AppUsageEntry("k" + i, "app" + i, 1000, 0.01));
        many.Top = rows;
        failed += Check($"Top 行数被截到 {CardData.MaxRows} 行（多给也不越界）",
            CardData.FromStatus(many).TopApps.Count == CardData.MaxRows);

        // 占比越界要夹紧，否则卡片会画出超出边框的条形。
        var wild = new RuntimeStatus();
        wild.Top = new[]
        {
            new AppUsageEntry("k1", "app1", 1000, 1.5),
            new AppUsageEntry("k2", "app2", 1000, -0.5),
        };
        CardModel clamped = CardData.FromStatus(wild);
        failed += Check("占比被夹到 [0,1]（1.5 → 1.0、-0.5 → 0.0）",
            Math.Abs(clamped.TopApps[0].Ratio - 1.0) < 1e-9 && Math.Abs(clamped.TopApps[1].Ratio) < 1e-9);

        // 时长格式化：满 1 小时用 h:mm，不足用 m:ss。
        failed += Check("格式化：0:59:30 → \"59:30\"（不足 1 小时用 m:ss）",
            CardData.Format(new TimeSpan(0, 59, 30)) == "59:30");
        failed += Check("格式化：1:00:00 → \"1:00\"（满 1 小时用 h:mm）",
            CardData.Format(TimeSpan.FromHours(1)) == "1:00");
        failed += Check("格式化：2:05:09 → \"2:05\"", CardData.Format(new TimeSpan(2, 5, 9)) == "2:05");
        failed += Check("格式化：负数被夹到 0 → \"0:00\"", CardData.Format(TimeSpan.FromSeconds(-5)) == "0:00");

        Console.WriteLine($"   小计：{(failed == 0 ? "全部通过" : failed + " 项失败")}");
        return failed;
    }

    // ------------------------------------------------------------------ B 窗口

    private static int WindowChecks()
    {
        Console.WriteLine("---- B 卡片窗口（真实创建，客观判据）----");
        int failed = 0;

        // 默认坐标必须在屏内：卡片“默认随产品启动显示”，默认位置跑到屏幕外就等于没显示。
        int screenW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        int screenH = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        failed += Check($"默认坐标在屏内（{CardWindow.DefaultX},{CardWindow.DefaultY} + " +
                        $"{CardWindow.DefaultWidth}×{CardWindow.DefaultHeight} ≤ {screenW}×{screenH}）",
            CardWindow.DefaultX >= 0 && CardWindow.DefaultY >= 0 &&
            CardWindow.DefaultX + CardWindow.DefaultWidth <= screenW &&
            CardWindow.DefaultY + CardWindow.DefaultHeight <= screenH);

        int modelCalls = 0;
        using var card = new CardWindow(() =>
        {
            Interlocked.Increment(ref modelCalls);
            return new CardModel { Title = "M6 自检", TotalTime = "0:07", CurrentApp = "自检" };
        });

        bool started = card.Start();
        failed += Check("启动成功且 Start() 返回时首帧已画完", started && card.Frames > 0);
        if (!started)
        {
            Console.WriteLine($"   原因：{card.LastError ?? "未知"}");
            Console.WriteLine($"   小计：{failed} 项失败");
            return failed;
        }

        IntPtr hwnd = card.Handle;
        failed += Check("窗口句柄有效", hwnd != IntPtr.Zero && NativeMethods.IsWindow(hwnd));

        int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        Console.WriteLine("   扩展样式：" + card.DescribeExStyle());
        failed += Check("含 WS_EX_LAYERED（逐像素 alpha 的前提）", (ex & NativeMethods.WS_EX_LAYERED) != 0);
        failed += Check("**不含** WS_EX_TRANSPARENT（M6 与 M7 的分界：不穿透）",
            (ex & NativeMethods.WS_EX_TRANSPARENT) == 0);
        failed += Check("**不含** WS_EX_NOACTIVATE（可激活 = 可交互）",
            (ex & NativeMethods.WS_EX_NOACTIVATE) == 0);
        failed += Check("含 WS_EX_TOOLWINDOW（不进任务栏 / Alt+Tab）",
            (ex & NativeMethods.WS_EX_TOOLWINDOW) != 0);

        // WM_NCHITTEST 返回 HTCAPTION 是“可以用鼠标拖动卡片”的**客观判据**：
        // 拖动交给系统代劳，因此命中测试对，拖动就对（手感无法在进程内断言）。
        IntPtr hit = NativeMethods.SendMessage(hwnd, NativeMethods.WM_NCHITTEST, IntPtr.Zero, IntPtr.Zero);
        Console.WriteLine($"   WM_NCHITTEST 返回 {hit.ToInt32()}（HTCAPTION={NativeMethods.HTCAPTION}）");
        failed += Check("命中测试返回 HTCAPTION（整窗可拖动）", hit.ToInt32() == NativeMethods.HTCAPTION);

        failed += Check("首帧后可见（默认随产品启动显示）", card.IsVisible);

        failed += Check("Hide() 后不可见", card.Hide() && !card.IsVisible);
        failed += Check("Show() 后恢复可见（可往返）", card.Show() && card.IsVisible);

        // 重绘持续推进：证明消息循环与 1s 定时器真的在跑（而不是只画了首帧就卡住）。
        int framesBefore = card.Frames;
        Thread.Sleep(CardWindow.RedrawMs + 400);
        failed += Check($"重绘仍在跑（{framesBefore} → {card.Frames} 帧）", card.Frames > framesBefore);

        // 取数函数必须真的被调用（否则卡片可能一直在画空白模型）。
        failed += Check("取数函数被真正调用过", modelCalls > 0);

        card.Dispose();
        Thread.Sleep(300);
        failed += Check("Dispose 后窗口已销毁（句柄失效）", !NativeMethods.IsWindow(hwnd));

        Console.WriteLine($"   小计：{(failed == 0 ? "全部通过" : failed + " 项失败")}");
        return failed;
    }

    // ------------------------------------------------------------------ C 托盘卡片开关

    private static int TrayCardChecks()
    {
        Console.WriteLine("---- C 托盘卡片开关（真实 NotifyIcon + 真实菜单）----");
        int failed = 0;

        bool visible = true;
        int calls = 0;
        bool? lastValue = null;

        TrayIconHost? tray = TrayIconHost.TryCreate(
            snapshot: () => new RuntimeStatus { TodayActive = TimeSpan.FromSeconds(5), Seeded = TimeSpan.FromSeconds(3) },
            setUserPaused: _ => { },
            openMainWindow: () => { },
            exit: () => { },
            isCardVisible: () => visible,
            setCardVisible: value => { calls++; lastValue = value; visible = value; });

        failed += Check("托盘创建成功", tray is not null);
        if (tray is null)
        {
            Console.WriteLine($"   小计：{failed} 项失败");
            return failed;
        }

        using (tray)
        {
            string[] menu = tray.DescribeMenuForDiagnostics();
            Console.WriteLine("   菜单：" + string.Join(" | ", menu));

            // 文案反映的是**点击后的动作**，勾选反映的是**当前状态**。
            failed += Check("卡片可见时菜单为「隐藏卡片」且已勾选",
                Array.Exists(menu, m => m.Contains("隐藏卡片") && m.Contains("[已勾选]")));

            failed += Check("卡片项已接线（点击返回 true）", tray.InvokeCardToggleForDiagnostics());
            failed += Check("点击后上层收到「隐藏」= false", calls == 1 && lastValue == false);
            failed += Check("点击后菜单变为「显示卡片」且无勾选（回读真实状态，不撒谎）",
                Array.Exists(tray.DescribeMenuForDiagnostics(),
                    m => m.Contains("显示卡片") && !m.Contains("[已勾选]")));

            tray.InvokeCardToggleForDiagnostics();
            failed += Check("再点一次 → 收到「显示」= true（可往返）", calls == 2 && lastValue == true);
        }

        // 不接线卡片时菜单里**不应**出现卡片项：M5b 当时验证过的菜单形态必须原样保留。
        TrayIconHost? plain = TrayIconHost.TryCreate(
            snapshot: () => new RuntimeStatus(),
            setUserPaused: _ => { },
            openMainWindow: () => { },
            exit: () => { });
        failed += Check("未接线卡片时菜单不含卡片项（M5b 形态不被悄悄改动）",
            plain is not null && !Array.Exists(plain.DescribeMenuForDiagnostics(), m => m.Contains("卡片")));
        try { plain?.Dispose(); } catch { failed++; }

        // tooltip 用的是“今天一整天”口径（与主界面大字号、卡片一致）。
        failed += Check("tooltip 使用整天口径（1:23:45 原样出现）",
            TrayIconHost.ComposeTooltip(new TimeSpan(1, 23, 45), paused: false, userPaused: false).Contains("1:23:45"));

        Console.WriteLine($"   小计：{(failed == 0 ? "全部通过" : failed + " 项失败")}");
        return failed;
    }

    // ------------------------------------------------------------------ D 主界面榜单末行

    /// <summary>
    /// 主界面「Top 5（今日）」末尾那条「不计入统计」行。
    ///
    /// 它同样是**纯逻辑**（<see cref="MainWindow.ComposeNonAppTopRow"/> 只吃一个快照 DTO、
    /// 不碰任何 WPF 控件），所以能在这里客观断言，不必真的创建主窗口。
    /// </summary>
    private static int MainWindowChecks()
    {
        Console.WriteLine("---- D 主界面榜单末行（「不计入统计」，纯逻辑）----");
        int failed = 0;

        // 行首时长字段（时间被右对齐补过空格，故按空格切分取第一段）。
        // 刻意写成局部函数：断言比对的是**产品输出的那一行**，而不是在测试里重算一遍时间。
        static string TimeField(string rowText)
        {
            string[] parts = rowText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : string.Empty;
        }

        // 一组自洽的数：本次运行 20 分（其中锁屏剔除 2 分）、库中基线 40 分；
        //   非软件活跃 = 本次(5 + 3) + 基线(10 + 2) = 20 分。
        var status = new RuntimeStatus
        {
            TodayActive = TimeSpan.FromMinutes(20),
            LockExcluded = TimeSpan.FromMinutes(2),
            Seeded = TimeSpan.FromMinutes(40),
            Filtered = TimeSpan.FromMinutes(5),
            SeededFiltered = TimeSpan.FromMinutes(10),
            Unattributed = TimeSpan.FromMinutes(3),
            SeededUnattributed = TimeSpan.FromMinutes(2),
        };

        string? row = MainWindow.ComposeNonAppTopRow(status);
        Console.WriteLine("   末行：" + (row ?? "(不插行)"));

        failed += Check("非软件活跃 > 0 时插入末行", row is not null);
        failed += Check($"行名恰为「{MainWindow.NonAppRowName}」（与产品共用同一常量，测试里不另抄字面量）",
            row is not null && row.EndsWith(MainWindow.NonAppRowName, StringComparison.Ordinal));
        failed += Check("时长用“今天一整天”口径（本次 8 分 + 基线 12 分 = 20 分 → 0:20:00）",
            row is not null && row.Contains("0:20:00"));
        failed += Check("占比列留空：整行**不含**百分号（不给一个与软件行分母不同的占比）",
            row is not null && !row.Contains('%'));

        // 对齐：拿**产品自己产出的**软件行做基准，比对“行名起始列”。
        // 这样宽度契约来自产品（ShareCell vs BlankShareCell），测试里不必另抄一份空格数。
        string appRow = MainWindow.ComposeAppTopRow(new AppUsageEntry("devenv", "Visual Studio", 1_800_000, 0.0));
        int appNameAt = appRow.IndexOf("Visual Studio", StringComparison.Ordinal);
        int nonAppNameAt = row?.IndexOf(MainWindow.NonAppRowName, StringComparison.Ordinal) ?? -1;
        Console.WriteLine($"   对齐：软件行行名在第 {appNameAt + 1} 列，末行行名在第 {nonAppNameAt + 1} 列");
        failed += Check("行名与软件行**同列对齐**（留空的是占比列，不是把整块缩掉）",
            appNameAt > 0 && appNameAt == nonAppNameAt);

        // 重启场景（本次明确修过的那类缺陷）：本次运行还没数据，但基线在 —— 这一行**不许**变小。
        var restarted = new RuntimeStatus
        {
            TodayActive = TimeSpan.Zero,
            LockExcluded = TimeSpan.Zero,
            Seeded = TimeSpan.FromMinutes(40),
            SeededFiltered = TimeSpan.FromMinutes(10),
            SeededUnattributed = TimeSpan.FromMinutes(2),
        };
        string? afterRestart = MainWindow.ComposeNonAppTopRow(restarted);
        failed += Check("重启后仍显示库中基线（10 + 2 = 12 分 → 0:12:00，不掉回 0:00:00）",
            afterRestart is not null && afterRestart.Contains("0:12:00"));
        failed += Check("重启后仍无占比（留空是形态，不是数值恰好为 0）",
            afterRestart is not null && !afterRestart.Contains('%'));

        // 锁屏剔除：按口径“完全不计入”。占比留空之后，它对本行应当**毫无影响** ——
        // 既不许混进时长，也不许从任何别的地方漏进这一行的文本里。
        var noLock = new RuntimeStatus
        {
            TodayActive = TimeSpan.FromMinutes(20),
            LockExcluded = TimeSpan.Zero,
            Seeded = TimeSpan.FromMinutes(40),
            Filtered = TimeSpan.FromMinutes(5),
            SeededFiltered = TimeSpan.FromMinutes(10),
            Unattributed = TimeSpan.FromMinutes(3),
            SeededUnattributed = TimeSpan.FromMinutes(2),
        };
        string? rowNoLock = MainWindow.ComposeNonAppTopRow(noLock);
        failed += Check("锁屏剔除**不进入**本行时长（只改 LockExcluded，时长字段必须仍是 0:20:00）",
            row is not null && rowNoLock is not null &&
            TimeField(row) == "0:20:00" && TimeField(rowNoLock) == "0:20:00");
        failed += Check("锁屏剔除对本行**完全无影响**（整行文本逐字符相同）",
            row is not null && row == rowNoLock);

        // 为 0 时整行不插（与卡片“为 0 隐藏”一致），否则会多出一条 0:00:00 的噪声行。
        failed += Check("非软件活跃为 0 → 整行不插（返回 null）",
            MainWindow.ComposeNonAppTopRow(new RuntimeStatus()) is null);

        // 边界：今日真实活跃为 0（库为空且刚启动）时仍要能出这一行，且不许抛异常（本行早已不做除法）。
        string? edgeRow = MainWindow.ComposeNonAppTopRow(new RuntimeStatus { Filtered = TimeSpan.FromMinutes(1) });
        failed += Check("今日真实活跃为 0 → 仍插行、不抛异常、仍无占比",
            edgeRow is not null && edgeRow.Contains("0:01:00") && !edgeRow.Contains('%'));

        Console.WriteLine($"   小计：{(failed == 0 ? "全部通过" : failed + " 项失败")}");
        return failed;
    }

    private static int Check(string label, bool ok)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        return ok ? 0 : 1;
    }
}
