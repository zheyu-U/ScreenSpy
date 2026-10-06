using System;
using System.IO;
using System.Text;
using System.Threading;
using ScreenSpy.AppHost;
using ScreenSpy.Interop;
using ScreenSpy.Rendering;
using ScreenSpy.Storage;
using ScreenSpy.Widget;

namespace ScreenSpy.Demo;

/// <summary>
/// M7 的控制台自检入口（内嵌入口，**长期保留**）。
///
/// 用法：ScreenSpy.exe --m7-selfcheck
///
/// 覆盖四类**可以在进程内客观判定**的事实：
///  【放置持久化】用**真实 SQLite** 走“存 → 重开 → 读”：键名恰为
///         <c>widget_pos_x</c>/<c>widget_pos_y</c>/<c>widget_mode</c>（拼错会静默失效）、
///         三个键互不干扰、非法值回退默认、大小写不敏感、存储不可用时全默认且不抛、
///         越界坐标被夹回屏幕内；
///  【卡片双形态】真实创建 <see cref="CardWindow"/>，断言嵌入形态是
///         “穿透 + 不抢焦点 + z 序复位在跑 + **不在最顶层**（会被别的窗口盖住）”，
///         浮动形态是“不穿透 + 命中测试 HTCAPTION（可拖）+ z 序管理器已停”，
///         以及**两形态共用一份坐标**（浮动下移动 → 回嵌入 → 位置不变，且重绘不会把它拽回）；
///  【界面接线】主界面按钮文案与提示的三种状态、托盘「调整组件位置」真的可点且走真实
///         <c>PerformClick</c> 路径，以及**未接线时仍保留禁用占位**（M5b/M6 形态不被悄悄改动）；
///  【调整入口互斥】「调整位置」与「切换形态」**不得同时可用**（D 组）：
///         调整中必须禁用形态按钮（否则会绕过「完成调整」的坐标上报，最后一次拖动
///         最多约 1 秒写不进库），常态为浮动时必须禁用调整按钮（那时无事可做）；
///         并验证**常态为浮动 ≠ 调整中**（否则那类用户的形态按钮会被永久禁用）。
///
/// **本自检不覆盖**（需要人眼）：
///  * 卡片看起来是否好看、半透明与圆角是否正确；
///  * 用鼠标真的拖一下（自检只能验“命中测试对”，不能验“手感”）；
///  * “改动一个窗口后卡片被它盖住”这一观感（自检只能验 z 序上确实有窗口在卡片之上）。
///
/// 注意：运行期间会**短暂出现一张真实卡片窗口**（创建后立即销毁），并会临时使用
/// <c>artifacts/m7</c> 下的一个库（跑完即删）。
/// </summary>
internal static class M7SelfCheck
{
    private const string SwitchName = "--m7-selfcheck";

    public static bool IsRequested(string[] args)
    {
        foreach (string a in args)
            if (a.Equals(SwitchName, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static int Run(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 输出被重定向时可能失败，忽略 */ }

        Console.WriteLine("============ ScreenSpy · M7 卡片双形态与位置持久化 自检 ============");
        Console.WriteLine();

        int failed = PlacementChecks();
        failed += WindowModeChecks();
        failed += UiWiringChecks();
        failed += AdjustExclusivityChecks();

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "M7 自检：全部通过。" : $"M7 自检：{failed} 项失败。");
        return failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ A 放置持久化

    private static int PlacementChecks()
    {
        Console.WriteLine("---- A 放置持久化（真实 SQLite：存 → 重开 → 读）----");
        int failed = 0;

        string dir = Path.GetFullPath(Path.Combine("artifacts", "m7"));
        string dbPath = Path.Combine(dir, "m7-selfcheck.db");

        SqliteStore.ClearPools();
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* 忽略 */ }
        Directory.CreateDirectory(dir);

        var options = new StorageOptions { DatabasePath = dbPath, Enabled = true };
        var fallback = new WidgetPlacement(WidgetMode.Embedded, 200, 700);

        // 键名是**与既有数据库之间的兼容契约**（改了名，旧库里的设置就再也读不到）。
        // 因此这里刻意写**字面量**，而不是引用产品常量 —— 否则常量一旦被改名，
        // 断言与写入会一起改、测试永远通过，正好是本项目最忌讳的“测试自己证明自己”。
        // （第一版就是引用常量的写法，变异测试当场证明了它抓不住改名。）
        const string LiteralKeyX = "widget_pos_x";
        const string LiteralKeyY = "widget_pos_y";
        const string LiteralKeyMode = "widget_mode";

        // ---- 空库：应当原样回退默认，且**不写入**任何键（没设置过就别假装设置过）
        using (var store = new SqliteStore(options))
        {
            store.Initialize();

            WidgetPlacement none = WidgetPlacementStore.Load(store, fallback);
            failed += Check($"空库 → 默认（embedded (200,700)），实得 {none}",
                none.Mode == WidgetMode.Embedded && none.X == 200 && none.Y == 700);

            failed += Check("空库时三个键都还不存在（读默认不等于写了默认）",
                store.GetSetting(LiteralKeyX) is null &&
                store.GetSetting(LiteralKeyY) is null &&
                store.GetSetting(LiteralKeyMode) is null);

            WidgetPlacementStore.SavePosition(store, 321, 654);
            WidgetPlacementStore.SaveMode(store, WidgetMode.Floating);

            // 键名是**契约**：拼错的话读永远得到默认值，且不报任何错。
            failed += Check("坐标落在 widget_pos_x / widget_pos_y（键名恰好是这两个字面量）",
                store.GetSetting(LiteralKeyX) == "321" &&
                store.GetSetting(LiteralKeyY) == "654");
            failed += Check("形态落在 widget_mode = floating",
                store.GetSetting(LiteralKeyMode) == "floating");
        }

        // ---- 重开（模拟重启）
        using (var store = new SqliteStore(options))
        {
            store.Initialize();

            WidgetPlacement reloaded = WidgetPlacementStore.Load(store, fallback);
            failed += Check($"重开后坐标不变（321,654），实得 {reloaded}", reloaded.X == 321 && reloaded.Y == 654);
            failed += Check("重开后形态不变（浮动）", reloaded.Mode == WidgetMode.Floating);

            // 三个键互相独立：写坐标不该把形态弄丢。
            WidgetPlacementStore.SavePosition(store, 10, 20);
            WidgetPlacement afterMove = WidgetPlacementStore.Load(store, fallback);
            failed += Check($"只写坐标不动形态（键互不干扰），实得 {afterMove}",
                afterMove.Mode == WidgetMode.Floating && afterMove.X == 10);

            // 大小写不敏感。
            store.SetSetting(LiteralKeyMode, "FLOATING");
            failed += Check("形态解析大小写不敏感（FLOATING → 浮动）",
                WidgetPlacementStore.Load(store, fallback).Mode == WidgetMode.Floating);

            // 非法值：回退默认，且**不抛异常**、不把合法的那一项也弄坏。
            store.SetSetting(LiteralKeyMode, "zzz");
            store.SetSetting(LiteralKeyX, "abc");
            WidgetPlacement bad = WidgetPlacementStore.Load(store, fallback);
            failed += Check($"非法值回退默认且互不牵连（形态=embedded、X=200、Y=20），实得 {bad}",
                bad.Mode == WidgetMode.Embedded && bad.X == 200 && bad.Y == 20);
        }

        // ---- 存储不可用（降级 / --no-store / SQLite 打不开）
        failed += Check("store=null → 读默认（卡片不至于没坐标可用）",
            WidgetPlacementStore.Load(null, fallback).Mode == WidgetMode.Embedded &&
            WidgetPlacementStore.Load(null, fallback).X == 200);

        bool threw = false;
        try
        {
            WidgetPlacementStore.SavePosition(null, 1, 2);
            WidgetPlacementStore.SaveMode(null, WidgetMode.Floating);
        }
        catch { threw = true; }

        failed += Check("store=null → 写静默忽略、不抛异常", !threw);

        // ---- 夹回屏幕内（换过分辨率 / 拔过显示器时，坐标可能落在屏幕外 = 卡片消失）
        failed += Check("坐标夹回屏幕内（-9999,-9999 → 0,0）",
            WidgetPlacementStore.ClampToScreen(new WidgetPlacement(WidgetMode.Embedded, -9999, -9999), 1920, 1080)
                is { X: 0, Y: 0 });

        failed += Check("坐标夹回屏幕内（右下越界 → 屏内最后一点）",
            WidgetPlacementStore.ClampToScreen(new WidgetPlacement(WidgetMode.Embedded, 99999, 99999), 1920, 1080)
                is { X: 1919, Y: 1079 });

        failed += Check("屏幕尺寸未知（0×0）时不夹取（不许把好坐标改成 0,0）",
            WidgetPlacementStore.ClampToScreen(new WidgetPlacement(WidgetMode.Embedded, 300, 400), 0, 0)
                is { X: 300, Y: 400 });

        SqliteStore.ClearPools();
        try { Directory.Delete(dir, recursive: true); } catch { /* 忽略 */ }

        Console.WriteLine($"   小计：{(failed == 0 ? "全部通过" : failed + " 项失败")}");
        return failed;
    }

    // ------------------------------------------------------------------ B 卡片双形态

    private static int WindowModeChecks()
    {
        Console.WriteLine("---- B 卡片双形态（真实窗口，客观判据）----");
        int failed = 0;

        int reportedX = int.MinValue;
        int reportedY = int.MinValue;
        int reportCalls = 0;

        using var card = new CardWindow(
            () => new CardModel { Title = "M7 自检", TotalTime = "0:07", CurrentApp = "自检" },
            WidgetMode.Embedded,
            x: CardWindow.DefaultX,
            y: CardWindow.DefaultY,
            onPositionChanged: (x, y) => { reportCalls++; reportedX = x; reportedY = y; });

        if (!card.Start())
        {
            Console.WriteLine($"   [FAIL] 卡片启动失败：{card.LastError ?? "未知原因"}");
            Console.WriteLine("   小计：1 项失败");
            return 1;
        }

        IntPtr hwnd = card.Handle;
        failed += Check("启动成功、首帧已画完、句柄有效",
            card.Frames > 0 && hwnd != IntPtr.Zero && NativeMethods.IsWindow(hwnd));

        int screenW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        int screenH = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);

        // ------------------------------------------------ 嵌入形态（默认）
        failed += Check("初始形态为嵌入（默认形态）且不在调整中",
            card.Mode == WidgetMode.Embedded && !card.IsAdjusting);

        Console.WriteLine("   嵌入扩展样式：" + card.DescribeExStyle());
        failed += Check("嵌入：含 TRANSPARENT + NOACTIVATE（鼠标穿透、不抢焦点）",
            HasStyle(hwnd, NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE, want: true));
        failed += Check($"嵌入：命中测试 = HTTRANSPARENT（点不到卡片，实得 {HitTest(hwnd)}）",
            HitTest(hwnd) == NativeMethods.HTTRANSPARENT);
        failed += Check("嵌入：z 序复位在跑（显示桌面检测已启动）",
            card.Polls > 0 || card.HookInstalled);

        // 「会被别的窗口盖住」的客观判据：不是 topmost，且 z 序里确实有窗口在它之上。
        int cardZ = NativeMethods.ZOrderIndex(hwnd);
        Console.WriteLine($"   嵌入 z 序：Card={cardZ}（0 表示最顶层）· {card.DescribeZOrder()}");
        failed += Check("嵌入：不是 topmost", !IsTopMost(hwnd));
        failed += Check($"嵌入：不在最顶层（其上确有窗口 ⇒ 会被盖住），CardZ={cardZ}", cardZ > 0);
        failed += Check("嵌入：当前可见", card.IsVisible);

        // ------------------------------------------------ 浮动形态
        int pollsBefore = card.Polls;
        failed += Check("切到浮动成功（BeginAdjust）", card.BeginAdjust());
        failed += Check("形态已确认为浮动", card.Mode == WidgetMode.Floating && card.IsAdjusting);

        Console.WriteLine("   浮动扩展样式：" + card.DescribeExStyle());
        failed += Check("浮动：**不含** TRANSPARENT / NOACTIVATE（可点、可激活）",
            HasStyle(hwnd, NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE, want: false));
        failed += Check("浮动：仍含 LAYERED（逐像素 alpha 没丢）",
            HasStyle(hwnd, NativeMethods.WS_EX_LAYERED, want: true));
        failed += Check($"浮动：命中测试 = HTCAPTION（整窗可拖，实得 {HitTest(hwnd)}）",
            HitTest(hwnd) == NativeMethods.HTCAPTION);
        failed += Check("浮动：z 序管理器已停（浮动形态不需要显示桌面复位）",
            card.DescribeZOrder().StartsWith("未启用", StringComparison.Ordinal));
        failed += Check("切换过程中窗口始终可见（同一个窗口，不闪断）", card.IsVisible);

        // ------------------------------------------------ 两形态共用一份坐标
        int targetX = Math.Min(CardWindow.DefaultX + 37, Math.Max(0, screenW - card.Width - 1));
        int targetY = Math.Min(CardWindow.DefaultY + 53, Math.Max(0, screenH - card.Height - 1));

        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOP, targetX, targetY, card.Width, card.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);
        Thread.Sleep(150);

        (int moveX, int moveY) = RectOf(hwnd);
        failed += Check($"浮动下移动到 ({targetX},{targetY})，实得 ({moveX},{moveY})",
            moveX == targetX && moveY == targetY);

        failed += Check("结束调整成功（EndAdjust）", card.EndAdjust());
        failed += Check("回到常态形态（嵌入）", card.Mode == WidgetMode.Embedded);

        (int backX, int backY) = RectOf(hwnd);
        failed += Check($"**两形态共用一份坐标**：回到嵌入后位置不变 ({targetX},{targetY})，实得 ({backX},{backY})",
            backX == targetX && backY == targetY);

        failed += Check($"坐标已上报给上层（共 {reportCalls} 次，最后 ({reportedX},{reportedY})）",
            reportCalls > 0 && reportedX == targetX && reportedY == targetY);

        // 嵌入形态每帧用 pptDst 定位 —— 它必须用**刚读回来的坐标**，而不是把窗口拽回默认位置。
        Thread.Sleep(CardWindow.RedrawMs + 600);
        (int afterX, int afterY) = RectOf(hwnd);
        failed += Check($"嵌入形态重绘后位置仍不变（不被拽回默认坐标），实得 ({afterX},{afterY})",
            afterX == targetX && afterY == targetY);

        failed += Check("回到嵌入后：恢复穿透 + z 序复位重新在跑",
            HitTest(hwnd) == NativeMethods.HTTRANSPARENT &&
            HasStyle(hwnd, NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE, want: true) &&
            card.Polls > pollsBefore);

        // ------------------------------------------------ 常态形态不被“调整”改写 + 「调整中」的判定
        card.SetRestingMode(WidgetMode.Floating);
        failed += Check("把常态切成浮动成功", card.SwitchMode(WidgetMode.Floating));

        // 关键回归：常态就是浮动时**不能**算“调整中”。
        // M7 第一版把 IsAdjusting 写成 Mode == Floating，于是这类用户的「调整位置」永远
        // 显示“完成调整”、而“调整中要禁用的形态按钮”会被**永久禁用** —— 再也切不回嵌入。
        failed += Check("常态为浮动时：IsAdjusting 必须为 false（否则形态按钮会被永久禁用）",
            !card.IsAdjusting);
        failed += Check("常态为浮动时：CanAdjust 为 false（“调整位置”无事可做）", !card.CanAdjust);
        failed += Check("常态为浮动时：BeginAdjust 返回 false，且不置位“调整中”、形态保持浮动",
            !card.BeginAdjust() && !card.IsAdjusting && card.Mode == WidgetMode.Floating);

        card.EndAdjust();
        failed += Check("常态为浮动时：调整结束后回到**浮动**（常态不被悄悄改写）",
            card.Mode == WidgetMode.Floating);

        card.SetRestingMode(WidgetMode.Embedded);
        failed += Check("把常态切回嵌入成功", card.SwitchMode(WidgetMode.Embedded));
        failed += Check("常态为嵌入时：CanAdjust 为 true（“调整位置”有意义）", card.CanAdjust);

        failed += Check("常态为嵌入时：BeginAdjust → IsAdjusting 为 true",
            card.BeginAdjust() && card.IsAdjusting);
        failed += Check("EndAdjust → IsAdjusting 归位为 false",
            card.EndAdjust() && !card.IsAdjusting);

        // 绕过「完成调整」直接切形态（自检、将来新增的入口都会这样）也不该把标志留在“调整中”：
        // 那会让形态按钮被永久禁用一个已经结束了的调整状态上。
        card.BeginAdjust();
        card.SwitchMode(WidgetMode.Embedded);
        failed += Check("绕过 EndAdjust 直接切回嵌入后：IsAdjusting 仍会归位（标志不会卡住）",
            !card.IsAdjusting);

        // ------------------------------------------------ 反复切换（覆盖只在第二轮暴露的隐患）
        bool roundsOk = true;
        for (int i = 0; i < 3; i++)
        {
            if (!card.SwitchMode(WidgetMode.Floating) || HitTest(hwnd) != NativeMethods.HTCAPTION) roundsOk = false;
            if (!card.SwitchMode(WidgetMode.Embedded) || HitTest(hwnd) != NativeMethods.HTTRANSPARENT) roundsOk = false;
        }
        failed += Check("嵌入 ⇄ 浮动 反复切换 3 轮，每轮样式与命中测试都正确", roundsOk);

        failed += Check("切换多轮后仍会重绘（消息循环没被打断）", FramesAdvance(card));

        failed += Check("Hide()/Show() 往返仍正常（嵌入形态）",
            card.Hide() && !card.IsVisible && card.Show() && card.IsVisible);

        card.Dispose();
        Thread.Sleep(300);
        failed += Check("Dispose 后窗口已销毁（句柄失效）", !NativeMethods.IsWindow(hwnd));

        Console.WriteLine($"   小计：{(failed == 0 ? "全部通过" : failed + " 项失败")}");
        return failed;
    }

    // ------------------------------------------------------------------ C 界面接线

    private static int UiWiringChecks()
    {
        Console.WriteLine("---- C 主界面按钮 / 托盘菜单（纯逻辑 + 真实菜单）----");
        int failed = 0;

        failed += Check("位置按钮：未调整 → 「调整位置」",
            MainWindow.ComposePositionButtonText(adjusting: false) == "调整位置");
        failed += Check("位置按钮：调整中 → 「完成调整」",
            MainWindow.ComposePositionButtonText(adjusting: true) == "完成调整");
        failed += Check("形态按钮：嵌入 → 「切换为浮动窗口」",
            MainWindow.ComposeModeButtonText(floating: false) == "切换为浮动窗口");
        failed += Check("形态按钮：浮动 → 「切换为嵌入桌面层」",
            MainWindow.ComposeModeButtonText(floating: true) == "切换为嵌入桌面层");

        string adjustingHint = MainWindow.BuildPositionHint(adjusting: true, floating: false);
        string embeddedHint = MainWindow.BuildPositionHint(adjusting: false, floating: false);
        string floatingHint = MainWindow.BuildPositionHint(adjusting: false, floating: true);

        failed += Check("提示三种状态互不相同（调整中 / 嵌入 / 浮动）",
            adjustingHint != embeddedHint && embeddedHint != floatingHint && adjustingHint != floatingHint);
        failed += Check("嵌入提示说清「会被其它窗口盖住 + 鼠标穿透」",
            embeddedHint.Contains("盖住", StringComparison.Ordinal) &&
            embeddedHint.Contains("穿透", StringComparison.Ordinal));
        failed += Check("浮动提示说清「可拖动 + 会记住位置」",
            floatingHint.Contains("拖动", StringComparison.Ordinal) &&
            floatingHint.Contains("记住", StringComparison.Ordinal));

        // ---- 托盘：接线了调整回调 → 菜单项可点，且 PerformClick 真的走到上层
        int adjustCalls = 0;
        bool floating = false;

        TrayIconHost? tray = TrayIconHost.TryCreate(
            snapshot: () => new RuntimeStatus(),
            setUserPaused: _ => { },
            openMainWindow: () => { },
            exit: () => { },
            isCardVisible: () => true,
            setCardVisible: _ => { },
            toggleAdjust: () => { adjustCalls++; floating = !floating; },
            isAdjusting: () => floating);

        failed += Check("托盘创建成功（接线了调整项）", tray is not null);

        if (tray is null)
        {
            Console.WriteLine($"   小计：{failed + 1} 项失败");
            return failed + 1;
        }

        using (tray)
        {
            string[] menu = tray.DescribeMenuForDiagnostics();
            Console.WriteLine("   菜单：" + string.Join(" | ", menu));

            failed += Check("菜单里是**可点**的「调整组件位置」（不再是 M8 禁用占位）",
                Array.Exists(menu, m => m.Contains("调整组件位置", StringComparison.Ordinal) &&
                                        !m.Contains("[禁用]", StringComparison.Ordinal)));
            failed += Check("菜单里没有「M8 未实现」占位",
                !Array.Exists(menu, m => m.Contains("M8 未实现", StringComparison.Ordinal)));

            failed += Check("点击走真实路径（菜单项 PerformClick）", tray.InvokeAdjustForDiagnostics());
            failed += Check($"点击后上层真的被调用（{adjustCalls} 次）", adjustCalls == 1);
            failed += Check("菜单文案按**真实形态**回读 → 变为「完成调整」",
                Array.Exists(tray.DescribeMenuForDiagnostics(),
                    m => m.Contains("完成调整", StringComparison.Ordinal)));
        }

        // ---- 未接线（M5b/M6 形态）：保留禁用占位，且不可点
        TrayIconHost? plain = TrayIconHost.TryCreate(
            snapshot: () => new RuntimeStatus(),
            setUserPaused: _ => { },
            openMainWindow: () => { },
            exit: () => { },
            isCardVisible: () => true,
            setCardVisible: _ => { });

        failed += Check("未接线调整回调时：保留**禁用占位**且不可点（M5b/M6 形态不被悄悄改动）",
            plain is not null &&
            Array.Exists(plain.DescribeMenuForDiagnostics(),
                m => m.Contains("调整组件位置", StringComparison.Ordinal) &&
                     m.Contains("[禁用]", StringComparison.Ordinal)) &&
            !plain.InvokeAdjustForDiagnostics());

        try { plain?.Dispose(); } catch { failed++; }

        Console.WriteLine($"   小计：{(failed == 0 ? "全部通过" : failed + " 项失败")}");
        return failed;
    }

    // ------------------------------------------------------------------ D 调整入口互斥

    /// <summary>
    /// 「调整位置」与「切换形态」的**互斥规则**（纯逻辑）+ 托盘菜单项的禁用。
    ///
    /// 为什么值得单独成组：这两条规则各自堵住一个“不报错的错” ——
    ///  * **调整中不禁用形态按钮** → 那个按钮走的是“改常态”那条路，会绕过「完成调整」的
    ///    “先取当前坐标再上报”，于是最后一次拖动可能还没写进库（最多约 1 秒的静默偏差）；
    ///    而且常态为嵌入时，在调整中点它还会把**临时**的浮动变成**永久**常态。
    ///  * **常态浮动时不禁用调整按钮** → 点了没反应（比禁用更糟）。
    ///
    /// 两者都是**逻辑规则**，因此可以确定性断言，不必去读真实 WPF 控件（自检也不该
    /// 依赖窗口是否真的显示出来）；托盘那一半则走真实菜单对象的 <c>PerformClick</c>。
    /// </summary>
    private static int AdjustExclusivityChecks()
    {
        Console.WriteLine("---- D 两按钮互斥 / 调整入口可用性（纯逻辑 + 真实菜单）----");
        int failed = 0;

        // ① 调整中（常态嵌入、临时浮动）：形态按钮必须禁用。
        (bool posAdjusting, bool modeAdjusting) =
            MainWindow.ComposePositionControlAvailability(adjusting: true, floating: false);
        failed += Check("调整中：形态按钮**禁用**（本次新增的互斥规则）", !modeAdjusting);
        failed += Check("调整中：位置按钮仍可用（文案为「完成调整」）", posAdjusting);

        // ② 常态嵌入、未调整：两个都可用。
        (bool posPlain, bool modePlain) =
            MainWindow.ComposePositionControlAvailability(adjusting: false, floating: false);
        failed += Check("常态嵌入且未调整：两个按钮都可用", posPlain && modePlain);

        // ③ 常态浮动：位置按钮禁用（无事可做），形态按钮可用（还能切回嵌入）。
        (bool posRestFloating, bool modeRestFloating) =
            MainWindow.ComposePositionControlAvailability(adjusting: false, floating: true);
        failed += Check("常态浮动：位置按钮**禁用**（直接拖即可，无需临时调整）", !posRestFloating);
        failed += Check("常态浮动：形态按钮仍可用（还能切回嵌入）——不是两个都被禁用",
            modeRestFloating);

        // ④ 不允许出现“调整中还允许切形态”的组合（那正是用户报告的两按钮重叠）。
        failed += Check("不存在“调整中且形态按钮仍可用”的组合（两按钮不会同时改常态）",
            !(posAdjusting && modeAdjusting));

        // ⑤ 读取竞争造成的“不可能状态”（调整中 + 常态浮动）不抛异常、且给出确定答案。
        bool noThrow = true;
        try
        {
            (bool p, bool m) = MainWindow.ComposePositionControlAvailability(adjusting: true, floating: true);
            if (!m) noThrow = true;   // 以“调整中”为准，形态按钮禁用
            else noThrow = false;
        }
        catch { noThrow = false; }
        failed += Check("“不可能状态”不抛异常，且仍以“调整中”为准（界面刷新不该因此崩）", noThrow);

        // ⑥ 提示文案要把禁用原因说出来（否则用户只看到灰按钮，不知道为什么）。
        string adjustingHint = MainWindow.BuildPositionHint(adjusting: true, floating: false);
        string restFloatingHint = MainWindow.BuildPositionHint(adjusting: false, floating: true);
        failed += Check("调整中的提示写明了“形态按钮已禁用”的原因",
            adjustingHint.Contains("禁用", StringComparison.Ordinal));
        failed += Check("常态浮动的提示写明了“位置按钮已禁用”的原因",
            restFloatingHint.Contains("禁用", StringComparison.Ordinal));

        // ---- 托盘：调整可用性为 false → 菜单项禁用，且点击真的不触发上层
        int adjustCalls = 0;
        TrayIconHost? tray = TrayIconHost.TryCreate(
            snapshot: () => new RuntimeStatus(),
            setUserPaused: _ => { },
            openMainWindow: () => { },
            exit: () => { },
            isCardVisible: () => true,
            setCardVisible: _ => { },
            toggleAdjust: () => adjustCalls++,
            isAdjusting: () => false,
            isAdjustAvailable: () => false);

        failed += Check("托盘创建成功（调整项不可用）", tray is not null);

        if (tray is null)
        {
            failed++;
        }
        else
        {
            using (tray)
            {
                string[] menu = tray.DescribeMenuForDiagnostics();
                Console.WriteLine("   菜单（常态浮动）：" + string.Join(" | ", menu));

                failed += Check("常态浮动时：托盘「调整组件位置」显示为 [禁用]",
                    Array.Exists(menu, m => m.Contains("调整组件位置", StringComparison.Ordinal) &&
                                            m.Contains("[禁用]", StringComparison.Ordinal)));
                failed += Check("常态浮动时：点它（走真实 PerformClick）**上层不被调用**",
                    tray.InvokeAdjustForDiagnostics() && adjustCalls == 0);
            }
        }

        // ---- 托盘：未接 isAdjustAvailable（M6/M7 已建立的调用形态）→ 菜单形态不变（仍可点）
        int legacyCalls = 0;
        TrayIconHost? legacy = TrayIconHost.TryCreate(
            snapshot: () => new RuntimeStatus(),
            setUserPaused: _ => { },
            openMainWindow: () => { },
            exit: () => { },
            isCardVisible: () => true,
            setCardVisible: _ => { },
            toggleAdjust: () => legacyCalls++,
            isAdjusting: () => false);

        failed += Check("未接 isAdjustAvailable 时：调整项保持**可点**（既有调用形态不被悄悄改动）",
            legacy is not null &&
            Array.Exists(legacy.DescribeMenuForDiagnostics(),
                m => m.Contains("调整组件位置", StringComparison.Ordinal) &&
                     !m.Contains("[禁用]", StringComparison.Ordinal)));
        failed += Check("未接 isAdjustAvailable 时：点击仍会触发上层", legacy?.InvokeAdjustForDiagnostics() == true && legacyCalls == 1);

        try { legacy?.Dispose(); } catch { failed++; }

        Console.WriteLine($"   小计：{(failed == 0 ? "全部通过" : failed + " 项失败")}");
        return failed;
    }

    // ------------------------------------------------------------------ 小工具

    /// <summary>等待一段时间后帧数是否还在增长（真实“定时器与消息循环还在跑”的判据）。</summary>
    private static bool FramesAdvance(CardWindow card)
    {
        int before = card.Frames;
        Thread.Sleep(CardWindow.RedrawMs + 600);
        return card.Frames > before;
    }

    private static bool HasStyle(IntPtr hwnd, int mask, bool want)
    {
        int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        bool has = (ex & mask) == mask;
        return has == want;
    }

    private static bool IsTopMost(IntPtr hwnd)
        => (NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOPMOST) != 0;

    private static int HitTest(IntPtr hwnd)
        => NativeMethods.SendMessage(hwnd, NativeMethods.WM_NCHITTEST, IntPtr.Zero, IntPtr.Zero).ToInt32();

    private static (int X, int Y) RectOf(IntPtr hwnd)
        => NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect) ? (rect.Left, rect.Top) : (int.MinValue, int.MinValue);

    private static int Check(string label, bool ok)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        return ok ? 0 : 1;
    }
}
