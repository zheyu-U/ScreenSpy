using System;
using System.Collections.Generic;
using ScreenSpy.Storage;

namespace ScreenSpy.Analytics;

/// <summary>读图表的结果：要么有模型，要么有一句**能摆到界面上**的原因。</summary>
internal readonly struct ChartResult
{
    private ChartResult(ChartModel? model, string? error)
    {
        Model = model;
        Error = error;
    }

    public ChartModel? Model { get; }

    /// <summary>失败原因（正常为 null）。界面直接显示它，而不是显示一张空图让人猜。</summary>
    public string? Error { get; }

    public bool Ok => Model is not null;

    public static ChartResult Success(ChartModel model) => new(model, null);

    public static ChartResult Failure(string error) => new(null, error ?? "未知错误");
}

/// <summary>
/// 图表的**数据入口**（M10）：从库读 7 天 → 交给 <see cref="ChartBuilder"/> → 给出 <see cref="ChartModel"/>。
///
/// ────────────────────────────────────────────────────────────────────────
/// 三个刻意的取舍
/// ────────────────────────────────────────────────────────────────────────
///  1. **只读库**（用户 2026-10-06 决策）：不掺内存里的实时口径，因此"今天"这根柱最多滞后约 15 秒
///     （落库间隔）。好处是 7 根柱完全同源；代价由界面**明说**（"数据来自已落库记录"），不藏着。
///  2. **存储不可用 → 明确报错，不返空图**：空图会让人以为"这几天真的没用电脑"。
///  3. **失败永不抛**：图表是展示层，读失败最多"这张图看不了"，不该带崩界面。
///
/// 身份层（显示名 / 分类）由构造时的委托**现取**，因为用户可能在运行期改名、改分类、合并软件 ——
/// 若在这里缓存一份，改名之后图表标题就会一直用旧名（不报错的错）。
/// </summary>
internal sealed class ChartQuery
{
    private readonly Func<SqliteStore?> _store;
    private readonly Func<string, string> _displayNameOf;
    private readonly Func<string, string> _categoryOf;

    public ChartQuery(
        Func<SqliteStore?> store,
        Func<string, string> displayNameOf,
        Func<string, string> categoryOf)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _displayNameOf = displayNameOf ?? throw new ArgumentNullException(nameof(displayNameOf));
        _categoryOf = categoryOf ?? throw new ArgumentNullException(nameof(categoryOf));
    }

    /// <summary>读一张图。窗口固定为"今天 + 前 days-1 天"。</summary>
    public ChartResult Read(ChartScope scope, DateOnly today, int days = ChartBuilder.DefaultDays)
    {
        if (days <= 0) days = ChartBuilder.DefaultDays;

        if (!scope.IsUsable)
            return ChartResult.Failure(
                "请先选一个具体的" + (scope.Dimension == ChartDimension.Category ? "分类" : "软件") + "。");

        SqliteStore? store = _store();
        if (store is null)
            return ChartResult.Failure("存储不可用，因此看不到历史数据（当前只有本次运行的统计）。");

        try
        {
            DateOnly from = today.AddDays(-(days - 1));
            IReadOnlyList<AppUsageRow> usage = store.ReadUsageRange(from, today);
            IReadOnlyList<DayActivityRow> activity = store.ReadActivityRange(from, today);

            return ChartResult.Success(
                ChartBuilder.Build(scope, today, days, usage, activity, _displayNameOf, _categoryOf));
        }
        catch (Exception ex)
        {
            return ChartResult.Failure("读取历史数据失败：" + ex.GetType().Name + "：" + ex.Message);
        }
    }

    /// <summary>
    /// 近 N 天在库里出现过的软件（**归一键**），按时长降序 —— 界面下拉框的候选。
    ///
    /// 候选来自**库**而不是内存榜单：既然图只读库，候选也该来自库，
    /// 否则会出现"下拉里能选一个软件，选了却是空图"。
    /// </summary>
    public (IReadOnlyList<string> Keys, string? Error) ReadAppTargets(DateOnly today, int days = ChartBuilder.DefaultDays)
    {
        if (days <= 0) days = ChartBuilder.DefaultDays;

        SqliteStore? store = _store();
        if (store is null)
            return (Array.Empty<string>(), "存储不可用，因此看不到历史数据。");

        try
        {
            DateOnly from = today.AddDays(-(days - 1));
            IReadOnlyList<AppUsageRow> rows = store.ReadUsageRange(from, today);

            var totals = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (AppUsageRow row in rows)
            {
                if (row.AppName.Length == 0) continue;
                totals[row.AppName] = totals.TryGetValue(row.AppName, out long cur) ? cur + row.Seconds : row.Seconds;
            }

            var list = new List<string>(totals.Keys);
            // 时长降序；相同则按键升序（稳定顺序，界面下拉不会每次刷新都换位置）。
            list.Sort((a, b) =>
            {
                int bySeconds = totals[b].CompareTo(totals[a]);
                return bySeconds != 0 ? bySeconds : string.CompareOrdinal(a, b);
            });

            return (list, null);
        }
        catch (Exception ex)
        {
            return (Array.Empty<string>(), "读取软件列表失败：" + ex.GetType().Name + "：" + ex.Message);
        }
    }
}
