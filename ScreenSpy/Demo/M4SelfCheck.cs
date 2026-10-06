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
using ScreenSpy.Storage;

namespace ScreenSpy.Demo;

/// <summary>
/// M4 的控制台自检入口（内嵌入口，**长期保留** —— 它仍是回归验证的主要手段）。
///
/// 用法：
///   ScreenSpy.exe --m4-selfcheck [--seconds=20] [--logic-only]
///                                [--dir=&lt;目录&gt;] [--log=&lt;path&gt;]
///                                [--flush-ms=15000] [--queue=8192]
///
/// 分两段：
///  【A】确定性检查 —— 用**假时钟 + 脚本化前台采样**驱动真实的调度器/归属/存储链路
///       （不是模拟存储，是真写 SQLite），覆盖：
///         schema 与版本、幂等建表、增量 upsert、**余数结转（零漂移）**、
///         **跨天分行**、**切换软件强制 flush**、**重启后仍在**、
///         **启动续算（Seed）**、settings/limits 往返、完整性检查、
///         **写盘失败不丢数据**、**入队记账自洽**、**存储与榜单毫秒级一致**。
///  【B】真实观察 —— 真实空闲源 + 真实前台进程，跑 N 秒后**关闭并重新打开数据库**，
///       验证“重启程序后今天的时长仍在”（§5.6 的验收方式），并演示从库中续算榜单。
///
/// 与 M2/M3 自检一致：产品是 <c>WinExe</c>（GUI 子系统），从交互式控制台直接运行时标准输出可能不可见，
/// 因此本自检把**全部输出同时写入日志文件**（见末尾打印的路径）。
///
/// 注意：本自检**绝不触碰产品数据库**（默认 <c>%LOCALAPPDATA%\ScreenSpy\data.db</c>），
/// 一律使用 <c>--dir</c> 下的临时库文件。
/// </summary>
internal static class M4SelfCheck
{
    private const string SwitchName = "--m4-selfcheck";

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
        _logPath = GetString(args, "--log=");

        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 输出被重定向时可能失败，忽略 */ }

        TextWriter original = Console.Out;
        try { Console.SetOut(new TeeWriter(original, LogBuffer)); } catch { /* 忽略 */ }

        int failures;
        try
        {
            Console.WriteLine("============ ScreenSpy · M4 SQLite 存储 + 批量 flush 自检 ============");
            Console.WriteLine($"真实观察 {seconds}s，仅确定性检查={logicOnly}");
            Console.WriteLine($"产品默认库路径：{StorageOptions.DefaultDatabasePath}");
            Console.WriteLine("（本自检使用临时库，不会写入产品库）");
            Console.WriteLine();

            failures = RunDeterministicChecks();

            if (logicOnly)
            {
                Console.WriteLine(failures == 0 ? "确定性检查：全部通过。" : $"确定性检查：{failures} 项失败。");
            }
            else
            {
                Console.WriteLine();
                failures += RunRealObservation(seconds, args);
                Console.WriteLine();
                Console.WriteLine(failures == 0 ? "M4 自检：全部通过。" : $"M4 自检：{failures} 项失败。");
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
        Console.WriteLine("---- A 确定性检查（假时钟 + 脚本化采样，但**真写 SQLite**）----");

        int failed = 0;
        var day = new DateOnly(2026, 10, 5);
        var day2 = new DateOnly(2026, 10, 6);

        string dir = Path.Combine("artifacts", "m4", "a");
        Directory.CreateDirectory(dir);
        TryCleanDatabases(dir);

        // ---- A1 schema / 版本 / 引擎版本 ----
        string dbA = Path.Combine(dir, "schema.db");
        var storeA = new SqliteStore(new StorageOptions { DatabasePath = dbA });
        storeA.Initialize();

        IReadOnlyList<string> tables = storeA.ListTables();
        Console.WriteLine($"   SQLite 引擎：{storeA.SqliteVersion()}，schema 版本：{storeA.UserVersion()}");
        Console.WriteLine($"   表：{string.Join(", ", tables)}");

        failed += Check("建表：app_usage / daily_activity / settings / limits 四张表齐备",
            Contains(tables, "app_usage") && Contains(tables, "daily_activity") &&
            Contains(tables, "settings") && Contains(tables, "limits"));
        failed += Check($"user_version 记为 {SqliteStore.SchemaVersion}（schema v2：新增 daily_activity）",
            storeA.UserVersion() == SqliteStore.SchemaVersion);

        // ---- A2 幂等 ----
        storeA.AddSeconds(new[] { new AppUsageRow(day, "chrome", 10) });
        storeA.Initialize();   // 再次初始化不得报错、不得清库
        long afterReinit = storeA.SecondsOf(day, "chrome");
        failed += Check("幂等：重复 Initialize 不报错、数据仍在", afterReinit == 10);

        // ---- A3 增量 upsert 累加 ----
        storeA.AddSeconds(new[] { new AppUsageRow(day, "chrome", 5) });
        failed += Check("增量累加：10 + 5 = 15 秒", storeA.SecondsOf(day, "chrome") == 15);

        // ---- A9 settings / limits 往返 ----
        storeA.SetSetting("widget_pos_x", "100");
        storeA.SetSetting("widget_pos_x", "200");
        failed += Check("设置：覆写后读到最新值", storeA.GetSetting("widget_pos_x") == "200");
        failed += Check("设置：不存在的键返回 null", storeA.GetSetting("nope") is null);

        storeA.SetLimit("total", "", 7200);
        storeA.SetLimit("total", "", 3600);          // 必须**更新**同一行（若用 NULL 会新增一行）
        storeA.SetLimit("app", "chrome", 1800);
        IReadOnlyList<LimitRow> limits = storeA.ReadLimits();
        failed += Check("限额：scope=total 重复写入只更新一行（空串而非 NULL 的语义）",
            limits.Count == 2 && FindLimit(limits, "total", "") == 3600 && FindLimit(limits, "app", "chrome") == 1800);

        failed += Check("完整性检查：integrity_check = ok", storeA.IntegrityCheck().Equals("ok", StringComparison.OrdinalIgnoreCase));

        // ---- A4 余数结转（逐拍 flush 也不漂移）+ 存储与榜单毫秒级一致 ----
        {
            Console.WriteLine("   -- 余数结转 / 与榜单一致（每拍 900ms，逐拍 flush，共 10 拍）--");
            string db = Path.Combine(dir, "carry.db");
            using var h = new Harness(db, day);
            h.Source.Push(App("chrome"));
            h.Start();

            for (int i = 0; i < 10; i++)
            {
                h.Advance(900);
                // 关键：必须等存储侧**真正入账**（累加进待写），而不只是“入队”。
                // 只等“入队 + 丢弃 == 被计入”会在后台线程还没取走这一拍时就返回，
                // 紧接着的 Flush 自然读不到它 —— 这是**装置竞态**，不是产品缺陷
                // （产品里不存在：聚合与写盘都在同一个后台线程上串行发生）。
                // 判据用 WaitConserved（“已落库 + 待写”追平榜单），而不是某个固定的毫秒数 ——
                // 这里每拍只有 900ms，写死阈值只会在超时上白等。
                if (!h.WaitConserved(5000))
                    Console.WriteLine($"      （警告）存储侧未在超时内入账：待写={h.Store.PendingMilliseconds}ms");
                h.Store.Flush();
            }

            long trackerMs = Ms(h.Bridge.Tracker.AttributedTotal);
            long storedMs = h.PersistedSeconds() * 1000 + h.Store.PendingMilliseconds;

            Console.WriteLine($"      榜单={trackerMs}ms，落库={h.PersistedSeconds()}s，待写={h.Store.PendingMilliseconds}ms，flush 次数={h.Store.Flushes}");

            failed += Check("存储累计 == 榜单累计（毫秒级一致，重取整不丢）", storedMs == trackerMs);
            failed += Check("榜单累计 == 调度器今日活跃", trackerMs == Ms(h.Scheduler.TodayActive));
            failed += Check("10 × 900ms 逐拍 flush 后恰为 9 秒（无逐拍取整漂移）", h.PersistedSeconds() == 9);
            failed += Check("余数已结转为 0", h.Store.PendingMilliseconds == 0);
            failed += Check("入队记账自洽（入队 + 丢弃 == 被计入拍数）",
                h.Store.Enqueued + h.Store.Dropped == h.Store.CreditedTicks);
        }

        // ---- A4b 余数的“攒”与“结转” ----
        {
            string db = Path.Combine(dir, "carry2.db");
            using var h = new Harness(db, day);
            h.Source.Push(App("chrome"));
            h.Start();

            h.Advance(900);
            h.WaitConserved(3000);
            h.Store.Flush();
            bool carryKept = h.PersistedSeconds() == 0 && h.Store.PendingMilliseconds == 900;

            h.Advance(100);
            h.WaitConserved(3000);
            h.Store.Flush();
            bool carried = h.PersistedSeconds() == 1 && h.Store.PendingMilliseconds == 0;

            failed += Check("不足 1 秒不落库、余数留在内存（0s / 900ms）", carryKept);
            failed += Check("补齐后再落库（900ms + 100ms = 1s，余数归零）", carried);
        }

        // ---- A5 跨天分行 ----
        {
            string db = Path.Combine(dir, "midnight.db");
            using var h = new Harness(db, day);
            h.Source.Push(App("chrome"));
            h.Now = new DateTime(2026, 10, 5, 23, 59, 59, DateTimeKind.Local);
            h.Start();

            h.Advance(1000);
            h.WaitConserved(3000);
            h.Store.Flush();

            h.Now = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Local);
            h.Advance(1000);
            h.WaitConsumed(3000);          // 这一拍属于新的一天，待写在新日期下
            h.Store.Flush();

            long d1 = h.PersistedSeconds(day);
            long d2 = h.PersistedSeconds(day2);
            Console.WriteLine($"      10-05={d1}s，10-06={d2}s（跨天各记一行）");
            failed += Check("跨天：两拍分别落在各自的日期行（各 1 秒）", d1 == 1 && d2 == 1);
            failed += Check("跨天：榜单被清空后重新累计（新的一天仅 1 秒）",
                Ms(h.Bridge.Tracker.AttributedTotal) == 1000);
        }

        // ---- A6 切换软件强制 flush（不等 15 秒）----
        {
            string db = Path.Combine(dir, "switch.db");
            // flush 间隔故意设得很大：若“切软件强制 flush”失效，则本项会超时失败。
            using var h = new Harness(db, day, flushInterval: TimeSpan.FromMinutes(10));
            h.Source.Push(App("chrome"));
            h.Start();

            h.Advance(1000);
            h.Advance(1000);
            h.WaitConserved(3000);

            long before = h.PersistedSeconds();
            h.Source.Push(App("notepad"));     // 切换
            h.Advance(1000);

            bool flushed = WaitUntil(() => h.PersistedSeconds() >= 2, 3000);
            Console.WriteLine($"      切换前落库={before}s，切换后={h.PersistedSeconds()}s，强制 flush 计数={h.Store.ForcedFlushes}");
            failed += Check("切换软件触发强制 flush（间隔 10 分钟也不等）", flushed && h.Store.ForcedFlushes >= 1);
        }

        // ---- A7 重启后仍在（§5.6 核心验收）----
        {
            string db = Path.Combine(dir, "restart.db");
            long before;
            {
                var s = new SqliteStore(new StorageOptions { DatabasePath = db });
                s.Initialize();
                s.AddSeconds(new[] { new AppUsageRow(day, "devenv", 1234) });
                before = s.SecondsOf(day, "devenv");
            }
            SqliteStore.ClearPools();     // 模拟进程退出：释放池化连接（否则 Windows 上文件句柄仍被持有）

            var reopened = new SqliteStore(new StorageOptions { DatabasePath = db });
            reopened.Initialize();
            long after = reopened.SecondsOf(day, "devenv");
            Console.WriteLine($"      关闭前={before}s，重新打开后={after}s");
            failed += Check("重启后今天的时长仍在（新连接读回同一数值）", after == before && after == 1234);

            // ---- A8 启动续算 Seed ----
            var tracker = new AppUsageTracker();
            int seeded = UsageSeeding.SeedTracker(tracker, reopened.ReadDay(day), day);

            long totalMs = tracker.TotalMillisecondsOf("devenv");
            string display = tracker.Top(1).Count > 0 ? tracker.Top(1)[0].DisplayName : "(空)";

            Console.WriteLine($"      续算：灌入 {seeded} 条，devenv={totalMs}ms，展示名=\"{display}\"");
            failed += Check("续算：基线灌入榜单（1234 秒 = 1234000ms）", totalMs == 1234000);
            failed += Check("续算：展示名由合并键现算恢复（devenv → Visual Studio）", display == "Visual Studio");
            failed += Check("续算：不污染时间守恒（本次运行已归因为 0）", Ms(tracker.AttributedTotal) == 0);
            failed += Check("续算：SeedTotal 单独统计", Ms(tracker.SeededTotal) == 1234000);

            reopened.Dispose();
            SqliteStore.ClearPools();
        }

        // ---- A11 写盘失败不丢数据、不抛异常 ----
        {
            string db = Path.Combine(dir, "failure.db");
            using var h = new Harness(db, day, flushInterval: TimeSpan.FromMinutes(10));
            h.Source.Push(App("chrome"));
            h.Start();

            h.Advance(2000);
            h.WaitConserved(3000);

            h.Store.Store.FailNextCommits = 1;          // 故障注入：下一次提交失败
            bool first = h.Store.Flush();
            bool preserved = h.Store.PendingMilliseconds == 2000 && h.PersistedSeconds() == 0;

            bool second = h.Store.Flush();              // 重试成功
            bool recovered = h.PersistedSeconds() == 2 && h.Store.PendingMilliseconds == 0;

            Console.WriteLine($"      第一次 flush 返回={first}（应 false）、待写保留={h.Store.PendingMilliseconds}ms、失败计数={h.Store.Failures}");
            failed += Check("写盘失败：返回 false、待写数据原样保留、库中仍未写入", !first && preserved);
            failed += Check("写盘失败：不抛异常到调用方（计数可见）", h.Store.Failures >= 1 && h.Store.LastError is not null);
            failed += Check("重试成功：数据补齐入库（2 秒），待写归零", second && recovered);
        }

        // ---- A12 队列满：不阻塞、丢弃并计数、记账自洽 ----
        {
            string db = Path.Combine(dir, "burst.db");
            using var h = new Harness(db, day, queueCapacity: 16, flushInterval: TimeSpan.FromMinutes(10));
            h.Source.Push(App("chrome"));
            h.Start();

            for (int i = 0; i < 200; i++) h.Advance(250);   // 快速灌入，远超队列容量
            bool drained = WaitUntil(() => h.Store.Enqueued + h.Store.Dropped >= h.Store.CreditedTicks, 3000);

            Console.WriteLine($"      被计入={h.Store.CreditedTicks}，入队={h.Store.Enqueued}，丢弃={h.Store.Dropped}");
            failed += Check("队列满：丢弃并计数，入队 + 丢弃 == 被计入（不阻塞、不丢账）",
                drained && h.Store.Enqueued + h.Store.Dropped == h.Store.CreditedTicks);
            failed += Check("队列满：心跳未被打断（200 拍全部计入）", h.Store.CreditedTicks == 200);
        }

        // ---- A13 非软件活跃落库 + 重启续算（“今日真实活跃”跨重启不下滑）----
        // 这是本次口径修复的**守门员**：只灌软件基线会让重启后的“今日”变小，
        // 而变小的那一刻**不报任何错** —— 只能靠这类断言咬住。
        {
            Console.WriteLine("   -- 非软件活跃（桌面 / 无前台 / 锁屏）落库与续算 --");
            string db = Path.Combine(dir, "daily.db");

            long appSeconds;
            long filteredSeconds;
            long unattributedSeconds;
            {
                using var h = new Harness(db, day, flushInterval: TimeSpan.FromMinutes(10));
                h.Source.Push(App("chrome"));
                h.Start();

                // 关键：**停掉真实定时器**，只用手动 Advance 驱动。
                // 否则真实 1s 定时器会与脚本序列**争抢样本**（其余用例因为“重复最后一个样本”而不受影响，
                // A13 是第一个依赖“不同样本序列”的用例 —— 会让落库结果随机）。
                h.Scheduler.Stop();

                h.Advance(1000);
                h.Advance(1000);
                h.Advance(1000);
                h.Source.Push(Desktop());      // 桌面：不计入使用时长
                h.Advance(1000);
                h.Advance(1000);
                h.Source.Push(NoWindow());     // 无前台：无前台/未知
                h.Advance(1000);
                h.Source.Push(LockScreenSample());   // 锁屏：完全不计入（也不落库）
                h.Advance(1000);

                h.WaitConserved(3000);

                // 还要等**非软件**那几拍也被存储侧累加进待写 —— WaitConserved 的目标是榜单的
                // “已归因”总量，**不覆盖**非软件拍；抢在它们入账前 Flush 会读出“2s / 0s”
                // （这正是本用例曾经随机失败过的形态）。
                // 合计 6 拍 × 1000ms（3 软件 + 2 计入不使用的桌面 + 1 无前台；锁屏那拍按口径根本不入队）。
                if (!WaitUntil(() => h.Store.PendingMilliseconds >= 6000, 5000))
                    Console.WriteLine($"      （警告）非软件增量未在超时内入账：待写={h.Store.PendingMilliseconds}ms");

                h.Store.Flush();

                appSeconds = h.PersistedSeconds();
                DayActivityRow da = h.Store.Store.ReadDayActivity(day);
                filteredSeconds = da.FilteredSeconds;
                unattributedSeconds = da.UnattributedSeconds;

                failed += Check("落库：桌面/无前台进 daily_activity，不进 app_usage（app_usage 仅 3s）",
                    appSeconds == 3);
                failed += Check($"落库：不计入使用时长=2s、无前台/未知=1s（实际 {filteredSeconds}/{unattributedSeconds}）",
                    filteredSeconds == 2 && unattributedSeconds == 1);
            }
            SqliteStore.ClearPools();

            var reopened = new SqliteStore(new StorageOptions { DatabasePath = db });
            reopened.Initialize();

            // 续算用**重新打开的库**里的值，而不是上面那几个局部变量：
            // “重启后续算”本就该以库为准（局部变量只是刚才那一瞬间的读数）。
            DayActivityRow persisted = reopened.ReadDayActivity(day);

            // 续算：软件 + 非软件**一起**灌回
            var full = new AppUsageTracker();
            UsageSeeding.SeedTracker(full, reopened.ReadDay(day), day,
                persisted.FilteredSeconds, persisted.UnattributedSeconds);
            long fullMs = Ms(full.SeededTotal);

            failed += Check("续算：基线 = 软件 + 非软件（3 + 2 + 1 = 6 秒）", fullMs == 6000);
            failed += Check("续算：非软件分量单独可见（不计入 2s / 无前台 1s）",
                Ms(full.SeededFilteredTotal) == 2000 && Ms(full.SeededUnattributedTotal) == 1000);

            // 回归：**只灌软件**会让“今日”少 3 秒 —— 这正是本次修复的缺陷形态。
            var naive = new AppUsageTracker();
            UsageSeeding.SeedTracker(naive, reopened.ReadDay(day), day);
            failed += Check("回归：不灌非软件基线时“今日”少 3 秒（缺陷会在此暴露）",
                Ms(naive.SeededTotal) == 3000 && fullMs - Ms(naive.SeededTotal) == 3000);

            // 关键：软件榜的占比分母**不应**被非软件稀释。
            failed += Check("续算：Top 占比只用软件基线（非软件不稀释占比）",
                full.Top(1).Count == 1 && Math.Abs(full.Top(1)[0].Share - 1.0) < 1e-9);

            reopened.Dispose();
            SqliteStore.ClearPools();
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "A 段：全部通过。" : $"A 段：{failed} 项失败。");
        return failed;
    }

    // ================================================================ B 真实观察

    private static int RunRealObservation(int seconds, string[] args)
    {
        Console.WriteLine("---- B 真实观察（真实空闲源 + 真实前台进程 → 真写库 → 关闭重开）----");

        int flushMs = Math.Max(1000, GetInt(args, "--flush-ms=", 15000));
        string dir = GetString(args, "--dir=") ?? Path.Combine("artifacts", "m4");
        Directory.CreateDirectory(dir);

        string db = Path.Combine(dir, "real.db");
        SqliteStore.ClearPools();
        TryDelete(db);

        var source = new Win32ForegroundAppSource();
        var clock = new Win32IdleClock();
        Console.WriteLine($"数据源     ：{(source.IsAvailable ? "可用" : "不可用：" + source.LastError)}");
        Console.WriteLine($"空闲源     ：{(clock.IsAvailable ? "可用" : "不可用")}（当前空闲 {clock.IdleTime.TotalSeconds:F1}s）");
        Console.WriteLine($"数据库     ：{Path.GetFullPath(db)}（flush 间隔 {flushMs / 1000.0:F1}s）");

        try { NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); } catch { }
        EnvironmentInfo.Print();

        var options = new StorageOptions
        {
            DatabasePath = db,
            FlushInterval = TimeSpan.FromMilliseconds(flushMs),
        };

        int rc = 0;
        long trackerMs;
        long persistedSeconds;
        long pendingMs;
        DateOnly day;

        using (var scheduler = new ActivityScheduler(clock))
        using (var bridge = new AppUsageBridge(scheduler, source))
        using (var store = new AppUsageStore(bridge, options))
        {
            Console.WriteLine();
            Console.WriteLine($"观察 {seconds}s：请**切换几个软件各停留若干秒**，以便产生可归因数据。");
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

                Console.WriteLine(
                    $"  [t={sec,3}s] 当前={bridge.Tracker.CurrentDisplayName} 今日={Format(scheduler.TodayActive)} " +
                    $"归因={Format(bridge.Tracker.AttributedTotal)} 落库={store.FlushedSeconds}s 待写={store.PendingMilliseconds}ms");
            }

            scheduler.Stop();
            sw.Stop();

            day = scheduler.Day;
            trackerMs = Ms(bridge.Tracker.AttributedTotal);
            pendingMs = store.PendingMilliseconds;

            Console.WriteLine();
            Console.WriteLine("---- 关闭前 ----");
            Console.WriteLine($"心跳         ：{scheduler.Sequence} 拍，今日活跃 {Format(scheduler.TodayActive)}（{Ms(scheduler.TodayActive)} ms）");
            Console.WriteLine($"榜单归因     ：{Format(bridge.Tracker.AttributedTotal)}（{trackerMs} ms）");
            Console.WriteLine($"守恒差       ：{Ms(bridge.Tracker.Total) - Ms(scheduler.TodayActive)} ms（应为 0）");
            Console.WriteLine($"存储         ：flush {store.Flushes} 次（其中强制 {store.ForcedFlushes} 次），已落库 {store.FlushedSeconds}s，待写 {pendingMs}ms");
            Console.WriteLine($"队列         ：入队 {store.Enqueued}，丢弃 {store.Dropped}，写失败 {store.Failures}");

            if (Ms(bridge.Tracker.Total) - Ms(scheduler.TodayActive) != 0) rc |= 2;
            if (!source.IsAvailable) rc |= 1;
            if (scheduler.Sequence == 0) rc |= 16;
            if (store.Failures > 0) rc |= 8;

            // Dispose 会强制刷出尾段；随后重开数据库验证持久化（§5.6 验收）。
        }

        SqliteStore.ClearPools();

        var reopened = new SqliteStore(new StorageOptions { DatabasePath = db });
        reopened.Initialize();
        IReadOnlyList<AppUsageRow> rows = reopened.ReadDay(day);
        persistedSeconds = reopened.TotalSecondsOfDay(day);

        Console.WriteLine();
        Console.WriteLine("---- 重新打开数据库（模拟程序重启）----");
        Console.WriteLine($"日期         ：{day:yyyy-MM-dd}");
        Console.WriteLine($"读出的总时长 ：{persistedSeconds}s");
        if (rows.Count == 0)
        {
            Console.WriteLine("  （本轮没有可归因数据 —— 可能全程停留在桌面/外壳/挂机；A 段已覆盖持久化逻辑）");
        }
        else
        {
            foreach (AppUsageRow r in rows)
                Console.WriteLine($"    {r.AppName,-24} {r.Seconds,6}s");
        }
        int failed = 0;

        long expectedSeconds = trackerMs / 1000;    // 落库只写整秒，余数本就该留下
        failed += Check($"重启后时长仍在：库中 {persistedSeconds}s == 榜单归因取整 {expectedSeconds}s",
            persistedSeconds == expectedSeconds);

        // ---- 续算演示：从库中把今天的量灌回一个新的榜单 ----
        var seededTracker = new AppUsageTracker();
        int seededCount = UsageSeeding.SeedTracker(seededTracker, rows, day);
        long seededMs = 0;
        foreach (AppUsageEntry e in seededTracker.Top(10)) seededMs += e.Milliseconds;

        Console.WriteLine($"续算         ：灌入 {seededCount} 条，合计 {seededMs}ms（应等于 {persistedSeconds * 1000}ms）");
        failed += Check("续算：灌入总量 == 库中总量", seededMs == persistedSeconds * 1000);

        reopened.Dispose();
        SqliteStore.ClearPools();

        if (failed > 0) rc |= 4;
        if (rc == 0)
            Console.WriteLine("真实观察：通过 —— 批量落库、退出强制 flush、重启后数据仍在、续算一致。");
        return rc;
    }

    // ================================================================ 辅助

    /// <summary>确定性测试用的“调度器 + 归属 + 存储”整套装置（假时钟、假空闲源、脚本化前台）。</summary>
    private sealed class Harness : IDisposable
    {
        private readonly FakeIdleClock _clock = new();
        private readonly FakeTickSource _ticks = new();

        public Harness(string dbPath, DateOnly day, TimeSpan? flushInterval = null, int queueCapacity = 8192)
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
                FlushInterval = flushInterval ?? TimeSpan.FromMinutes(10),
                QueueCapacity = queueCapacity,
            });
        }

        public DateTime Now { get; set; }

        public ScriptedSource Source { get; } = new();

        public ActivityScheduler Scheduler { get; }

        public AppUsageBridge Bridge { get; }

        public AppUsageStore Store { get; }

        public void Start() => Scheduler.Start();

        /// <summary>推进假时钟若干毫秒并同步心跳一次（完全不依赖真实定时器）。</summary>
        public void Advance(long milliseconds)
        {
            _ticks.Milliseconds += milliseconds;
            Scheduler.Pump();
        }

        /// <summary>落库秒数（单日）。</summary>
        public long PersistedSeconds(DateOnly day) => Store.Store.TotalSecondsOfDay(day);

        public long PersistedSeconds() => PersistedSeconds(DateOnly.FromDateTime(Now));

        /// <summary>等待“已落库 + 待写”达到与该日累计一致（与 flush 时机无关的守恒判据）。</summary>
        public bool WaitConserved(int timeoutMs)
        {
            DateOnly d = DateOnly.FromDateTime(Now);
            long target = Ms(Bridge.Tracker.AttributedTotal);
            return WaitUntil(() => PersistedSeconds(d) * 1000 + Store.PendingMilliseconds >= target
                                   && Store.Enqueued + Store.Dropped >= Store.CreditedTicks
                                   && Store.DayEnqueued + Store.DayDropped >= Store.DayCreditedTicks, timeoutMs);
        }

        /// <summary>只等待队列被消费（跨天等场景用：目标日期不等于“当前累计”）。</summary>
        public bool WaitConsumed(int timeoutMs)
            => WaitUntil(() => Store.Enqueued + Store.Dropped >= Store.CreditedTicks
                               && Store.DayEnqueued + Store.DayDropped >= Store.DayCreditedTicks, timeoutMs);

        public void Dispose()
        {
            Store.Dispose();
            Bridge.Dispose();
            Scheduler.Dispose();
            SqliteStore.ClearPools();
        }
    }

    private static ForegroundAppSample App(string processName)
        => ForegroundAppRules.Create(new IntPtr(0x1234), 4321, processName, "Win32Class", "标题", isSelfProcess: false);

    /// <summary>桌面采样 → “不计入使用时长”。</summary>
    private static ForegroundAppSample Desktop()
        => ForegroundAppRules.Create(new IntPtr(0x2222), 2222, "explorer", "Progman", "", isSelfProcess: false);

    /// <summary>无前台窗口采样 → “无前台 / 未知”。</summary>
    private static ForegroundAppSample NoWindow()
        => ForegroundAppRules.Create(IntPtr.Zero, 0, null, null, null, isSelfProcess: false);

    /// <summary>锁屏采样 → 按口径完全不计入（既不展示也不落库）。</summary>
    private static ForegroundAppSample LockScreenSample()
        => ForegroundAppRules.Create(new IntPtr(0x3333), 3333, "LogonUI", "LogonUI", "", isSelfProcess: false);

    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }
        return condition();
    }

    private static bool Contains(IReadOnlyList<string> list, string value)
    {
        foreach (string s in list)
            if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static long FindLimit(IReadOnlyList<LimitRow> limits, string scope, string target)
    {
        foreach (LimitRow l in limits)
            if (string.Equals(l.Scope, scope, StringComparison.Ordinal) &&
                string.Equals(l.Target, target, StringComparison.Ordinal))
                return l.Seconds;
        return -1;
    }

    private static void TryCleanDatabases(string dir)
    {
        SqliteStore.ClearPools();
        try
        {
            foreach (string f in Directory.EnumerateFiles(dir, "*.db*"))
                TryDelete(f);
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

    private static long Ms(TimeSpan t) => (long)Math.Round(t.TotalMilliseconds, MidpointRounding.AwayFromZero);

    private static string Format(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";

    private static void WriteLogFile()
    {
        string path = _logPath ?? Path.Combine("artifacts", "m4", "m4-selfcheck.log");

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
            AppendConsole("写日志文件失败：" + ex.Message);
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
        public bool IsAvailable { get; set; } = true;
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
            _buffer.Append(value);
            _buffer.Append('\n');
        }
    }
}
