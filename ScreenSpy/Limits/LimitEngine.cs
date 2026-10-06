using System;
using System.Collections.Generic;
using ScreenSpy.Collector;
using ScreenSpy.Storage;

namespace ScreenSpy.Limits;

/// <summary>一条限额规则（从 <c>limits</c> 表读来、供引擎使用）。</summary>
internal readonly struct LimitRule
{
    public LimitRule(LimitKind kind, string target, string displayName, long limitSeconds)
    {
        Kind = kind;
        Target = target ?? string.Empty;
        DisplayName = displayName ?? string.Empty;
        LimitSeconds = limitSeconds < 0 ? 0 : limitSeconds;
    }

    public LimitKind Kind { get; }

    /// <summary>范围键：总限额为空串；软件限额为归一键；分类限额为分类名。</summary>
    public string Target { get; }

    /// <summary>给人看的名字（软件限额=软件显示名；分类限额=分类名；总限额=“今日总限额”）。</summary>
    public string DisplayName { get; }

    public long LimitSeconds { get; }

    public TimeSpan Limit => TimeSpan.FromSeconds(LimitSeconds);

    public override string ToString() => $"{LimitRules.KindName(Kind)} target={Target} limit={LimitSeconds}s";
}

/// <summary>
/// 判定限额所需的**一份输入快照**（全部为“今天一整天”口径，单位：秒）。
///
/// 三处都用“整天”而不是“本次运行”：限额是“今天还能用多久”，重启一次就把它清零是荒谬的。
/// </summary>
internal sealed class LimitInput
{
    public LimitInput(long totalSeconds,
                      IReadOnlyDictionary<string, long>? appSeconds = null,
                      IReadOnlyDictionary<string, long>? categorySeconds = null)
    {
        TotalSeconds = totalSeconds < 0 ? 0 : totalSeconds;
        AppSeconds = appSeconds ?? new Dictionary<string, long>(StringComparer.Ordinal);
        CategorySeconds = categorySeconds ?? new Dictionary<string, long>(StringComparer.Ordinal);
    }

    public long TotalSeconds { get; }
    public IReadOnlyDictionary<string, long> AppSeconds { get; }
    public IReadOnlyDictionary<string, long> CategorySeconds { get; }

    public static readonly LimitInput Empty = new(0);
}

/// <summary>某条限额的**当前状态**（界面与卡片展示用；纯数据）。</summary>
internal readonly struct LimitStatus
{
    public LimitStatus(LimitKind kind, string target, string displayName, long usedSeconds, long limitSeconds)
    {
        Kind = kind;
        Target = target ?? string.Empty;
        DisplayName = displayName ?? string.Empty;
        UsedSeconds = usedSeconds < 0 ? 0 : usedSeconds;
        LimitSeconds = limitSeconds < 0 ? 0 : limitSeconds;
    }

    public LimitKind Kind { get; }
    public string Target { get; }
    public string DisplayName { get; }
    public long UsedSeconds { get; }
    public long LimitSeconds { get; }

    public TimeSpan Used => TimeSpan.FromSeconds(UsedSeconds);
    public TimeSpan Limit => TimeSpan.FromSeconds(LimitSeconds);
    public double Ratio => LimitRules.Ratio(Used, Limit);

    /// <summary>是否已经超（或恰好达到）限额。</summary>
    public bool Exceeded => LimitSeconds > 0 && UsedSeconds >= LimitSeconds;

    public override string ToString() => $"{DisplayName} {UsedSeconds}s / {LimitSeconds}s";
}

/// <summary>一条“该提醒了”的通知（纯数据；文本已在 <see cref="LimitRules"/> 里定稿）。</summary>
internal readonly struct LimitNotification
{
    public LimitNotification(LimitKind kind, string target, string displayName,
                             TimeSpan used, TimeSpan limit, double ratio,
                             double threshold, string dedupKey, string title, string message)
    {
        Kind = kind;
        Target = target ?? string.Empty;
        DisplayName = displayName ?? string.Empty;
        Used = used;
        Limit = limit;
        Ratio = ratio;
        Threshold = threshold;
        DedupKey = dedupKey ?? string.Empty;
        Title = title ?? string.Empty;
        Message = message ?? string.Empty;
    }

    public LimitKind Kind { get; }
    public string Target { get; }
    public string DisplayName { get; }
    public TimeSpan Used { get; }
    public TimeSpan Limit { get; }
    public double Ratio { get; }
    public double Threshold { get; }

    /// <summary>去重键（调用方负责落库；本结构自身不产生副作用）。</summary>
    public string DedupKey { get; }

    public string Title { get; }
    public string Message { get; }

    public override string ToString() => $"[{Threshold:P0}] {Message}";
}

/// <summary>
/// 限额引擎：**纯函数**地把「规则 + 当前用量 + 阈值 + 已提醒标记」变成「该发哪些提醒」。
///
/// 刻意不做任何 IO、不持有状态：
///  * 去重标记的读写（落库）由 <c>LimitGuard</c> 负责；
///  * 于是“跨过阈值只提醒一次”“一次跨两档只提醒最高那档”“改限额后重新提醒”
///    这些**最容易写错、又最难人工复现**的规则可以被确定性自检逐条推演。
/// </summary>
internal static class LimitEngine
{
    /// <summary>总限额在界面/卡片上的显示名（软件与分类用它们自己的名字）。</summary>
    public const string TotalDisplayName = "今日总限额";

    // ---------------------------------------------------------------- 规则构造

    /// <summary>
    /// 把 <c>limits</c> 表里的行翻译成规则。
    ///
    /// 三条**跳过**而不是猜的规则（跳过 = 该行不生效，且不会悄悄按错误的范围生效）：
    ///  * 未知 scope（旧版本可能写入过别的值）；
    ///  * 时长 ≤ 0（无意义的限额）；
    ///  * 软件 / 分类限额的空键（空串在库里是总限额的合法 target，但换个 scope 就没有意义）。
    /// </summary>
    public static IReadOnlyList<LimitRule> BuildRules(IReadOnlyList<LimitRow>? rows, Func<string, string>? appDisplayName)
    {
        var list = new List<LimitRule>();
        if (rows is null) return list;

        foreach (LimitRow row in rows)
        {
            LimitKind? kind = LimitRules.KindOf(row.Scope);
            if (kind is null) continue;
            if (row.Seconds <= 0) continue;

            string target = row.Target ?? string.Empty;
            string display;

            if (kind == LimitKind.Total)
            {
                display = TotalDisplayName;
            }
            else
            {
                if (target.Length == 0) continue;
                display = kind == LimitKind.App
                    ? (appDisplayName?.Invoke(target) ?? target)
                    : target;
                if (display.Length == 0) display = target;
            }

            list.Add(new LimitRule(kind.Value, target, display, row.Seconds));
        }

        return list;
    }

    // ---------------------------------------------------------------- 输入构造

    /// <summary>
    /// 由“今日全部软件（含基线）”构造引擎输入。
    ///
    /// 分类用量 = 该分类下**各软件用量之和**；因为分类口径是“一个软件只属于一个分类”（M9-1 决策），
    /// 这个求和不会重复计算，且 <c>各分类之和 ≤ 各软件之和</c> 恒成立（未分类的不计入任何分类）。
    /// </summary>
    public static LimitInput BuildInput(long totalSeconds,
                                        IReadOnlyList<AppUsageEntry>? apps,
                                        Func<string, string>? categoryOf)
    {
        var appSeconds = new Dictionary<string, long>(StringComparer.Ordinal);
        var categorySeconds = new Dictionary<string, long>(StringComparer.Ordinal);

        if (apps is not null)
        {
            foreach (AppUsageEntry entry in apps)
            {
                long seconds = (long)Math.Round(entry.Time.TotalSeconds, MidpointRounding.AwayFromZero);
                if (seconds <= 0) continue;

                appSeconds[entry.MergeKey] = seconds;

                string category = categoryOf?.Invoke(entry.MergeKey) ?? string.Empty;
                if (category.Length == 0) continue;

                categorySeconds.TryGetValue(category, out long sum);
                categorySeconds[category] = sum + seconds;
            }
        }

        return new LimitInput(totalSeconds, appSeconds, categorySeconds);
    }

    // ---------------------------------------------------------------- 判定

    /// <summary>某条规则当前的用量（秒）。</summary>
    public static long UsedOf(LimitRule rule, LimitInput input)
    {
        if (input is null) return 0;

        switch (rule.Kind)
        {
            case LimitKind.App:
                return input.AppSeconds.TryGetValue(rule.Target, out long app) ? app : 0;

            case LimitKind.Category:
                return input.CategorySeconds.TryGetValue(rule.Target, out long category) ? category : 0;

            default:
                return input.TotalSeconds;
        }
    }

    /// <summary>全部规则的当前状态（顺序与 <paramref name="rules"/> 一致，便于界面稳定显示）。</summary>
    public static IReadOnlyList<LimitStatus> Statuses(IReadOnlyList<LimitRule>? rules, LimitInput? input)
    {
        var list = new List<LimitStatus>();
        if (rules is null || input is null) return list;

        foreach (LimitRule rule in rules)
            list.Add(new LimitStatus(rule.Kind, rule.Target, rule.DisplayName, UsedOf(rule, input), rule.LimitSeconds));

        return list;
    }

    /// <summary>
    /// 决定这一拍该发哪些提醒。
    ///
    /// <paramref name="alreadyNotified"/> 由调用方按 <see cref="LimitNotification.DedupKey"/> 回答
    /// “今天这条是不是已经提醒过”。返回值里的每条通知都应被**落库去重**（调用方的责任）。
    /// </summary>
    public static IReadOnlyList<LimitNotification> Evaluate(
        IReadOnlyList<LimitRule>? rules,
        LimitInput? input,
        IReadOnlyList<double>? thresholds,
        DateOnly day,
        Func<string, bool>? alreadyNotified)
    {
        var list = new List<LimitNotification>();
        if (rules is null || input is null) return list;

        IReadOnlyList<double> effective = thresholds is { Count: > 0 } ? thresholds : LimitRules.DefaultThresholds;

        foreach (LimitRule rule in rules)
        {
            long usedSeconds = UsedOf(rule, input);
            TimeSpan used = TimeSpan.FromSeconds(usedSeconds);
            double ratio = LimitRules.Ratio(used, rule.Limit);

            double threshold = LimitRules.HighestCrossedThreshold(ratio, effective);
            if (threshold <= 0) continue;

            string key = LimitRules.DedupKey(day, rule.Kind, rule.Target, threshold);
            if (alreadyNotified is not null && alreadyNotified(key)) continue;

            list.Add(new LimitNotification(
                rule.Kind, rule.Target, rule.DisplayName,
                used, rule.Limit, ratio, threshold, key,
                LimitRules.ComposeTitle(threshold),
                LimitRules.ComposeMessage(rule.Kind, rule.DisplayName, used, rule.Limit, threshold)));
        }

        return list;
    }

    /// <summary>
    /// 最“紧急”的一条限额（比例最高者）—— 卡片只有一根进度条，放最接近上限的那条才有意义。
    /// 没有任何限额时返回 <c>null</c>（卡片据此刻意隐藏整块限额区域）。
    /// </summary>
    public static LimitStatus? MostCritical(IReadOnlyList<LimitStatus>? statuses)
    {
        if (statuses is null || statuses.Count == 0) return null;

        LimitStatus? best = null;
        double bestRatio = -1.0;

        foreach (LimitStatus status in statuses)
        {
            if (status.LimitSeconds <= 0) continue;

            double ratio = status.Ratio;
            if (best is null || ratio > bestRatio)
            {
                best = status;
                bestRatio = ratio;
            }
        }

        return best;
    }

    /// <summary>
    /// 卡片限额文案，如 <c>"3:42 / 5:00"</c>；软件 / 分类限额会带上名字
    /// （卡片上有多个限额时，用户必须看得出这根条说的是哪一个）。
    /// 渲染器会再补一个「限额 」前缀。
    /// </summary>
    public static string ComposeCardLimitText(LimitStatus status)
    {
        string pair = $"{LimitRules.Format(status.Used)} / {LimitRules.Format(status.Limit)}";
        return status.Kind == LimitKind.Total
            ? pair
            : $"{status.DisplayName} {pair}";
    }
}
