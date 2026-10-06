using System;
using System.Collections.Generic;

namespace ScreenSpy.Collector;

/// <summary>
/// M9-1b「自定义软件名称」的**纯逻辑**：把用户输入解析成"最终会显示的名字"，并判断是否**重名**。
///
/// ────────────────────────────────────────────────────────────────────────
/// 为什么"禁止重名"（而不是允许）
/// ────────────────────────────────────────────────────────────────────────
/// 界面上每一行只能靠名字区分软件。两个软件同名时，用户看到的是两行一模一样的文字，
/// 而它们的时长、分类、以及将来的限额**各自独立** —— 用户会以为是同一个东西、
/// 却把设置下到了另一个上，而且**不会有任何报错**。这正是本项目一路上在防的那类缺陷。
/// 所以宁可明确拒绝，也不产生一个"看起来对得上、其实对不上"的界面。
///
/// ────────────────────────────────────────────────────────────────────────
/// 冲突的搜索范围（判据只有一条：别的软件"现在会显示的名字"等于候选名）
/// ────────────────────────────────────────────────────────────────────────
///   ① 别的软件**用户设过的名字**（库里 <c>app_meta.display_name</c>，哪怕它今天没出现）；
///   ② 别的**今日出现的软件**当前会显示的名字；
///   ③ **系统友好名表**里的全部默认名（<c>devenv</c> 默认就叫 Visual Studio）。
///
/// ────────────────────────────────────────────────────────────────────────
/// 两个刻意的"不对称"，都是为了让用户**不被卡住**
/// ────────────────────────────────────────────────────────────────────────
///  * **恢复默认名**（清空自定义名）时，只与其他软件的**自定义名**比较，
///    不与别人的**默认名**比较。理由：名字表本身可能就有两个键共用一个默认名
///    （本项目的 <c>calc</c> 与 <c>CalculatorApp</c> 都叫"计算器"），
///    那是名字表自己的事；若因此拒绝，用户会陷入"想恢复默认名却被拒绝"的死角。
///  * **本来就是默认名**时的"恢复默认名"是**空操作**，一律放行（什么都不变，没有理由不允）。
///
/// 已知边界（诚实写明）：若某软件既没人给它设过名字、今天没运行、也不在友好名表里，
/// 它的名字不在搜索范围内 —— 那种重名只会在它**首次运行时**才浮现，
/// 届时它会以重名出现在「软件与分类」列表里（那是唯一的发现途径）。
/// 我们选择接受这个边界，而不是把"今后可能出现的一切进程名"预先塞进一张名单里
/// （那既做不到，也会凭空误判）。
///
/// 无 IO、无状态、不碰 Win32 —— 因此规则可确定性推演（由 <c>--m9-selfcheck</c> 覆盖）。
/// </summary>
internal static class AppNamingRules
{
    /// <summary>自定义显示名的最大长度。纯展示字段，限长只为不让界面被一行字撑爆。</summary>
    public const int MaxDisplayNameLength = 40;

    /// <summary>
    /// 把"用户输入的值"解析成**最终会显示的名字**：非空即用它，空/纯空白 = 用系统默认名。
    /// （空串不是一个合法的显示名，它是"恢复默认名"这个动作的编码。）
    /// </summary>
    public static string ResolveName(string? appKey, string? overrideName)
    {
        string name = (overrideName ?? string.Empty).Trim();
        return name.Length > 0 ? name : ForegroundAppRules.NormalizeDisplayName(appKey);
    }

    /// <summary>校验"最终名字"本身是否合法：非空、不超长、不含控制字符。返回错误说明或 <c>null</c>。</summary>
    public static string? ValidateName(string? resolvedName)
    {
        string name = (resolvedName ?? string.Empty).Trim();
        if (name.Length == 0) return "名字不能为空。";
        if (name.Length > MaxDisplayNameLength)
            return $"名字太长：最多 {MaxDisplayNameLength} 个字符，当前 {name.Length} 个。";

        foreach (char c in name)
        {
            if (char.IsControl(c)) return "名字里不能包含换行、制表符等控制字符。";
        }

        return null;
    }

    /// <summary>某个软件"现在实际会显示的名字"：用户设的名字优先，否则系统友好名。</summary>
    public static string EffectiveName(AppIdentity? identity, string? appKey)
    {
        string key = (appKey ?? string.Empty).Trim();
        if (key.Length == 0) return string.Empty;
        return identity?.DisplayNameOverride(key) ?? ForegroundAppRules.NormalizeDisplayName(key);
    }

    /// <summary>给用户看的"这是哪个软件"：显示名（归一键）。用于重名提示。</summary>
    public static string DescribeKey(AppIdentity? identity, string? appKey)
    {
        string key = (appKey ?? string.Empty).Trim();
        if (key.Length == 0) return "(未知软件)";

        string name = EffectiveName(identity, key);
        return string.Equals(name, key, StringComparison.Ordinal) ? key : $"{name}（{key}）";
    }

    /// <summary>
    /// 找出与 <paramref name="resolvedName"/> 同名的**别的软件**的归一键；无冲突返回空串。
    /// <paramref name="appKey"/> 自己不算冲突（把自己的名字改成当前显示的名字是合法的空操作）。
    /// </summary>
    /// <param name="customNamesOnly">
    /// 为 true 时只比较别人的**自定义名**（"恢复默认名"用，见类注释里的不对称说明）。
    /// </param>
    public static string FindConflict(AppIdentity? identity, string? appKey, string? resolvedName,
                                      IEnumerable<string>? knownAppKeys, bool customNamesOnly = false)
    {
        string name = (resolvedName ?? string.Empty).Trim();
        string self = (appKey ?? string.Empty).Trim();
        if (identity is null || name.Length == 0) return string.Empty;

        foreach (string key in CandidateKeys(identity, knownAppKeys))
        {
            if (key.Length == 0 || string.Equals(key, self, StringComparison.Ordinal)) continue;

            string other = customNamesOnly
                ? identity.DisplayNameOverride(key) ?? string.Empty
                : EffectiveName(identity, key);

            if (other.Length == 0) continue;
            if (string.Equals(other, name, StringComparison.OrdinalIgnoreCase)) return key;
        }

        return string.Empty;
    }

    /// <summary>
    /// **唯一的预检实现**：返回错误说明，<c>null</c> 表示可以保存。
    /// <paramref name="overrideName"/> 为空串表示"恢复系统默认名"。
    ///
    /// 对话框、组合根、以及写库前的最后一道闸门都调这里 ——
    /// 因此不会出现"对话框说能改、保存时却说不能"这种分叉（那会让用户以为程序在耍他）。
    /// </summary>
    public static string? Validate(AppIdentity? identity, string? appKey, string? overrideName,
                                   IEnumerable<string>? knownAppKeys)
    {
        string key = (appKey ?? string.Empty).Trim();
        if (key.Length == 0) return "缺少软件标识，无法改名。";

        string raw = (overrideName ?? string.Empty).Trim();
        string resolved = ResolveName(key, raw);

        if (ValidateName(resolved) is { } illegal) return illegal;

        if (raw.Length == 0)
        {
            // "恢复默认名"：本来就是默认名 → 空操作，直接放行（否则用户会被卡在一个死角里）。
            if (identity?.DisplayNameOverride(key) is null) return null;

            string selfConflict = FindConflict(identity, key, resolved, knownAppKeys, customNamesOnly: true);
            return selfConflict.Length == 0
                ? null
                : $"不能恢复默认名：「{resolved}」现在被「{DescribeKey(identity, selfConflict)}」用作自定义名。" +
                  "请先给它换一个名字。";
        }

        string conflict = FindConflict(identity, key, resolved, knownAppKeys);
        return conflict.Length == 0
            ? null
            : $"「{resolved}」与「{DescribeKey(identity, conflict)}」重复。" +
              "两个软件同名时界面上分不清谁是谁（而它们的时长是各算各的），所以不允许重名。";
    }

    /// <summary>
    /// 全部"可能出现在界面上的软件键"：用户设过名字的 + 今日出现过的 + 友好名表里登记过的。
    /// 用 <see cref="SortedSet{T}"/> 去重并保证顺序稳定（冲突结论可复现）。
    /// </summary>
    private static IEnumerable<string> CandidateKeys(AppIdentity identity, IEnumerable<string>? knownAppKeys)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);

        foreach (string key in identity.DisplayNameOverrides.Keys) keys.Add(key);

        if (knownAppKeys is not null)
        {
            foreach (string key in knownAppKeys)
            {
                if (!string.IsNullOrEmpty(key)) keys.Add(key);
            }
        }

        foreach (string processName in ForegroundAppRules.MappedProcessNames)
            keys.Add(ForegroundAppRules.MergeKeyOf(processName));

        return keys;
    }
}
