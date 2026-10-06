using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;
using ScreenSpy.AppHost;
using ScreenSpy.Collector;
using ScreenSpy.Limits;
using ScreenSpy.Scheduling;
using ScreenSpy.Storage;
using ScreenSpy.Widget;

namespace ScreenSpy.Demo;

/// <summary>
/// M9-1 自检：**软件身份层**（进程名合并 + 分类）。
///
/// 分三组：
///  * <b>A 身份解析（纯逻辑）</b>：别名解析、显示名覆盖、分类、合并时的"改指 / 搬家"规则，
///    以及"过滤类伪键不受身份层影响"这条红线。
///  * <b>B 存储与迁移</b>：schema v3、v2 → v3 时**清空时间历史但保留 settings/limits**、
///    两列互不覆盖、删除分类的三件套、合并重写历史行。
///  * <b>C 合并的三处一致</b>：内存榜单 / 待写增量 / 库内行 —— 少做一处，今天就会多出一条账。
///  * <b>D 自定义名称（M9-1b）</b>：解析与合法性、**禁止重名**的三类撞车与两个"不放行就会把用户卡死"的不对称、
///    冲突时**一个字都不写库**、以及改一次名要在**四处**立刻生效（采样 / 榜单 / 卡片当前软件 / 重启续算）。
///  * <b>E 组合根接线（M9-1b）</b>：启动一个**真实 ProductRuntime**，验证改名的「成功 / 重名被拒」两条路径
///    与"跟谁重名"的反馈确实传到了上层 —— 防的是"函数写对了，但组合根没接上"。
///  * <b>F 限额纯逻辑（M9-2）</b>：阈值解析（含非法输入与往返）、**只取最高档**的跨阈值规则、
///    去重键的**字面量契约**、提醒文案、时长解析、规则构造的"跳过而不是猜"、分类求和、最紧急选取。
///  * <b>G 限额存储与去重（M9-2）</b>：<c>limits</c>/<c>settings</c> 的增删往返、守门的
///    "同一天同一档只提醒一次 / 重启不重发 / 跨天自动重发 / 改限额后重新提醒"、
///    以及**存储中途不可用时的内存去重兜底**（绝不刷屏）。
///  * <b>H 限额组合根与卡片（M9-2）</b>：真实 ProductRuntime 上的"设/删限额 → 卡片限额块出现/隐藏"闭环，
///    以及阈值非法时不写库。
///
/// 与既有自检一致：全部是**确定性**断言（假时钟 + 脚本化采样），不依赖你的实际操作。
/// </summary>
internal static class M9SelfCheck
{
    private static readonly StringBuilder LogBuffer = new();
    private static string? _logPath;
    private static string? _dir;

    public static bool IsRequested(string[]? args) => HasFlag(args, "--m9-selfcheck");

    public static int Run(string[] args)
    {
        _logPath = GetString(args, "--log=");
        string dir = GetString(args, "--dir=") ?? Path.Combine("artifacts", "m9");
        _dir = Path.GetFullPath(dir);

        TextWriter original = Console.Out;
        try { Console.SetOut(new TeeWriter(original, LogBuffer)); } catch { /* 忽略 */ }

        int failed = 0;
        try
        {
            Console.WriteLine("M9 自检：软件身份层（M9-1）+ 自定义名称（M9-1b）+ 限额引擎（M9-2）");
            Console.WriteLine("目录：" + _dir);
            Console.WriteLine();

            try { Directory.CreateDirectory(_dir); } catch { /* 忽略 */ }

            Console.WriteLine("A. 身份解析（纯逻辑）");
            failed += IdentityChecks();

            Console.WriteLine();
            Console.WriteLine("B. 存储与迁移（schema v3）");
            failed += StorageChecks(Path.Combine(_dir, "identity.db"));

            Console.WriteLine();
            Console.WriteLine("C. 合并的三处一致（内存 / 待写 / 库）");
            failed += MergeChecks(Path.Combine(_dir, "merge.db"));

            Console.WriteLine();
            Console.WriteLine("D. 自定义名称（纯规则 / 存储 / 四处立即生效）");
            failed += RenameChecks(Path.Combine(_dir, "rename.db"));

            Console.WriteLine();
            Console.WriteLine("E. 组合根改名接线（真实 ProductRuntime）");
            failed += RenameRootChecks(Path.Combine(_dir, "rename-root"));

            Console.WriteLine();
            Console.WriteLine("F. 限额纯逻辑（阈值 / 去重键 / 文案 / 时长解析 / 规则构造）");
            failed += LimitLogicChecks();

            Console.WriteLine();
            Console.WriteLine("G. 限额存储与去重（limits / settings / 守门）");
            failed += LimitStorageChecks(Path.Combine(_dir, "limits.db"));

            Console.WriteLine();
            Console.WriteLine("H. 限额组合根与卡片（真实 ProductRuntime）");
            failed += LimitRootChecks(Path.Combine(_dir, "limit-root"));

            Console.WriteLine();
            Console.WriteLine(failed == 0
                ? "M9 自检：全部通过。"
                : $"M9 自检：有 {failed} 项失败。");
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

    // ================================================================ A 身份解析

    private static int IdentityChecks()
    {
        int failed = 0;

        // ---- A1 空身份：一切解析为自身 ----
        AppIdentity empty = AppIdentity.Empty;
        failed += Check("空身份：未合并的进程名解析为自身", empty.Resolve("devenv") == "devenv");
        failed += Check("空身份：无显示名覆盖、无分类",
            empty.DisplayNameOverride("devenv") is null && empty.CategoryOf("devenv").Length == 0);
        failed += Check("空身份：空串安全（不抛异常）", empty.Resolve(null) == string.Empty);

        // ---- A2 别名解析 ----
        AppIdentity merged = AppIdentity.Build(
            new[] { new KeyValuePair<string, string>("code", "vscode") },
            new[] { new KeyValuePair<string, (string, string)>("vscode", ("VS Code", "工作")) },
            new[] { "工作" });

        failed += Check("别名：code → vscode", merged.Resolve("code") == "vscode");
        failed += Check("别名：未登记的键仍解析为自身", merged.Resolve("chrome") == "chrome");
        failed += Check("显示名覆盖生效", merged.DisplayNameOverride("vscode") == "VS Code");
        failed += Check("分类读取生效", merged.CategoryOf("vscode") == "工作");
        failed += Check("分类：未设置返回空串（“未分类”不是一个分类）",
            merged.CategoryOf("chrome").Length == 0);
        failed += Check("分类表内容正确", merged.Categories.Count == 1 && merged.Categories[0] == "工作");

        // ---- A3 自指别名必须被丢弃（否则 Resolve 的语义会变得没有意义）----
        AppIdentity selfAlias = AppIdentity.Build(
            new[] { new KeyValuePair<string, string>("chrome", "chrome") }, null, null);
        failed += Check("自指别名被丢弃：chrome → chrome 不生效", selfAlias.Resolve("chrome") == "chrome");
        failed += Check("自指别名不进别名表", selfAlias.Aliases.Count == 0);

        // ---- A4 合并时的“改指”：不允许出现 a→b→c 的链 ----
        AppIdentity chain = AppIdentity.Empty
            .WithAlias("a", "b")
            .WithMerged("b", "c");
        failed += Check("合并改指：既有别名 a→b 会改成 a→c（维持单跳语义）", chain.Resolve("a") == "c");
        failed += Check("合并改指：b→c 本身生效", chain.Resolve("b") == "c");

        // ---- A5 合并时的“搬家”：分类/显示名只在目标为空时才补 ----
        AppIdentity moveInto = AppIdentity.Empty
            .WithCategory("b", "工作")
            .WithCategory("c", string.Empty)
            .WithMerged("b", "c");
        failed += Check("合并搬家：目标无分类时，源的分类补过去", moveInto.CategoryOf("c") == "工作");
        failed += Check("合并搬家：源不再保留分类", moveInto.CategoryOf("b").Length == 0);

        AppIdentity keepTarget = AppIdentity.Empty
            .WithCategory("b", "工作")
            .WithCategory("c", "娱乐")
            .WithMerged("b", "c");
        failed += Check("合并搬家：目标已有分类时**不覆盖**（不静默丢掉用户的选择）",
            keepTarget.CategoryOf("c") == "娱乐");

        // ---- A6 分类的增删 ----
        failed += Check("新建分类：重复添加只留一条",
            AppIdentity.Empty.WithCategoryAdded("工作").WithCategoryAdded("工作").Categories.Count == 1);
        failed += Check("删除分类：该分类下的软件回到未分类",
            AppIdentity.Empty.WithCategory("k", "工作").WithCategoryRemoved("工作").CategoryOf("k").Length == 0);

        // ---- A7 归属点解析（唯一解析点）----
        ForegroundAppSample devenv = ForegroundAppRules.Create(
            new IntPtr(1), 10, "devenv", "HwndWrapper", "x", isSelfProcess: false, identity: merged);
        failed += Check("归属点：未合并的软件 键=自身、无覆盖名",
            devenv.MergeKey == "devenv" && devenv.RawKey == "devenv");

        ForegroundAppSample code = ForegroundAppRules.Create(
            new IntPtr(1), 10, "Code", "SomeClass", "x", isSelfProcess: false, identity: merged);
        failed += Check("归属点：合并后的软件 键=vscode、原始键=code",
            code.MergeKey == "vscode" && code.RawKey == "code");
        failed += Check("归属点：显示名取用户的覆盖（VS Code）", code.DisplayName == "VS Code");
        failed += Check("归属点：仍是 App 类（计入软件），分类不受影响", code.CountsAsApp);

        // 合并后没有显示名覆盖时，按**归一键**再查一次友好名（否则会把 VS Code 显示成生硬的 vscode）
        AppIdentity aliasOnly = AppIdentity.Build(
            new[] { new KeyValuePair<string, string>("code-insiders", "devenv") }, null, null);
        ForegroundAppSample insiders = ForegroundAppRules.Create(
            new IntPtr(1), 11, "Code-Insiders", "C", "x", isSelfProcess: false, identity: aliasOnly);
        failed += Check("归属点：按归一键查友好名（devenv → Visual Studio）",
            insiders.MergeKey == "devenv" && insiders.DisplayName == "Visual Studio");

        // 红线：过滤类用伪键，**不受**身份层影响（否则用户能把"桌面"合并成某个软件，守恒就乱了）
        ForegroundAppSample desktop = ForegroundAppRules.Create(
            new IntPtr(2), 20, "explorer", "Progman", "", isSelfProcess: false, identity: merged);
        failed += Check("红线：过滤类（桌面）不受身份层影响，键仍为 #desktop",
            desktop.MergeKey == "#desktop" && desktop.RawKey == "#desktop");
        failed += Check("红线：过滤类不因身份层而变成“计入软件”", !desktop.CountsAsApp);

        // UWP 宿主同样参与身份层（它会被计入软件）
        AppIdentity uwp = AppIdentity.Build(
            new[] { new KeyValuePair<string, string>("applicationframehost", "calculator") },
            new[] { new KeyValuePair<string, (string, string)>("calculator", ("计算器", "工具")) },
            new[] { "工具" });
        ForegroundAppSample calc = ForegroundAppRules.Create(
            new IntPtr(3), 30, "ApplicationFrameHost", "C", "计算器", isSelfProcess: false, identity: uwp);
        failed += Check("UWP 宿主也参与身份层（键=calculator、名=计算器）",
            calc.MergeKey == "calculator" && calc.DisplayName == "计算器" && calc.CountsAsApp);

        return failed;
    }

    // ================================================================ B 存储与迁移

    private static int StorageChecks(string dbPath)
    {
        int failed = 0;
        TryCleanDatabases(Path.GetDirectoryName(dbPath) ?? ".");
        SqliteStore.ClearPools();

        // ---- B1 新库：版本与表 ----
        var options = new StorageOptions { DatabasePath = dbPath };
        using (var store = new SqliteStore(options))
        {
            store.Initialize();

            failed += Check("新库：user_version == 3", store.UserVersion() == 3);

            IReadOnlyList<string> tables = store.ListTables();
            failed += Check("新库：七张表齐备（含 app_alias / app_meta / categories）",
                Contains(tables, "app_usage") && Contains(tables, "daily_activity") &&
                Contains(tables, "settings") && Contains(tables, "limits") &&
                Contains(tables, "app_alias") && Contains(tables, "app_meta") &&
                Contains(tables, "categories"));
        }

        // ---- B2 两列互不覆盖（同一行的两个独立设置）----
        using (var store = new SqliteStore(options))
        {
            store.Initialize();
            store.SetAppCategory("vscode", "工作");
            store.SetAppDisplayName("vscode", "VS Code");

            IReadOnlyList<AppMetaRow> meta = store.ReadAppMeta();
            bool bothKept = meta.Count == 1 &&
                            meta[0].Category == "工作" && meta[0].DisplayName == "VS Code";
            failed += Check("app_meta：设分类不冲掉显示名、设显示名不冲掉分类（两列独立）", bothKept);

            // 只改一列时，另一列必须原样保留
            store.SetAppCategory("vscode", "娱乐");
            AppMetaRow afterCategory = store.ReadAppMeta()[0];
            failed += Check("app_meta：改分类后显示名仍在",
                afterCategory.Category == "娱乐" && afterCategory.DisplayName == "VS Code");

            store.SetAppDisplayName("vscode", string.Empty);
            AppMetaRow afterName = store.ReadAppMeta()[0];
            failed += Check("app_meta：清显示名不影响分类",
                afterName.Category == "娱乐" && afterName.DisplayName.Length == 0);

            // ---- B3 分类：重名不报错、删除时三件套一起做 ----
            store.UpsertCategory("娱乐");
            store.UpsertCategory("娱乐");
            failed += Check("分类：重名新建只留一条（重复不是错误）", store.ReadCategories().Count == 1);

            store.SetLimit("category", "娱乐", 3600);
            bool removed = store.DeleteCategory("娱乐");
            AppMetaRow cleared = store.ReadAppMeta()[0];
            failed += Check("删除分类：返回“确实删掉了”", removed);
            failed += Check("删除分类：该分类下的软件回到未分类", cleared.Category.Length == 0);
            failed += Check("删除分类：分类行已删除", store.ReadCategories().Count == 0);
            failed += Check("删除分类：该分类的限额一并删除（不留“看不见却仍生效”的限额）",
                FindLimit(store.ReadLimits(), "category", "娱乐") < 0);
        }

        // ---- B4 合并重写历史行：跨天并入、同日累加、源行删除、别名登记、元信息搬家 ----
        using (var store = new SqliteStore(options))
        {
            store.Initialize();
            DateOnly d1 = new(2026, 10, 5);
            DateOnly d2 = new(2026, 10, 6);
            store.AddSeconds(new[]
            {
                new AppUsageRow(d1, "code", 600),
                new AppUsageRow(d2, "code", 300),
            });
            store.AddSeconds(new[]
            {
                new AppUsageRow(d2, "devenv", 100),   // 目标在 d2 已有行 → 必须累加而不是覆盖
            });

            store.SetAppCategory("code", "工作");
            store.SetAppDisplayName("devenv", "Visual Studio");   // 目标已有显示名 → 不该被源的冲掉

            int days = store.MergeAppKey("code", "devenv");

            failed += Check("合并：返回被并入的天数（2 天）", days == 2);
            failed += Check("合并：源行已删除（code 无任何行）",
                store.SecondsOf(d1, "code") == 0 && store.SecondsOf(d2, "code") == 0);
            failed += Check("合并：跨天并入目标（10-05 = 600s）", store.SecondsOf(d1, "devenv") == 600);
            failed += Check("合并：同日**累加**而不是覆盖（10-06 = 100 + 300 = 400s）",
                store.SecondsOf(d2, "devenv") == 400);
            failed += Check("合并：登记了别名 code → devenv", AliasTarget(store.ReadAppAliases(), "code") == "devenv");
            failed += Check("合并：源的分类搬到目标", CategoryOf(store.ReadAppMeta(), "devenv") == "工作");
            failed += Check("合并：目标已有的显示名不被源的覆盖",
                DisplayNameOf(store.ReadAppMeta(), "devenv") == "Visual Studio");

            failed += Check("合并：自合并（code → code）安全返回 0", store.MergeAppKey("code", "code") == 0);
            failed += Check("合并：空键安全返回 0", store.MergeAppKey("", "devenv") == 0);
        }

        // ---- B5 v2 → v3 迁移：清空时间历史，但保留 settings / limits ----
        string legacy = dbPath + ".legacy.db";
        TryDelete(legacy);
        CreateLegacyV2Database(legacy);

        using (var legacyStore = new SqliteStore(new StorageOptions { DatabasePath = legacy }))
        {
            legacyStore.Initialize();

            failed += Check("迁移：user_version 升到 3", legacyStore.UserVersion() == 3);
            failed += Check("迁移：app_usage 历史被清空（按决策“从今天重新开始”）",
                legacyStore.TotalSecondsOfDay(new DateOnly(2026, 10, 5)) == 0);
            failed += Check("迁移：daily_activity 历史被清空",
                legacyStore.ReadDayActivity(new DateOnly(2026, 10, 5)).FilteredSeconds == 0);
            failed += Check("迁移：settings **必须保留**（卡片形态/坐标是用户设置，不是时间数据）",
                legacyStore.GetSetting("widget_pos_x") == "321");
            failed += Check("迁移：limits **必须保留**（限额是用户设置）",
                FindLimit(legacyStore.ReadLimits(), "total", string.Empty) == 7200);
            failed += Check("迁移：新表已就位（app_alias / app_meta / categories）",
                Contains(legacyStore.ListTables(), "app_alias") &&
                Contains(legacyStore.ListTables(), "app_meta") &&
                Contains(legacyStore.ListTables(), "categories"));
        }

        return failed;
    }

    // ================================================================ C 合并的三处一致

    private static int MergeChecks(string dbPath)
    {
        int failed = 0;
        TryCleanDatabases(Path.GetDirectoryName(dbPath) ?? ".");
        SqliteStore.ClearPools();

        DateOnly day = new(2026, 10, 6);
        using var h = new Harness(dbPath, day, flushInterval: TimeSpan.FromMinutes(10));

        // 先“续算”一段基线（模拟今天早些时候已经落库的 code）。
        // ⚠️ 必须在**观测之前**：Seed 会重置当日状态（生产代码也是在 Start() 之前播种），
        //    反过来做会把刚观测到的量冲掉 —— 我第一次就是这么写错的，自检因此报了两项 FAIL。
        h.Bridge.Tracker.Seed(day, new[] { new AppUsageSeed("code", "code", 5000) },
                              TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(0));

        // 两拍 code —— 不 Start 定时器，完全靠手动 Advance，避免与真实定时器抢样本。
        h.Source.Push(App("code"));
        h.Advance(1000);
        h.Advance(1000);

        // 用**相对量**断言：首拍是否会计满取决于调度器的基准建立方式，写死数字会让自检变脆。
        long runMs = Ms(h.Bridge.Tracker.AttributedTotal);
        long beforeTotal = h.Bridge.Tracker.TotalMillisecondsOf("code");
        failed += Check($"前置：本次运行已计入 {runMs}ms（>0）且基线 5000ms 也在", runMs > 0);
        failed += Check($"合并前：code 的今日总量 {beforeTotal}ms == 本次运行 {runMs}ms + 基线 5000ms",
            beforeTotal == runMs + 5000);

        // ---- C1 内存榜单重键 ----
        h.Bridge.Tracker.MergeKeys("code", "vscode", "VS Code");
        long afterMerge = h.Bridge.Tracker.TotalMillisecondsOf("vscode");
        failed += Check($"内存重键：vscode 得到 {afterMerge}ms == 本次运行 + 基线（{runMs} + 5000）",
            afterMerge == runMs + 5000);
        failed += Check("内存重键：code 桶已消失（今天不会再出现两行）",
            h.Bridge.Tracker.TotalMillisecondsOf("code") == 0);

        // ---- C2 待写增量重键 ----
        // 这时两拍仍**未落库**（flush 间隔 10 分钟），因此待写里是 code 的两秒。
        long pendingBefore = h.Store.PendingMilliseconds;
        failed += Check("待写：两拍确实还在待写缓冲里（未落库）", pendingBefore >= 2000);

        h.Store.MergePendingKeys("code", "vscode", "VS Code");

        bool codeGone = true;
        foreach ((DateOnly day2, string key, long ms, string _) in h.Store.PendingSnapshot())
        {
            if (string.Equals(key, "code", StringComparison.Ordinal) && ms > 0) codeGone = false;
        }

        failed += Check("待写重键：缓冲里已没有 code", codeGone);
        failed += Check("待写重键：总毫秒数不变（只是换了键，没丢也没多）",
            h.Store.PendingMilliseconds == pendingBefore);

        // ---- C3 落库后：库里只有 vscode 一行 ----
        h.Store.Flush();

        IReadOnlyList<AppUsageRow> rows = h.Store.Store.ReadDay(day);
        bool onlyVscode = rows.Count == 1 && rows[0].AppName == "vscode";
        failed += Check("落库后：当天只有 vscode 一行（没有残留的 code 行）", onlyVscode);

        long expectedSeconds = pendingBefore / 1000;
        failed += Check($"落库后：vscode 的秒数 == 待写的整秒数（{expectedSeconds}s；基线本就不在待写里）",
            onlyVscode && rows[0].Seconds == expectedSeconds);

        // ---- C4 续算按用户设置恢复（归一键 + 覆盖显示名）----
        var seeded = new AppUsageTracker();
        AppIdentity identity = AppIdentity.Build(
            new[] { new KeyValuePair<string, string>("code", "vscode") },
            new[] { new KeyValuePair<string, (string, string)>("vscode", ("VS Code", "工作")) },
            new[] { "工作" });
        UsageSeeding.SeedTracker(seeded, rows, day, 0, 0, identity);

        failed += Check("续算：库中 vscode → 榜单键 vscode（秒数一致）",
            seeded.TotalMillisecondsOf("vscode") == expectedSeconds * 1000);

        IReadOnlyList<AppUsageEntry> seededTop = seeded.Top(1);
        failed += Check("续算：展示名取用户的覆盖（VS Code）",
            seededTop.Count == 1 && seededTop[0].DisplayName == "VS Code");

        return failed;
    }

    // ================================================================ D 自定义名称（M9-1b）

    /// <summary>
    /// M9-1b「自定义软件名称」：纯规则（解析 / 合法性 / 禁止重名）、存储（冲突不写库、清空即恢复默认名），
    /// 以及"改一次名要在**四处**同时生效"。
    /// </summary>
    private static int RenameChecks(string dbPath)
    {
        int failed = 0;
        TryCleanDatabases(Path.GetDirectoryName(dbPath) ?? ".");
        SqliteStore.ClearPools();

        DateOnly day = new(2026, 10, 6);

        // ---- D1 解析与合法性（纯逻辑）----
        failed += Check("解析：空值 → 系统默认名（devenv → Visual Studio）",
            AppNamingRules.ResolveName("devenv", string.Empty) == "Visual Studio");
        failed += Check("解析：纯空白同样视为“恢复默认名”",
            AppNamingRules.ResolveName("devenv", "   ") == "Visual Studio");
        failed += Check("解析：友好名表里没有的键 → 用键自身",
            AppNamingRules.ResolveName("foobar", string.Empty) == "foobar");
        failed += Check("合法性：正常名字通过", AppNamingRules.ValidateName("我的编辑器") is null);
        failed += Check($"合法性：超过 {AppNamingRules.MaxDisplayNameLength} 个字符被拒",
            AppNamingRules.ValidateName(new string('x', AppNamingRules.MaxDisplayNameLength + 1)) is not null);
        failed += Check("合法性：含换行被拒", AppNamingRules.ValidateName("上\n下") is not null);

        // ---- D2 禁止重名（三类撞车都要挡住，且不能把用户卡死）----
        AppIdentity id = AppIdentity.Build(
            new[] { new KeyValuePair<string, string>("code", "vscode") },
            new[] { new KeyValuePair<string, (string, string)>("mspaint", ("我的画板", string.Empty)) },
            null);

        string[] known = { "vscode", "chrome" };

        failed += Check("重名：与别的软件**用户设过的名字**撞（它今天没出现也算）",
            AppNamingRules.FindConflict(id, "vscode", "我的画板", known) == "mspaint");
        failed += Check("重名：与**系统默认名**撞（devenv 默认就叫 Visual Studio）",
            AppNamingRules.FindConflict(id, "chrome", "Visual Studio", known) == "devenv");
        failed += Check("重名：与**今日出现的软件**的名字撞（不在友好名表里也一样）",
            AppNamingRules.FindConflict(id, "chrome", "vscode", known) == "vscode");
        failed += Check("重名：大小写不敏感（notepad 改成 chrome 会撞 Chrome）",
            AppNamingRules.FindConflict(id, "notepad", "chrome", new[] { "notepad" }) == "chrome");
        failed += Check("重名：改成**自己当前的名字**不算冲突（合法的空操作）",
            AppNamingRules.FindConflict(id, "chrome", "Chrome", new[] { "chrome" }).Length == 0);
        failed += Check("重名：全新名字无冲突",
            AppNamingRules.FindConflict(id, "chrome", "我的浏览器", new[] { "chrome" }).Length == 0);

        failed += Check("预检：重名时不可保存，且说明里点出是跟谁撞了",
            AppNamingRules.Validate(id, "chrome", "我的画板", new[] { "chrome" }) is { } conflictMessage &&
            conflictMessage.Contains("mspaint"));

        // 两个"不放行就会把用户卡死"的不对称 —— 都是刻意设计，这里把它们钉死
        failed += Check("预检：本来就是默认名时，「恢复默认名」是空操作 → 放行（否则用户会卡在死角）",
            AppNamingRules.Validate(id, "chrome", string.Empty, new[] { "chrome" }) is null);
        failed += Check("预检：默认名与**别人的默认名**相同仍允许恢复（改名不背名字表的锅）",
            AppNamingRules.Validate(id.WithDisplayName("calc", "临时名"), "calc", string.Empty, new[] { "calc" }) is null);
        failed += Check("预检：恢复默认名会撞上**别人的自定义名** → 拒绝并说明原因",
            AppNamingRules.Validate(
                id.WithDisplayName("notepad", "临时名").WithDisplayName("mspaint", "记事本"),
                "notepad", string.Empty, new[] { "notepad", "mspaint" }) is { } restoreMessage &&
            restoreMessage.Contains("mspaint"));

        // ---- D3 存储：写入 / 冲突不写 / 清空即恢复默认名 ----
        var options = new StorageOptions { DatabasePath = dbPath };
        using (var store = new SqliteStore(options))
        {
            store.Initialize();

            AppIdentity identity = AppIdentityStore.Load(store);
            identity = AppIdentityStore.SetCategory(store, identity, "chrome", "工作");
            identity = AppIdentityStore.SetDisplayName(store, identity, "chrome", "我的浏览器",
                new[] { "chrome" }, out string conflict1);

            failed += Check("存储：改名写进身份层", identity.DisplayNameOverride("chrome") == "我的浏览器");
            failed += Check("存储：无冲突时 conflictKey 为空", conflict1.Length == 0);

            IReadOnlyList<AppMetaRow> meta = store.ReadAppMeta();
            failed += Check("存储：app_meta 已写入，且**不动分类**（两列独立）",
                meta.Count == 1 && meta[0].AppKey == "chrome" &&
                meta[0].DisplayName == "我的浏览器" && meta[0].Category == "工作");

            // 冲突：必须**一个字都不写**
            AppIdentity refused = AppIdentityStore.SetDisplayName(store, identity, "notepad", "我的浏览器",
                new[] { "chrome", "notepad" }, out string conflict2);

            failed += Check("存储：冲突时指出冲突键 chrome", conflict2 == "chrome");
            failed += Check("存储：冲突时**不写库**（app_meta 仍只有一行）", store.ReadAppMeta().Count == 1);
            failed += Check("存储：冲突时返回的身份与传入的完全相同（notepad 没有得到名字）",
                refused.DisplayNameOverride("notepad") is null);

            // 清空 = 恢复系统默认名
            identity = AppIdentityStore.SetDisplayName(store, identity, "chrome", string.Empty,
                new[] { "chrome" }, out string conflict3);

            failed += Check("存储：清空覆盖后 conflictKey 为空", conflict3.Length == 0);
            failed += Check("存储：清空覆盖 → 回到系统默认名（Chrome）",
                identity.DisplayNameOverride("chrome") is null &&
                AppNamingRules.EffectiveName(identity, "chrome") == "Chrome");

            IReadOnlyList<AppMetaRow> afterClear = store.ReadAppMeta();
            failed += Check("存储：清空后 display_name 为空（不残留旧名字），分类仍在",
                afterClear.Count == 1 && afterClear[0].DisplayName.Length == 0 &&
                afterClear[0].Category == "工作");
        }

        // ---- D4 改一次名，四处同时生效 ----
        AppIdentity named = AppIdentity.Empty.WithDisplayName("chrome", "我的浏览器");

        ForegroundAppSample sample = ForegroundAppRules.Create(
            new IntPtr(1), 1, "chrome", "SomeClass", "t", isSelfProcess: false, identity: named);
        failed += Check("生效①采样（今后）：立刻用新名，键不变",
            sample.DisplayName == "我的浏览器" && sample.MergeKey == "chrome");

        var tracker = new AppUsageTracker();
        tracker.Seed(day, new[] { new AppUsageSeed("chrome", "Chrome", 3000) });
        bool renamed = tracker.SetDisplayName("chrome", "我的浏览器");
        IReadOnlyList<AppUsageEntry> top = tracker.Top(1);
        failed += Check("生效②榜单（现在）：那一行立刻改名",
            renamed && top.Count == 1 && top[0].DisplayName == "我的浏览器");
        failed += Check("生效②榜单：改名**不动毫秒**（仍 3000ms）",
            top.Count == 1 && top[0].Milliseconds == 3000);

        var live = new AppUsageTracker();
        live.Seed(day, Array.Empty<AppUsageSeed>());
        live.Observe(App("chrome"), TimeSpan.FromSeconds(1), true, day.ToDateTime(new TimeOnly(12, 0, 0)));
        live.SetDisplayName("chrome", "我的浏览器");
        failed += Check("生效③卡片“当前软件”：立刻改名（不必等它再次成为前台）",
            live.CurrentDisplayName == "我的浏览器");

        var seeded = new AppUsageTracker();
        UsageSeeding.SeedTracker(seeded, new[] { new AppUsageRow(day, "chrome", 60) }, day, 0, 0, named);
        IReadOnlyList<AppUsageEntry> seededTop = seeded.Top(1);
        failed += Check("生效④重启续算：仍是新名（不回退成 Chrome）",
            seededTop.Count == 1 && seededTop[0].DisplayName == "我的浏览器");

        return failed;
    }

    // ================================================================ E 组合根改名接线

    /// <summary>
    /// M9-1b 的**组合根接线**：启动一个真实 <c>ProductRuntime</c>，验证改名的
    /// 「成功 / 重名被拒」两条路径，以及"跟谁重名"的反馈确实传到了上层。
    ///
    /// 为什么单独有这一组：本类缺陷的主要形态是"函数写对了，但组合根没接上"——
    /// 本项目已经栽过两次（M5b 的 Snapshot 少填一个字段、M6 的派生值忘了填），
    /// 那两次都是**不报错、只是显示错**。这一组用真实实例把这条路径钉住。
    ///
    /// 单实例互斥体已被占用时（产品正在托盘运行）**跳过而非判失败**：那是环境限制，不是缺陷。
    /// </summary>
    private static int RenameRootChecks(string dataDir)
    {
        int failed = 0;

        TryCleanDatabases(dataDir);
        SqliteStore.ClearPools();
        try { Directory.CreateDirectory(dataDir); } catch { /* 忽略 */ }

        StartupOptions options = StartupOptions.Parse(
            new[] { "--data-dir=" + dataDir, "--no-raw-log", "--probe-ms=0" });

        HostStartResult start = ProductRuntime.Start(options);
        if (start.Outcome == HostStartOutcome.AlreadyRunning)
        {
            Console.WriteLine("   [SKIP] 已有 ScreenSpy 实例在运行，无法独占单实例互斥体 —— 跳过。");
            Console.WriteLine("   小计：0 项失败（跳过）");
            return 0;
        }

        if (start.Outcome != HostStartOutcome.Started || start.Runtime is null)
        {
            Console.WriteLine("   [FAIL] 无法启动 ProductRuntime：" + (start.Message ?? "(无说明)"));
            Console.WriteLine("   小计：1 项失败");
            return 1;
        }

        try
        {
            ProductRuntime runtime = start.Runtime;

            failed += Check("组合根：默认名（devenv → Visual Studio）",
                runtime.DefaultDisplayName("devenv") == "Visual Studio");

            failed += Check("组合根：预检放行全新名字", runtime.ValidateRename("chrome", "我的浏览器") is null);
            failed += Check("组合根：预检拦住重名（Visual Studio 是 devenv 的默认名）",
                runtime.ValidateRename("chrome", "Visual Studio") is not null);

            AppRenameResult first = runtime.RenameApp("chrome", "我的浏览器", out string key1);
            failed += Check("组合根：改名成功（Ok）且无冲突方",
                first == AppRenameResult.Ok && key1.Length == 0);
            failed += Check("组合根：改完后名字真的生效了（DescribeApp）",
                runtime.DescribeApp("chrome").Contains("我的浏览器"));

            AppRenameResult second = runtime.RenameApp("notepad", "我的浏览器", out string key2);
            failed += Check("组合根：重名被拒（Conflict）且指出是 chrome",
                second == AppRenameResult.Conflict && key2 == "chrome");
            failed += Check("组合根：被拒后 chrome 的名字没被改动（没有写一半）",
                runtime.DescribeApp("chrome").Contains("我的浏览器"));

            AppRenameResult third = runtime.RenameApp("chrome", string.Empty, out string key3);
            failed += Check("组合根：恢复默认名成功，且回到 Chrome",
                third == AppRenameResult.Ok && key3.Length == 0 &&
                runtime.DescribeApp("chrome").Contains("Chrome"));
        }
        finally
        {
            try { start.Runtime.Dispose(); } catch { /* 忽略 */ }
        }

        return failed;
    }

    // ================================================================ F 限额纯逻辑

    private static int LimitLogicChecks()
    {
        int failed = 0;

        // ---- F1 阈值解析 ----
        failed += Check("阈值：默认 80,100 → 0.8 / 1.0（升序）",
            LimitRules.TryParseThresholds("80,100", out double[] t1, out _) &&
            t1.Length == 2 && Near(t1[0], 0.8) && Near(t1[1], 1.0));
        failed += Check("阈值：中文逗号与百分号也接受",
            LimitRules.TryParseThresholds("80%， 100", out double[] t2, out _) && t2.Length == 2);
        failed += Check("阈值：去重并升序（100,80,80 → 0.8,1.0）",
            LimitRules.TryParseThresholds("100,80,80", out double[] t3, out _) &&
            t3.Length == 2 && Near(t3[0], 0.8) && Near(t3[1], 1.0));
        failed += Check("阈值：空串被拒且给出原因",
            !LimitRules.TryParseThresholds("  ", out _, out string? e1) && !string.IsNullOrEmpty(e1));
        failed += Check("阈值：0 被拒（否则“恰好 0%”也会提醒）",
            !LimitRules.TryParseThresholds("0", out _, out _));
        failed += Check("阈值：101 被拒", !LimitRules.TryParseThresholds("101", out _, out _));
        failed += Check("阈值：非数字被拒并指出是哪一个",
            !LimitRules.TryParseThresholds("80,abc", out _, out string? e2) && (e2 ?? string.Empty).Contains("abc"));
        failed += Check("阈值：格式化（0.8,1.0 → \"80,100\"）",
            LimitRules.FormatThresholds(new[] { 0.8, 1.0 }) == "80,100");
        failed += Check("阈值：未设置（null）按“没有”处理 → 由调用方退回默认",
            !LimitRules.TryParseThresholds(null, out _, out _));

        // ---- F2 跨阈值：只取最高档 ----
        IReadOnlyList<double> th = new[] { 0.8, 1.0 };
        failed += Check("跨阈值：79% 未跨过任何档 → 0", Near(LimitRules.HighestCrossedThreshold(0.79, th), 0));
        failed += Check("跨阈值：80% 恰好跨过低档 → 0.8", Near(LimitRules.HighestCrossedThreshold(0.80, th), 0.8));
        failed += Check("跨阈值：95% 仍是 0.8（未到 100）", Near(LimitRules.HighestCrossedThreshold(0.95, th), 0.8));
        failed += Check("跨阈值：100% → 1.0", Near(LimitRules.HighestCrossedThreshold(1.0, th), 1.0));
        failed += Check("跨阈值：110% **只取最高档** 1.0（一次跨两档不连弹两条）",
            Near(LimitRules.HighestCrossedThreshold(1.1, th), 1.0));

        // ---- F3 去重键：字面量契约（键名是与既有数据库的兼容契约，不能用常量自证）----
        failed += Check("去重键：总限额 = notified|2026-10-06|total||800（字面量）",
            LimitRules.DedupKey(new DateOnly(2026, 10, 6), LimitKind.Total, string.Empty, 0.8)
                == "notified|2026-10-06|total||800");
        failed += Check("去重键：软件限额带目标键（字面量）",
            LimitRules.DedupKey(new DateOnly(2026, 10, 6), LimitKind.App, "chrome", 1.0)
                == "notified|2026-10-06|app|chrome|1000");
        failed += Check("去重键：分类限额带分类名（中文安全）",
            LimitRules.DedupKey(new DateOnly(2026, 10, 6), LimitKind.Category, "工作", 0.8)
                == "notified|2026-10-06|category|工作|800");
        failed += Check("去重键：不同日期 → 不同键（跨天自动重置的前提）",
            LimitRules.DedupKey(new DateOnly(2026, 10, 6), LimitKind.Total, string.Empty, 0.8) !=
            LimitRules.DedupKey(new DateOnly(2026, 10, 7), LimitKind.Total, string.Empty, 0.8));

        // ---- F4 文案 ----
        failed += Check("文案：将达与已达的标题不同，且“已达”标题含「已」",
            LimitRules.ComposeTitle(0.8) != LimitRules.ComposeTitle(1.0) &&
            LimitRules.ComposeTitle(1.0).Contains("已", StringComparison.Ordinal));
        string totalMsg = LimitRules.ComposeMessage(LimitKind.Total, LimitEngine.TotalDisplayName,
            TimeSpan.FromMinutes(90), TimeSpan.FromHours(2), 0.8);
        failed += Check("文案：总限额提到「今日真实活跃」并给出百分比 75%",
            totalMsg.Contains("今日真实活跃", StringComparison.Ordinal) &&
            totalMsg.Contains("75%", StringComparison.Ordinal));
        string appMsg = LimitRules.ComposeMessage(LimitKind.App, "Chrome",
            TimeSpan.FromMinutes(31), TimeSpan.FromMinutes(30), 1.0);
        failed += Check("文案：软件限额带软件名且措辞为「已达到」",
            appMsg.Contains("Chrome", StringComparison.Ordinal) &&
            appMsg.Contains("已达到", StringComparison.Ordinal));
        string catMsg = LimitRules.ComposeMessage(LimitKind.Category, "工作",
            TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(60), 1.0);
        failed += Check("文案：分类限额带「分类」字样与分类名",
            catMsg.Contains("分类", StringComparison.Ordinal) && catMsg.Contains("工作", StringComparison.Ordinal));

        // ---- F5 时长解析（对话框输入；规则只此一处定义）----
        failed += Check("时长：1 小时 30 分 → 90 分钟",
            LimitRules.TryParseDuration("1", "30", out TimeSpan d1, out _) && d1 == TimeSpan.FromMinutes(90));
        failed += Check("时长：小时留空按 0 处理（0 小时 30 分合法）",
            LimitRules.TryParseDuration("", "30", out TimeSpan d2, out _) && d2 == TimeSpan.FromMinutes(30));
        failed += Check("时长：全 0 被拒（“清除限额”是另一个按钮，不靠填 0）",
            !LimitRules.TryParseDuration("0", "0", out _, out string? de1) && !string.IsNullOrEmpty(de1));
        failed += Check("时长：分钟 60 被拒（避免“1 小时 60 分”的歧义）",
            !LimitRules.TryParseDuration("0", "60", out _, out _));
        failed += Check("时长：241 小时被拒", !LimitRules.TryParseDuration("241", "0", out _, out _));
        failed += Check("时长：非数字被拒并指出原文",
            !LimitRules.TryParseDuration("abc", "0", out _, out string? de2) && (de2 ?? string.Empty).Contains("abc"));

        // ---- F6 规则构造：跳过而不是猜 ----
        var rows = new List<LimitRow>
        {
            new("total", "", 3600),
            new("app", "chrome", 1800),
            new("category", "工作", 7200),
            new("app", "", 600),      // 空目标的软件限额 → 跳过（不是“当成总限额”）
            new("app", "x", 0),       // 0 秒 → 跳过
            new("bogus", "y", 600),   // 未知 scope → 跳过
        };
        IReadOnlyList<LimitRule> rules = LimitEngine.BuildRules(rows, key => key == "chrome" ? "Chrome" : key);
        failed += Check("规则：只保留 total / app(chrome) / category(工作) 三条，其余跳过而不是猜", rules.Count == 3);
        failed += Check("规则：软件限额显示名由外部解析（chrome → Chrome）",
            rules[1].Kind == LimitKind.App && rules[1].DisplayName == "Chrome");
        failed += Check("规则：总限额显示名是「今日总限额」",
            rules[0].Kind == LimitKind.Total && rules[0].DisplayName == LimitEngine.TotalDisplayName);

        // ---- F7 输入构造：分类求和 ----
        var apps = new List<AppUsageEntry>
        {
            new("devenv", "Visual Studio", 3600_000, 0.5),
            new("chrome", "Chrome", 1800_000, 0.25),
            new("notepad", "记事本", 900_000, 0.125),
        };
        LimitInput input = LimitEngine.BuildInput(7200, apps, key => key == "notepad" ? string.Empty : "工作");
        failed += Check("输入：软件用量按秒求和（devenv = 3600）", input.AppSeconds["devenv"] == 3600);
        failed += Check("输入：分类用量 = 该分类下各软件之和（devenv + chrome = 5400）",
            input.CategorySeconds["工作"] == 5400);
        failed += Check("输入：未分类的软件不进任何分类（不重复计）", !input.CategorySeconds.ContainsKey(string.Empty));
        failed += Check("输入：总量由调用方给（与 TodayTotal 同口径）", input.TotalSeconds == 7200);

        // ---- F8 用量与最紧急 ----
        failed += Check("用量：软件不在输入里 → 0（而不是抛异常）",
            LimitEngine.UsedOf(rules[1], new LimitInput(0)) == 0);
        failed += Check("用量：总限额的用量就是输入总量",
            LimitEngine.UsedOf(rules[0], input) == 7200);

        IReadOnlyList<LimitStatus> statuses = LimitEngine.Statuses(rules, input);
        failed += Check("状态：条数与规则一致", statuses.Count == rules.Count);

        LimitStatus? critical = LimitEngine.MostCritical(statuses);
        failed += Check("最紧急：取比例最高者（总限额 7200/3600 = 200%）",
            critical is { } c && c.Kind == LimitKind.Total && Near(c.Ratio, 2.0));
        failed += Check("最紧急：没有限额时返回 null（卡片据此隐藏整块）",
            LimitEngine.MostCritical(Array.Empty<LimitStatus>()) is null);

        failed += Check("卡片文案：总限额不带名字，软件限额带名字",
            LimitEngine.ComposeCardLimitText(new LimitStatus(LimitKind.Total, "", LimitEngine.TotalDisplayName, 3600, 7200)) == "1:00 / 2:00" &&
            LimitEngine.ComposeCardLimitText(new LimitStatus(LimitKind.App, "chrome", "Chrome", 1800, 3600)) == "Chrome 0:30 / 1:00");

        return failed;
    }

    // ================================================================ G 限额存储与去重

    private static int LimitStorageChecks(string dbPath)
    {
        int failed = 0;
        TryCleanDatabases(Path.GetDirectoryName(dbPath) ?? ".");
        SqliteStore.ClearPools();

        var store = new SqliteStore(new StorageOptions { DatabasePath = dbPath });
        try
        {
            store.Initialize();

            // ---- G1 limits 表往返 ----
            store.SetLimit("total", "", 3600);
            store.SetLimit("app", "chrome", 1800);
            failed += Check("限额存储：写入两条后读回两条", store.ReadLimits().Count == 2);
            failed += Check("限额存储：DeleteLimit 确实删到了一条", store.DeleteLimit("app", "chrome"));
            failed += Check("限额存储：删除后只剩一条", store.ReadLimits().Count == 1);
            failed += Check("限额存储：再删同一条返回 false（界面据此说“本来就没有”，不假装成功）",
                !store.DeleteLimit("app", "chrome"));

            // ---- G2 去重标记（settings）----
            store.SetSetting("notified|2026-10-06|total||800", "1");
            failed += Check("去重标记：写入后读得到", store.GetSetting("notified|2026-10-06|total||800") == "1");
            failed += Check("去重标记：DeleteSetting 删到了", store.DeleteSetting("notified|2026-10-06|total||800"));
            failed += Check("去重标记：删除后读不到（否则“调低限额却再也不提醒”）",
                store.GetSetting("notified|2026-10-06|total||800") is null);

            // ---- G3 守门：跨阈值一次、重启不重发、跨天重发、改限额重发 ----
            DateOnly currentDay = new(2026, 10, 6);
            store.SetLimit("total", "", 100);   // 100 秒
            var guard = new LimitGuard(() => store, key => key, () => currentDay);
            guard.Reload();

            var notes = new List<LimitNotification>();
            guard.Notified += n => notes.Add(n);

            failed += Check("守门：50%（未达阈值）不发提醒",
                guard.Evaluate(new LimitInput(50), currentDay) == 0 && notes.Count == 0);

            failed += Check("守门：85% 恰好发一条，且阈值取 0.8",
                guard.Evaluate(new LimitInput(85), currentDay) == 1 &&
                notes.Count == 1 && Near(notes[0].Threshold, 0.8));

            failed += Check("守门：去重标记以**字面量键**落库",
                store.GetSetting("notified|2026-10-06|total||800") == "1");

            failed += Check("守门：同一天同一档再判也不重复提醒",
                guard.Evaluate(new LimitInput(90), currentDay) == 0 && notes.Count == 1);

            failed += Check("守门：继续涨到 105% 会再发一条（阈值 1.0）",
                guard.Evaluate(new LimitInput(105), currentDay) == 1 &&
                notes.Count == 2 && Near(notes[1].Threshold, 1.0));

            // 重启：新建守门（同一份库）→ 同一天不应重发
            var guard2 = new LimitGuard(() => store, key => key, () => currentDay);
            guard2.Reload();
            var notes2 = new List<LimitNotification>();
            guard2.Notified += n => notes2.Add(n);
            failed += Check("守门：**重启后同一天不再重复提醒**（标记落库的全部意义）",
                guard2.Evaluate(new LimitInput(105), currentDay) == 0 && notes2.Count == 0);

            // 跨天：键含日期 → 自动重新提醒
            currentDay = new DateOnly(2026, 10, 7);
            failed += Check("守门：**跨天自动重新提醒**（无需任何清标记的定时任务）",
                guard2.Evaluate(new LimitInput(105), currentDay) == 1 && notes2.Count == 1);

            // 一次跨两档：只发最高那档
            store.SetLimit("app", "chrome", 100);
            guard2.Reload();
            notes2.Clear();
            var appInput = new LimitInput(0, new Dictionary<string, long> { { "chrome", 110 } });
            failed += Check("守门：一次涨到 110% 只发一条且取最高档（1.0）",
                guard2.Evaluate(appInput, currentDay) == 1 &&
                notes2.Count == 1 && Near(notes2[0].Threshold, 1.0));

            // 改限额 → 清标记 → 同一档重新提醒
            guard2.SetLimit(LimitKind.App, "chrome", TimeSpan.FromSeconds(50));
            notes2.Clear();
            failed += Check("守门：**改限额后清掉今天的标记** → 同一档会重新提醒",
                guard2.Evaluate(appInput, currentDay) == 1 && notes2.Count == 1);

            // 阈值：非法不写库并给原因；合法则生效
            failed += Check("守门：阈值非法时不写库并给出可读原因",
                !guard2.SetThresholds("0,50", out string? thresholdError) && !string.IsNullOrEmpty(thresholdError));
            failed += Check("守门：保存阈值成功（90,100）",
                guard2.SetThresholds("90,100", out _) && guard2.Thresholds.Count == 2);

            // 存储中途不可用：内存去重兜底，绝不刷屏
            DateOnly day4 = new(2026, 10, 9);
            SqliteStore? currentStore = store;
            var degradeGuard = new LimitGuard(() => currentStore, key => key, () => day4);
            degradeGuard.Reload();
            currentStore = null;
            var notes3 = new List<LimitNotification>();
            degradeGuard.Notified += n => notes3.Add(n);
            failed += Check("守门：**存储中途不可用时，同一档仍只提醒一次**（内存兜底，不刷屏）",
                degradeGuard.Evaluate(new LimitInput(9999), day4) == 1 &&
                degradeGuard.Evaluate(new LimitInput(9999), day4) == 0 &&
                notes3.Count == 1);

            // 无存储：不抛异常、不提醒（降级安全）
            var emptyGuard = new LimitGuard(() => null, key => key, () => day4);
            emptyGuard.Reload();
            failed += Check("守门：完全没有存储时安全降级（0 条规则、不抛异常、不提醒）",
                emptyGuard.Rules.Count == 0 && emptyGuard.Evaluate(new LimitInput(9999), day4) == 0);
        }
        finally
        {
            try { store.Dispose(); } catch { /* 忽略 */ }
            SqliteStore.ClearPools();
        }

        return failed;
    }

    // ================================================================ H 限额组合根与卡片

    private static int LimitRootChecks(string dataDir)
    {
        int failed = 0;
        SqliteStore.ClearPools();
        try { if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true); } catch { /* 忽略 */ }
        try { Directory.CreateDirectory(dataDir); } catch { /* 忽略 */ }

        StartupOptions options = StartupOptions.Parse(
            new[] { "--data-dir=" + dataDir, "--no-raw-log", "--probe-ms=0" });

        HostStartResult start = ProductRuntime.Start(options);
        if (start.Outcome == HostStartOutcome.AlreadyRunning)
        {
            Console.WriteLine("   [SKIP] 已有 ScreenSpy 实例在运行，无法独占单实例互斥体 —— 跳过。");
            Console.WriteLine("   小计：0 项失败（跳过）");
            return 0;
        }

        if (start.Outcome != HostStartOutcome.Started || start.Runtime is null)
        {
            Console.WriteLine("   [FAIL] 无法启动 ProductRuntime：" + (start.Message ?? "(无说明)"));
            Console.WriteLine("   小计：1 项失败");
            return 1;
        }

        try
        {
            ProductRuntime runtime = start.Runtime;

            failed += Check("组合根：初始没有任何限额", runtime.Limits.Count == 0);
            failed += Check("组合根：**没有限额时卡片不显示限额块**（不显示一个并不存在的约束）",
                CardData.FromStatus(runtime.Snapshot()).LimitText.Length == 0);

            failed += Check("组合根：阈值文本非法时不写库并给出原因",
                !runtime.SetLimitThresholds("abc", out string? error) && !string.IsNullOrEmpty(error));
            failed += Check("组合根：保存阈值成功（80,100）", runtime.SetLimitThresholds("80,100", out _));

            failed += Check("组合根：设置总限额成功",
                runtime.SetLimit(LimitKind.Total, string.Empty, TimeSpan.FromHours(2)));
            failed += Check("组合根：规则已生效（1 条 total）",
                runtime.Limits.Count == 1 && runtime.Limits[0].Kind == LimitKind.Total);
            failed += Check("组合根：限额状态可读（1 条）", runtime.LimitStatuses().Count == 1);
            failed += Check("组合根：**有限额时卡片出现限额块**",
                CardData.FromStatus(runtime.Snapshot()).LimitText.Length > 0);

            failed += Check("组合根：删除限额返回“确实删掉了”",
                runtime.RemoveLimit(LimitKind.Total, string.Empty, out bool didRemove) && didRemove);
            failed += Check("组合根：删除后规则为空", runtime.Limits.Count == 0);
            failed += Check("组合根：删除后卡片又隐藏限额块",
                CardData.FromStatus(runtime.Snapshot()).LimitText.Length == 0);

            failed += Check("组合根：新建分类成功", runtime.CreateCategory("工作"));
            failed += Check("组合根：设置分类限额成功",
                runtime.SetLimit(LimitKind.Category, "工作", TimeSpan.FromMinutes(30)));

            IReadOnlyList<LimitStatus> statuses = runtime.LimitStatuses();
            failed += Check("组合根：分类限额出现在状态里，且用量为 0（今天还没归到该分类）",
                statuses.Count == 1 && statuses[0].Kind == LimitKind.Category && statuses[0].UsedSeconds == 0);

            failed += Check("组合根：非法时长（0）被拒，且不留下规则",
                !runtime.SetLimit(LimitKind.App, "chrome", TimeSpan.Zero) && runtime.Limits.Count == 1);
        }
        finally
        {
            try { start.Runtime.Dispose(); } catch { /* 忽略 */ }
            SqliteStore.ClearPools();
        }

        return failed;
    }

    // ================================================================ 辅助

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;

    private static ForegroundAppSample App(string processName)
        => ForegroundAppRules.Create(new IntPtr(0x1234), 4321, processName, "Win32Class", "标题", isSelfProcess: false);

    private sealed class Harness : IDisposable
    {
        private readonly FakeIdleClock _clock = new();
        private readonly FakeTickSource _ticks = new();

        public Harness(string dbPath, DateOnly day, TimeSpan flushInterval)
        {
            Now = day.ToDateTime(new TimeOnly(12, 0, 0), DateTimeKind.Local);
            _ticks.Milliseconds = 0;

            Scheduler = new ActivityScheduler(
                _clock,
                idleThreshold: TimeSpan.FromMinutes(5),
                heartbeat: TimeSpan.FromSeconds(1),
                tickSource: _ticks,
                localNow: () => Now,
                maxCreditedInterval: TimeSpan.FromMinutes(1));

            Bridge = new AppUsageBridge(Scheduler, Source);
            Store = new AppUsageStore(Bridge, new StorageOptions
            {
                DatabasePath = dbPath,
                FlushInterval = flushInterval,
            });

            // 与产品启动顺序一致：**先建立调度器的“当日基准”**，再观测。
            //
            // 为什么必须有这两行：`ActivityScheduler.Sample()` 会在 `_day` 为默认值时先建立当日基准，
            // 但 `Pump()` **没有这个保护** —— 首次 `Pump()` 会因此触发一次跨天翻页
            // （`DayRolled` → `AppUsageTracker.RollDay` → 榜单清零），把刚 `Seed` 的基线冲掉。
            // 生产代码先 `Start()`（`Start()` 会建立当日基准与首拍基准），所以不受影响。
            // （我第一次跑本自检就是被这个陷阱咬的：两项断言显示"基线 5000ms 不见了"。）
            Scheduler.Start();
            Scheduler.Stop();
        }

        public DateTime Now { get; set; }
        public ScriptedSource Source { get; } = new();
        public ActivityScheduler Scheduler { get; }
        public AppUsageBridge Bridge { get; }
        public AppUsageStore Store { get; }

        /// <summary>推进假时钟并手动心跳一次（**不启动真实定时器**：否则它会与脚本化序列抢样本）。</summary>
        public void Advance(long milliseconds)
        {
            _ticks.Milliseconds += milliseconds;
            Scheduler.Pump();
        }

        public void Dispose()
        {
            Store.Dispose();
            Bridge.Dispose();
            Scheduler.Dispose();
            SqliteStore.ClearPools();
        }
    }

    private sealed class FakeIdleClock : IIdleClock
    {
        public bool IsAvailable => true;
        public TimeSpan IdleTime => TimeSpan.Zero;   // 恒为“有输入”→ 每拍计入
    }

    private sealed class FakeTickSource : ITickSource
    {
        public long Milliseconds { get; set; }
    }

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

    /// <summary>造一个 v2 形态的旧库（含时间数据与用户设置），用于验证 v2 → v3 迁移。</summary>
    private static void CreateLegacyV2Database(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };

        using var conn = new SqliteConnection(builder.ToString());
        conn.Open();

        void Exec(string sql)
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        Exec("CREATE TABLE app_usage (date TEXT NOT NULL, app_name TEXT NOT NULL, seconds INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (date, app_name));");
        Exec("CREATE TABLE daily_activity (date TEXT PRIMARY KEY, filtered_seconds INTEGER NOT NULL DEFAULT 0, unattributed_seconds INTEGER NOT NULL DEFAULT 0);");
        Exec("CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT);");
        Exec("CREATE TABLE limits (scope TEXT NOT NULL, target TEXT NOT NULL DEFAULT '', seconds INTEGER NOT NULL, PRIMARY KEY (scope, target));");

        Exec("INSERT INTO app_usage (date, app_name, seconds) VALUES ('2026-10-05', 'devenv', 600);");
        Exec("INSERT INTO app_usage (date, app_name, seconds) VALUES ('2026-10-05', 'chrome', 300);");
        Exec("INSERT INTO daily_activity (date, filtered_seconds, unattributed_seconds) VALUES ('2026-10-05', 30, 20);");
        Exec("INSERT INTO settings (key, value) VALUES ('widget_pos_x', '321');");
        Exec("INSERT INTO limits (scope, target, seconds) VALUES ('total', '', 7200);");
        Exec("PRAGMA user_version=2;");

        SqliteConnection.ClearAllPools();
    }

    private static string AliasTarget(IReadOnlyList<AppAliasRow> aliases, string rawKey)
    {
        foreach (AppAliasRow row in aliases)
            if (string.Equals(row.RawKey, rawKey, StringComparison.Ordinal)) return row.AppKey;
        return string.Empty;
    }

    private static string CategoryOf(IReadOnlyList<AppMetaRow> meta, string appKey)
    {
        foreach (AppMetaRow row in meta)
            if (string.Equals(row.AppKey, appKey, StringComparison.Ordinal)) return row.Category;
        return string.Empty;
    }

    private static string DisplayNameOf(IReadOnlyList<AppMetaRow> meta, string appKey)
    {
        foreach (AppMetaRow row in meta)
            if (string.Equals(row.AppKey, appKey, StringComparison.Ordinal)) return row.DisplayName;
        return string.Empty;
    }

    private static long FindLimit(IReadOnlyList<LimitRow> limits, string scope, string target)
    {
        foreach (LimitRow row in limits)
            if (string.Equals(row.Scope, scope, StringComparison.Ordinal) &&
                string.Equals(row.Target, target, StringComparison.Ordinal))
                return row.Seconds;
        return -1;
    }

    private static bool Contains(IReadOnlyList<string> list, string value)
    {
        foreach (string item in list)
            if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void TryCleanDatabases(string dir)
    {
        SqliteStore.ClearPools();
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*.db*"))
                TryDelete(file);
        }
        catch { /* 忽略 */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
        try { if (File.Exists(path + "-wal")) File.Delete(path + "-wal"); } catch { /* 忽略 */ }
        try { if (File.Exists(path + "-shm")) File.Delete(path + "-shm"); } catch { /* 忽略 */ }
    }

    private static int Check(string label, bool ok)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        return ok ? 0 : 1;
    }

    private static long Ms(TimeSpan value) => (long)Math.Round(value.TotalMilliseconds, MidpointRounding.AwayFromZero);

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
        string path = _logPath ?? Path.Combine(_dir ?? "artifacts", "m9", "m9-selfcheck.log");
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
            _buffer.Append(value).Append('\n');
        }
    }
}
