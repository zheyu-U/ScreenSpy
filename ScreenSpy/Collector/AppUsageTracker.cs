using System;
using System.Collections.Generic;

namespace ScreenSpy.Collector;

/// <summary>排行榜中的一行（按软件统计的输出单元）。</summary>
internal readonly struct AppUsageEntry
{
    public AppUsageEntry(string mergeKey, string displayName, long milliseconds, double share)
    {
        MergeKey = mergeKey;
        DisplayName = displayName;
        Milliseconds = milliseconds;
        Share = share;
    }

    /// <summary>合并键（进程名小写）。</summary>
    public string MergeKey { get; }

    /// <summary>规范化后的软件名。</summary>
    public string DisplayName { get; }

    /// <summary>累计时长（毫秒）。</summary>
    public long Milliseconds { get; }

    /// <summary>占**已归因**时长的比例（0.0 ~ 1.0），用于卡片条形宽度。</summary>
    public double Share { get; }

    public TimeSpan Time => TimeSpan.FromMilliseconds(Milliseconds);

    public override string ToString() => $"{DisplayName} {Time:hh\\:mm\\:ss} ({Share * 100:F1}%)";
}

/// <summary>
/// 按软件累计活跃时长（开发文档 §5.4 的“把这一秒归到它头上”）。
///
/// 这是**纯逻辑**（不碰 Win32、不依赖时钟）：所有累加都由外部把
/// “采样结果 + 本拍时长 + 本拍是否计入”喂进来，因此整条归属逻辑可确定性推演。
///
/// 时间守恒（本项目的重要不变量）：
/// <code>
/// 已归因(AttributedTotal) + 不计入使用时长(FilteredTotal) + 无前台/未知(UnattributedTotal)
///   + 锁屏剔除(LockScreenExcludedTotal) == 被计入的活跃时长（调度器 TodayActive）
/// </code>
/// 四者互斥且覆盖全部被计入的拍，所以这个等式必须**精确成立**（毫秒级）。
/// 自检把它当作核心断言之一 —— 它能一次性发现“漏记 / 重复记 / 单位错”三类问题。
///
/// 【口径（本次确认）】四个桶里只有前三者进入对外展示的“今日真实活跃”：
///  * <see cref="FilteredTotal"/>（桌面 / 外壳 / 自身）= **不计入使用时长**，但仍然是活跃时间；
///  * <see cref="UnattributedTotal"/>（无前台 / 未知）= 另一列（人在电脑前、但没落到任何软件）；
///  * <see cref="LockScreenExcludedTotal"/>（锁屏 / 登录界面）= **完全不计入**，单独统计只为守住守恒。
/// 之所以不把锁屏那几拍直接丢掉：丢掉会让守恒等式少一项、从而**无法用等式发现漏记**。
///
/// 线程模型：心跳在**线程池**上执行，所有读写加锁；无事件，故无死锁风险。
/// </summary>
internal sealed class AppUsageTracker
{
    private sealed class Bucket
    {
        public string DisplayName = string.Empty;

        /// <summary>**本次运行**观测到的毫秒数（参与时间守恒不变量）。</summary>
        public long Milliseconds;

        /// <summary>启动续算灌入的**历史基线**毫秒数（不参与时间守恒不变量）。</summary>
        public long SeedMilliseconds;

        public long TotalMilliseconds => Milliseconds + SeedMilliseconds;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    private DateOnly _day;

    private long _attributedMs;
    private long _filteredMs;
    private long _unattributedMs;
    private long _lockExcludedMs;
    private long _seededMs;
    private long _seededFilteredMs;
    private long _seededUnattributedMs;

    private long _observations;
    private long _countedTicks;
    private long _skippedTicks;

    private long _appTicks;
    private long _uwpTicks;
    private long _selfTicks;
    private long _desktopTicks;
    private long _shellTicks;
    private long _lockTicks;
    private long _unknownTicks;
    private long _noneTicks;

    private ForegroundAppSample _last;
    private bool _hasLast;
    private long _lastCreditedMs;

    // ------------------------------------------------------------ 查询

    /// <summary>当前统计日。</summary>
    public DateOnly Day { get { lock (_gate) return _day; } }

    /// <summary>收到的采样次数（被计入 + 未被计入）。</summary>
    public long Observations { get { lock (_gate) return _observations; } }

    /// <summary>被计入（活跃）的拍数。</summary>
    public long CountedTicks { get { lock (_gate) return _countedTicks; } }

    /// <summary>未被计入的拍数（挂机 / 暂停 / 锁屏 / 时间空洞）。</summary>
    public long SkippedTicks { get { lock (_gate) return _skippedTicks; } }

    /// <summary>已归因到具体软件的时长。</summary>
    public TimeSpan AttributedTotal { get { lock (_gate) return TimeSpan.FromMilliseconds(_attributedMs); } }

    /// <summary>
    /// **不计入使用时长**（桌面 / 外壳 / 自身）的时长 —— 本次运行。
    /// 注意：**不含锁屏**（锁屏是完全不计入，见 <see cref="LockScreenExcludedTotal"/>）。
    /// </summary>
    public TimeSpan FilteredTotal { get { lock (_gate) return TimeSpan.FromMilliseconds(_filteredMs); } }

    /// <summary>**无前台 / 未知**（没有前台窗口 / 取不到进程名）的时长 —— 本次运行。</summary>
    public TimeSpan UnattributedTotal { get { lock (_gate) return TimeSpan.FromMilliseconds(_unattributedMs); } }

    /// <summary>
    /// 锁屏 / 登录界面被**剔除**的时长 —— 按口径“完全不计入”，因此**不属于**对外展示的活跃，
    /// 单独统计只为让守恒等式仍然闭合：<c>Total + LockScreenExcludedTotal == 调度器今日活跃</c>。
    /// （它只在“锁屏已发生、M2 尚未把暂停置位”的检测延迟窗口里出现，通常只有几秒。）
    /// </summary>
    public TimeSpan LockScreenExcludedTotal { get { lock (_gate) return TimeSpan.FromMilliseconds(_lockExcludedMs); } }

    /// <summary>上述三桶之和（不含锁屏剔除）—— 加上锁屏剔除后应与调度器的“今日活跃”精确相等。</summary>
    public TimeSpan Total { get { lock (_gate) return TimeSpan.FromMilliseconds(_attributedMs + _filteredMs + _unattributedMs); } }

    /// <summary>
    /// 启动续算灌入的历史基线总量（M4）= 软件基线 + 不计入基线 + 无前台基线。
    /// **不属于**本次运行的时间守恒不变量：各 <c>*Total</c> 仍只统计本次运行，
    /// 因此自检的核心断言不受续算影响。
    /// </summary>
    public TimeSpan SeededTotal
    {
        get { lock (_gate) return TimeSpan.FromMilliseconds(_seededMs + _seededFilteredMs + _seededUnattributedMs); }
    }

    /// <summary>库中基线的“不计入使用时长”分量。</summary>
    public TimeSpan SeededFilteredTotal { get { lock (_gate) return TimeSpan.FromMilliseconds(_seededFilteredMs); } }

    /// <summary>库中基线的“无前台 / 未知”分量。</summary>
    public TimeSpan SeededUnattributedTotal { get { lock (_gate) return TimeSpan.FromMilliseconds(_seededUnattributedMs); } }

    /// <summary>
    /// 对外展示口径：**今天一整天**的“不计入使用时长” = 本次运行 + 库中基线。
    /// （主界面与卡片都用这个值，因此重启后不会掉。）
    /// </summary>
    public TimeSpan FilteredDayTotal { get { lock (_gate) return TimeSpan.FromMilliseconds(_filteredMs + _seededFilteredMs); } }

    /// <summary>对外展示口径：**今天一整天**的“无前台 / 未知” = 本次运行 + 库中基线。</summary>
    public TimeSpan UnattributedDayTotal { get { lock (_gate) return TimeSpan.FromMilliseconds(_unattributedMs + _seededUnattributedMs); } }

    public long AppTicks { get { lock (_gate) return _appTicks; } }
    public long UwpTicks { get { lock (_gate) return _uwpTicks; } }
    public long SelfTicks { get { lock (_gate) return _selfTicks; } }
    public long DesktopTicks { get { lock (_gate) return _desktopTicks; } }
    public long ShellTicks { get { lock (_gate) return _shellTicks; } }
    public long LockScreenTicks { get { lock (_gate) return _lockTicks; } }
    public long UnknownTicks { get { lock (_gate) return _unknownTicks; } }
    public long NoWindowTicks { get { lock (_gate) return _noneTicks; } }

    /// <summary>已出现的软件条目数（合并键数量）。</summary>
    public int AppCount { get { lock (_gate) return _buckets.Count; } }

    /// <summary>是否已经至少采样过一次。</summary>
    public bool HasLastSample { get { lock (_gate) return _hasLast; } }

    /// <summary>最近一次采样（供卡片显示“当前软件”）。</summary>
    public ForegroundAppSample LastSample { get { lock (_gate) return _last; } }

    /// <summary>最近一次**被计入**的拍时长（毫秒）；未计入时为 0。</summary>
    public long LastCreditedMs { get { lock (_gate) return _lastCreditedMs; } }

    /// <summary>当前软件的展示名（尚未采样时给出占位文案）。</summary>
    public string CurrentDisplayName
    {
        get
        {
            lock (_gate)
                return _hasLast ? _last.DisplayName : "(尚未采样)";
        }
    }

    // ------------------------------------------------------------ 写入

    /// <summary>
    /// 记录一次采样。
    /// </summary>
    /// <param name="sample">前台采样结果。</param>
    /// <param name="elapsed">本拍的墙钟间隔（由调度器给出）。</param>
    /// <param name="counted">本拍是否被计入活跃（挂机 / 暂停 / 时间空洞时为 false）。</param>
    /// <param name="localTime">本拍的本地时间（用于跨天重置与防御性自愈）。</param>
    public void Observe(ForegroundAppSample sample, TimeSpan elapsed, bool counted, DateTime localTime)
    {
        lock (_gate)
        {
            _observations++;

            // “当前软件”始终更新 —— 即便这一拍不计入，卡片也想知道此刻前台是什么。
            _last = sample;
            _hasLast = true;

            // 防御性跨天：上层的 DayRolled 是主路径，这里兜底（漏掉一次也不会把昨天的时长算进今天）。
            DateOnly today = DateOnly.FromDateTime(localTime);
            if (_day != today) ResetDayLocked(today);

            if (!counted)
            {
                _skippedTicks++;
                _lastCreditedMs = 0;
                return;
            }

            long ms = (long)Math.Round(elapsed.TotalMilliseconds, MidpointRounding.AwayFromZero);
            if (ms < 0) ms = 0;

            _countedTicks++;
            _lastCreditedMs = ms;

            switch (sample.Kind)
            {
                case ForegroundAppKind.App:
                case ForegroundAppKind.UwpHost:
                {
                    // 防御：即便拿到一个 `default` 样本（MergeKey/DisplayName 为 null），也只当“未知应用”处理，绝不抛异常。
                    string key = string.IsNullOrEmpty(sample.MergeKey) ? "#app" : sample.MergeKey;
                    string display = string.IsNullOrEmpty(sample.DisplayName)
                        ? ForegroundAppRules.DisplayUnknown
                        : sample.DisplayName;

                    if (!_buckets.TryGetValue(key, out Bucket? bucket))
                    {
                        bucket = new Bucket();
                        _buckets[key] = bucket;
                    }
                    bucket.DisplayName = display;   // 以最新一次为准（友好名可被修正）
                    bucket.Milliseconds += ms;
                    _attributedMs += ms;
                    if (sample.Kind == ForegroundAppKind.UwpHost) _uwpTicks++; else _appTicks++;
                    break;
                }

                case ForegroundAppKind.Self:
                    _selfTicks++;
                    _filteredMs += ms;
                    break;

                case ForegroundAppKind.Desktop:
                    _desktopTicks++;
                    _filteredMs += ms;
                    break;

                case ForegroundAppKind.Shell:
                    _shellTicks++;
                    _filteredMs += ms;
                    break;

                case ForegroundAppKind.LockScreen:
                    // 口径（本次确认）：锁屏 / 登录界面**完全不计入**，不进“今日真实活跃”。
                    // 单独累计只为让守恒等式闭合：Total + LockScreenExcluded == 调度器今日活跃。
                    // 正常情况下 M2 一判为暂停，本拍就不会被计入；能走到这里的只有
                    // “锁屏已发生、M2 尚未察觉”的检测延迟窗口（通常几秒）。
                    _lockTicks++;
                    _lockExcludedMs += ms;
                    break;

                case ForegroundAppKind.None:
                    _noneTicks++;
                    _unattributedMs += ms;
                    break;

                default:
                    _unknownTicks++;
                    _unattributedMs += ms;
                    break;
            }
        }
    }

    /// <summary>跨天：清空全部累计并切换统计日（由 <c>ActivityScheduler.DayRolled</c> 驱动）。</summary>
    public void RollDay(DateOnly day)
    {
        lock (_gate) ResetDayLocked(day);
    }

    /// <summary>
    /// 启动续算（M4）：把数据库里“今日已累计”的数值作为**基线**灌入，使重启后
    /// 卡片/榜单显示的是今天一整天，而不是从 0 重新开始。
    ///
    /// 语义边界（刻意如此）：
    ///  * 基线存放在独立字段（<c>SeedMilliseconds</c>），**不计入** <c>AttributedTotal</c>；
    ///    因此时间守恒不变量仍只描述“本次运行”，不会被历史数据破坏。
    ///  * 会**重置**当日状态（与跨天同路径），所以必须在调度器 <c>Start()</c> 之前调用。
    ///  * 跨天后基线自然被清空（今天的历史不应出现在明天）。
    ///  * <paramref name="filtered"/> / <paramref name="unattributed"/> 是库中今日的
    ///    “不计入使用时长 / 无前台·未知”基线。**它们必须一起灌入**，否则重启会让
    ///    “今日真实活跃”凭空变小（这正是本次修复的口径缺陷）。
    /// </summary>
    public void Seed(DateOnly day, IReadOnlyList<AppUsageSeed> seeds,
                     TimeSpan filtered = default, TimeSpan unattributed = default)
    {
        lock (_gate)
        {
            ResetDayLocked(day);

            _seededFilteredMs = ClampMs(filtered);
            _seededUnattributedMs = ClampMs(unattributed);

            if (seeds is null) return;

            foreach (AppUsageSeed seed in seeds)
            {
                if (seed.Milliseconds <= 0) continue;

                string key = string.IsNullOrEmpty(seed.MergeKey) ? "#app" : seed.MergeKey;

                if (!_buckets.TryGetValue(key, out Bucket? bucket))
                {
                    bucket = new Bucket();
                    _buckets[key] = bucket;
                }

                bucket.DisplayName = string.IsNullOrEmpty(seed.DisplayName) ? key : seed.DisplayName;
                bucket.SeedMilliseconds += seed.Milliseconds;
                _seededMs += seed.Milliseconds;
            }
        }
    }

    private void ResetDayLocked(DateOnly day)
    {
        _buckets.Clear();
        _attributedMs = 0;
        _filteredMs = 0;
        _unattributedMs = 0;
        _lockExcludedMs = 0;
        _seededMs = 0;
        _seededFilteredMs = 0;
        _seededUnattributedMs = 0;
        _countedTicks = 0;
        _skippedTicks = 0;
        _appTicks = _uwpTicks = _selfTicks = _desktopTicks = _shellTicks = _lockTicks = _unknownTicks = _noneTicks = 0;
        _lastCreditedMs = 0;
        _day = day;
    }

    /// <summary>把时长取整成毫秒并夹到非负（与 <see cref="Observe"/> 的取整方式一致）。</summary>
    private static long ClampMs(TimeSpan value)
    {
        long ms = (long)Math.Round(value.TotalMilliseconds, MidpointRounding.AwayFromZero);
        return ms < 0 ? 0 : ms;
    }

    /// <summary>
    /// 按累计时长取前 <paramref name="count"/> 名。排序：时长降序，时长相同按展示名（序号）升序 ——
    /// 保证结果**稳定**（否则同一份数据两次调用顺序可能不同，卡片会闪烁、自检也不可复现）。
    /// </summary>
    public IReadOnlyList<AppUsageEntry> Top(int count)
    {
        if (count <= 0) return Array.Empty<AppUsageEntry>();

        lock (_gate)
        {
            var list = new List<AppUsageEntry>(_buckets.Count);
            long total = _attributedMs + _seededMs;

            foreach (KeyValuePair<string, Bucket> kv in _buckets)
            {
                long ms = kv.Value.TotalMilliseconds;
                double share = total > 0 ? (double)ms / total : 0.0;
                list.Add(new AppUsageEntry(kv.Key, kv.Value.DisplayName, ms, share));
            }

            list.Sort(static (a, b) =>
            {
                int byMs = b.Milliseconds.CompareTo(a.Milliseconds);
                return byMs != 0 ? byMs : string.CompareOrdinal(a.DisplayName, b.DisplayName);
            });

            if (list.Count > count) list.RemoveRange(count, list.Count - count);
            return list;
        }
    }

    /// <summary>取某个合并键的累计时长（毫秒）；不存在返回 0。自检用。</summary>
    public long MillisecondsOf(string mergeKey)
    {
        lock (_gate)
            return _buckets.TryGetValue(mergeKey, out Bucket? bucket) ? bucket.Milliseconds : 0;
    }

    /// <summary>
    /// 取某个合并键的**展示总量**（本次运行观测 + 启动续算基线）毫秒数；不存在返回 0。
    /// 卡片榜单用的是这个口径；<see cref="MillisecondsOf"/> 是“仅本次运行”口径（自检对齐用）。
    /// </summary>
    public long TotalMillisecondsOf(string mergeKey)
    {
        lock (_gate)
            return _buckets.TryGetValue(mergeKey, out Bucket? bucket) ? bucket.TotalMilliseconds : 0;
    }
}
