using System;
using System.Collections.Generic;

namespace ScreenSpy.Collector;

/// <summary>
/// 软件**身份层**（M9-1）：把「原始进程名」解析成「软件（归一键）」，并给出用户设置的显示名与分类。
///
/// ────────────────────────────────────────────────────────────────────────
/// 为什么需要它（不是"锦上添花"，而是为了不产生错数据）
/// ────────────────────────────────────────────────────────────────────────
/// 同一个软件常常有多个进程名（<c>Code</c> / <c>Code - Insiders</c>、<c>chrome</c> / <c>msedgewebview2</c>）。
/// 若把统计与限额直接挂在"原始进程名"上，用户会看到"我给 VS Code 分了类，另一半时长却跑在别处"，
/// 而且单软件限额会**静默地只统计到其中一部分**（不报错，只是数字偏小 —— 本项目最忌的那类缺陷）。
///
/// 因此在**归属那一刻**只做一次解析（<see cref="ForegroundAppRules.Create"/>），
/// 下游（榜单 / 卡片 / 存储 / 将来的限额引擎）全部只见归一键。
/// 这就是"一个解析点"：多一处解析，就多一处可能与别处分叉。
///
/// ────────────────────────────────────────────────────────────────────────
/// 语义与红线
/// ────────────────────────────────────────────────────────────────────────
///  * **解析是单跳的**：<c>raw → canonical</c>。合并时会把指向"被合并方"的别名一并改指到目标，
///    因此不会出现 <c>a→b→c</c> 这样的链（链会让"到底算谁"变得无法解释）。
///  * **不可变**：运行期修改用 <c>With*</c> 返回**新实例**，旧实例仍可被其他线程安全读取
///    （采样在心跳线程、界面在主线程，不能共享可变字典）。
///  * 这是**纯逻辑**：不碰 IO、不碰 Win32，因此解析规则可确定性推演（由 <c>--m9-selfcheck</c> 覆盖）。
/// </summary>
internal sealed class AppIdentity
{
    /// <summary>空身份：任何原始键解析为自身，无显示名覆盖、无分类、无分类表。</summary>
    public static readonly AppIdentity Empty = new(
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal),
        Array.Empty<string>());

    private readonly Dictionary<string, string> _aliases;       // rawKey      → canonicalKey
    private readonly Dictionary<string, string> _displayNames;  // canonicalKey → 用户显示名（非空者生效）
    private readonly Dictionary<string, string> _categories;    // canonicalKey → 分类名
    private readonly List<string> _categoryNames;

    private AppIdentity(Dictionary<string, string> aliases,
                        Dictionary<string, string> displayNames,
                        Dictionary<string, string> categories,
                        IReadOnlyList<string> categoryNames)
    {
        _aliases = aliases;
        _displayNames = displayNames;
        _categories = categories;
        _categoryNames = new List<string>(categoryNames);
    }

    /// <summary>全部已定义的分类名（按写入顺序；界面下拉框用）。</summary>
    public IReadOnlyList<string> Categories => _categoryNames;

    /// <summary>全部已定义的别名（原始键 → 归一键）；界面列出"现存合并"用。</summary>
    public IReadOnlyDictionary<string, string> Aliases => _aliases;

    /// <summary>
    /// 全部**用户设置过的显示名**（归一键 → 名字）。
    /// M9-1b 的「禁止重名」要遍历它：即便某个软件今天没出现，它占着的名字也必须算数
    /// （否则用户可以把另一个软件改成同一个名字，界面就会出现两行分不清的相同文字）。
    /// </summary>
    public IReadOnlyDictionary<string, string> DisplayNameOverrides => _displayNames;

    /// <summary>
    /// 解析归一键：命中别名则返回目标键，否则返回**原始键自身**（这就是"默认不动"）。
    /// 传入空串时返回空串 —— 调用方（<see cref="ForegroundAppRules"/>）只对 <c>App</c>/<c>UwpHost</c> 调用它，
    /// 但这里仍然按纯函数处理，不做假设。
    /// </summary>
    public string Resolve(string? rawKey)
    {
        string raw = rawKey ?? string.Empty;
        if (raw.Length == 0) return raw;
        return _aliases.TryGetValue(raw, out string? canonical) ? canonical : raw;
    }

    /// <summary>用户为某软件设置的显示名覆盖；未设置返回 null。</summary>
    public string? DisplayNameOverride(string? canonicalKey)
    {
        string key = canonicalKey ?? string.Empty;
        if (key.Length == 0) return null;
        return _displayNames.TryGetValue(key, out string? name) && name.Length > 0 ? name : null;
    }

    /// <summary>某软件的归属分类；未分类返回空串（**"未分类"不是一种分类**，它只是"没设"）。</summary>
    public string CategoryOf(string? canonicalKey)
    {
        string key = canonicalKey ?? string.Empty;
        if (key.Length == 0) return string.Empty;
        return _categories.TryGetValue(key, out string? category) ? category : string.Empty;
    }

    /// <summary>
    /// 从三张表的行**构造**一份身份（启动加载与自检用）。
    ///
    /// 参数刻意用基元类型集合而不是存储层的行结构体：<c>Collector</c> 是被 <c>Storage</c> 依赖的一方，
    /// 反向依赖会让"纯逻辑层"被 IO 层绑住（本项目的分层红线之一）。
    /// </summary>
    public static AppIdentity Build(
        IEnumerable<KeyValuePair<string, string>>? aliases,
        IEnumerable<KeyValuePair<string, (string DisplayName, string Category)>>? metas,
        IEnumerable<string>? categories)
    {
        var aliasMap = new Dictionary<string, string>(StringComparer.Ordinal);
        if (aliases is not null)
        {
            foreach (KeyValuePair<string, string> kv in aliases)
            {
                if (kv.Key.Length == 0 || kv.Value.Length == 0) continue;
                if (string.Equals(kv.Key, kv.Value, StringComparison.Ordinal)) continue;   // 自指别名没有意义
                aliasMap[kv.Key] = kv.Value;
            }
        }

        var displayNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var categoryMap = new Dictionary<string, string>(StringComparer.Ordinal);
        if (metas is not null)
        {
            foreach (KeyValuePair<string, (string DisplayName, string Category)> kv in metas)
            {
                if (kv.Key.Length == 0) continue;
                if (!string.IsNullOrEmpty(kv.Value.DisplayName)) displayNames[kv.Key] = kv.Value.DisplayName;
                if (!string.IsNullOrEmpty(kv.Value.Category)) categoryMap[kv.Key] = kv.Value.Category;
            }
        }

        var categoryNames = new List<string>();
        if (categories is not null)
        {
            foreach (string name in categories)
            {
                if (string.IsNullOrEmpty(name) || categoryNames.Contains(name)) continue;
                categoryNames.Add(name);
            }
        }

        return new AppIdentity(aliasMap, displayNames, categoryMap, categoryNames);
    }

    // ---------------------------------------------------------------- 运行期修改（返回新实例）

    public AppIdentity WithAlias(string rawKey, string canonicalKey)
    {
        var aliases = new Dictionary<string, string>(_aliases, StringComparer.Ordinal)
        {
            [rawKey ?? string.Empty] = canonicalKey ?? string.Empty,
        };
        return new AppIdentity(aliases, _displayNames, _categories, _categoryNames);
    }

    public AppIdentity WithoutAlias(string rawKey)
    {
        var aliases = new Dictionary<string, string>(_aliases, StringComparer.Ordinal);
        aliases.Remove(rawKey ?? string.Empty);
        return new AppIdentity(aliases, _displayNames, _categories, _categoryNames);
    }

    public AppIdentity WithCategory(string canonicalKey, string category)
    {
        var categories = new Dictionary<string, string>(_categories, StringComparer.Ordinal);
        string key = canonicalKey ?? string.Empty;
        if (string.IsNullOrEmpty(category)) categories.Remove(key);
        else categories[key] = category;

        return new AppIdentity(_aliases, _displayNames, categories, _categoryNames);
    }

    public AppIdentity WithDisplayName(string canonicalKey, string displayName)
    {
        var displayNames = new Dictionary<string, string>(_displayNames, StringComparer.Ordinal);
        string key = canonicalKey ?? string.Empty;
        if (string.IsNullOrEmpty(displayName)) displayNames.Remove(key);
        else displayNames[key] = displayName;

        return new AppIdentity(_aliases, displayNames, _categories, _categoryNames);
    }

    public AppIdentity WithCategoryAdded(string category)
    {
        if (string.IsNullOrEmpty(category) || _categoryNames.Contains(category)) return this;

        var names = new List<string>(_categoryNames) { category };
        return new AppIdentity(_aliases, _displayNames, _categories, names);
    }

    public AppIdentity WithCategoryRemoved(string category)
    {
        if (string.IsNullOrEmpty(category)) return this;

        var names = new List<string>(_categoryNames);
        names.Remove(category);

        var categories = new Dictionary<string, string>(_categories, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> kv in _categories)
        {
            if (string.Equals(kv.Value, category, StringComparison.Ordinal)) categories.Remove(kv.Key);
        }

        return new AppIdentity(_aliases, _displayNames, categories, names);
    }

    /// <summary>
    /// 把一个软件合并到另一个软件时的**身份侧**改动（其余三处——内存榜单、待写增量、库内行——
    /// 由各自的所有者分别执行，见 <c>ProductRuntime.MergeApps</c>）：
    ///  * 记录 <paramref name="fromKey"/> → <paramref name="toKey"/> 的别名；
    ///  * 把**任何指向 fromKey 的既有别名**改指到 toKey（避免出现 a→b→c 的链）；
    ///  * 把 fromKey 的分类搬到 toKey（**仅当 toKey 还没有分类**时才覆盖，不静默丢掉用户已有的选择）；
    ///  * 把 fromKey 的显示名覆盖搬到 toKey（同样仅在 toKey 没有时）。
    /// </summary>
    public AppIdentity WithMerged(string fromKey, string toKey)
    {
        string from = fromKey ?? string.Empty;
        string to = toKey ?? string.Empty;
        if (from.Length == 0 || to.Length == 0 || string.Equals(from, to, StringComparison.Ordinal))
            return this;

        var aliases = new Dictionary<string, string>(_aliases, StringComparer.Ordinal);

        // 改指：既有别名凡是指向 from 的，一律改指 to（单跳语义的维护点）。
        var rePointed = new List<string>();
        foreach (KeyValuePair<string, string> kv in aliases)
        {
            if (string.Equals(kv.Value, from, StringComparison.Ordinal)) rePointed.Add(kv.Key);
        }
        foreach (string raw in rePointed) aliases[raw] = to;

        aliases[from] = to;

        var categories = new Dictionary<string, string>(_categories, StringComparer.Ordinal);
        if (!categories.ContainsKey(to) && categories.TryGetValue(from, out string? movedCategory))
            categories[to] = movedCategory;
        categories.Remove(from);

        var displayNames = new Dictionary<string, string>(_displayNames, StringComparer.Ordinal);
        if (!displayNames.ContainsKey(to) && displayNames.TryGetValue(from, out string? movedName))
            displayNames[to] = movedName;
        displayNames.Remove(from);

        return new AppIdentity(aliases, displayNames, categories, _categoryNames);
    }
}
