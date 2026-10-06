using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ScreenSpy.Collector;
using ScreenSpy.Interop;
using ScreenSpy.Storage;

namespace ScreenSpy.Demo;

/// <summary>
/// M11 自检：**UWP 应用真实名修正**（开发文档 §5.5）。
///
/// 分三组：
///  * <b>A 解析规则（纯逻辑）</b>：两条路径（子窗口 / 顶层 + 标题配对）各自的正例与全部反例；
///    识别失败一律返回 None（**绝不猜测**）；成功后键 = 进程名小写、Kind 仍是 UwpHost、仍计入软件统计；
///    以及"键 → 名"的自洽性。
///  * <b>B 一致性（旧数据与重启口径）</b>：显示名必须**能只由键推出** ——
///    采样时的名字要与重启续算时（<c>AppNamingRules.EffectiveName</c>）逐字一致，
///    否则名字会在重启边界跳变；以及按决策**保留**旧 <c>applicationframehost</c> 行、**不新增 schema 版本**。
///  * <b>C 真实 Win32</b>：把解析器指向**真实的**窗口。核心是一条端到端断言：
///    对每个"可见的顶层 CoreWindow"，用**标题**找到它所属的宿主帧，断言解析器算出的应用
///    **就是那个窗口的进程** —— 这条会同时抓住"路径 2 没实现""配对条件写反""pid 过滤写错"等错误。
///
/// 为什么 A 组要有"路径 2"：M11 第一版按教科书写（只找子窗口），在 Windows 11 26H2 上
/// **编译通过、自检通过、实际永远认不出任何 UWP 应用**（子窗口里根本没有 CoreWindow）。
/// 这类"不报错的错"只能靠真实取证（<c>--diag-uwp</c>）发现，并固化成断言。
/// </summary>
internal static class M11SelfCheck
{
    private static readonly StringBuilder LogBuffer = new();
    private static string? _logPath;
    private static string? _dir;

    public static bool IsRequested(string[]? args) => HasFlag(args, "--m11-selfcheck");

    public static int Run(string[] args)
    {
        _logPath = GetString(args, "--log=");
        string dir = GetString(args, "--dir=") ?? Path.Combine("artifacts", "m11");
        _dir = Path.GetFullPath(dir);

        TextWriter original = Console.Out;
        try { Console.SetOut(new TeeWriter(original, LogBuffer)); } catch { /* 忽略 */ }

        int failed = 0;
        try
        {
            Console.WriteLine("M11 自检：UWP 应用真实名修正");
            Console.WriteLine("目录：" + _dir);
            Console.WriteLine();

            try { Directory.CreateDirectory(_dir); } catch { /* 忽略 */ }

            Console.WriteLine("A. 解析规则（纯逻辑）");
            failed += RuleChecks();

            Console.WriteLine();
            Console.WriteLine("B. 一致性（键 → 名 / 旧数据保留）");
            failed += ConsistencyChecks(Path.Combine(_dir, "legacy.db"));

            Console.WriteLine();
            Console.WriteLine("C. 真实 Win32");
            failed += Win32Checks();

            Console.WriteLine();
            Console.WriteLine(failed == 0
                ? "M11 自检：全部通过。"
                : $"M11 自检：有 {failed} 项失败。");
        }
        catch (Exception ex)
        {
            Console.WriteLine("自检自身异常：" + ex);
            failed++;
        }
        finally
        {
            try { Console.SetOut(original); } catch { /* 忽略 */ }
            WriteLogFile();
        }

        return failed == 0 ? 0 : 1;
    }

    // ================================================================ A 解析规则

    private static int RuleChecks()
    {
        int failed = 0;

        const int hostPid = 4000;

        // ---- A1 路径 1（子窗口）正常识别 ----
        var core = new List<UwpChildWindow>
        {
            new("ApplicationFrameTitleBarWindow", hostPid, "ApplicationFrameHost"),   // 宿主自己的标题栏
            new("Windows.UI.Core.CoreWindow", 4321, "Calculator"),                    // 真正的应用
        };

        UwpAppResolution r = UwpAppRules.ResolveInChildren(hostPid, core);
        failed += Check("路径1：CoreWindow + pid 与宿主不同 + 有进程名 → 成功", r.Resolved);
        failed += Check("路径1：键 = 进程名小写（calculator）", r.Key == "calculator");
        failed += Check("路径1：进程名保留原始大小写（Calculator）", r.ProcessName == "Calculator");
        failed += Check("路径1：展示名走友好名表（计算器）", r.DisplayName == "计算器");

        failed += Check("路径1 保守：非 CoreWindow 族的子窗口即使 pid 不同也不认（宁可暂不精确，不可错计）",
            !UwpAppRules.ResolveInChildren(hostPid, new List<UwpChildWindow>
            {
                new("IME", 9999, "SomeIme"),
            }).Resolved);
        failed += Check("路径1 次取：类名含 CoreWindow（防将来带前后缀）也能认",
            UwpAppRules.ResolveInChildren(hostPid, new List<UwpChildWindow>
            {
                new("Windows.UI.Core.CoreWindowEx", 4321, "Photos"),
            }).Resolved);

        failed += Check("路径1 不认：没有子窗口",
            !UwpAppRules.ResolveInChildren(hostPid, Array.Empty<UwpChildWindow>()).Resolved);
        failed += Check("路径1 不认：子窗口列表为 null（不抛异常）",
            !UwpAppRules.ResolveInChildren(hostPid, null).Resolved);
        failed += Check("路径1 不认：CoreWindow 的 pid 与宿主**相同**（那是宿主自己，不是应用）",
            !UwpAppRules.ResolveInChildren(hostPid, new List<UwpChildWindow>
            {
                new("Windows.UI.Core.CoreWindow", hostPid, "ApplicationFrameHost"),
            }).Resolved);
        failed += Check("路径1 不认：pid 取不到（<=0）",
            !UwpAppRules.ResolveInChildren(hostPid, new List<UwpChildWindow>
            {
                new("Windows.UI.Core.CoreWindow", 0, "Calculator"),
            }).Resolved);
        failed += Check("路径1 不认：进程名取不到（空串）→ 不猜测",
            !UwpAppRules.ResolveInChildren(hostPid, new List<UwpChildWindow>
            {
                new("Windows.UI.Core.CoreWindow", 4321, ""),
            }).Resolved);
        failed += Check("路径1 不认：进程名只有空白 → 同样不猜",
            !UwpAppRules.ResolveInChildren(hostPid, new List<UwpChildWindow>
            {
                new("Windows.UI.Core.CoreWindow", 4321, "   "),
            }).Resolved);
        failed += Check("路径1 优先：先取类名精确等于 CoreWindow 的那个",
            UwpAppRules.ResolveInChildren(hostPid, new List<UwpChildWindow>
            {
                new("Windows.UI.Core.CoreWindowEx", 1111, "Photos"),
                new("Windows.UI.Core.CoreWindow", 2222, "Calculator"),
            }).Key == "calculator");

        // ---- A2 路径 2（顶层 + 标题配对，Windows 11 26H2 实测形态） ----
        var topSettings = new List<UwpChildWindow>
        {
            new("Windows.UI.Core.CoreWindow", 9332, "SystemSettings", "设置", visible: true),
        };
        UwpAppResolution byTitle = UwpAppRules.ResolveByTitle(hostPid, "设置", topSettings);
        failed += Check("路径2：顶层 CoreWindow + 标题与宿主相同 → 成功", byTitle.Resolved);
        failed += Check("路径2：身份来自**进程名**（键 = systemsettings），不是标题",
            byTitle.Key == "systemsettings" && byTitle.ProcessName == "SystemSettings");
        failed += Check("路径2：展示名走友好名表（设置）", byTitle.DisplayName == "设置");

        failed += Check("路径2 不认：宿主标题为空 → 不做配对（空标题窗口太多，配上必错）",
            !UwpAppRules.ResolveByTitle(hostPid, "", topSettings).Resolved);
        failed += Check("路径2 不认：宿主标题与候选不同",
            !UwpAppRules.ResolveByTitle(hostPid, "计算器", topSettings).Resolved);
        failed += Check("路径2 不认：候选不可见",
            !UwpAppRules.ResolveByTitle(hostPid, "设置", new List<UwpChildWindow>
            {
                new("Windows.UI.Core.CoreWindow", 9332, "SystemSettings", "设置", visible: false),
            }).Resolved);
        failed += Check("路径2 不认：候选 pid 与宿主相同（宿主自己的 XAML 岛不算应用）",
            !UwpAppRules.ResolveByTitle(hostPid, "设置", new List<UwpChildWindow>
            {
                new("Windows.UI.Core.CoreWindow", hostPid, "ApplicationFrameHost", "设置", visible: true),
            }).Resolved);
        failed += Check("路径2 不认：候选列表为 null（不抛）",
            !UwpAppRules.ResolveByTitle(hostPid, "设置", null).Resolved);
        failed += Check("路径2 不认：**两个**同标题候选 → 分不清就不猜（宁可认不出，不可认错）",
            !UwpAppRules.ResolveByTitle(hostPid, "设置", new List<UwpChildWindow>
            {
                new("Windows.UI.Core.CoreWindow", 9332, "SystemSettings", "设置", visible: true),
                new("Windows.UI.Core.CoreWindow", 9333, "SomeOtherApp", "设置", visible: true),
            }).Resolved);
        failed += Check("路径2 不认：候选是\"非应用\"宿主进程（输入体验 TextInputHost）",
            !UwpAppRules.ResolveByTitle(hostPid, "Windows 输入体验", new List<UwpChildWindow>
            {
                new("Windows.UI.Core.CoreWindow", 17220, "TextInputHost", "Windows 输入体验", visible: true),
            }).Resolved);
        failed += Check("路径2 不认：类名不含 CoreWindow",
            !UwpAppRules.ResolveByTitle(hostPid, "设置", new List<UwpChildWindow>
            {
                new("SomeOtherClass", 9332, "SystemSettings", "设置", visible: true),
            }).Resolved);
        failed += Check("路径2：大小写与首尾空白不影响配对",
            UwpAppRules.ResolveByTitle(hostPid, "  设置 ", new List<UwpChildWindow>
            {
                new("Windows.UI.Core.CoreWindow", 9332, "SystemSettings", "设置", visible: true),
            }).Resolved);

        // ---- A3 各种"不认"（通用） ----
        failed += Check("不认：IdentityOf(\"\") → None", !UwpAppRules.IdentityOf("").Resolved);
        failed += Check("不认：IdentityOf(null) → None（不抛）", !UwpAppRules.IdentityOf(null).Resolved);
        failed += Check("None：就是 default（Resolved=false、键与名均为空）",
            !UwpAppResolution.None.Resolved
            && UwpAppResolution.None.Key.Length == 0
            && UwpAppResolution.None.DisplayName.Length == 0);

        // ---- A4 接进样本：Kind 不变、仍计入软件、键与名被替换 ----
        IntPtr h = new(777);
        ForegroundAppSample resolvedSample = ForegroundAppRules.Create(
            h, hostPid, "ApplicationFrameHost", "ApplicationFrameWindow", "设置", isSelfProcess: false,
            uwp: UwpAppRules.IdentityOf("SystemSettings"));

        failed += Check("样本：Kind 仍是 UwpHost（表示\"这一拍来自 UWP 宿主\"，诊断/计数用）",
            resolvedSample.Kind == ForegroundAppKind.UwpHost);
        failed += Check("样本：仍然计入软件统计（CountsAsApp）", resolvedSample.CountsAsApp);
        failed += Check("样本：归一键 = systemsettings（不再落到宿主）", resolvedSample.MergeKey == "systemsettings");
        failed += Check("样本：原始键也 = systemsettings（原始日志里能看出是哪个应用）",
            resolvedSample.RawKey == "systemsettings");
        failed += Check("样本：展示名 = 设置", resolvedSample.DisplayName == "设置");

        ForegroundAppSample unresolved = ForegroundAppRules.Create(
            h, hostPid, "ApplicationFrameHost", "ApplicationFrameWindow", "某个应用", isSelfProcess: false,
            uwp: UwpAppResolution.None);

        failed += Check("样本：识别失败时行为与 M11 之前完全一致（键退回 applicationframehost）",
            unresolved.MergeKey == "applicationframehost" && unresolved.RawKey == "applicationframehost");
        failed += Check("样本：识别失败时展示名 = 未能识别标注（不谎报成功）",
            unresolved.DisplayName == ForegroundAppRules.DisplayUwpUnresolved);

        // ---- A5 UWP 应用与桌面软件平权（同一命名空间） ----
        failed += Check("命名空间：UWP 的键与桌面软件同一形式（进程名小写，无前缀）",
            resolvedSample.MergeKey == ForegroundAppRules.MergeKeyOf("SystemSettings"));

        // ---- A6 身份层照常作用于 UWP 的真实键 ----
        AppIdentity merged = AppIdentity.Build(
            new[] { new KeyValuePair<string, string>("systemsettings", "系统设置") },
            null, null);
        ForegroundAppSample afterMerge = ForegroundAppRules.Create(
            h, hostPid, "ApplicationFrameHost", "ApplicationFrameWindow", "设置", isSelfProcess: false,
            identity: merged, uwp: UwpAppRules.IdentityOf("SystemSettings"));

        failed += Check("身份层：用户把 UWP 应用合并到别的键后，归一键跟着走",
            afterMerge.MergeKey == "系统设置" && afterMerge.RawKey == "systemsettings");
        failed += Check("身份层：合并后展示名按归一键再查一次（不显示生硬的键名）",
            afterMerge.DisplayName == "系统设置");

        return failed;
    }

    // ================================================================ B 一致性

    private static int ConsistencyChecks(string dbPath)
    {
        int failed = 0;

        string unresolvedName = ForegroundAppRules.DisplayUwpUnresolved;

        // ---- B1 键 → 名：采样时与重启续算时必须逐字相同 ----
        failed += Check("同源：未识别的键（applicationframehost）在身份层算出的名字 = 常量",
            AppNamingRules.EffectiveName(AppIdentity.Empty, "applicationframehost") == unresolvedName);
        failed += Check("同源：未识别的键在采样时的名字也 = 该常量",
            ForegroundAppRules.DisplayNameFor(ForegroundAppKind.UwpHost, "ApplicationFrameHost") == unresolvedName);
        failed += Check("同源：友好名表里 ApplicationFrameHost 指向同一常量（否则界面会是两个名字）",
            ForegroundAppRules.NormalizeDisplayName("applicationframehost") == unresolvedName);

        UwpAppResolution settings = UwpAppRules.IdentityOf("SystemSettings");
        failed += Check("同源：已识别的 UWP 应用，采样名 = 身份层算出的名字",
            settings.DisplayName == AppNamingRules.EffectiveName(AppIdentity.Empty, settings.Key));

        // 未收录的应用是**最容易露馅**的一种：显示名若用进程名原始大小写（FooBar），
        // 采样时显示 FooBar、重启后显示 foobar。故必须由键推出。
        UwpAppResolution unknown = UwpAppRules.IdentityOf("FooBar");
        failed += Check("同源：未收录的 UWP 应用也由键推出名字（不因大小写而跳变）",
            unknown.DisplayName == AppNamingRules.EffectiveName(AppIdentity.Empty, unknown.Key)
            && unknown.Key == "foobar");

        // ---- B2 旧数据按决策保留、且不新增 schema 版本 ----
        // 注意：这里清的是**文件**（dbPath），不是目录 —— 传目录会导致删不掉，
        // 从而每次运行都在旧库上再累加 3600 秒，断言变成"看运气"。自检必须可重复。
        SqliteStore.ClearPools();
        TryDelete(dbPath);
        try
        {
            var store = new SqliteStore(new StorageOptions { DatabasePath = dbPath });
            store.Initialize();

            failed += Check("存储：schema 版本仍是 3（M11 不迁移、不动表）", store.UserVersion() == 3);

            var today = DateOnly.FromDateTime(DateTime.Now);
            store.AddSeconds(new[] { new AppUsageRow(today, "applicationframehost", 3600) });

            long kept = store.SecondsOf(today, "applicationframehost");
            IReadOnlyList<AppUsageRow> rows = store.ReadDay(today);
            string dump = rows.Count == 0 ? "(无行)" : rows[0].ToString();

            failed += Check($"保留：旧的 applicationframehost 行**仍在库里**（按决策不删；SecondsOf={kept}s，ReadDay={dump}）",
                kept == 3600);

            failed += Check("保留：读回来仍是同一个键（历史数据展示为\"未能识别/旧合并\"）",
                rows.Count == 1 && rows[0].AppName == "applicationframehost");

            // 刻意断言"**没有**把旧行改写成真实应用名"——聚合数据已不知道当时跑的是哪个 UWP 应用，
            // 任何"自动迁移"都会是编造。
            failed += Check("保留：不会把旧行伪造成某个真实应用名（聚合数据无法回溯，迁移即编造）",
                store.SecondsOf(today, "systemsettings") == 0);
        }
        catch (Exception ex)
        {
            Console.WriteLine("   [FAIL] B 组异常：" + ex.Message);
            failed++;
        }
        finally
        {
            SqliteStore.ClearPools();
            TryDelete(dbPath);
        }

        return failed;
    }

    // ================================================================ C 真实 Win32

    private static int Win32Checks()
    {
        int failed = 0;

        // ---- C1 null 句柄不抛 ----
        failed += Check("真实：句柄为 0 时返回 None，且不抛",
            !UwpWindowResolver.Resolve(IntPtr.Zero, 0).Resolved);

        // ---- C2 拿一个**真实的**非 UWP 窗口：必须判为"不是 UWP 应用" ----
        IntPtr desktop = NativeMethods.FindWindow("Progman", null);
        if (desktop == IntPtr.Zero)
        {
            Console.WriteLine("   [SKIP] 真实：没找到桌面窗口（Progman），无法验证\"非 UWP 窗口不被误认\"。");
        }
        else
        {
            NativeMethods.GetWindowThreadProcessId(desktop, out int explorerPid);
            List<UwpChildWindow> children = UwpWindowResolver.CollectChildren(desktop, explorerPid);

            Console.WriteLine($"   桌面窗口 pid={explorerPid}，子窗口 {children.Count} 个。");
            failed += Check("真实：真实枚举不抛、且桌面窗口不被误认成 UWP 应用",
                !UwpWindowResolver.Resolve(desktop, explorerPid).Resolved);
        }

        // ---- C3 真实的 UWP 宿主窗口 ----
        var frames = new List<(IntPtr Hwnd, int Pid, string Title)>();
        try
        {
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (string.Equals(NativeMethods.ClassNameOf(hwnd), "ApplicationFrameWindow",
                                  StringComparison.OrdinalIgnoreCase))
                {
                    NativeMethods.GetWindowThreadProcessId(hwnd, out int pid);
                    frames.Add((hwnd, pid, NativeMethods.TitleOf(hwnd)));
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Console.WriteLine("   [SKIP] 真实：枚举顶层窗口失败（" + ex.Message + "）。");
        }

        // 顶层 CoreWindow 候选（可见的才算 —— 不可见的那些是外壳的 XAML 岛）
        List<UwpChildWindow> tops = UwpWindowResolver.CollectTopLevelCoreWindows(0);
        var visibleTops = new List<UwpChildWindow>();
        foreach (UwpChildWindow c in tops)
        {
            if (c.Visible && c.Title.Trim().Length > 0) visibleTops.Add(c);
        }

        Console.WriteLine($"   本机此刻：UWP 宿主窗口 {frames.Count} 个；可见且带标题的顶层 CoreWindow {visibleTops.Count} 个。");
        foreach ((IntPtr hwnd, int pid, string title) in frames)
        {
            List<UwpChildWindow> kids = UwpWindowResolver.CollectChildren(hwnd, pid);
            UwpAppResolution r = UwpWindowResolver.Resolve(hwnd, pid);
            Console.WriteLine($"     宿主 \"{title}\" pid={pid}（子窗口 {kids.Count} 个）→ {(r.Resolved ? r.ToString() : "未识别")}");
        }
        foreach (UwpChildWindow c in visibleTops) Console.WriteLine("     可见顶层 CoreWindow：" + c);

        if (frames.Count == 0)
        {
            Console.WriteLine("   [SKIP] 真实：此刻没有 UWP 应用在运行 → 无法做真实解析。");
            Console.WriteLine("          （想看这一条：先打开\"计算器\"或\"设置\"，再跑一次 --m11-selfcheck）");
        }
        else
        {
            int resolvedCount = 0;
            foreach ((IntPtr hwnd, int pid, string title) in frames)
            {
                UwpAppResolution r = UwpWindowResolver.Resolve(hwnd, pid);
                if (!r.Resolved) continue;

                resolvedCount++;
                failed += Check($"真实：识别出 {r.Key} → 名字与\"键推出的名字\"一致",
                    r.DisplayName == AppNamingRules.EffectiveName(AppIdentity.Empty, r.Key));
                failed += Check($"真实：识别出的键不是宿主名（{r.Key}）",
                    r.Key != ForegroundAppRules.UwpHostProcess);
            }
            Console.WriteLine($"   识别成功 {resolvedCount} / {frames.Count}。");

            // ---- C4 端到端：用**真实数据**把"应用窗口"与"它的宿主"配对，验证解析结果 ----
            // 这是本组最有价值的断言：它同时覆盖两条路径的选择、pid 过滤、可见性过滤与配对条件。
            int paired = 0;
            foreach (UwpChildWindow c in visibleTops)
            {
                // 找出标题与它相同、且唯一的宿主帧（可能有多个帧共用标题，那就不判定）
                IntPtr match = IntPtr.Zero;
                int matchCount = 0;
                foreach ((IntPtr hwnd, int pid, string title) in frames)
                {
                    if (!string.Equals(title.Trim(), c.Title.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                    match = hwnd;
                    matchCount++;
                }
                if (matchCount != 1) continue;

                paired++;
                NativeMethods.GetWindowThreadProcessId(match, out int framePid);
                UwpAppResolution r = UwpWindowResolver.Resolve(match, framePid);
                string expect = ForegroundAppRules.MergeKeyOf(c.ProcessName);

                failed += Check($"端到端：宿主 \"{c.Title}\"（pid={framePid}）解析出的应用 = 那个 CoreWindow 的进程（{expect}）",
                    r.Resolved && r.Key == expect);
            }

            if (paired == 0)
                Console.WriteLine("   [SKIP] 端到端：没有\"标题唯一对应\"的真实宿主/应用窗口配对可验。");
            else
                Console.WriteLine($"   端到端配对验证了 {paired} 组。");
        }

        return failed;
    }

    // ================================================================ 基础设施

    private static int Check(string label, bool ok)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        return ok ? 0 : 1;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
        try { if (File.Exists(path + "-wal")) File.Delete(path + "-wal"); } catch { /* 忽略 */ }
        try { if (File.Exists(path + "-shm")) File.Delete(path + "-shm"); } catch { /* 忽略 */ }
    }

    private static bool HasFlag(string[]? args, string flag)
    {
        if (args is null) return false;
        foreach (string a in args)
            if (a.Equals(flag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string? GetString(string[]? args, string prefix)
    {
        if (args is null) return null;
        foreach (string a in args)
            if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return a[prefix.Length..];
        return null;
    }

    private static void WriteLogFile()
    {
        string path = _logPath ?? Path.Combine(_dir ?? "artifacts", "m11", "m11-selfcheck.log");
        try
        {
            string full = Path.GetFullPath(path);
            string? dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(full, LogBuffer.ToString(), new UTF8Encoding(false));
            try { Console.WriteLine($"[日志] 全部输出已写入：{full}"); } catch { }
        }
        catch { /* 忽略 */ }
    }

    /// <summary>把输出同时写到原控制台与内存缓冲区（用于"控制台看不到"时的文件兜底）。</summary>
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
            try { _console.Write(value); } catch { /* 忽略 */ }
            _buffer.Append(value);
        }

        public override void Write(string? value)
        {
            try { _console.Write(value); } catch { /* 忽略 */ }
            _buffer.Append(value);
        }

        public override void WriteLine(string? value)
        {
            try { _console.WriteLine(value); } catch { /* 忽略 */ }
            _buffer.Append(value).Append('\n');
        }

        public override void WriteLine()
        {
            try { _console.WriteLine(); } catch { /* 忽略 */ }
            _buffer.Append('\n');
        }
    }
}
