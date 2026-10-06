using System;
using System.Collections.Generic;
using ScreenSpy.Storage;

namespace ScreenSpy.Limits;

/// <summary>
/// 限额守门（M9-2）：把**纯逻辑**的 <see cref="LimitEngine"/> 与**存储**（<c>limits</c> 表、
/// 去重标记）接起来，并在每次判定时把「该发的提醒」通过 <see cref="Notified"/> 抛给上层。
///
/// ────────────────────────────────────────────────────────────────────────
/// 三条刻意的设计
/// ────────────────────────────────────────────────────────────────────────
///  1. **去重标记落库**（设置键 <c>notified|…</c>）：重启后不会因为进程重启而重复弹同一条；
///     键里带日期，跨天自动重置，不需要任何“清标记”的定时任务。
///  2. **无存储时退回内存去重**：即使 SQLite 降级关闭，也不会变成“每秒弹一条”。
///     降级时宁可“本次运行内只提醒一次”，也绝不用刷屏去惩罚用户。
///  3. **改限额 / 改阈值时清掉今天的去重标记**：否则“把限额调低”之后永远不再提醒，
///     用户会以为功能坏了 —— 而它其实只是被一条过期的标记挡住了。
///
/// 线程：判定在心跳线程上被调用，编辑在 UI 线程上被调用，故全部读写加锁。
/// </summary>
internal sealed class LimitGuard
{
    private readonly Func<SqliteStore?> _store;
    private readonly Func<string, string> _appDisplayName;
    private readonly Func<DateOnly> _today;
    private readonly object _gate = new();

    /// <summary>无存储时的内存去重（跨天清空，避免无限增长）。</summary>
    private readonly HashSet<string> _notifiedMemory = new(StringComparer.Ordinal);
    private DateOnly _memoryDay;

    private IReadOnlyList<LimitRule> _rules = Array.Empty<LimitRule>();
    private IReadOnlyList<double> _thresholds = LimitRules.DefaultThresholds;
    private string _thresholdsText = LimitRules.DefaultThresholdsText;
    private string? _lastError;

    public LimitGuard(Func<SqliteStore?> store,
                      Func<string, string> appDisplayName,
                      Func<DateOnly>? today = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _appDisplayName = appDisplayName ?? throw new ArgumentNullException(nameof(appDisplayName));
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
        _memoryDay = _today();
    }

    /// <summary>当前规则（快照）。</summary>
    public IReadOnlyList<LimitRule> Rules { get { lock (_gate) return _rules; } }

    /// <summary>当前阈值（升序的 0.0~1.0 比例）。</summary>
    public IReadOnlyList<double> Thresholds { get { lock (_gate) return _thresholds; } }

    /// <summary>阈值的设置文本（界面输入框用），如 <c>"80,100"</c>。</summary>
    public string ThresholdsText { get { lock (_gate) return _thresholdsText; } }

    /// <summary>最近一次内部错误（正常为 null）。上层据此显示可见警告，而不是静默失效。</summary>
    public string? LastError { get { lock (_gate) return _lastError; } }

    /// <summary>该发提醒了。回调在**判定线程**上，上层必须自行 marshal 到 UI 线程。</summary>
    public event Action<LimitNotification>? Notified;

    // ---------------------------------------------------------------- 加载

    /// <summary>
    /// 从存储重新加载规则与阈值。读失败时**退回默认**（无规则 / 默认阈值）并记下错误 ——
    /// 绝不“用一半的规则”运行（那会让一部分限额生效、一部分静默失效）。
    /// </summary>
    public void Reload()
    {
        try
        {
            SqliteStore? store = _store();

            IReadOnlyList<LimitRow> rows = store?.ReadLimits() ?? Array.Empty<LimitRow>();
            IReadOnlyList<LimitRule> rules = LimitEngine.BuildRules(rows, _appDisplayName);

            IReadOnlyList<double> thresholds = LimitRules.DefaultThresholds;
            string text = LimitRules.DefaultThresholdsText;

            string? stored = store?.GetSetting(LimitRules.ThresholdsKey);
            if (LimitRules.TryParseThresholds(stored, out double[] parsed, out _))
            {
                thresholds = parsed;
                text = LimitRules.FormatThresholds(parsed);
            }

            lock (_gate)
            {
                _rules = rules;
                _thresholds = thresholds;
                _thresholdsText = text;
                _lastError = null;
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _rules = Array.Empty<LimitRule>();
                _thresholds = LimitRules.DefaultThresholds;
                _thresholdsText = LimitRules.DefaultThresholdsText;
                _lastError = "限额设置读取失败（" + ex.GetType().Name + ": " + ex.Message + "）：本次不启用任何限额。";
            }
        }
    }

    // ---------------------------------------------------------------- 判定

    /// <summary>
    /// 判定一次并发出该发的提醒。返回发出的条数（自检用）。
    /// **永不抛异常**：这是每秒都会走的路径，一次异常不该让心跳停摆。
    /// </summary>
    public int Evaluate(LimitInput input, DateOnly day)
    {
        try
        {
            RollMemoryDay(day);

            IReadOnlyList<LimitRule> rules;
            IReadOnlyList<double> thresholds;
            lock (_gate)
            {
                rules = _rules;
                thresholds = _thresholds;
            }

            if (rules.Count == 0) return 0;

            IReadOnlyList<LimitNotification> notes =
                LimitEngine.Evaluate(rules, input, thresholds, day, AlreadyNotified);

            int raised = 0;
            foreach (LimitNotification note in notes)
            {
                MarkNotified(note.DedupKey);
                raised++;

                try { Notified?.Invoke(note); }
                catch { /* 上层（气泡）失败不该影响统计 */ }
            }

            return raised;
        }
        catch (Exception ex)
        {
            lock (_gate) _lastError = "限额判定异常（" + ex.GetType().Name + ": " + ex.Message + "）。";
            return 0;
        }
    }

    private bool AlreadyNotified(string key)
    {
        SqliteStore? store = _store();
        if (store is not null)
        {
            try
            {
                if (store.GetSetting(key) is not null) return true;
            }
            catch
            {
                // 读失败 → 退回内存判断：宁可本次运行少提醒，也绝不刷屏。
            }
        }

        lock (_gate) return _notifiedMemory.Contains(key);
    }

    private void MarkNotified(string key)
    {
        lock (_gate) _notifiedMemory.Add(key);

        SqliteStore? store = _store();
        if (store is null) return;

        try
        {
            store.SetSetting(key, "1");
        }
        catch (Exception ex)
        {
            lock (_gate) _lastError = "限额去重标记写入失败（" + ex.GetType().Name + ": " + ex.Message +
                                      "）：本次运行内仍只提醒一次，但重启后可能重复提醒。";
        }
    }

    private void RollMemoryDay(DateOnly day)
    {
        lock (_gate)
        {
            if (_memoryDay == day) return;
            _memoryDay = day;
            _notifiedMemory.Clear();
        }
    }

    // ---------------------------------------------------------------- 编辑

    /// <summary>设置 / 更新一条限额。存储不可用或时长非法 → 返回 false（不改内存、不假装成功）。</summary>
    public bool SetLimit(LimitKind kind, string target, TimeSpan limit)
    {
        long seconds = (long)Math.Round(limit.TotalSeconds, MidpointRounding.AwayFromZero);
        if (seconds <= 0) return false;

        string scope = LimitRules.ScopeOf(kind);
        string key = target ?? string.Empty;
        if (kind != LimitKind.Total && key.Length == 0) return false;

        SqliteStore? store = _store();
        if (store is null) return false;

        try
        {
            store.SetLimit(scope, key, seconds);
            ClearDedup(kind, key, store);
            Reload();
            return true;
        }
        catch (Exception ex)
        {
            lock (_gate) _lastError = "保存限额失败（" + ex.GetType().Name + ": " + ex.Message + "）：该改动未生效。";
            return false;
        }
    }

    /// <summary>删除一条限额。返回是否确实删掉了一行。</summary>
    public bool RemoveLimit(LimitKind kind, string target, out bool removed)
    {
        removed = false;
        SqliteStore? store = _store();
        if (store is null) return false;

        try
        {
            removed = store.DeleteLimit(LimitRules.ScopeOf(kind), target ?? string.Empty);
            ClearDedup(kind, target ?? string.Empty, store);
            Reload();
            return true;
        }
        catch (Exception ex)
        {
            lock (_gate) _lastError = "删除限额失败（" + ex.GetType().Name + ": " + ex.Message + "）：该改动未生效。";
            return false;
        }
    }

    /// <summary>保存阈值文本（如 <c>"80,100"</c>）。解析失败时给出**可读的原因**，且一个字都不写。</summary>
    public bool SetThresholds(string? text, out string? error)
    {
        if (!LimitRules.TryParseThresholds(text, out double[] thresholds, out error)) return false;

        SqliteStore? store = _store();
        if (store is null)
        {
            error = "存储不可用（见上方警告），阈值未保存。";
            return false;
        }

        try
        {
            store.SetSetting(LimitRules.ThresholdsKey, LimitRules.FormatThresholds(thresholds));

            // 阈值本身就是去重键的一部分，但仍把今天的标记清一遍：
            // 用户“删掉 100 再加回来”时，旧标记不该挡住新的一轮提醒。
            ClearDedupForAll(store);

            Reload();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = "保存阈值失败（" + ex.GetType().Name + ": " + ex.Message + "）。";
            lock (_gate) _lastError = error;
            return false;
        }
    }

    /// <summary>清掉某条限额**今天**的去重标记（所有阈值档位）。</summary>
    private void ClearDedup(LimitKind kind, string target, SqliteStore store)
    {
        DateOnly day = _today();
        IReadOnlyList<double> thresholds;
        lock (_gate) thresholds = _thresholds;

        foreach (double threshold in thresholds)
        {
            string key = LimitRules.DedupKey(day, kind, target, threshold);
            try { store.DeleteSetting(key); } catch { /* 清标记失败不是致命问题 */ }
            lock (_gate) _notifiedMemory.Remove(key);
        }
    }

    /// <summary>清掉**全部**限额今天的去重标记（改阈值时用）。</summary>
    private void ClearDedupForAll(SqliteStore store)
    {
        DateOnly day = _today();
        IReadOnlyList<LimitRule> rules;
        IReadOnlyList<double> thresholds;
        lock (_gate)
        {
            rules = _rules;
            thresholds = _thresholds;
        }

        foreach (LimitRule rule in rules)
            foreach (double threshold in thresholds)
            {
                string key = LimitRules.DedupKey(day, rule.Kind, rule.Target, threshold);
                try { store.DeleteSetting(key); } catch { /* 同上 */ }
                lock (_gate) _notifiedMemory.Remove(key);
            }
    }
}
