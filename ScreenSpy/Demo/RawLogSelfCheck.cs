using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using ScreenSpy.Collector;
using ScreenSpy.Logging;
using ScreenSpy.Scheduling;

namespace ScreenSpy.Demo;

/// <summary>
/// 原始活动日志的**自检探针**：负责在自检/演示入口里挂上日志、并在收尾时打印证据。
///
/// 之所以单独成文件：日志是**旁路**功能，不应该让 M3 的归属链路代码变复杂。
/// 入口只需两行（<see cref="Create"/> 与 <see cref="Report"/>）。
/// </summary>
internal sealed class RawLogProbe : IDisposable
{
    private readonly AppActivityLogger _logger;
    private readonly ActivityLogOptions _options;
    private int _disposed;

    private RawLogProbe(AppUsageBridge bridge, ActivityLogOptions options)
    {
        _options = options;
        _logger = new AppActivityLogger(bridge, options);

        Console.WriteLine($"原始日志   ：{(IsEnabled ? "开启（仅变化时记录）" : "关闭")}");
        Console.WriteLine($"日志目录   ：{Path.GetFullPath(options.Directory)}");
        if (options.IncludeWindowTitle)
            Console.WriteLine("             字段含窗口标题（可能包含网页标题/文档名等敏感内容）；" +
                              "关闭方式见 docs/M3-原始活动日志.md。");
    }

    public bool IsEnabled => _logger.IsEnabled;

    /// <summary>
    /// 按命令行参数挂上日志。未指定时**默认开启**（与产品默认一致），
    /// 但目录落在开发用的 <paramref name="defaultLeaf"/> 下，避免开发期污染用户目录。
    /// </summary>
    public static RawLogProbe? Create(AppUsageBridge bridge, string[] args, string defaultLeaf)
    {
        if (HasFlag(args, "--no-raw-log"))
        {
            Console.WriteLine("原始日志   ：本次已关闭（--no-raw-log）");
            return null;
        }

        var options = new ActivityLogOptions
        {
            Directory = GetString(args, "--log-dir=") ?? Path.Combine("artifacts", defaultLeaf),
        }.Normalize();

        return new RawLogProbe(bridge, options);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _logger.Dispose();   // 关闭尾段并等待后台线程落盘
    }

    /// <summary>Dispose 之后调用：给出可写进文档/日志的客观证据（行数、路径、样例行）。</summary>
    public string Report()
    {
        var sb = new StringBuilder();
        ActivityLogWriter w = _logger.Writer;

        sb.AppendLine($"原始日志     ：{w.Written} 行（丢弃 {w.Dropped}，写盘错误 {w.Errors}，折叠失败 {_logger.Failures}）");

        if (w.CurrentPath is { } path)
        {
            sb.AppendLine($"日志文件     ：{path}");
            foreach (string line in ReadFirstLines(path, 2))
                sb.AppendLine("  > " + line);
        }
        else
        {
            sb.AppendLine("日志文件     ：（本轮未发生状态变化，没有产生任何行）");
        }

        if (_logger.LastError is { } err) sb.AppendLine($"日志错误     ：{err}");
        return sb.ToString();
    }

    private static List<string> ReadFirstLines(string path, int count)
    {
        var lines = new List<string>();
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8);
            for (int i = 0; i < count; i++)
            {
                string? line = reader.ReadLine();
                if (line is null) break;
                lines.Add(line);
            }
        }
        catch { /* 读不到就不显示 */ }
        return lines;
    }

    private static bool HasFlag(string[] args, string flag)
    {
        foreach (string a in args)
            if (a.Equals(flag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string? GetString(string[] args, string prefix)
    {
        foreach (string a in args)
            if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return a[prefix.Length..];
        return null;
    }
}

/// <summary>
/// 原始活动日志的确定性检查（A9 段）。由 M3 自检调用。
///
/// 覆盖：折叠语义（仅变化时产出）、原始名保留、原始类名与标题、不计入的三种原因、
/// 标题首/末/变更次数、隐私开关、跨天切分、过滤类记录，以及**真实落盘 + JSON 转义 + 保留期剪枝**。
/// </summary>
internal static class RawLogSelfCheck
{
    public static int Run()
    {
        Console.WriteLine();
        Console.WriteLine("---- A9 原始活动日志（JSONL；仅变化时记录；含真实落盘与剪枝）----");
        int failed = 0;

        DateTime day = new(2026, 10, 5, 12, 0, 0);
        IntPtr h = new(0x1234);

        ForegroundAppSample devenv = ForegroundAppRules.Create(h, 100, "devenv", "HwndWrapper[devenv", "Program.cs - ScreenSpy", false);
        ForegroundAppSample chrome = ForegroundAppRules.Create(h, 300, "chrome", "Chrome_WidgetWin_1", "页面 A", false);
        ForegroundAppSample desktop = ForegroundAppRules.Create(h, 200, "explorer", "Progman", "", false);

        // ---------------- 折叠：只在变化时产出 ----------------
        var t1 = new ActivityLogStateTracker(trackTitles: true);
        failed += Check("日志：首拍不产出记录（状态还没结束）", t1.Observe(Obs(1, day, devenv, true)) is null);
        failed += Check("日志：同状态继续 2 拍仍不产出（“仅变化时记录”的核心）",
            t1.Observe(Obs(2, day, devenv, true)) is null && t1.Observe(Obs(3, day, devenv, true)) is null);
        failed += Check("日志：此时存在未关闭状态", t1.HasOpenState);

        ActivityLogRecord r1 = t1.Flush() ?? default;
        failed += Check("日志：Flush 关闭状态 → dur=3000ms / ticks=3 / reason=active",
            r1.DurationMs == 3000 && r1.Ticks == 3 && r1.Counted && r1.Reason == ActivityLogReason.Active);
        failed += Check("日志（本功能核心）：保留**原始**进程名 devenv，同时保留显示名 Visual Studio",
            r1.ProcessName == "devenv" && r1.MergeKey == "devenv" && r1.DisplayName == "Visual Studio");
        failed += Check("日志：保留原始窗口类名与首标题",
            r1.ClassName == "HwndWrapper[devenv" && r1.Title == "Program.cs - ScreenSpy");
        failed += Check("日志：Flush 后不再有未关闭状态", !t1.HasOpenState);

        // ---------------- 状态切换 ----------------
        var t2 = new ActivityLogStateTracker();
        t2.Observe(Obs(1, day, devenv, true));
        ActivityLogRecord? closed = t2.Observe(Obs(2, day, chrome, true));
        failed += Check("日志：切换软件 → 关闭上一段（devenv 1 拍 = 1000ms）",
            closed.HasValue && closed.Value.ProcessName == "devenv"
            && closed.Value.DurationMs == 1000 && closed.Value.Ticks == 1);
        ActivityLogRecord r2 = t2.Flush() ?? default;
        failed += Check("日志：新段独立累计（chrome 1 拍，merge_key=chrome）",
            r2.MergeKey == "chrome" && r2.DurationMs == 1000 && r2.Ticks == 1);

        // ---------------- 不计入的三种原因 ----------------
        var t3 = new ActivityLogStateTracker();
        t3.Observe(Obs(1, day, devenv, false));
        ActivityLogRecord rIdle = t3.Flush() ?? default;
        failed += Check("日志：挂机 → counted=false / reason=idle",
            !rIdle.Counted && rIdle.Reason == ActivityLogReason.Idle);

        var t4 = new ActivityLogStateTracker();
        t4.Observe(Obs(1, day, devenv, false, paused: true));
        ActivityLogRecord rPaused = t4.Flush() ?? default;
        failed += Check("日志：暂停（锁屏/睡眠）→ reason=paused（优先于 idle）",
            rPaused.Reason == ActivityLogReason.Paused);

        var t5 = new ActivityLogStateTracker();
        t5.Observe(Obs(1, day, devenv, false, gap: true));
        ActivityLogRecord rGap = t5.Flush() ?? default;
        failed += Check("日志：时间空洞 → reason=gap", rGap.Reason == ActivityLogReason.Gap);

        // ---------------- 标题：首 / 末 / 变更次数 ----------------
        var t6 = new ActivityLogStateTracker(trackTitles: true);
        t6.Observe(Obs(1, day, Titled(devenv, "A"), true));
        t6.Observe(Obs(2, day, Titled(devenv, "B"), true));
        t6.Observe(Obs(3, day, Titled(devenv, "B"), true));
        t6.Observe(Obs(4, day, Titled(devenv, "C"), true));
        ActivityLogRecord rTitle = t6.Flush() ?? default;
        failed += Check("日志：标题记首=A、末=C、变更 2 次（标题不参与状态界定，故不刷屏）",
            rTitle.Title == "A" && rTitle.TitleLast == "C" && rTitle.TitleChanges == 2 && rTitle.Ticks == 4);

        var t7 = new ActivityLogStateTracker(trackTitles: false);
        t7.Observe(Obs(1, day, Titled(devenv, "敏感标题"), true));
        ActivityLogRecord rNoTitle = t7.Flush() ?? default;
        failed += Check("日志：关闭标题记录 → 标题为空、变更计数为 0（隐私开关生效）",
            rNoTitle.Title.Length == 0 && rNoTitle.TitleLast.Length == 0 && rNoTitle.TitleChanges == 0);

        // ---------------- 跨天：一行不跨天 ----------------
        var t8 = new ActivityLogStateTracker();
        t8.Observe(Obs(1, day, devenv, true));
        t8.Observe(Obs(2, day, devenv, true));
        ActivityLogRecord? straddle = t8.Observe(Obs(3, new DateTime(2026, 10, 6, 0, 0, 0), devenv, true));
        failed += Check("日志：跨天先关闭旧段（day=10-05、dur=2000ms、ticks=2）",
            straddle.HasValue && straddle.Value.Day == new DateOnly(2026, 10, 5)
            && straddle.Value.DurationMs == 2000 && straddle.Value.Ticks == 2);
        ActivityLogRecord rNewDay = t8.Flush() ?? default;
        failed += Check("日志：跨天那一拍整拍归入新的一天（day=10-06、ticks=1、dur=1000ms）",
            rNewDay.Day == new DateOnly(2026, 10, 6) && rNewDay.Ticks == 1 && rNewDay.DurationMs == 1000);

        // ---------------- 过滤类同样记录 ----------------
        var t9 = new ActivityLogStateTracker();
        t9.Observe(Obs(1, day, desktop, true));
        ActivityLogRecord rDesktop = t9.Flush() ?? default;
        failed += Check("日志：桌面在前台（计入活跃但不归因软件）→ kind=desktop、merge_key=#desktop、reason=active",
            rDesktop.Kind == ForegroundAppKind.Desktop && rDesktop.MergeKey == "#desktop"
            && rDesktop.Reason == ActivityLogReason.Active);

        // ---------------- 默认选项 ----------------
        ActivityLogOptions def = ActivityLogOptions.CreateDefault();
        failed += Check("日志：产品默认开启，目录位于 %LOCALAPPDATA%\\ScreenSpy\\logs",
            def.Enabled && def.IncludeWindowTitle
            && def.Directory.EndsWith(Path.Combine("ScreenSpy", "logs"), StringComparison.OrdinalIgnoreCase));
        failed += Check("日志：保留期默认 30 天", def.RetentionDays == 30);

        // ---------------- 真实落盘 / 转义 / 剪枝 ----------------
        string tmp = Path.Combine(Path.GetTempPath(), "screenspy-logtest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var writer = new ActivityLogWriter(new ActivityLogOptions { Directory = tmp, RetentionDays = 30 });
            failed += Check("日志：启用时写入器报告 IsEnabled", writer.IsEnabled);

            var probe = new ActivityLogStateTracker(trackTitles: true);
            probe.Observe(Obs(1, day, Titled(devenv, "标题 \"引号\" 与 \\ 反斜杠"), true));
            ActivityLogRecord tricky = probe.Flush() ?? default;

            writer.TryWrite(tricky);
            writer.TryWrite(r1);
            writer.Dispose();   // 关闭并等待后台线程落盘

            string file = Path.Combine(tmp, "app-activity-20261005.jsonl");
            string[] lines = File.Exists(file) ? File.ReadAllLines(file) : Array.Empty<string>();

            failed += Check("日志：真实落盘 → 文件存在且恰好 2 行（JSONL）", lines.Length == 2);
            failed += Check("日志：计数与实际一致（Written=2 / Dropped=0 / Errors=0）",
                writer.Written == 2 && writer.Dropped == 0 && writer.Errors == 0);

            bool parsedOk = false;
            bool escapedOk = false;
            if (lines.Length >= 1)
            {
                using JsonDocument doc = JsonDocument.Parse(lines[0]);
                JsonElement root = doc.RootElement;
                parsedOk = root.GetProperty("proc").GetString() == "devenv"
                           && root.GetProperty("display").GetString() == "Visual Studio"
                           && root.GetProperty("kind").GetString() == "app"
                           && root.GetProperty("reason").GetString() == "active"
                           && root.GetProperty("counted").GetBoolean()
                           && root.GetProperty("dur_ms").GetInt64() == 1000;
                escapedOk = root.GetProperty("title").GetString() == "标题 \"引号\" 与 \\ 反斜杠";
            }
            failed += Check("日志：连标准 JSON 解析器读回，原始名/显示名/时长/原因/分类均正确", parsedOk);
            failed += Check("日志：含引号与反斜杠的标题被正确转义并可原样读回", escapedOk);

            // 保留期剪枝：相对 2026-10-05，30 天前为 2026-09-05。
            string stale = Path.Combine(tmp, "app-activity-20200101.jsonl");
            string recent = Path.Combine(tmp, "app-activity-20261004.jsonl");
            File.WriteAllText(stale, "{\"stale\":true}\n");
            File.WriteAllText(recent, "{\"recent\":true}\n");

            var writer2 = new ActivityLogWriter(new ActivityLogOptions { Directory = tmp, RetentionDays = 30 });
            writer2.TryWrite(r1);   // day=2026-10-05 → 打开分片并触发剪枝
            writer2.Dispose();

            failed += Check("日志：保留期剪枝 → 超出 30 天的分片删除、昨天的保留",
                !File.Exists(stale) && File.Exists(recent));
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); } catch { /* 忽略 */ }
        }

        return failed;
    }

    // ------------------------------------------------------------ 辅助

    private static AppUsageObservation Obs(long seq, DateTime localTime, ForegroundAppSample sample, bool counted,
                                           bool paused = false, bool gap = false)
        => new(new ActivityTick(seq, localTime, TimeSpan.Zero, idleSourceAvailable: true,
                                isActive: counted, isPaused: paused, isGap: gap,
                                elapsed: TimeSpan.FromSeconds(1), todayActive: TimeSpan.Zero),
               sample, counted);

    private static ForegroundAppSample Titled(ForegroundAppSample baseSample, string title)
        => ForegroundAppRules.Create(baseSample.Hwnd, baseSample.ProcessId, baseSample.ProcessName,
                                     baseSample.ClassName, title, isSelfProcess: false);

    private static int Check(string label, bool ok)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        return ok ? 0 : 1;
    }
}
