using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ScreenSpy.Analytics;
using ScreenSpy.Collector;
using ScreenSpy.Interop;
using ScreenSpy.Limits;
using ScreenSpy.Logging;
using ScreenSpy.Scheduling;
using ScreenSpy.Storage;
using ScreenSpy.Widget;

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
/// 「自定义软件名称」（M9-1b）的结果。
///
/// 刻意把四种情况分开，而不是只回一个 bool：界面要靠它给出**准确**的原因。
/// 只说"失败"会让用户猜（是他名字打错了？还是跟别人重名了？还是存储坏了？），
/// 而"把原因说错"本身就是一种"界面在骗人"。
/// </summary>
internal enum AppRenameResult
{
    /// <summary>已保存（显示名已生效）。</summary>
    Ok,

    /// <summary>名字本身不合法（空、过长、含控制字符）。</summary>
    Invalid,

    /// <summary>与别的软件重名 —— <c>conflictAppKey</c> 指出是跟谁撞了。</summary>
    Conflict,

    /// <summary>存储不可用（已降级运行），改动未生效。</summary>
    NoStore,
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

    /// <summary>M3 采样源的具体类型（M9-1 需要把身份层按引用推给它，因此不能只留接口）。</summary>
    private Win32ForegroundAppSource? _appSource;

    /// <summary>M9-1 软件身份层（别名 + 分类）。不可变，改动即整份替换。</summary>
    private AppIdentity _identity = AppIdentity.Empty;

    /// <summary>M9-2 限额守门（规则 + 阈值 + 去重）。为 null 表示限额功能不可用（不致命）。</summary>
    private LimitGuard? _limits;

    /// <summary>M10 近 7 天柱状图的数据入口（只读库）。为 null 表示图表不可用（不致命）。</summary>
    private Analytics.ChartQuery? _chart;

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
        _appSource = new Win32ForegroundAppSource();
        _usage = new AppUsageBridge(_scheduler, _appSource);

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

        // ---- M9-1：软件身份层（别名 + 分类）
        // 必须在**续算之前**加载：续算灌入的展示名要按用户的设置来，
        // 否则重启后的那一瞬间，界面上的名字会先跳回进程名、再被纠正（甚至一直不回）。
        LoadIdentity();

        // ---- M9-2：限额守门（规则 + 阈值 + 去重）
        // 每个心跳判一次（心跳默认 1s）。判定本身是纯算术，成本可忽略；
        // 只有**真的跨过阈值**那一次才写库（去重标记），因此不会每秒写盘。
        //
        // 订阅放在 Start() 之前：否则会漏掉最初那几拍（虽然它们几乎不可能超限，但"漏掉"这件事本身不可靠）。
        AppUsageBridge? usageBridge = _usage;
        if (usageBridge is not null)
        {
            try
            {
                _limits = new LimitGuard(
                    () => _store is { IsEnabled: true } s ? s.Store : null,
                    key => AppNamingRules.EffectiveName(_identity, key));
                _limits.Reload();

                // ---- M10：近 7 天柱状图的数据入口（**只读库**，用户 2026-10-06 决策）。
                //  身份层（显示名 / 分类）用委托**现取**、不缓存：用户改名 / 改分类 / 合并软件之后，
                //  图表标题与分类聚合必须立刻跟着变 —— 缓存一份就会"标题永远停在旧名"，且不报错。
                _chart = new Analytics.ChartQuery(
                    () => IdentityStore,
                    key => AppNamingRules.EffectiveName(_identity, key),
                    key => _identity.CategoryOf(key));
                _limits.Notified += note => LimitNotified?.Invoke(note);
                usageBridge.Observed += OnUsageObserved;
            }
            catch (Exception ex)
            {
                _limits = null;
                Warn($"限额功能初始化失败（{Describe(ex)}）：本次运行不启用任何限额提醒。");
            }
        }

        // ---- 启动续算（必须在 Start 之前：Seed 会重置当日状态）
        if (_store is { IsEnabled: true } && usageBridge is not null)
        {
            try
            {
                DateOnly today = DateOnly.FromDateTime(DateTime.Now);
                IReadOnlyList<AppUsageRow> rows = _store.Store.ReadDay(today);
                // 一并读“不计入使用时长 / 无前台·未知”基线 —— 只灌软件会让重启后“今日”变小。
                DayActivityRow dayActivity = _store.Store.ReadDayActivity(today);
                _seededApps = UsageSeeding.SeedTracker(usageBridge.Tracker, rows, today,
                    dayActivity.FilteredSeconds, dayActivity.UnattributedSeconds, _identity);
            }
            catch (Exception ex)
            {
                Warn($"启动续算失败（{Describe(ex)}）：今日时长从 0 开始累计（历史数据仍在库中）。");
            }
        }

        // ---- 数据源可用性（启动时就能判定的，直接警告出来）
        ActivityScheduler? scheduler = _scheduler;
        if (scheduler is not null && !scheduler.IdleSourceAvailable)
            Warn("空闲数据源不可用（GetLastInputInfo 失败）：无法判定挂机，活跃时长会偏大。");

        if (usageBridge is not null && !usageBridge.Source.IsAvailable)
            Warn($"前台窗口采样不可用：无法按软件归属。{usageBridge.Source.LastError}");

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

        // ---- M9-2：卡片的限额进度条
        // 只有真的存在限额时才填；为空时渲染器**整块不画**（宁可不显示，也不显示一个并不存在的约束）。
        // 取“最接近上限”的那一条：卡片只有一根条，放最紧急的才有意义。
        LimitGuard? limitGuard = _limits;
        if (limitGuard is not null)
        {
            if (limitGuard.LastError is { } limitError)
                status.Warnings = AppendWarning(_warnings, limitError);

            if (limitGuard.Rules.Count > 0 && LimitEngine.MostCritical(LimitStatuses()) is { } critical)
            {
                status.LimitText = LimitEngine.ComposeCardLimitText(critical);
                status.LimitRatio = Math.Clamp(critical.Ratio, 0.0, 1.0);
            }
        }

        return status;
    }

    /// <summary>把一条运行期警告并进启动期警告列表（不改动原列表，界面读到的始终是同一份快照）。</summary>
    private static IReadOnlyList<string> AppendWarning(IReadOnlyList<string> warnings, string extra)
    {
        var list = new List<string>(warnings.Count + 1);
        foreach (string warning in warnings) list.Add(warning);
        if (!list.Contains(extra)) list.Add(extra);
        return list;
    }

    // ================================================================ 放置（M7：形态 + 坐标）

    /// <summary>
    /// 读卡片放置（形态 + 坐标）。存储不可用或从未设置过 → 返回默认（**嵌入** + 默认坐标）。
    ///
    /// 坐标会被夹回屏幕内：它是上一次会话存的，中间可能换过分辨率或拔过显示器，
    /// 直接照搬会让卡片落在屏幕外 —— 那就是卡片消失了，且屏幕上没有任何提示。
    /// </summary>
    public WidgetPlacement LoadWidgetPlacement()
    {
        var fallback = new WidgetPlacement(WidgetMode.Embedded, 200, 700);
        SqliteStore? store = _store is { IsEnabled: true } s ? s.Store : null;

        WidgetPlacement placement = WidgetPlacementStore.Load(store, fallback);

        int screenWidth = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        int screenHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        return WidgetPlacementStore.ClampToScreen(placement, screenWidth, screenHeight);
    }

    /// <summary>保存卡片坐标（「调整位置」结束时调用）。存储不可用则静默忽略。</summary>
    public void SaveWidgetPosition(int x, int y)
        => WidgetPlacementStore.SavePosition(_store is { IsEnabled: true } s ? s.Store : null, x, y);

    /// <summary>保存卡片形态。</summary>
    public void SaveWidgetMode(WidgetMode mode)
        => WidgetPlacementStore.SaveMode(_store is { IsEnabled: true } s ? s.Store : null, mode);

    // ================================================================ 软件身份层（M9-1）

    /// <summary>当前分类列表（界面下拉框用）。</summary>
    public IReadOnlyList<string> Categories => _identity.Categories;

    /// <summary>当前全部别名（原始键 → 归一键；界面列出"现存合并"用）。</summary>
    public IReadOnlyDictionary<string, string> Aliases => _identity.Aliases;

    /// <summary>今日全部软件（按时长降序，含库中基线）。界面「软件与分类」一节用。</summary>
    public IReadOnlyList<AppUsageEntry> AppsToday()
        => _usage?.Tracker.All() ?? Array.Empty<AppUsageEntry>();

    /// <summary>某软件的归属分类（未分类返回空串）。</summary>
    public string CategoryOf(string appKey) => _identity.CategoryOf(appKey);

    /// <summary>存储可用时给出库；不可用返回 null（此时身份层的一切改动都"不生效"而不是"只改内存"）。</summary>
    private SqliteStore? IdentityStore => _store is { IsEnabled: true } s ? s.Store : null;

    /// <summary>
    /// 加载身份层并推给采样源。
    /// 读失败时**退回空身份**（= M9-1 之前的行为：不合并、不改名）并给出可见警告 ——
    /// 绝不"用一半的身份"运行，那会让部分软件被合并、部分没有，比全都退回更难解释。
    /// </summary>
    private void LoadIdentity()
    {
        try
        {
            _identity = AppIdentityStore.Load(IdentityStore);
        }
        catch (Exception ex)
        {
            _identity = AppIdentity.Empty;
            Warn($"软件合并/分类设置读取失败（{Describe(ex)}）：本次按“不合并、不改名”运行，" +
                 "已保存的合并与分类**本次不生效**（数据本身未受影响）。");
        }

        if (_appSource is not null) _appSource.Identity = _identity;
    }

    /// <summary>设置某软件的分类（空串 = 未分类）。返回是否写入成功。</summary>
    public bool SetAppCategory(string appKey, string category)
    {
        try
        {
            _identity = AppIdentityStore.SetCategory(IdentityStore, _identity, appKey, category);
            if (_appSource is not null) _appSource.Identity = _identity;
            return IdentityStore is not null;
        }
        catch (Exception ex)
        {
            Warn($"保存软件分类失败（{Describe(ex)}）：该改动未生效（界面会显示回原值）。");
            return false;
        }
    }

    /// <summary>新建分类。返回是否成功（存储不可用则失败）。</summary>
    public bool CreateCategory(string name)
    {
        try
        {
            string trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length == 0) return false;

            _identity = AppIdentityStore.CreateCategory(IdentityStore, _identity, trimmed);
            return IdentityStore is not null;
        }
        catch (Exception ex)
        {
            Warn($"新建分类失败（{Describe(ex)}）。");
            return false;
        }
    }

    /// <summary>删除分类；<paramref name="removed"/> 说明是否确实删掉了（用于给出准确反馈）。</summary>
    public bool DeleteCategory(string name, out bool removed)
    {
        removed = false;
        try
        {
            _identity = AppIdentityStore.DeleteCategory(IdentityStore, _identity, name, out removed);
            if (_appSource is not null) _appSource.Identity = _identity;
            return IdentityStore is not null;
        }
        catch (Exception ex)
        {
            Warn($"删除分类失败（{Describe(ex)}）。");
            return false;
        }
    }

    /// <summary>
    /// 把一个软件**合并**到另一个软件（M9-1）。
    ///
    /// 这个动作必须同时改**三处**，少一处今天就会多出一条账（不报错、只是数字对不上）：
    ///  ① 库内历史行（<c>SqliteStore.MergeAppKey</c>，含今天早些时候已经落库的部分）；
    ///  ② 内存榜单（<c>AppUsageTracker.MergeKeys</c>，含"本次运行 + 库中基线"两部分）；
    ///  ③ 待写增量（<c>AppUsageStore.MergePendingKeys</c>，尚未 flush 的那些毫秒）。
    /// 然后登记别名，让**今后**的采样直接归到目标键上。
    ///
    /// 失败时如实返回 false 并给出警告，不假装成功。
    /// </summary>
    public bool MergeApps(string fromKey, string toKey)
    {
        try
        {
            string from = (fromKey ?? string.Empty).Trim();
            string to = (toKey ?? string.Empty).Trim();
            if (from.Length == 0 || to.Length == 0 || string.Equals(from, to, StringComparison.Ordinal)) return false;
            if (_usage is null) return false;

            // 显示名取**目标软件当前的显示名**：用户在列表里点的是"合并到 Visual Studio"，
            // 合并后它就该叫这个；若目标还没有显示名，则退回身份层的默认算法。
            string? display = null;
            foreach (AppUsageEntry entry in _usage.Tracker.All())
            {
                if (string.Equals(entry.MergeKey, to, StringComparison.Ordinal))
                {
                    display = entry.DisplayName;
                    break;
                }
            }

            // 顺序刻意如此：**先库、后内存**。库写失败会抛出 → 内存一个字都没动，
            // 于是"要么都改、要么都没改"，不会留下半截状态。
            SqliteStore? store = IdentityStore;
            store?.MergeAppKey(from, to);

            _usage.Tracker.MergeKeys(from, to, display);
            _store?.MergePendingKeys(from, to, display);

            _identity = _identity.WithMerged(from, to);
            if (!string.IsNullOrEmpty(display)) _identity = _identity.WithDisplayName(to, display!);
            if (store is not null) store.SetAppDisplayName(to, display ?? string.Empty);
            if (_appSource is not null) _appSource.Identity = _identity;
            return true;
        }
        catch (Exception ex)
        {
            Warn($"合并软件失败（{Describe(ex)}）：未做任何改动。");
            return false;
        }
    }

    /// <summary>
    /// 取消合并（**对今后生效**）：删掉别名，之后这个进程名不再被并入目标软件。
    /// <paramref name="removed"/> 说明是否确实删掉了一条别名。
    ///
    /// ⚠️ 语义边界：已经并入的**历史秒数不会退回**（库里的行已被重写成归一键）。
    /// 这是"清空历史、从今天重新开始"这一决策的必然结果；原始活动日志仍保留原始进程名，追溯不丢。
    /// </summary>
    public bool UnmergeApp(string rawKey, out bool removed)
    {
        removed = false;
        try
        {
            _identity = AppIdentityStore.Unmerge(IdentityStore, _identity, rawKey, out removed);
            if (_appSource is not null) _appSource.Identity = _identity;
            return IdentityStore is not null;
        }
        catch (Exception ex)
        {
            Warn($"取消合并失败（{Describe(ex)}）。");
            return false;
        }
    }

    // ================================================================ 自定义软件名称（M9-1b）

    /// <summary>某软件的**系统默认名**（友好名表命中则用之，否则是归一键本身）。对话框用它说明"恢复默认名"会变成什么。</summary>
    public string DefaultDisplayName(string appKey)
        => ForegroundAppRules.NormalizeDisplayName((appKey ?? string.Empty).Trim());

    /// <summary>给用户看的"这是哪个软件"：显示名（归一键）。用于"跟谁重名了"的提示。</summary>
    public string DescribeApp(string appKey)
        => AppNamingRules.DescribeKey(_identity, (appKey ?? string.Empty).Trim());

    /// <summary>
    /// 改名**预检**（对话框每次点「保存」时调用，不写任何东西）：返回错误说明，<c>null</c> 表示可以保存。
    /// 规则本体在 <see cref="AppNamingRules.Validate"/>，与写库前那道闸门是**同一份实现**。
    /// </summary>
    public string? ValidateRename(string appKey, string displayName)
    {
        try
        {
            return AppNamingRules.Validate(_identity, (appKey ?? string.Empty).Trim(), displayName, KnownAppKeys());
        }
        catch (Exception ex)
        {
            return "预检失败：" + Describe(ex);
        }
    }

    /// <summary>
    /// 给某软件起一个**自定义显示名**（M9-1b）；传空串 = 恢复系统默认名。
    ///
    /// 三处必须一起生效（与"合并"同一个道理，少一处就会出现自相矛盾的界面）：
    ///  ① 身份层 → **今后**的采样与**重启后**的续算都带上新名；
    ///  ② 内存榜单 → 主界面「Top 5」与卡片榜单**现在就**是新名；
    ///  ③ "当前软件"（<c>_last</c>）→ 卡片上"正在使用 …"**现在就**是新名，不必等它再次成为前台。
    ///
    /// ⚠️ 它是**纯展示层**动作：毫秒数、归一键、分类、守恒等式**一个都不动**。
    /// 顺带一提，这也意味着"改名"会作用于**所有日期**（库里只存归一键，显示名是读取时现算的）——
    /// 这正是用户期望的（改一次名，昨天/上周的记录也一起变），不需要回填任何历史。
    /// </summary>
    public AppRenameResult RenameApp(string appKey, string displayName, out string conflictAppKey)
    {
        conflictAppKey = string.Empty;

        try
        {
            string key = (appKey ?? string.Empty).Trim();
            if (key.Length == 0) return AppRenameResult.Invalid;

            string name = (displayName ?? string.Empty).Trim();
            bool restoring = name.Length == 0;
            IReadOnlyList<string> known = KnownAppKeys();

            // 预检（与对话框同一规则）。失败时如实区分"不合法"与"重名"，绝不"写一半"。
            if (AppNamingRules.Validate(_identity, key, name, known) is not null)
            {
                string conflict = AppNamingRules.FindConflict(
                    _identity, key, AppNamingRules.ResolveName(key, name), known,
                    customNamesOnly: restoring);

                if (conflict.Length != 0)
                {
                    conflictAppKey = conflict;
                    return AppRenameResult.Conflict;
                }

                return AppRenameResult.Invalid;
            }

            SqliteStore? store = IdentityStore;
            if (store is null) return AppRenameResult.NoStore;

            _identity = AppIdentityStore.SetDisplayName(store, _identity, key, name, known, out string raced);
            if (raced.Length != 0)
            {
                conflictAppKey = raced;
                return AppRenameResult.Conflict;
            }

            _appSource?.Identity = _identity;
            _usage?.Tracker.SetDisplayName(key, AppNamingRules.ResolveName(key, name));
            return AppRenameResult.Ok;
        }
        catch (Exception ex)
        {
            Warn($"保存软件名称失败（{Describe(ex)}）：该改动未生效。");
            return AppRenameResult.NoStore;
        }
    }

    /// <summary>
    /// 当前"可能出现在界面上的软件键"（今日榜单的全部条目）。
    /// 「禁止重名」用它把"今日出现过的软件"纳入搜索范围；
    /// 其余两类（用户设过的名字 / 友好名表）由 <see cref="AppNamingRules"/> 自己补上。
    /// </summary>
    private IReadOnlyList<string> KnownAppKeys()
    {
        IReadOnlyList<AppUsageEntry> apps = AppsToday();
        if (apps.Count == 0) return Array.Empty<string>();

        var keys = new List<string>(apps.Count);
        foreach (AppUsageEntry entry in apps) keys.Add(entry.MergeKey);
        return keys;
    }

    // ================================================================ 限额（M9-2）

    /// <summary>该发限额提醒了。回调可能在**心跳线程**上，上层必须自行 marshal 到 UI 线程。</summary>
    public event Action<LimitNotification>? LimitNotified;

    // ================================================================ 近 7 天柱状图（M10）

    /// <summary>
    /// 读一张图表（**只读库**）。窗口 = 今天 + 前 <paramref name="days"/>-1 天。
    ///
    /// 为什么不掺内存里的实时口径：用户已明确选择"只读库"（7 根柱完全同源）。
    /// 代价是今天这根最多滞后约 15 秒（落库间隔），由界面**明说**，不藏着。
    /// 失败（存储不可用 / 读库异常）时返回可显示的原因，而不是一张空图 —— 空图会被读成"这几天真没用电脑"。
    /// </summary>
    public ChartResult ReadChart(ChartScope scope, int days = ChartBuilder.DefaultDays)
        => _chart?.Read(scope, DateOnly.FromDateTime(DateTime.Now), days)
           ?? ChartResult.Failure("图表数据入口不可用（启动时装配失败）。");

    /// <summary>图表「软件」维度的候选（近 N 天库中出现过的归一键，按时长降序）与可能的失败原因。</summary>
    public (IReadOnlyList<string> Keys, string? Error) ReadChartApps(int days = ChartBuilder.DefaultDays)
        => _chart?.ReadAppTargets(DateOnly.FromDateTime(DateTime.Now), days)
           ?? (Array.Empty<string>(), "图表数据入口不可用（启动时装配失败）。");

    /// <summary>图表「分类」维度的候选项（身份层的分类表）。</summary>
    public IReadOnlyList<string> ChartCategories => _identity.Categories;

    /// <summary>当前限额规则（界面展示用）。</summary>
    public IReadOnlyList<LimitRule> Limits => _limits?.Rules ?? Array.Empty<LimitRule>();

    /// <summary>阈值的设置文本（界面输入框用），如 <c>"80,100"</c>。</summary>
    public string LimitThresholdsText => _limits?.ThresholdsText ?? LimitRules.DefaultThresholdsText;

    /// <summary>限额功能的内部错误（正常为 null），供界面显示可见警告。</summary>
    public string? LimitError => _limits?.LastError;

    /// <summary>各条限额的当前状态（界面按需刷新用；纯读取，不产生副作用）。</summary>
    public IReadOnlyList<LimitStatus> LimitStatuses()
        => LimitEngine.Statuses(_limits?.Rules, BuildLimitInput());

    /// <summary>设置 / 更新一条限额。存储不可用或时长非法 → false（不改内存、不假装成功）。</summary>
    public bool SetLimit(LimitKind kind, string target, TimeSpan limit)
        => _limits?.SetLimit(kind, target, limit) ?? false;

    /// <summary>删除一条限额；<paramref name="removed"/> 说明是否确实删掉了一行。</summary>
    public bool RemoveLimit(LimitKind kind, string target, out bool removed)
    {
        removed = false;
        return _limits?.RemoveLimit(kind, target, out removed) ?? false;
    }

    /// <summary>保存阈值文本（如 <c>"80,100"</c>）；失败时给出可读原因。</summary>
    public bool SetLimitThresholds(string? text, out string? error)
    {
        if (_limits is null)
        {
            error = "限额功能不可用（见上方警告）。";
            return false;
        }
        return _limits.SetThresholds(text, out error);
    }

    /// <summary>
    /// 组装一份限额判定输入（“今天一整天”口径）。
    ///
    /// 总量刻意沿用与 <see cref="RuntimeStatus.TodayTotal"/> **同一个公式**：
    /// 本次运行活跃 − 锁屏剔除 + 库中基线。
    /// 若这里另算一套，就会出现“限额说超了、大字号看着没超”的自相矛盾 ——
    /// 而两处数字对不上时，用户只会怀疑整个程序。
    /// </summary>
    private LimitInput BuildLimitInput()
    {
        AppUsageBridge? usage = _usage;
        ActivityScheduler? scheduler = _scheduler;
        if (usage is null || scheduler is null) return LimitInput.Empty;

        AppUsageTracker tracker = usage.Tracker;
        TimeSpan todayTotal = scheduler.TodayActive - tracker.LockScreenExcludedTotal + tracker.SeededTotal;
        long totalSeconds = (long)Math.Round(todayTotal.TotalSeconds, MidpointRounding.AwayFromZero);

        return LimitEngine.BuildInput(totalSeconds, tracker.All(), _identity.CategoryOf);
    }

    /// <summary>
    /// 每拍（默认 1 秒）判定一次限额。在**心跳线程**上执行，因此必须短小、且绝不抛异常
    /// —— 一次异常不该让计时停摆。
    /// </summary>
    private void OnUsageObserved(AppUsageObservation observation)
    {
        LimitGuard? guard = _limits;
        if (guard is null) return;

        try
        {
            if (guard.Rules.Count == 0) return;   // 没有限额 → 连输入都不必组装
            guard.Evaluate(BuildLimitInput(), DateOnly.FromDateTime(observation.Tick.LocalTime));
        }
        catch
        {
            // 判定异常绝不该打断心跳（判定内部已自行记录错误）。
        }
    }

    // ================================================================ 释放

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // 先停心跳：确保不再产生新的观测（否则边刷边释放会与后台线程竞争）。
        try { _scheduler?.Stop(); } catch { /* 忽略 */ }

        // 限额守门挂在 Observed 上（由心跳线程触发），必须与心跳一起摘掉。
        try { if (_usage is not null) _usage.Observed -= OnUsageObserved; } catch { /* 忽略 */ }
        _limits = null;

        // 再刷尾段：日志的“未关闭状态段”与落库的“未 flush 增量”都在这里落盘。
        try { _logger?.Dispose(); } catch { /* 忽略 */ }
        try { _store?.Dispose(); } catch { /* 忽略 */ }

        try { _usage?.Dispose(); } catch { /* 忽略 */ }
        try { _session?.Dispose(); } catch { /* 忽略 */ }
        try { _scheduler?.Dispose(); } catch { /* 忽略 */ }

        try { _guard.Dispose(); } catch { /* 忽略 */ }
    }
}
