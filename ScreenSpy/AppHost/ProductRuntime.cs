using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ScreenSpy.Collector;
using ScreenSpy.Logging;
using ScreenSpy.Scheduling;
using ScreenSpy.Storage;

namespace ScreenSpy.AppHost;

/// <summary>产品启动结果。</summary>
internal enum HostStartOutcome
{
    /// <summary>成功启动。</summary>
    Started,

    /// <summary>已有实例在运行（本次不启动，也不写任何数据）。</summary>
    AlreadyRunning,

    /// <summary>致命失败（连计时核心都没能建立）。</summary>
    Failed,
}

/// <summary>启动结果（成功时携带运行体）。</summary>
internal readonly struct HostStartResult
{
    private HostStartResult(HostStartOutcome outcome, ProductRuntime? runtime, string? message)
    {
        Outcome = outcome;
        Runtime = runtime;
        Message = message;
    }

    public HostStartOutcome Outcome { get; }
    public ProductRuntime? Runtime { get; }
    public string? Message { get; }

    public static HostStartResult Started(ProductRuntime runtime) => new(HostStartOutcome.Started, runtime, null);

    public static HostStartResult AlreadyRunning() =>
        new(HostStartOutcome.AlreadyRunning, null, "ScreenSpy 已经在运行（同一个用户会话内只能有一个实例）。");

    public static HostStartResult Failed(string message) => new(HostStartOutcome.Failed, null, message);
}

/// <summary>
/// **产品组合根**：把 M1（计时）、M2（锁屏/睡眠）、M3（软件归属）、M4（落库）装配成一条真实运行链路，
/// 并在启动时按既定策略处理失败（**降级继续运行 + 显示警告**，而不是拒绝启动）。
///
/// ────────────────────────────────────────────────────────────────────────
/// 装配顺序（顺序本身就是契约，不能随意调整）
/// ────────────────────────────────────────────────────────────────────────
///  1. 单实例保护 —— 必须在打开数据库**之前**，否则两个实例都会先写库再发现冲突；
///  2. <see cref="ActivityScheduler"/>（M1）—— 一切的事件源都挂在它身上；
///  3. <see cref="SessionPauseBridge"/>（M2）—— 构造时即做一次锁屏探测（启动初值）；
///  4. <see cref="AppUsageBridge"/>（M3）—— 旁听心跳，产出“逐拍观测”；
///  5. <see cref="AppUsageStore"/>（M4）与 <see cref="AppActivityLogger"/> —— 都旁听 <c>Observed</c>；
///  6. **启动续算**（把库中今日数据灌回榜单基线）—— 必须在 <c>Start()</c> 之前，
///     因为 <c>AppUsageTracker.Seed</c> 会重置当日状态（见其文档）；
///  7. <c>Start()</c> —— 开始心跳。
///
/// ────────────────────────────────────────────────────────────────────────
/// 降级策略（本次确认：降级继续运行并显示警告）
/// ────────────────────────────────────────────────────────────────────────
///  * **SQLite 打不开** → 计时与界面照常，只是不落库、不续算；
///  * **日志目录不可写** → 跳过日志（启动时就探测，而不是等到第一次写盘才安静地失败）；
///  * **会话监听失败** → 继续运行，但明确警告“锁屏/睡眠可能被计入”；
///  * **单实例保护不可用** → 放行运行并警告（保护失效不该等于程序打不开）。
///  所有这些都变成 <see cref="Warnings"/> 里的可见文字，而不是被吞掉。
///
/// 释放顺序与装配相反：先停心跳（不再产生观测），再刷日志尾段与落库尾段，最后释放其余。
/// </summary>
internal sealed class ProductRuntime : IDisposable
{
    private readonly StartupOptions _options;
    private readonly SingleInstanceGuard _guard;
    private readonly List<string> _warnings = new();

    private ActivityScheduler? _scheduler;
    private SessionPauseBridge? _session;
    private AppUsageBridge? _usage;
    private AppUsageStore? _store;
    private AppActivityLogger? _logger;

    private int _seededApps;
    private int _disposed;

    private ProductRuntime(StartupOptions options, SingleInstanceGuard guard)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));

        try
        {
            Build();
        }
        catch
        {
            // 构造中途失败：把自己已经建起来的部分释放掉（Dispose 对 null 与半成品都安全）。
            Dispose();
            throw;
        }
    }

    /// <summary>崩溃/异常时收集到的降级说明（正常为空）。</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>实际使用的数据库路径（即使存储被降级关闭，这里也显示“打算写哪”）。</summary>
    public string DatabasePath => _options.DatabasePath;

    /// <summary>实际使用的日志目录。</summary>
    public string LogDirectory => _options.LogDirectory;

    /// <summary>
    /// 用户手动暂停（托盘「暂停统计」，M5b）。
    ///
    /// 它写的是 <see cref="ActivityScheduler.UserPaused"/> 而**不是** <c>Paused</c>：
    /// 后者由 M2 的周期探测按会话状态重写，写它会让用户的暂停在几秒后被静默清除。
    /// </summary>
    public bool UserPaused
    {
        get => _scheduler?.UserPaused ?? false;
        set
        {
            if (_scheduler is not null) _scheduler.UserPaused = value;
        }
    }

    /// <summary>尝试启动产品运行时。</summary>
    public static HostStartResult Start(StartupOptions options)
    {
        SingleInstanceGuard guard = SingleInstanceGuard.Acquire();
        if (!guard.IsOwner)
        {
            guard.Dispose();
            return HostStartResult.AlreadyRunning();
        }

        try
        {
            return HostStartResult.Started(new ProductRuntime(options, guard));
        }
        catch (Exception ex)
        {
            // 构造函数已负责释放半成品；互斥体也在其中被释放。
            return HostStartResult.Failed(ex.GetType().Name + ": " + ex.Message);
        }
    }

    // ================================================================ 装配

    private void Build()
    {
        foreach (string unknown in _options.Unknown)
            Warn($"无法识别的启动参数「{unknown}」已忽略。请检查拼写 —— 拼错的路径参数会被静默退回产品默认路径。");

        if (_guard.Error is { } guardError)
            Warn($"单实例保护不可用（{guardError}）。若同时启动两个实例，时长会被重复累加。");

        // ---- M1：计时核心（唯一的“致命”依赖，失败则整体启动失败）
        _scheduler = new ActivityScheduler(new Win32IdleClock(), _options.IdleThreshold, _options.Heartbeat);

        // ---- M2：锁屏 / 睡眠
        try
        {
            _session = new SessionPauseBridge(
                _scheduler,
                new SystemSessionSignalSource(),
                new Win32SessionStateProbe(),
                _options.SessionProbeInterval);
        }
        catch (Exception ex)
        {
            _session = null;
            Warn($"会话/电源监听初始化失败（{Describe(ex)}）：锁屏与睡眠期间的时长**可能被计入**。");
        }

        // ---- M3：前台软件归属（旁听心跳，不修改 M1/M2）
        _usage = new AppUsageBridge(_scheduler, new Win32ForegroundAppSource());

        // ---- M4：SQLite 聚合持久化
        StorageOptions storageOptions = _options.ToStorageOptions();
        if (storageOptions.Enabled)
        {
            try
            {
                _store = new AppUsageStore(_usage, storageOptions);
            }
            catch (Exception ex)
            {
                _store = null;
                Warn($"SQLite 存储不可用（{Describe(ex)}）：计时继续，但今日数据**不会落库**、重启后也不续算。库={storageOptions.DatabasePath}");
                try { SqliteStore.ClearPools(); } catch { /* 忽略 */ }
            }
        }

        // ---- 原始活动日志
        ActivityLogOptions logOptions = _options.ToLogOptions();
        if (logOptions.Enabled)
        {
            if (TryPrepareLogDirectory(logOptions.Directory, out string? logError))
            {
                try
                {
                    _logger = new AppActivityLogger(_usage, logOptions);
                }
                catch (Exception ex)
                {
                    _logger = null;
                    Warn($"原始活动日志不可用（{Describe(ex)}）：计时与落库不受影响。");
                }
            }
            else
            {
                Warn($"原始活动日志目录不可写（{logError}）：已跳过日志记录。目录={logOptions.Directory}");
            }
        }

        // ---- 启动续算（必须在 Start 之前：Seed 会重置当日状态）
        if (_store is { IsEnabled: true })
        {
            try
            {
                DateOnly today = DateOnly.FromDateTime(DateTime.Now);
                IReadOnlyList<AppUsageRow> rows = _store.Store.ReadDay(today);
                // 一并读“不计入使用时长 / 无前台·未知”基线 —— 只灌软件会让重启后“今日”变小。
                DayActivityRow dayActivity = _store.Store.ReadDayActivity(today);
                _seededApps = UsageSeeding.SeedTracker(_usage.Tracker, rows, today,
                    dayActivity.FilteredSeconds, dayActivity.UnattributedSeconds);
            }
            catch (Exception ex)
            {
                Warn($"启动续算失败（{Describe(ex)}）：今日时长从 0 开始累计（历史数据仍在库中）。");
            }
        }

        // ---- 数据源可用性（启动时就能判定的，直接警告出来）
        if (!_scheduler.IdleSourceAvailable)
            Warn("空闲数据源不可用（GetLastInputInfo 失败）：无法判定挂机，活跃时长会偏大。");

        if (!_usage.Source.IsAvailable)
            Warn($"前台窗口采样不可用：无法按软件归属。{_usage.Source.LastError}");

        _scheduler.Start();
    }

    /// <summary>
    /// 启动时就把日志目录探活（建目录 + 写删一个临时文件）。
    ///
    /// 为什么值得单独做：<see cref="ActivityLogWriter"/> 是**懒创建**目录的 ——
    /// 若目录不可写，失败会推迟到“第一次写盘”才发生，届时很容易被忽略成“日志有点慢”。
    /// 既然既定策略是“降级并显示警告”，就应该在启动那一刻把话说清楚。
    /// </summary>
    private static bool TryPrepareLogDirectory(string directory, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(directory);

            string probe = Path.Combine(directory, ".screenspy-write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            error = Describe(ex);
            return false;
        }
    }

    private void Warn(string message)
    {
        if (!_warnings.Contains(message)) _warnings.Add(message);
    }

    private static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message;

    // ================================================================ 状态快照

    /// <summary>取一份运行状态快照（供主界面每秒刷新）。永不抛异常。</summary>
    public RuntimeStatus Snapshot()
    {
        var status = new RuntimeStatus
        {
            SampledAt = DateTime.Now,
            OptionsDescription = _options.Describe(),
            Warnings = _warnings,
            DatabasePath = _options.DatabasePath,
            LogDirectory = _options.LogDirectory,
        };

        ActivityScheduler? scheduler = _scheduler;
        if (scheduler is not null)
        {
            status.Running = scheduler.IsRunning;
            status.TodayActive = scheduler.TodayActive;
            status.AccountedElapsed = scheduler.AccountedElapsed;
            status.LastIdle = scheduler.LastIdle;
            status.Sequence = scheduler.Sequence;
            status.GapTicks = scheduler.GapTicks;
            status.TimerFirings = scheduler.TimerFirings;
            status.LastActive = scheduler.LastActive;
            status.IdleSourceAvailable = scheduler.IdleSourceAvailable;
            status.SchedulerError = scheduler.LastError;
            status.IdleThreshold = scheduler.IdleThreshold;
            status.Heartbeat = scheduler.Heartbeat;
            // 生效暂停必须取 IsPaused（= 会话暂停 || 用户手动暂停），而**不是** Paused。
            // 只取 Paused 会让“托盘手动暂停”在状态面板里显示成「挂机中」、
            // 托盘 tooltip 也不显示「（已暂停）」—— 界面在骗人，且不报错。
            // 回归：--m5-selfcheck 的 D 组（回退此行会恰好在该组 FAIL）。
            status.Paused = scheduler.IsPaused;
            status.UserPaused = scheduler.UserPaused;
        }

        SessionPauseBridge? session = _session;
        if (session is not null)
        {
            status.SessionAttached = true;
            status.SessionHooked = session.IsHooked;
            status.SessionHookError = session.HookError;
            status.Locked = session.State.Locked;
            status.Suspended = session.State.Suspended;
            status.SessionProbeText = session.LastProbeResult.ToString();
            status.SessionProbeError = session.LastProbeError;
        }

        AppUsageBridge? usage = _usage;
        if (usage is not null)
        {
            AppUsageTracker tracker = usage.Tracker;
            status.AppSourceAvailable = usage.Source.IsAvailable;
            status.AppSourceError = usage.Source.LastError;
            status.CurrentApp = tracker.CurrentDisplayName;
            status.HasCurrentApp = tracker.HasLastSample;
            status.Top = tracker.Top(5);
            status.Attributed = tracker.AttributedTotal;
            status.Filtered = tracker.FilteredTotal;
            status.Unattributed = tracker.UnattributedTotal;
            status.LockExcluded = tracker.LockScreenExcludedTotal;
            status.Seeded = tracker.SeededTotal;
            status.SeededFiltered = tracker.SeededFilteredTotal;
            status.SeededUnattributed = tracker.SeededUnattributedTotal;
            status.AppCount = tracker.AppCount;
            status.SamplingFailures = usage.SamplingFailures;
        }

        // ---- “今日”的对外口径（本次统一）
        // 对外展示（主界面大字号、托盘 tooltip、卡片）应当是**今天一整天的真实活跃**，
        // 而不是“本次运行”，也**不含锁屏/睡眠**：
        //   今天一整天 = 本次运行的全部计入拍（TodayActive）
        //              − 锁屏/登录界面剔除（LockExcluded，口径：完全不计入）
        //              + 库中今日基线（Seeded = 软件 + 不计入使用时长 + 无前台/未知）。
        //
        // 该值由 RuntimeStatus.TodayTotal **计算属性**给出，不在这里赋值：
        // 派生值若存成字段，漏填或填错时机都会让界面安静地显示 0:00（不报错的错）。
        //
        // 为什么两个来源都保留、不把 TodayActive 直接改成整天：
        //   M3 的时间守恒等式（已归因 + 已过滤 + 未归因 == 被计入的活跃）**只描述本次运行**，
        //   基线不计入其中（见 AppUsageTracker.Seed 的语义边界）。若 TodayActive 变成整天，
        //   守恒等式会立刻被历史数据破坏，自检的核心断言随之失效。
        status.SeededApps = _seededApps;

        AppUsageStore? store = _store;
        if (store is not null)
        {
            status.StoreActive = store.IsEnabled;
            status.FlushInterval = store.Options.FlushInterval;
            status.FlushedSeconds = store.FlushedSeconds;
            status.PendingMilliseconds = store.PendingMilliseconds;
            status.Flushes = store.Flushes;
            status.ForcedFlushes = store.ForcedFlushes;
            status.StoreDropped = store.Dropped;
            status.StoreFailures = store.Failures;
            status.StoreError = store.LastError;
        }

        AppActivityLogger? logger = _logger;
        if (logger is not null)
        {
            status.LogActive = logger.IsEnabled;
            status.LogRecords = logger.Records;
            status.LogWritten = logger.Writer.Written;
            status.LogDropped = logger.Writer.Dropped;
            status.LogCurrentPath = logger.Writer.CurrentPath;
            status.LogError = logger.LastError ?? logger.Writer.LastError;
        }

        return status;
    }

    // ================================================================ 释放

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // 先停心跳：确保不再产生新的观测（否则边刷边释放会与后台线程竞争）。
        try { _scheduler?.Stop(); } catch { /* 忽略 */ }

        // 再刷尾段：日志的“未关闭状态段”与落库的“未 flush 增量”都在这里落盘。
        try { _logger?.Dispose(); } catch { /* 忽略 */ }
        try { _store?.Dispose(); } catch { /* 忽略 */ }

        try { _usage?.Dispose(); } catch { /* 忽略 */ }
        try { _session?.Dispose(); } catch { /* 忽略 */ }
        try { _scheduler?.Dispose(); } catch { /* 忽略 */ }

        try { _guard.Dispose(); } catch { /* 忽略 */ }
    }
}
