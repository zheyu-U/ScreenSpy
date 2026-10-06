using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ScreenSpy.Analytics;
using ScreenSpy.AppHost;
using ScreenSpy.Storage;

namespace ScreenSpy.Demo;

/// <summary>
/// M10 自检：**近 7 天柱状图**。
///
/// 分四组，全部是**确定性**断言（不依赖你的实际操作）：
///  * <b>A 纯逻辑（口径）</b>：7 天窗口（今天 + 前 6 天，含首尾）、日期升序、
///    总量 = 各软件 + 不计入使用时长 + 无前台/未知、软件/分类维度、**非软件不进软件维度**、
///    范围外的行被忽略、"有记录"与"为 0"分开、以及题面文案。
///  * <b>B 布局（几何）</b>：柱高成比例、0 高、**除零保护**、极小值仍可见、不超过柱区高度。
///  * <b>C 存储范围查询</b>：<c>ReadUsageRange</c>/<c>ReadActivityRange</c> 含首尾、不含范围外、
///    反向范围返回空（不抛）、日期升序。
///  * <b>D 组合根与降级</b>：真实 <see cref="ProductRuntime"/> 上验证
///    **只读库**（今天那根 = 库里值，而不是内存实时口径）、**身份层现取**（改名后标题立刻变）、
///    候选列表来自库、存储不可用时**明确报错而不是给一张空图**，以及界面的两个纯函数契约。
///
/// 为什么 D 组非要起真实运行体：本项目已经栽过两次"函数写对了，但组合根没接上"。
/// </summary>
internal static class M10SelfCheck
{
    private static readonly StringBuilder LogBuffer = new();
    private static string? _logPath;
    private static string? _dir;

    public static bool IsRequested(string[]? args) => HasFlag(args, "--m10-selfcheck");

    public static int Run(string[] args)
    {
        _logPath = GetString(args, "--log=");
        string dir = GetString(args, "--dir=") ?? Path.Combine("artifacts", "m10");
        _dir = Path.GetFullPath(dir);

        TextWriter original = Console.Out;
        try { Console.SetOut(new TeeWriter(original, LogBuffer)); } catch { /* 忽略 */ }

        int failed = 0;
        try
        {
            Console.WriteLine("M10 自检：近 7 天柱状图");
            Console.WriteLine("目录：" + _dir);
            Console.WriteLine();

            try { Directory.CreateDirectory(_dir); } catch { /* 忽略 */ }

            Console.WriteLine("A. 图表口径（纯逻辑）");
            failed += BuilderChecks();

            Console.WriteLine();
            Console.WriteLine("B. 柱高布局（纯几何）");
            failed += LayoutChecks();

            Console.WriteLine();
            Console.WriteLine("C. 存储范围查询");
            failed += RangeChecks(Path.Combine(_dir, "range.db"));

            Console.WriteLine();
            Console.WriteLine("D. 组合根与降级（真实 ProductRuntime / 界面纯函数）");
            failed += RootChecks(Path.Combine(_dir, "root"), Path.Combine(_dir, "nostore"));

            Console.WriteLine();
            Console.WriteLine(failed == 0
                ? "M10 自检：全部通过。"
                : $"M10 自检：有 {failed} 项失败。");
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

    // ================================================================ A 口径

    private static int BuilderChecks()
    {
        int failed = 0;

        var today = new DateOnly(2026, 10, 6);
        var usage = new List<AppUsageRow>
        {
            new(new DateOnly(2026, 10, 6), "devenv", 3600),
            new(new DateOnly(2026, 10, 6), "chrome", 1800),
            new(new DateOnly(2026, 10, 5), "devenv", 7200),
            new(new DateOnly(2026, 10, 5), "code", 600),
            new(new DateOnly(2026, 10, 2), "chrome", 60),
            new(new DateOnly(2026, 9, 29), "chrome", 999),      // 左边界外 → 必须忽略
            new(new DateOnly(2026, 10, 7), "devenv", 111),      // 右边界外（未来）→ 必须忽略
        };
        var activity = new List<DayActivityRow>
        {
            new(new DateOnly(2026, 10, 6), 1200, 600),
            new(new DateOnly(2026, 10, 4), 300, 0),
        };

        Func<string, string> displayName = key => key == "devenv" ? "Visual Studio" : key;
        Func<string, string> category = key => key switch
        {
            "devenv" => "工作",
            "code" => "工作",
            "chrome" => "娱乐",
            _ => string.Empty,
        };

        ChartModel total = ChartBuilder.Build(
            ChartScope.Total, today, ChartBuilder.DefaultDays, usage, activity, displayName, category);

        // ---- 窗口与顺序 ----
        failed += Check("窗口：恰好 7 根柱", total.Bars.Count == 7);
        failed += Check("窗口：左端 = 今天 - 6 天（含首尾）", total.Bars[0].Day == new DateOnly(2026, 9, 30));
        failed += Check("窗口：右端 = 今天（今天必须在图里）", total.Bars[6].Day == today);

        bool ascending = true;
        for (int i = 1; i < total.Bars.Count; i++)
        {
            if (total.Bars[i].Day <= total.Bars[i - 1].Day) ascending = false;
        }
        failed += Check("顺序：日期升序（左旧右新）", ascending);
        failed += Check("日期标签：今天显示为 10-06", total.Bars[6].DayLabel == "10-06");

        // ---- 总量口径 ----
        failed += Check("总量：今天 = 软件 5400 + 非软件 1800 = 7200",
            total.Bars[6].Seconds == 7200);
        failed += Check("总量：10-05 = 7800（那天没有非软件行）", total.Bars[5].Seconds == 7800);
        failed += Check("总量：只有非软件行的那天也要算（10-04 = 300）", total.Bars[4].Seconds == 300);
        failed += Check("总量：范围外的行被忽略（合计 15360，不含 999 / 111）",
            total.TotalSeconds == 15360);
        failed += Check("分母：MaxSeconds = 7800", total.MaxSeconds == 7800);
        failed += Check("有数据：至少一天有记录", total.HasAnyData);

        // ---- 有记录 / 无记录 ----
        failed += Check("有记录：今天 = true", total.Bars[6].HasData);
        failed += Check("有记录：只有非软件行的 10-04 也算有记录", total.Bars[4].HasData);
        failed += Check("无记录：10-03 = false（空槽，不是 0）", !total.Bars[3].HasData);
        failed += Check("无记录：09-30 = false（窗口左端外没有数据）", !total.Bars[0].HasData);
        failed += Check("无记录与 0 分开：标签是 —", total.Bars[3].ValueText == ChartText.NoDataText);
        failed += Check("有记录但为 0：标签是 0:00", total.Bars[4].Seconds == 300
            && ChartText.Duration(0) == "0:00");

        // ---- 软件维度 ----
        ChartModel devenv = ChartBuilder.Build(
            new ChartScope(ChartDimension.App, "devenv"), today, ChartBuilder.DefaultDays, usage, activity, displayName, category);

        failed += Check("软件维度：今天 = 3600（**不含**非软件那 1800）", devenv.Bars[6].Seconds == 3600);
        failed += Check("软件维度：10-05 = 7200", devenv.Bars[5].Seconds == 7200);
        failed += Check("软件维度：没用的那天是 0，但仍算有记录",
            devenv.Bars[4].Seconds == 0 && devenv.Bars[4].HasData);
        failed += Check("软件维度：窗口左端仍是无记录", !devenv.Bars[0].HasData);
        failed += Check("软件维度：标题用显示名", devenv.Title.Contains("Visual Studio", StringComparison.Ordinal));
        failed += Check("软件维度：MaxSeconds = 7200", devenv.MaxSeconds == 7200);

        // ---- 分类维度 ----
        ChartModel work = ChartBuilder.Build(
            new ChartScope(ChartDimension.Category, "工作"), today, ChartBuilder.DefaultDays, usage, activity, displayName, category);

        failed += Check("分类维度：今天 = devenv 3600（工作）", work.Bars[6].Seconds == 3600);
        failed += Check("分类维度：10-05 = devenv 7200 + code 600 = 7800", work.Bars[5].Seconds == 7800);
        failed += Check("分类维度：标题含分类名", work.Title.Contains("工作", StringComparison.Ordinal));

        ChartModel fun = ChartBuilder.Build(
            new ChartScope(ChartDimension.Category, "娱乐"), today, ChartBuilder.DefaultDays, usage, activity, displayName, category);
        failed += Check("分类维度：娱乐 = 只有 chrome（今天 1800）", fun.Bars[6].Seconds == 1800);

        // ---- 防御与边界 ----
        ChartModel zeroDays = ChartBuilder.Build(
            ChartScope.Total, today, 0, usage, activity, displayName, category);
        failed += Check("防御：days<=0 回退到 7 天（不画出 0 根柱）", zeroDays.Bars.Count == 7);

        ChartModel empty = ChartBuilder.Build(
            ChartScope.Total, today, ChartBuilder.DefaultDays, null, null, displayName, category);
        failed += Check("空输入：不抛、7 根空槽", empty.Bars.Count == 7 && !empty.HasAnyData);
        failed += Check("空输入：MaxSeconds = 0（界面据此不除零）", empty.MaxSeconds == 0);
        failed += Check("空输入：合计 = 0", empty.TotalSeconds == 0);

        failed += Check("scope：总量可用（不需要目标）", ChartScope.Total.IsUsable);
        failed += Check("scope：软件维度没选目标 → 不可用", !new ChartScope(ChartDimension.App, "").IsUsable);
        failed += Check("scope：分类维度没选目标 → 不可用", !new ChartScope(ChartDimension.Category, "").IsUsable);

        // ---- 文本契约 ----
        failed += Check("时长文本：3600s → 1:00", ChartText.Duration(3600) == "1:00");
        failed += Check("时长文本：5400s → 1:30", ChartText.Duration(5400) == "1:30");
        failed += Check("时长文本：负数按 0 处理", ChartText.Duration(-5) == "0:00");
        failed += Check("日期标签：用不变文化（不随系统区域变化）",
            ChartText.DayLabel(new DateOnly(2026, 1, 2)) == "01-02");

        return failed;
    }

    // ================================================================ B 布局

    private static int LayoutChecks()
    {
        int failed = 0;

        failed += Check("布局：一半时长 → 一半高度（3600/7200 × 100 = 50）",
            Math.Abs(BarChartLayout.HeightOf(3600, 7200, 100) - 50) < 0.001);
        failed += Check("布局：满值 → 满高", Math.Abs(BarChartLayout.HeightOf(7200, 7200, 100) - 100) < 0.001);
        failed += Check("布局：成比例（3600 的高度 = 2 × 1800 的高度）",
            Math.Abs(BarChartLayout.HeightOf(3600, 7200, 100) - 2 * BarChartLayout.HeightOf(1800, 7200, 100)) < 0.001);
        failed += Check("布局：0 秒 → 0 高", BarChartLayout.HeightOf(0, 7200, 100) == 0);
        failed += Check("布局：全程为 0 时不除零（max=0 → 0 高）",
            BarChartLayout.HeightOf(100, 0, 100) == 0);
        failed += Check("布局：柱区高度为 0 → 0 高（不产生 NaN/负值）",
            BarChartLayout.HeightOf(100, 200, 0) == 0);
        failed += Check("布局：极小值仍可见（不小于最小可见高度）",
            Math.Abs(BarChartLayout.HeightOf(1, 100000, 100) - BarChartLayout.MinVisibleHeight) < 0.001);
        failed += Check("布局：秒数超过分母也不溢出柱区", BarChartLayout.HeightOf(999999, 7200, 100) <= 100);

        return failed;
    }

    // ================================================================ C 存储范围

    private static int RangeChecks(string dbPath)
    {
        int failed = 0;

        TryDelete(dbPath);
        SqliteStore.ClearPools();

        var store = new SqliteStore(new StorageOptions { DatabasePath = dbPath });
        try
        {
            store.Initialize();

            failed += Check("库：schema 版本为 3（M9-1 身份层）", store.UserVersion() == 3);

            store.AddSeconds(
                new[]
                {
                    new AppUsageRow(new DateOnly(2026, 10, 1), "devenv", 100),
                    new AppUsageRow(new DateOnly(2026, 10, 3), "chrome", 200),
                    new AppUsageRow(new DateOnly(2026, 10, 5), "devenv", 300),
                },
                new[]
                {
                    new DayActivityRow(new DateOnly(2026, 10, 1), 10, 1),
                    new DayActivityRow(new DateOnly(2026, 10, 5), 30, 3),
                });

            IReadOnlyList<AppUsageRow> all = store.ReadUsageRange(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5));
            failed += Check("范围查询（软件）：含首尾 → 3 行", all.Count == 3);
            failed += Check("范围查询（软件）：日期升序", all[0].Day < all[1].Day && all[1].Day < all[2].Day);
            failed += Check("范围查询（软件）：首日在内", all[0].Day == new DateOnly(2026, 10, 1));
            failed += Check("范围查询（软件）：末日在内", all[2].Day == new DateOnly(2026, 10, 5));

            IReadOnlyList<AppUsageRow> narrow = store.ReadUsageRange(new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 4));
            failed += Check("范围查询（软件）：只取范围内的那一天", narrow.Count == 1 && narrow[0].AppName == "chrome");

            failed += Check("范围查询（软件）：反向范围返回空（不抛）",
                store.ReadUsageRange(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 1)).Count == 0);
            failed += Check("范围查询（软件）：范围外没有数据时返回空",
                store.ReadUsageRange(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 2)).Count == 0);

            IReadOnlyList<DayActivityRow> act = store.ReadActivityRange(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5));
            failed += Check("范围查询（非软件）：含首尾 → 2 行", act.Count == 2);
            failed += Check("范围查询（非软件）：两列都读对",
                act[0].FilteredSeconds == 10 && act[0].UnattributedSeconds == 1
                && act[1].FilteredSeconds == 30 && act[1].UnattributedSeconds == 3);
            failed += Check("范围查询（非软件）：反向范围返回空",
                store.ReadActivityRange(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 1)).Count == 0);
        }
        catch (Exception ex)
        {
            Console.WriteLine("   [FAIL] C 组异常：" + ex.Message);
            failed++;
        }
        finally
        {
            store.Dispose();
            SqliteStore.ClearPools();
        }

        return failed;
    }

    // ================================================================ D 组合根

    private static int RootChecks(string rootDir, string noStoreDir)
    {
        int failed = 0;

        // ---- D1 界面纯函数（不依赖运行体，先测）----
        failed += Check("界面契约：总量维度丢弃目标（免得带进上一次选的软件名）",
            MainWindow.ComposeChartScope(ChartDimension.Total, "devenv").Target.Length == 0);
        failed += Check("界面契约：软件维度保留目标",
            MainWindow.ComposeChartScope(ChartDimension.App, "devenv").Target == "devenv");

        // 用**真实的下拉项**断言，而不是另抄一份键名 —— 抄的那份永远会"通过"。
        IReadOnlyList<MainWindow.IdentityChoice> dimensions = MainWindow.BuildChartDimensionChoices();
        failed += Check("界面契约：维度下拉恰好 3 项", dimensions.Count == 3);
        failed += Check("界面契约：三个键都能被解析（键写错会让维度**静默**退回总量）",
            dimensions.Count == 3
            && CanParse(dimensions[0].Key) && CanParse(dimensions[1].Key) && CanParse(dimensions[2].Key));
        failed += Check("界面契约：三个键互不相同（重复会让某一维度永远选不到）",
            dimensions.Count == 3
            && dimensions[0].Key != dimensions[1].Key
            && dimensions[1].Key != dimensions[2].Key
            && dimensions[0].Key != dimensions[2].Key);
        failed += Check("界面契约：维度文案是字面量（与常量同源）",
            dimensions.Count == 3
            && dimensions[0].Display == MainWindow.ChartTotalLabel
            && dimensions[1].Display == MainWindow.ChartAppLabel
            && dimensions[2].Display == MainWindow.ChartCategoryLabel);

        // 用**真的 7 天全空**的模型（而不是 ChartModel.Empty）——空图提示针对的正是这种"有窗口但没记录"。
        ChartModel noHistory = ChartBuilder.Build(
            ChartScope.Total, new DateOnly(2026, 10, 6), ChartBuilder.DefaultDays, null, null);
        failed += Check("界面契约：无历史记录时提示里明说（不显示空图让人猜）",
            MainWindow.ComposeChartHint(noHistory).Contains("没有历史记录", StringComparison.Ordinal));
        ChartModel oneDay = ChartBuilder.Build(
            ChartScope.Total, new DateOnly(2026, 10, 6), ChartBuilder.DefaultDays,
            new[] { new AppUsageRow(new DateOnly(2026, 10, 6), "devenv", 1800) }, null);
        string hint = MainWindow.ComposeChartHint(oneDay);
        failed += Check("界面契约：有数据时提示里给出合计与最高一天",
            hint.Contains(ChartText.Duration(oneDay.TotalSeconds), StringComparison.Ordinal)
            && hint.Contains(ChartText.Duration(oneDay.MaxSeconds), StringComparison.Ordinal));

        // ---- D2 无存储：必须明确报错，而不是给一张空图 ----
        TryCleanDatabases(noStoreDir);
        HostStartResult noStore = ProductRuntime.Start(AppHost.StartupOptions.Parse(new[]
        {
            "--data-dir=" + noStoreDir, "--no-store", "--no-raw-log", "--no-card", "--probe-ms=0",
        }));

        if (noStore.Outcome == HostStartOutcome.AlreadyRunning)
        {
            Console.WriteLine("   [SKIP] D2/D3：已有 ScreenSpy 实例在运行（单实例互斥体被占用），无法起第二个运行体。");
        }
        else if (noStore.Outcome != HostStartOutcome.Started)
        {
            Console.WriteLine("   [FAIL] 运行体启动失败：" + noStore.Message);
            failed++;
        }
        else
        {
            ProductRuntime runtime = noStore.Runtime!;
            try
            {
                ChartResult degraded = runtime.ReadChart(ChartScope.Total);
                failed += Check("无存储：读图表失败而不是返回空图", !degraded.Ok);
                failed += Check("无存储：给出的原因非空且提到存储",
                    !string.IsNullOrEmpty(degraded.Error) && degraded.Error!.Contains("存储", StringComparison.Ordinal));
            }
            finally { runtime.Dispose(); }
        }

        // ---- D3 有存储：只读库 + 身份层现取 + 候选来自库 ----
        TryCleanDatabases(rootDir);
        string dbPath = Path.Combine(rootDir, "data.db");

        HostStartResult started = ProductRuntime.Start(AppHost.StartupOptions.Parse(new[]
        {
            "--db=" + dbPath, "--no-raw-log", "--no-card", "--probe-ms=0", "--flush-ms=1000",
        }));

        if (started.Outcome == HostStartOutcome.AlreadyRunning)
        {
            Console.WriteLine("   [SKIP] D3：已有实例在运行，跳过（与 D2 同因）。");
            return failed;
        }
        if (started.Outcome != HostStartOutcome.Started)
        {
            Console.WriteLine("   [FAIL] 运行体启动失败：" + started.Message);
            return failed + 1;
        }

        ProductRuntime host = started.Runtime!;
        try
        {
            DateTime now = DateTime.Now;
            var today = DateOnly.FromDateTime(now);

            // 直接写库（模拟"已经落库的历史"）——运行体的内存口径对此一无所知。
            var writer = new SqliteStore(new StorageOptions { DatabasePath = dbPath });
            try
            {
                writer.Initialize();
                writer.AddSeconds(
                    new[] { new AppUsageRow(today, "devenv", 600) },
                    new[] { new DayActivityRow(today, 300, 0) });
            }
            finally
            {
                writer.Dispose();
                SqliteStore.ClearPools();
            }

            ChartResult total = host.ReadChart(ChartScope.Total);
            failed += Check("只读库：读图成功", total.Ok);
            failed += Check("只读库：今天那根 = 库里的 900（600 软件 + 300 非软件）",
                total.Ok && total.Model!.Bars[total.Model.Bars.Count - 1].Seconds == 900);
            failed += Check("只读库：不掺内存实时口径（与快照的今日值无关，快照此时为 " +
                            ChartText.Duration((long)host.Snapshot().TodayTotal.TotalSeconds) + "）",
                total.Ok && total.Model!.Bars[total.Model.Bars.Count - 1].Seconds !=
                (long)host.Snapshot().TodayTotal.TotalSeconds);

            // 再写一笔：图表必须**重新读库**，而不是缓存启动时的基线。
            var writer2 = new SqliteStore(new StorageOptions { DatabasePath = dbPath });
            try
            {
                writer2.Initialize();
                writer2.AddSeconds(new[] { new AppUsageRow(today, "devenv", 400) }, null);
            }
            finally
            {
                writer2.Dispose();
                SqliteStore.ClearPools();
            }

            ChartResult again = host.ReadChart(ChartScope.Total);
            failed += Check("只读库：每次读都重新查库（900 → 1300）",
                again.Ok && again.Model!.Bars[again.Model.Bars.Count - 1].Seconds == 1300);

            // 候选列表来自库
            (IReadOnlyList<string> keys, string? keyError) = host.ReadChartApps();
            failed += Check("候选来自库：有 devenv 且无错误",
                keyError is null && keys.Count == 1 && keys[0] == "devenv");

            // 软件维度 + 身份层**现取**
            ChartResult app = host.ReadChart(new ChartScope(ChartDimension.App, "devenv"));
            // 未改名时用**友好名**（devenv → Visual Studio），不是生硬的进程名。
            failed += Check("软件维度：标题在未改名时用友好名（Visual Studio）",
                app.Ok && app.Model!.Title.Contains("Visual Studio", StringComparison.Ordinal));
            failed += Check("软件维度：标题里不出现生硬的进程名 devenv",
                app.Ok && !app.Model!.Title.Contains("devenv", StringComparison.Ordinal));

            string? renameError = host.ValidateRename("devenv", "我的开发工具");
            failed += Check("改名前置校验：不冲突（" + (renameError ?? "无") + "）", renameError is null);

            host.RenameApp("devenv", "我的开发工具", out _);

            ChartResult renamed = host.ReadChart(new ChartScope(ChartDimension.App, "devenv"));
            failed += Check("身份层现取：改名后图表标题**立刻**用新名（没缓存旧名）",
                renamed.Ok && renamed.Model!.Title.Contains("我的开发工具", StringComparison.Ordinal));
            failed += Check("身份层现取：不缓存还意味着旧名（含友好名）不再出现在标题里",
                renamed.Ok
                && !renamed.Model!.Title.Contains("devenv", StringComparison.Ordinal)
                && !renamed.Model!.Title.Contains("Visual Studio", StringComparison.Ordinal));

            // 分类维度：把 devenv 归入分类后，分类图能查到它
            host.CreateCategory("工作");
            host.SetAppCategory("devenv", "工作");
            failed += Check("分类候选来自身份层", host.ChartCategories.Contains("工作"));

            ChartResult byCategory = host.ReadChart(new ChartScope(ChartDimension.Category, "工作"));
            // 今天库里 = 软件 600 + 400 = 1000，另有非软件 300。**分类维度不该含非软件那 300**（它不属于任何软件）。
            failed += Check("分类维度：归类后能查到该软件今天的时长（600 + 400 = 1000，**不含**非软件 300）",
                byCategory.Ok && byCategory.Model!.Bars[byCategory.Model.Bars.Count - 1].Seconds == 1000);
            failed += Check("分类维度：与非软件口径互不污染（总量 1300 ≠ 分类 1000）",
                again.Ok && byCategory.Ok
                && again.Model!.Bars[again.Model.Bars.Count - 1].Seconds
                   != byCategory.Model!.Bars[byCategory.Model.Bars.Count - 1].Seconds);

            failed += Check("空目标：明确报错而不是给一张空图",
                !host.ReadChart(new ChartScope(ChartDimension.App, "")).Ok);
        }
        catch (Exception ex)
        {
            Console.WriteLine("   [FAIL] D3 异常：" + ex.Message);
            failed++;
        }
        finally
        {
            host.Dispose();
            SqliteStore.ClearPools();
        }

        return failed;
    }

    private static bool CanParse(string key) => Enum.TryParse(key, out ChartDimension _);

    // ================================================================ 基础设施

    private static int Check(string label, bool ok)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        return ok ? 0 : 1;
    }

    private static void TryCleanDatabases(string dir)
    {
        SqliteStore.ClearPools();
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*.db*")) TryDelete(file);
        }
        catch { /* 忽略 */ }
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
        string path = _logPath ?? Path.Combine(_dir ?? "artifacts", "m10", "m10-selfcheck.log");
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
