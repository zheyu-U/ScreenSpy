using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ScreenSpy.Limits;

/// <summary>限额的作用范围（M9-2）。存储里用字符串表示，见 <see cref="LimitRules.ScopeOf"/>。</summary>
internal enum LimitKind
{
    /// <summary>今日总限额（对“今日真实活跃”）。</summary>
    Total,

    /// <summary>单软件限额（对某个归一键，见 M9-1 身份层）。</summary>
    App,

    /// <summary>分类限额（对某个分类；分类口径为“一个软件只属于一个分类”）。</summary>
    Category,
}

/// <summary>
/// 限额的**纯逻辑**（无 IO、无状态、无平台依赖）。
///
/// 单独成类是为了让“阈值边界”“去重键格式”“提醒文案”这类**人工难以复现**的判断
/// 有唯一的、可推演的落点（由 <c>--m9-selfcheck</c> 的 F 组确定性覆盖）。
///
/// ────────────────────────────────────────────────────────────────────────
/// 三条刻意的口径选择
/// ────────────────────────────────────────────────────────────────────────
///  1. **总限额比的是“今日真实活跃”**（本次运行 − 锁屏剔除 + 库中基线），
///     与主界面大字号、托盘 tooltip、卡片**同一个数** —— 否则会出现
///     “限额说超了、主界面看着没超”这种自相矛盾的界面。
///  2. **去重键带日期**：跨天自动重置，不需要任何“清标记”的定时任务。
///  3. **阈值按百分比存**（设置键 <c>limit_thresholds</c> = <c>"80,100"</c>）：
///     人可读、可手改；内部一律换成 0.0 ~ 1.0 的比例参与比较。
/// </summary>
internal static class LimitRules
{
    // ---------------------------------------------------------------- 口径常量

    public const string ScopeTotal = "total";
    public const string ScopeApp = "app";
    public const string ScopeCategory = "category";

    /// <summary>阈值设置键。值是逗号分隔的**百分比**，如 <c>"80,100"</c>。</summary>
    public const string ThresholdsKey = "limit_thresholds";

    /// <summary>默认阈值文本（即将达到 80% / 已达到 100%）。</summary>
    public const string DefaultThresholdsText = "80,100";

    /// <summary>去重键前缀。键形如 <c>notified|2026-10-06|total||800</c>（阈值用千分比整数，避免小数格式漂移）。</summary>
    public const string DedupPrefix = "notified";

    /// <summary>默认阈值（0.8 = 即将达到，1.0 = 已达到）。</summary>
    public static IReadOnlyList<double> DefaultThresholds { get; } = new[] { 0.8, 1.0 };

    public static string ScopeOf(LimitKind kind) => kind switch
    {
        LimitKind.App => ScopeApp,
        LimitKind.Category => ScopeCategory,
        _ => ScopeTotal,
    };

    /// <summary>把存储里的 scope 字符串解析回枚举；未知 scope 返回 <c>null</c>（调用方应跳过而不是猜）。</summary>
    public static LimitKind? KindOf(string scope)
    {
        if (string.Equals(scope, ScopeTotal, StringComparison.Ordinal)) return LimitKind.Total;
        if (string.Equals(scope, ScopeApp, StringComparison.Ordinal)) return LimitKind.App;
        if (string.Equals(scope, ScopeCategory, StringComparison.Ordinal)) return LimitKind.Category;
        return null;
    }

    /// <summary>限额种类的界面用名。</summary>
    public static string KindName(LimitKind kind) => kind switch
    {
        LimitKind.App => "软件限额",
        LimitKind.Category => "分类限额",
        _ => "总限额",
    };

    // ---------------------------------------------------------------- 阈值

    /// <summary>
    /// 解析阈值文本（百分比，逗号分隔；中文逗号也接受）。
    /// 规则：至少一个；每个 &gt; 0 且 ≤ 100；自动去重并升序 ——
    /// 升序是**契约**（“跨过 80 与 100 时只弹最高那一条”依赖它）。
    /// </summary>
    public static bool TryParseThresholds(string? text, out double[] thresholds, out string? error)
    {
        thresholds = Array.Empty<double>();
        error = null;

        string raw = (text ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            error = "阈值不能为空（例如：80,100）。";
            return false;
        }

        string[] parts = raw.Split(new[] { ',', '，', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var values = new List<double>(parts.Length);
        var seen = new HashSet<int>();

        foreach (string part in parts)
        {
            string token = part.Trim().TrimEnd('%');
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
            {
                error = $"「{part}」不是数字。请填百分比（例如 80,100）。";
                return false;
            }

            if (percent <= 0 || percent > 100)
            {
                error = $"「{part}」超出范围：阈值必须在 0（不含）到 100（含）之间。";
                return false;
            }

            int permille = (int)Math.Round(percent * 10, MidpointRounding.AwayFromZero);
            if (seen.Add(permille)) values.Add(permille / 1000.0);
        }

        if (values.Count == 0)
        {
            error = "至少需要一个阈值（例如：80,100）。";
            return false;
        }

        values.Sort();
        thresholds = values.ToArray();
        return true;
    }

    /// <summary>把阈值列表格式化回设置文本（百分比，升序）。</summary>
    public static string FormatThresholds(IReadOnlyList<double> thresholds)
    {
        if (thresholds is null || thresholds.Count == 0) return DefaultThresholdsText;

        var builder = new StringBuilder();
        for (int i = 0; i < thresholds.Count; i++)
        {
            if (i > 0) builder.Append(',');
            double percent = Math.Round(thresholds[i] * 100, 1, MidpointRounding.AwayFromZero);
            builder.Append(percent.ToString("0.#", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    /// <summary>
    /// 取**已经被跨过的最高阈值**；一个都没跨过时返回 0。
    ///
    /// “最高”而不是“第一个”：一次从 70% 涨到 110% 时应当只提醒“已达到”，
    /// 而不是连弹两条（那正是用户最讨厌的刷屏）。
    /// </summary>
    public static double HighestCrossedThreshold(double ratio, IReadOnlyList<double> thresholds)
    {
        if (thresholds is null) return 0.0;

        double best = 0.0;
        for (int i = 0; i < thresholds.Count; i++)
        {
            double threshold = thresholds[i];
            if (ratio >= threshold && threshold > best) best = threshold;
        }
        return best;
    }

    // ---------------------------------------------------------------- 去重键

    /// <summary>
    /// 去重键：<c>notified|{yyyy-MM-dd}|{scope}|{target}|{阈值千分比}</c>。
    ///
    /// 带日期 → 跨天自然重置；带阈值 → 同一限额的 80% 与 100% 各自只提醒一次。
    /// 阈值用**千分比整数**（800 / 1000）而不是小数字符串：小数格式随区域性/版本漂移，
    /// 一旦漂移，去重就会静默失效（每秒弹一条）。
    /// </summary>
    public static string DedupKey(DateOnly day, LimitKind kind, string target, double threshold)
    {
        int permille = (int)Math.Round(threshold * 1000, MidpointRounding.AwayFromZero);
        return string.Format(CultureInfo.InvariantCulture,
            "{0}|{1:yyyy-MM-dd}|{2}|{3}|{4}",
            DedupPrefix, day, ScopeOf(kind), target ?? string.Empty, permille);
    }

    // ---------------------------------------------------------------- 文案

    /// <summary>提醒标题：按“已超 / 将达”分开，因为两者的紧急程度不同。</summary>
    public static string ComposeTitle(double threshold)
        => threshold >= 1.0 ? "ScreenSpy · 已达到限额" : "ScreenSpy · 即将达到限额";

    /// <summary>
    /// 提醒正文。四种限额外加“未超/已超”的措辞差异，都由这一个函数产出 ——
    /// 文案只在**一处**定义，界面/自检不会各写一份。
    /// </summary>
    public static string ComposeMessage(LimitKind kind, string displayName, TimeSpan used, TimeSpan limit, double threshold)
    {
        string verb = threshold >= 1.0 ? "已达到" : "即将达到";
        int percent = (int)Math.Round(Ratio(used, limit) * 100, MidpointRounding.AwayFromZero);

        return kind switch
        {
            LimitKind.App =>
                $"「{displayName}」今日 {Format(used)} {verb}设定限额 {Format(limit)}（{percent}%）。",
            LimitKind.Category =>
                $"分类「{displayName}」今日 {Format(used)} {verb}设定限额 {Format(limit)}（{percent}%）。",
            _ =>
                $"今日真实活跃 {Format(used)} {verb}设定总限额 {Format(limit)}（{percent}%）。",
        };
    }

    // ---------------------------------------------------------------- 数值

    /// <summary>已用 / 限额（限额非法时返回 0，绝不除零）。</summary>
    public static double Ratio(TimeSpan used, TimeSpan limit)
        => limit > TimeSpan.Zero ? used.TotalSeconds / limit.TotalSeconds : 0.0;

    /// <summary>时长文案（h:mm；满 24 小时也照常显示小时数，便于“2 小时限额”这类读数）。</summary>
    public static string Format(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return $"{(int)value.TotalHours}:{value.Minutes:00}";
    }

    /// <summary>
    /// 从“小时 / 分钟”两个输入框解析时长（对话框用）。
    /// 判定规则放在这里（而不是对话框里），保证“什么算合法限额”全项目只有一处定义。
    /// </summary>
    public static bool TryParseDuration(string? hoursText, string? minutesText, out TimeSpan value, out string? error)
    {
        value = TimeSpan.Zero;
        error = null;

        if (!TryParsePart(hoursText, 0, 240, out int hours, out error)) return false;
        if (!TryParsePart(minutesText, 0, 59, out int minutes, out error)) return false;

        value = TimeSpan.FromMinutes(hours * 60 + minutes);
        if (value <= TimeSpan.Zero)
        {
            error = "限额必须大于 0（例如 0 小时 30 分）。";
            return false;
        }
        return true;
    }

    private static bool TryParsePart(string? text, int min, int max, out int value, out string? error)
    {
        value = 0;
        error = null;

        string raw = (text ?? string.Empty).Trim();
        if (raw.Length == 0) return true;   // 空 = 0

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            error = $"「{raw}」不是整数。";
            return false;
        }

        if (parsed < min || parsed > max)
        {
            error = $"取值必须在 {min} ~ {max} 之间（当前 {parsed}）。";
            return false;
        }

        value = parsed;
        return true;
    }
}
