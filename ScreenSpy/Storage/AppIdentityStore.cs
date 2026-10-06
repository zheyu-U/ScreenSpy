using System;
using System.Collections.Generic;
using ScreenSpy.Collector;

namespace ScreenSpy.Storage;

/// <summary>
/// M9-1 身份层的**存储接线**：把三张表（<c>app_alias</c> / <c>app_meta</c> / <c>categories</c>）
/// 读成一份 <see cref="AppIdentity"/>，并把界面上的改动安全地写回去。
///
/// 为什么单独一个文件、而不塞进 <see cref="SqliteStore"/>：
/// SqliteStore 是**薄封装**（只认 SQL），这里承载的是**领域语义**（别名怎么合成一张映射、分类怎么排序、
/// 改动后返回的新身份长什么样）。混在一起会让"SQL 层"开始懂业务。
///
/// 一个贯穿全篇的原则：**存储不可用时一律"当作没改"并如实返回旧身份**，
/// 绝不返回一个"内存里改了、库里没改"的身份 —— 那会在下次启动时静默退回旧值。
/// </summary>
internal static class AppIdentityStore
{
    /// <summary>从库中加载身份；存储不可用（或读失败）返回 <see cref="AppIdentity.Empty"/>。</summary>
    public static AppIdentity Load(SqliteStore? store)
    {
        if (store is null) return AppIdentity.Empty;

        var aliases = new List<KeyValuePair<string, string>>();
        foreach (AppAliasRow row in store.ReadAppAliases())
            aliases.Add(new KeyValuePair<string, string>(row.RawKey, row.AppKey));

        var metas = new List<KeyValuePair<string, (string, string)>>();
        foreach (AppMetaRow row in store.ReadAppMeta())
            metas.Add(new KeyValuePair<string, (string, string)>(row.AppKey, (row.DisplayName, row.Category)));

        var categories = new List<string>();
        foreach (CategoryRow row in store.ReadCategories())
            if (!string.IsNullOrEmpty(row.Name)) categories.Add(row.Name);

        return AppIdentity.Build(aliases, metas.AsReadOnly(), categories);
    }

    /// <summary>设置某软件的分类（空串 = 未分类）；返回改动后的身份。</summary>
    public static AppIdentity SetCategory(SqliteStore? store, AppIdentity current, string appKey, string category)
    {
        if (store is null || string.IsNullOrEmpty(appKey)) return current;

        store.SetAppCategory(appKey, category ?? string.Empty);
        return current.WithCategory(appKey, category ?? string.Empty);
    }

    /// <summary>
    /// 设置某软件的**自定义显示名**（M9-1b）；空串 = 恢复系统默认名。返回改动后的身份。
    ///
    /// 「禁止重名」在这里**再查一次**（与界面预检调的是同一个 <see cref="AppNamingRules.Validate"/>）：
    /// 界面预检是为了让用户在对话框里当场看到原因，而这道闸门是为了**守住数据** ——
    /// 即便将来多出别的入口（托盘、导入、脚本），也不可能写进一个重名。
    ///
    /// 冲突或名字不合法时：**不写库、不返回新身份**（"要么都改、要么都没改"），
    /// 并把冲突对象的键放进 <paramref name="conflictKey"/>（取不到时为空串，表示"只是不合法"）。
    /// </summary>
    public static AppIdentity SetDisplayName(SqliteStore? store, AppIdentity current, string appKey,
                                             string displayName, IReadOnlyList<string> knownAppKeys,
                                             out string conflictKey)
    {
        conflictKey = string.Empty;

        string key = (appKey ?? string.Empty).Trim();
        if (store is null || key.Length == 0) return current;

        string name = (displayName ?? string.Empty).Trim();

        if (AppNamingRules.Validate(current, key, name, knownAppKeys) is not null)
        {
            // 注意这里刻意**再算一次**冲突键：Validate 返回的是"给人看的说明"，
            // 而调用方需要知道"到底跟谁撞了"才能给出准确的界面反馈。
            conflictKey = AppNamingRules.FindConflict(
                current, key, AppNamingRules.ResolveName(key, name), knownAppKeys,
                customNamesOnly: name.Length == 0);

            return current;
        }

        store.SetAppDisplayName(key, name);
        return current.WithDisplayName(key, name);
    }

    /// <summary>新建分类；返回改动后的身份（重名时保持原样，因为"已存在"不是错误）。</summary>
    public static AppIdentity CreateCategory(SqliteStore? store, AppIdentity current, string name)
    {
        string category = (name ?? string.Empty).Trim();
        if (store is null || category.Length == 0) return current;

        store.UpsertCategory(category);
        return current.WithCategoryAdded(category);
    }

    /// <summary>
    /// 删除分类（该分类下的软件回到未分类，该分类的限额一并删除）；返回改动后的身份。
    /// 返回值同时用 <c>out</c> 说明"是否真的删掉了一个已存在的分类"，便于界面给出准确反馈。
    /// </summary>
    public static AppIdentity DeleteCategory(SqliteStore? store, AppIdentity current, string name, out bool removed)
    {
        removed = false;
        string category = (name ?? string.Empty).Trim();
        if (store is null || category.Length == 0) return current;

        removed = store.DeleteCategory(category);
        return removed ? current.WithCategoryRemoved(category) : current;
    }

    /// <summary>
    /// 取消合并（对**今后**生效）：删掉别名，于是之后这个进程名不再被并入目标软件。
    ///
    /// ⚠️ 语义边界（必须在界面与文档写明）：已经并入的**历史秒数不会退回** ——
    /// 库里的行已经被重写成归一键，原始键的信息在聚合层已经不存在（原始活动日志 JSONL 里还有）。
    /// 这是"清空历史、从今天重新开始"这一决策的必然结果：它换来了实现的简单与一致。
    /// </summary>
    public static AppIdentity Unmerge(SqliteStore? store, AppIdentity current, string rawKey, out bool removed)
    {
        removed = false;
        string raw = (rawKey ?? string.Empty).Trim();
        if (store is null || raw.Length == 0) return current;

        removed = store.RemoveAppAlias(raw);
        return removed ? current.WithoutAlias(raw) : current;
    }
}
