using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ScreenSpy.Collector;

namespace ScreenSpy.Storage;

/// <summary>
/// M4 的核心：把“按软件累计”的活跃时长**批量**落库（开发文档 §5.6）。
///
/// ────────────────────────────────────────────────────────────────────────
/// 为什么这么设计（三条都是实际风险，不是理论洁癖）
/// ────────────────────────────────────────────────────────────────────────
///
/// 【1】**不能每秒写盘**：§5.6 明确要求，否则会疯狂写 SSD。
///       → 心跳线程只做“入队”（有界队列、永不阻塞）；真正的写盘在后台线程，
///         每 <see cref="StorageOptions.FlushInterval"/>（默认 15s）批量提交一次；
///         并在**切换软件时**与**退出时**强制 flush。
///
/// 【2】**不能依赖内存榜单**：调度器在跨天时**先**触发 <c>DayRolled</c>（榜单被清空）、
///      **后**触发 <c>Ticked</c>；而事件按订阅顺序调用，本类必然晚于榜单清空。
///      若“到点去读一眼榜单再写库”，午夜那一拍会读到**已清空的新一天**，
///      昨天的尾段将**永久丢失**。
///       → 本类自己在每拍观测里按**该拍自身的本地日期**累加增量，
///         与榜单的清空完全解耦；跨天天然写成两行，不需要任何特殊处理。
///
/// 【3】**秒级取整必须结转余数**：schema 是 <c>seconds INTEGER</c>，而每拍是毫秒。
///      若每拍各自取整成秒再累加，每拍最多丢 0.5 秒，一小时能丢几十秒。
///       → 内存里按**毫秒**累加，落库只写**整秒**，不足 1 秒的余数**留在内存继续攒**，
///         因此长期运行**零漂移**。
///
/// 写入模型：**增量累加 + 单事务**，且**只有提交成功之后才扣减待写数据**；
/// 失败则原样保留、下一轮重试。因此写盘失败**不会丢数据**，也不会把已有数据改小。
/// 唯一可能丢的是“尚未 flush 的内存增量”，上限即 flush 间隔（§5.6 验收：断电最多丢 15 秒）。
///
/// ────────────────────────────────────────────────────────────────────────
/// 线程模型
/// ────────────────────────────────────────────────────────────────────────
///  * 心跳线程：只调用 <see cref="OnObserved"/> → <c>TryAdd</c>（**绝不阻塞、绝不抛异常**）。
///  * 后台线程：唯一的聚合者与写盘者（<see cref="Pump"/>）。
///  * <c>_flushGate</c> 串行化 flush，避免“后台线程收尾”与“Dispose 兜底”并发把同一份数据写两遍。
/// </summary>
internal sealed class AppUsageStore : IDisposable
{
    private sealed class Pending
    {
        /// <summary>最近一次见到的展示名（仅诊断用）。</summary>
        public string DisplayName = string.Empty;

        /// <summary>待写毫秒数，**包含不足 1 秒的余数**（这就是“余数结转”的载体）。</summary>
        public long Milliseconds;

        /// <summary>累计收到的毫秒数（诊断：应与已落库秒数 + 待写毫秒自洽）。</summary>
        public long Received;
    }

    /// <summary>某一天“非软件”待写毫秒（不计入使用时长 / 无前台·未知），同样是余数结转的载体。</summary>
    private sealed class PendingDay
    {
        /// <summary>“不计入使用时长”（桌面 / 外壳 / 自身）待写毫秒。</summary>
        public long FilteredMilliseconds;

        /// <summary>“无前台 / 未知”待写毫秒。</summary>
        public long UnattributedMilliseconds;
    }

    private readonly AppUsageBridge? _bridge;
    private readonly StorageOptions _options;
    private readonly SqliteStore _store;
    private readonly BlockingCollection<UsageDelta>? _queue;
    private readonly Thread? _thread;
    private readonly bool _subscribed;

    private readonly object _gate = new();
    private readonly Dictionary<(DateOnly Day, string Key), Pending> _pending = new();
    private readonly Dictionary<DateOnly, PendingDay> _pendingDay = new();

    private readonly object _flushGate = new();

    private string? _lastKey;
    private int _disposed;

    private long _enqueued;
    private long _dropped;
    private long _creditedTicks;
    private long _dayEnqueued;
    private long _dayDropped;
    private long _dayCreditedTicks;
    private long _flushes;
    private long _forcedFlushes;
    private long _flushedSeconds;
    private long _failures;
    private string? _lastError;

    public AppUsageStore(AppUsageBridge bridge, StorageOptions? options = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _options = (options ?? StorageOptions.CreateDefault()).Normalize();
        _store = new SqliteStore(_options);

        if (!_options.Enabled) return;

        _store.Initialize();
        _queue = new BlockingCollection<UsageDelta>(_options.QueueCapacity);
        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "screenspy-usage-store",
        };
        _thread.Start();

        _bridge.Observed += OnObserved;
        _subscribed = true;
    }

    public bool IsEnabled => _queue is not null;

    public StorageOptions Options => _options;

    public SqliteStore Store => _store;

    /// <summary>入队的事件数（= 被计入且可归因的拍数）。</summary>
    public long Enqueued => Interlocked.Read(ref _enqueued);

    /// <summary>因队列满被丢弃的拍数（&gt;0 说明磁盘跟不上；计时未受影响）。</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>被计入的拍数（与入队数一致；分开统计便于对账）。</summary>
    public long CreditedTicks => Interlocked.Read(ref _creditedTicks);

    // ---- 非软件增量（不计入使用时长 / 无前台·未知）的独立记账 ----
    // 与软件增量分开计数，是为了让既有的“入队 + 丢弃 == 被计入”对账断言**语义不变**。
    // 软件侧从来只关心 app_usage 的账。

    /// <summary>非软件增量入队数。</summary>
    public long DayEnqueued => Interlocked.Read(ref _dayEnqueued);

    /// <summary>非软件增量因队列满被丢弃的拍数。</summary>
    public long DayDropped => Interlocked.Read(ref _dayDropped);

    /// <summary>非软件增量被计入的拍数。</summary>
    public long DayCreditedTicks => Interlocked.Read(ref _dayCreditedTicks);

    /// <summary>成功提交的事务次数。</summary>
    public long Flushes => Interlocked.Read(ref _flushes);

    /// <summary>其中由“切换软件”触发的强制 flush 次数。</summary>
    public long ForcedFlushes => Interlocked.Read(ref _forcedFlushes);

    /// <summary>已成功落库的总秒数。</summary>
    public long FlushedSeconds => Interlocked.Read(ref _flushedSeconds);

    /// <summary>写盘失败次数（&gt;0 说明磁盘/权限有问题；数据仍在内存等待重试）。</summary>
    public long Failures => Interlocked.Read(ref _failures);

    public string? LastError => _lastError;

    /// <summary>当前待写毫秒总数（诊断与自检用）。</summary>
    public long PendingMilliseconds
    {
        get
        {
            lock (_gate)
            {
                long total = 0;
                foreach (Pending p in _pending.Values) total += p.Milliseconds;
                foreach (PendingDay p in _pendingDay.Values)
                    total += p.FilteredMilliseconds + p.UnattributedMilliseconds;
                return total;
            }
        }
    }

    /// <summary>当前待写的条目明细（自检用）。</summary>
    public IReadOnlyList<(DateOnly Day, string Key, long Milliseconds, string DisplayName)> PendingSnapshot()
    {
        lock (_gate)
        {
            var list = new List<(DateOnly, string, long, string)>(_pending.Count);
            foreach (KeyValuePair<(DateOnly Day, string Key), Pending> kv in _pending)
                list.Add((kv.Key.Day, kv.Key.Key, kv.Value.Milliseconds, kv.Value.DisplayName));
            return list;
        }
    }

    // ================================================================ 心跳侧（绝不阻塞）

    private void OnObserved(AppUsageObservation observation)
    {
        try
        {
            // 只有“被计入”的拍才产生持久化增量（挂机 / 暂停 / 时间空洞一律不进）。
            if (!observation.Counted) return;

            ForegroundAppSample sample = observation.Sample;

            // 锁屏 / 登录界面：按口径**完全不计入**，既不落库也不展示。
            if (sample.IsLockScreen) return;

            long ms = CreditMilliseconds(observation.Tick.Elapsed);
            if (ms <= 0) return;

            bool isApp = sample.CountsAsApp;
            DateOnly day = DateOnly.FromDateTime(observation.Tick.LocalTime);

            // 非软件的两类也要落库（否则重启续算会让“今日真实活跃”变小）：
            //   桌面/外壳/自身 → “不计入使用时长”；无前台/未知 → “无前台/未知”。
            UsageDeltaKind dayKind = sample.IsFiltered
                ? UsageDeltaKind.Filtered
                : UsageDeltaKind.Unattributed;

            UsageDelta delta = isApp
                ? new UsageDelta(day,
                                 string.IsNullOrEmpty(sample.MergeKey) ? "#app" : sample.MergeKey,
                                 sample.DisplayName,
                                 ms,
                                 observation.Tick.Sequence)
                : UsageDelta.NonApp(day, dayKind, ms, observation.Tick.Sequence);

            if (isApp) Interlocked.Increment(ref _creditedTicks);
            else Interlocked.Increment(ref _dayCreditedTicks);

            BlockingCollection<UsageDelta>? queue = _queue;
            if (queue is null) return;

            if (queue.TryAdd(delta, 0))
            {
                if (isApp) Interlocked.Increment(ref _enqueued);
                else Interlocked.Increment(ref _dayEnqueued);
            }
            else
            {
                // 队列满：丢弃并计数。宁可丢数据，绝不拖慢心跳。
                if (isApp) Interlocked.Increment(ref _dropped);
                else Interlocked.Increment(ref _dayDropped);
            }
        }
        catch (Exception ex)
        {
            // 存储侧异常绝不允许冒泡到心跳线程（那会影响计时）。
            RecordError(ex);
        }
    }

    /// <summary>
    /// 与 <c>AppUsageTracker.Observe</c> **完全一致**的取整方式。
    /// 两处必须一致，否则“落库总量”与“内存榜单总量”会缓慢分叉 ——
    /// 自检里有专门的等式断言来守住这一点。
    /// </summary>
    private static long CreditMilliseconds(TimeSpan elapsed)
    {
        long ms = (long)Math.Round(elapsed.TotalMilliseconds, MidpointRounding.AwayFromZero);
        return ms < 0 ? 0 : ms;
    }

    // ================================================================ 后台线程（唯一的聚合者与写盘者）

    private void Pump()
    {
        BlockingCollection<UsageDelta> queue = _queue!;
        var sw = Stopwatch.StartNew();

        try
        {
            while (true)
            {
                TimeSpan remaining = _options.FlushInterval - sw.Elapsed;
                int waitMs = remaining <= TimeSpan.Zero
                    ? 0
                    : (int)Math.Min(remaining.TotalMilliseconds, int.MaxValue);

                bool got;
                UsageDelta delta;
                try
                {
                    got = queue.TryTake(out delta, waitMs);
                }
                catch (Exception ex)
                {
                    RecordError(ex);
                    break;
                }

                if (got)
                {
                    bool switched = Accumulate(delta);
                    if (switched)
                    {
                        // §5.6：切换软件时强制 flush。
                        Flush(forced: true);
                        sw.Restart();
                    }
                    continue;
                }

                // 等待超时（到点）→ 批量落库。
                Flush(forced: false);
                sw.Restart();

                if (queue.IsCompleted && queue.Count == 0) break;
            }
        }
        catch (Exception ex)
        {
            RecordError(ex);
        }
        finally
        {
            // 收尾：把剩余增量全部刷出去（退出/flush 中断都走这里），否则每次退出都丢尾段。
            Flush(forced: true);
        }
    }

    /// <summary>累加一条增量；返回是否检测到“切换软件”。</summary>
    private bool Accumulate(in UsageDelta delta)
    {
        lock (_gate)
        {
            // 非软件增量：不进 _pending（那本字典的键是软件合并键），单独按天累计。
            // 它不参与“切换软件”判定 —— 在桌面停留一秒不该触发强制 flush。
            if (delta.Kind != UsageDeltaKind.App)
            {
                if (!_pendingDay.TryGetValue(delta.Day, out PendingDay? day))
                {
                    day = new PendingDay();
                    _pendingDay[delta.Day] = day;
                }

                if (delta.Kind == UsageDeltaKind.Filtered) day.FilteredMilliseconds += delta.Milliseconds;
                else day.UnattributedMilliseconds += delta.Milliseconds;

                return false;
            }

            var key = (delta.Day, delta.MergeKey);

            if (!_pending.TryGetValue(key, out Pending? pending))
            {
                pending = new Pending();
                _pending[key] = pending;
            }

            pending.DisplayName = delta.DisplayName;
            pending.Milliseconds += delta.Milliseconds;
            pending.Received += delta.Milliseconds;

            bool switched = _lastKey is not null &&
                            !string.Equals(_lastKey, delta.MergeKey, StringComparison.Ordinal);
            _lastKey = delta.MergeKey;
            return switched;
        }
    }

    /// <summary>
    /// 把待写增量中**满 1 秒的部分**落库。
    ///
    /// 返回是否成功（无待写数据也算成功）。整个方法由 <c>_flushGate</c> 串行化，
    /// 因此不会出现“两个线程各自把同一份数据各写一遍”。
    /// </summary>
    public bool Flush(bool forced = true)
    {
        lock (_flushGate)
        {
            List<AppUsageRow> rows;
            List<DayActivityRow> dayRows;
            long totalSeconds = 0;

            lock (_gate)
            {
                rows = new List<AppUsageRow>(_pending.Count);
                foreach (KeyValuePair<(DateOnly Day, string Key), Pending> kv in _pending)
                {
                    long seconds = kv.Value.Milliseconds / 1000;
                    if (seconds <= 0) continue;      // 不足 1 秒：留在内存继续攒（余数结转）
                    rows.Add(new AppUsageRow(kv.Key.Day, kv.Key.Key, seconds));
                    totalSeconds += seconds;
                }

                dayRows = new List<DayActivityRow>(_pendingDay.Count);
                foreach (KeyValuePair<DateOnly, PendingDay> kv in _pendingDay)
                {
                    long filtered = kv.Value.FilteredMilliseconds / 1000;
                    long unattributed = kv.Value.UnattributedMilliseconds / 1000;
                    if (filtered <= 0 && unattributed <= 0) continue;   // 同样结转余数
                    dayRows.Add(new DayActivityRow(kv.Key, filtered, unattributed));
                    totalSeconds += filtered + unattributed;
                }
            }

            if (rows.Count == 0 && dayRows.Count == 0)
            {
                if (forced) Interlocked.Increment(ref _forcedFlushes);
                return true;
            }

            try
            {
                // 软件行与非软件行**同一事务**提交：不会出现“软件写了、非软件没写”的半截状态。
                _store.AddSeconds(rows, dayRows);
            }
            catch (Exception ex)
            {
                // 失败：**不扣减**待写数据 → 下一轮重试。数据不会丢。
                RecordError(ex);
                return false;
            }

            lock (_gate)
            {
                foreach (AppUsageRow row in rows)
                {
                    if (_pending.TryGetValue((row.Day, row.AppName), out Pending? pending))
                    {
                        pending.Milliseconds -= row.Seconds * 1000;
                        if (pending.Milliseconds < 0) pending.Milliseconds = 0;   // 防御
                    }
                }

                foreach (DayActivityRow row in dayRows)
                {
                    if (_pendingDay.TryGetValue(row.Day, out PendingDay? day))
                    {
                        day.FilteredMilliseconds -= row.FilteredSeconds * 1000;
                        day.UnattributedMilliseconds -= row.UnattributedSeconds * 1000;
                        if (day.FilteredMilliseconds < 0) day.FilteredMilliseconds = 0;         // 防御
                        if (day.UnattributedMilliseconds < 0) day.UnattributedMilliseconds = 0; // 防御
                    }
                }
            }

            Interlocked.Increment(ref _flushes);
            if (forced) Interlocked.Increment(ref _forcedFlushes);
            Interlocked.Add(ref _flushedSeconds, totalSeconds);
            return true;
        }
    }

    /// <summary>
    /// 合并软件时把**待写增量**重键（<paramref name="fromKey"/> 的各天 → <paramref name="toKey"/>）。
    ///
    /// 这是"合并"这个动作的第二个部分（第一部分是内存榜单 <c>AppUsageTracker.MergeKeys</c>，
    /// 第三部分是库内历史行 <c>SqliteStore.MergeAppKey</c>）。
    /// 三处必须一起做：少做这一处，那部分尚未落库的秒数会在下一轮 flush 时又写成**原始键**，
    /// 于是合并"看起来生效了"，但今天仍会多出一行 —— 不报错，只是数字对不上。
    /// </summary>
    public void MergePendingKeys(string fromKey, string toKey, string? displayName = null)
    {
        string from = fromKey ?? string.Empty;
        string to = toKey ?? string.Empty;
        if (from.Length == 0 || to.Length == 0 || string.Equals(from, to, StringComparison.Ordinal)) return;

        lock (_gate)
        {
            var moved = new List<(DateOnly Day, string Key)>();
            foreach (KeyValuePair<(DateOnly Day, string Key), Pending> kv in _pending)
            {
                if (string.Equals(kv.Key.Key, from, StringComparison.Ordinal)) moved.Add(kv.Key);
            }

            foreach ((DateOnly Day, string Key) key in moved)
            {
                Pending source = _pending[key];
                var targetKey = (key.Day, to);

                if (!_pending.TryGetValue(targetKey, out Pending? target))
                {
                    target = new Pending();
                    _pending[targetKey] = target;
                }

                target.Milliseconds += source.Milliseconds;
                target.Received += source.Received;

                if (!string.IsNullOrEmpty(displayName)) target.DisplayName = displayName!;
                else if (string.IsNullOrEmpty(target.DisplayName)) target.DisplayName = source.DisplayName;

                _pending.Remove(key);
            }

            // “上一次见到的键”也要改指：否则下一拍会被误判成“切换了软件”而触发一次多余的强制 flush。
            if (_lastKey is not null && string.Equals(_lastKey, from, StringComparison.Ordinal)) _lastKey = to;
        }
    }

    private void RecordError(Exception ex)
    {
        Interlocked.Increment(ref _failures);
        _lastError ??= ex.GetType().Name + ": " + ex.Message;
    }

    // ================================================================ 释放

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        if (_subscribed)
        {
            try { _bridge!.Observed -= OnObserved; } catch { /* 忽略 */ }
        }

        BlockingCollection<UsageDelta>? queue = _queue;
        if (queue is not null)
        {
            try { queue.CompleteAdding(); } catch { /* 忽略 */ }
            try { _thread!.Join(TimeSpan.FromSeconds(5)); } catch { /* 忽略 */ }
        }

        // 兜底：若后台线程未在超时内收尾，这里再刷一次（_flushGate 保证不会重复写）。
        Flush(forced: true);

        // 只有在后台线程确实已退出时才释放队列，避免正在使用的线程拿到 ObjectDisposedException。
        if (queue is not null && _thread is not null && !_thread.IsAlive)
        {
            try { queue.Dispose(); } catch { /* 忽略 */ }
        }

        _store.Dispose();
    }
}
