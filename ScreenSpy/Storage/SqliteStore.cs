using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace ScreenSpy.Storage;

/// <summary>app_usage 的一行：某天某软件的累计秒数。</summary>
internal readonly struct AppUsageRow
{
    public AppUsageRow(DateOnly day, string appName, long seconds)
    {
        Day = day;
        AppName = appName ?? string.Empty;
        Seconds = seconds;
    }

    public DateOnly Day { get; }

    /// <summary>软件标识。用**合并键**（进程名小写）而非展示名，避免改名后重复建行。</summary>
    public string AppName { get; }

    public long Seconds { get; }

    public override string ToString() => $"{Day:yyyy-MM-dd} {AppName}={Seconds}s";
}

/// <summary>
/// daily_activity 的一行：某天“计数为活跃、但没归属到任何软件”的秒数（schema v2）。
///
/// 为什么需要它：<c>app_usage</c> 只存软件（可归因）时长，因此重启续算时
/// “桌面/外壳/自身”与“无前台/未知”这两部分会凭空消失 —— “今日真实活跃”在重启后会变小。
/// 本表把这两部分按天单独存下来，续算时一并灌回，今日口径就**跨重启可加、不下滑**。
///
/// 刻意分成两列（而不是一个总数）：展示上它们是两栏（“不计入使用时长”与“无前台/未知”）。
/// 锁屏**不在此表** —— 它按口径完全不计入，既不展示也不续算。
/// </summary>
internal readonly struct DayActivityRow
{
    public DayActivityRow(DateOnly day, long filteredSeconds, long unattributedSeconds)
    {
        Day = day;
        FilteredSeconds = filteredSeconds;
        UnattributedSeconds = unattributedSeconds;
    }

    public DateOnly Day { get; }

    /// <summary>“不计入使用时长”（桌面 / 外壳 / 自身）秒数。</summary>
    public long FilteredSeconds { get; }

    /// <summary>“无前台 / 未知”秒数。</summary>
    public long UnattributedSeconds { get; }

    public override string ToString() =>
        $"{Day:yyyy-MM-dd} filtered={FilteredSeconds}s unattributed={UnattributedSeconds}s";
}

/// <summary>limits 表的一行。</summary>
internal readonly struct LimitRow
{
    public LimitRow(string scope, string target, long seconds)
    {
        Scope = scope ?? string.Empty;
        Target = target ?? string.Empty;
        Seconds = seconds;
    }

    /// <summary><c>"total"</c> 或 <c>"app"</c>。</summary>
    public string Scope { get; }

    /// <summary>scope=<c>"app"</c> 时为软件合并键；scope=<c>"total"</c> 时为**空串**（不是 NULL，见 §备注）。</summary>
    public string Target { get; }

    public long Seconds { get; }

    public override string ToString() => $"{Scope}/{Target}={Seconds}s";
}

/// <summary>
/// app_meta 的一行：某个**软件（归一键）**的用户设置（显示名 + 归属分类）。
///
/// 只存"用户改过的部分"，没有行就等于"用系统默认"（显示名按友好名映射表现算、分类为空=未分类）——
/// 这样代码里的映射表升级后不会被库里的旧名字覆盖。
/// </summary>
internal readonly struct AppMetaRow
{
    public AppMetaRow(string appKey, string displayName, string category)
    {
        AppKey = appKey ?? string.Empty;
        DisplayName = displayName ?? string.Empty;
        Category = category ?? string.Empty;
    }

    /// <summary>归一键（进程名小写，或合并后的目标键）。</summary>
    public string AppKey { get; }

    /// <summary>用户设置的显示名覆盖；空串 = 未设置。</summary>
    public string DisplayName { get; }

    /// <summary>归属分类名；空串 = 未分类。口径：**一个软件只能属于一个分类**（M9-1 决策 A）。</summary>
    public string Category { get; }

    public override string ToString() => $"{AppKey} name='{DisplayName}' category='{Category}'";
}

/// <summary>app_alias 的一行：原始进程名 → 归一键（多对一，即"把多个进程合并成一个软件"）。</summary>
internal readonly struct AppAliasRow
{
    public AppAliasRow(string rawKey, string appKey)
    {
        RawKey = rawKey ?? string.Empty;
        AppKey = appKey ?? string.Empty;
    }

    /// <summary>原始合并键（进程名小写）。</summary>
    public string RawKey { get; }

    /// <summary>归一键（目标软件）。</summary>
    public string AppKey { get; }

    public override string ToString() => $"{RawKey}→{AppKey}";
}

/// <summary>categories 的一行：用户定义的一个分类。</summary>
internal readonly struct CategoryRow
{
    public CategoryRow(string name, long sortOrder)
    {
        Name = name ?? string.Empty;
        SortOrder = sortOrder;
    }

    public string Name { get; }
    public long SortOrder { get; }

    public override string ToString() => $"{Name}(#{SortOrder})";
}

/// <summary>
/// SQLite 的薄封装（开发文档 §5.6）：建库、建表、版本、事务、增量 upsert、设置与限额读写。
///
/// 设计取舍：
///  * **每次操作开一条连接**（依赖 Microsoft.Data.Sqlite 的连接池）。
///    SQLite 连接不是线程安全的；而本项目既有“心跳线程”又有“后台写线程”，
///    用连接池 + 一条操作一条连接，比手工维护长连接 + 锁更不容易出错。
///  * **WAL**：读写并发更友好，且崩溃后可自动恢复。WAL 是**文件级持久属性**，
///    因此只在 <see cref="Initialize"/> 里设置一次；<c>busy_timeout</c>/<c>synchronous</c>
///    是**连接级**属性，每次开连接都要重设。
///  * **幂等建表**：全部用 <c>IF NOT EXISTS</c>，可反复 <see cref="Initialize"/>。
///  * <c>PRAGMA user_version</c> 记录 schema 版本，供日后迁移使用。
///
/// ⚠️ 一个容易踩的 SQL 语义（本文件特意统一成空串）：
///    SQLite 的 UNIQUE / PRIMARY KEY 认为 **NULL 之间互不相等**，
///    因此若 limits.scope='total' 时把 target 存成 NULL，
///    那么 <c>ON CONFLICT(scope,target)</c> **永远匹配不上**，
///    每次写入都会**新增一行**而不是更新。故这里规定：total 用 <c>""</c>（空串）。
/// </summary>
internal sealed class SqliteStore : IDisposable
{
    /// <summary>
    /// 当前 schema 版本（写入 <c>PRAGMA user_version</c>）。
    ///
    /// 版本历史：
    ///  * <b>1</b> —— app_usage / settings / limits；
    ///  * <b>2</b> —— 新增 <c>daily_activity</c>（“不计入使用时长 / 无前台·未知”的按天秒数，
    ///    用于修复“重启后今日真实活跃变小”的口径缺陷）。
    ///  * <b>3</b> —— M9-1 **软件身份层**：新增 <c>app_alias</c>（原始进程名 → 归一键）、
    ///    <c>app_meta</c>（用户显示名 + 归属分类）、<c>categories</c>（分类表）。
    ///    ⚠️ 同时按既定决策（2026-10-06）**清空时间历史**：<c>app_usage</c> 与 <c>daily_activity</c>
    ///    的全部行被删除 —— 因为身份口径变了（进程名会被合并），旧行留着会让同一软件分成两半。
    ///    用户已明确选择「历史数据不要了，从今天重新开始」；<c>settings</c>（卡片形态/坐标等）与
    ///    <c>limits</c> **不受影响**。
    ///
    /// v1→v2 是**纯增量**（只多建一张空表）；v2→v3 是**唯一一次会删数据的迁移**，原因见上。
    /// </summary>
    public const int SchemaVersion = 3;

    private readonly StorageOptions _options;
    private readonly string _connectionString;
    private int _disposed;

    /// <summary>
    /// 【仅供自检】故障注入：&gt;0 时下一次提交会**在提交前抛异常**并递减。
    /// 用于确定性验证“写盘失败不得丢数据、不得抛到心跳线程”。
    /// 生产代码永不设置它。
    /// </summary>
    internal int FailNextCommits { get; set; }

    public SqliteStore(StorageOptions options)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Normalize();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    public StorageOptions Options => _options;

    public string DatabasePath => _options.DatabasePath;

    // ================================================================ 初始化

    /// <summary>建目录、建库、建表、设版本。可重复调用（幂等）。</summary>
    public void Initialize()
    {
        string? dir = Path.GetDirectoryName(_options.DatabasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using SqliteConnection conn = Open();

        if (_options.UseWal)
            Execute(conn, "PRAGMA journal_mode=WAL;");      // 文件级持久属性

        Execute(conn, "PRAGMA foreign_keys=ON;");

        int version = ReadUserVersion(conn);

        if (version > SchemaVersion)
            throw new InvalidOperationException(
                $"数据库 schema 版本为 {version}，高于本程序支持的 {SchemaVersion}；拒绝以旧版本写入，以免损坏数据。");

        if (version < SchemaVersion)
        {
            using SqliteTransaction tx = conn.BeginTransaction();

            // v2 → v3：按既定决策（2026-10-06）**清空时间历史**，从今天重新开始累计。
            // 为什么必须清：身份口径变了（多个进程名会被合并成一个软件），旧行按原始进程名存着，
            // 合并后同一软件会被拆成"合并前"和"合并后"两条 —— 数字看着对不上，而且无法解释。
            // 用户已明确选择"历史数据不要了"。settings（卡片形态/坐标）与 limits **不在此列**。
            // 分版本判断：v1 的库里还没有 daily_activity，直接 DELETE 会报错。
            if (version >= 1) Execute(conn, tx, "DELETE FROM app_usage;");
            if (version >= 2) Execute(conn, tx, "DELETE FROM daily_activity;");

            CreateSchema(conn, tx);
            Execute(conn, tx, $"PRAGMA user_version={SchemaVersion};");
            tx.Commit();
        }
        else
        {
            // 版本已是最新：仍然跑一次 IF NOT EXISTS，防御“表被外部删除”。
            using SqliteTransaction tx = conn.BeginTransaction();
            CreateSchema(conn, tx);
            tx.Commit();
        }
    }

    private static void CreateSchema(SqliteConnection conn, SqliteTransaction tx)
    {
        // 每天每软件累计（单位：秒）—— 开发文档 §5.6 的原始设计。
        Execute(conn, tx, """
            CREATE TABLE IF NOT EXISTS app_usage (
                date      TEXT    NOT NULL,
                app_name  TEXT    NOT NULL,
                seconds   INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (date, app_name)
            );
            """);

        // 每天“不计入使用时长 / 无前台·未知”的秒数（schema v2）。
        // 与 app_usage 分开，是为了让 app_usage 保持“纯软件”——Top 排名与占比分母不受污染。
        // 锁屏不计入此表（按口径完全不计入）。
        Execute(conn, tx, """
            CREATE TABLE IF NOT EXISTS daily_activity (
                date              TEXT    PRIMARY KEY,
                filtered_seconds  INTEGER NOT NULL DEFAULT 0,
                unattributed_seconds INTEGER NOT NULL DEFAULT 0
            );
            """);

        // 设置表（键值对）。widget_pos_x / widget_pos_y 等落在这里（§5.6 / §5.10）。
        Execute(conn, tx, """
            CREATE TABLE IF NOT EXISTS settings (
                key    TEXT PRIMARY KEY,
                value  TEXT
            );
            """);

        // 限额表（§5.6，由 M9 的限额引擎使用）。
        // 注意：scope='total' 时 target 存空串而非 NULL —— 原因见类注释（NULL 不参与 UNIQUE 匹配）。
        Execute(conn, tx, """
            CREATE TABLE IF NOT EXISTS limits (
                scope   TEXT    NOT NULL,
                target  TEXT    NOT NULL DEFAULT '',
                seconds INTEGER NOT NULL,
                PRIMARY KEY (scope, target)
            );
            """);

        // ---- M9-1 软件身份层（schema v3）----
        // 别名：原始进程名 → 归一键。**默认不写行**（解析时查不到即返回原始键自身），
        // 因此这张表只承载"用户主动做过的合并"，语义干净、也能一眼看出合并过什么。
        Execute(conn, tx, """
            CREATE TABLE IF NOT EXISTS app_alias (
                raw_key TEXT PRIMARY KEY,
                app_key TEXT NOT NULL
            );
            """);

        // 软件的用户设置：显示名覆盖 + 归属分类。
        // 只存用户改过的行；没有行 = 用系统默认（显示名按友好名映射表现算）。
        // 分类口径（M9-1 决策）：**一个软件只能属于一个分类**（单选）——
        // 这样"各分类之和 == 各软件之和"精确成立，将来的分类限额才可对账、可相加。
        // 空串表示"未分类"（不是 NULL：理由同 limits.target，NULL 不参与 UNIQUE/比较）。
        Execute(conn, tx, """
            CREATE TABLE IF NOT EXISTS app_meta (
                app_key      TEXT PRIMARY KEY,
                display_name TEXT NOT NULL DEFAULT '',
                category     TEXT NOT NULL DEFAULT ''
            );
            """);

        // 分类表。分类名即主键（用户可见、无隐藏 id），重名即同一个分类。
        Execute(conn, tx, """
            CREATE TABLE IF NOT EXISTS categories (
                name       TEXT PRIMARY KEY,
                sort_order INTEGER NOT NULL DEFAULT 0
            );
            """);
    }

    // ================================================================ 写入

    /// <summary>
    /// 在**一个事务**里按“增量累加”方式写入若干行：软件行（app_usage）与
    /// 每日“不计入 / 无前台·未知”行（daily_activity）**同事务提交**，
    /// 因此两者永远同进同退，不会出现“软件写了、非软件没写”的半截状态。
    /// 任何一行失败 → 整个事务回滚 → 抛异常（由调用方决定重试），因此**不会出现写一半的状态**。
    /// </summary>
    /// <exception cref="InvalidOperationException">故障注入生效（仅供自检）。</exception>
    public void AddSeconds(IReadOnlyList<AppUsageRow>? rows, IReadOnlyList<DayActivityRow>? dayRows = null)
    {
        bool hasApp = rows is { Count: > 0 };
        bool hasDay = dayRows is { Count: > 0 };
        if (!hasApp && !hasDay) return;

        using SqliteConnection conn = Open();
        using SqliteTransaction tx = conn.BeginTransaction();

        if (hasApp)
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO app_usage (date, app_name, seconds)
                VALUES ($d, $a, $s)
                ON CONFLICT(date, app_name) DO UPDATE SET seconds = seconds + excluded.seconds;
                """;

            SqliteParameter pd = cmd.Parameters.Add("$d", SqliteType.Text);
            SqliteParameter pa = cmd.Parameters.Add("$a", SqliteType.Text);
            SqliteParameter ps = cmd.Parameters.Add("$s", SqliteType.Integer);

            foreach (AppUsageRow row in rows!)
            {
                if (row.Seconds == 0) continue;
                pd.Value = FormatDay(row.Day);
                pa.Value = row.AppName;
                ps.Value = row.Seconds;
                cmd.ExecuteNonQuery();
            }
        }

        if (hasDay)
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO daily_activity (date, filtered_seconds, unattributed_seconds)
                VALUES ($d, $f, $u)
                ON CONFLICT(date) DO UPDATE SET
                    filtered_seconds     = filtered_seconds + excluded.filtered_seconds,
                    unattributed_seconds = unattributed_seconds + excluded.unattributed_seconds;
                """;

            SqliteParameter pd = cmd.Parameters.Add("$d", SqliteType.Text);
            SqliteParameter pf = cmd.Parameters.Add("$f", SqliteType.Integer);
            SqliteParameter pu = cmd.Parameters.Add("$u", SqliteType.Integer);

            foreach (DayActivityRow row in dayRows!)
            {
                if (row.FilteredSeconds == 0 && row.UnattributedSeconds == 0) continue;
                pd.Value = FormatDay(row.Day);
                pf.Value = row.FilteredSeconds;
                pu.Value = row.UnattributedSeconds;
                cmd.ExecuteNonQuery();
            }
        }

        // 故障注入：在提交之前抛出，等效于“写盘失败”。事务随 using 释放而回滚。
        if (FailNextCommits > 0)
        {
            FailNextCommits--;
            throw new InvalidOperationException("（自检故障注入）模拟提交失败");
        }

        tx.Commit();
    }

    /// <summary>写设置（upsert）。</summary>
    public void SetSetting(string key, string? value)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings (key, value) VALUES ($k, $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$k", key ?? string.Empty);
        cmd.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>写限额（upsert）。scope='total' 时请传 target=""。</summary>
    public void SetLimit(string scope, string target, long seconds)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO limits (scope, target, seconds) VALUES ($sc, $t, $s)
            ON CONFLICT(scope, target) DO UPDATE SET seconds = excluded.seconds;
            """;
        cmd.Parameters.AddWithValue("$sc", scope ?? string.Empty);
        cmd.Parameters.AddWithValue("$t", target ?? string.Empty);
        cmd.Parameters.AddWithValue("$s", seconds);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 删除限额（M9-2）。返回是否确实删掉了一行 ——
    /// 界面要据此说“已删除”还是“本来就没有”，而不是一律报成功。
    /// </summary>
    public bool DeleteLimit(string scope, string target)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM limits WHERE scope = $sc AND target = $t;";
        cmd.Parameters.AddWithValue("$sc", scope ?? string.Empty);
        cmd.Parameters.AddWithValue("$t", target ?? string.Empty);
        return cmd.ExecuteNonQuery() > 0;
    }

    // ================================================================ 软件身份层（M9-1）

    /// <summary>读全部别名（原始键 → 归一键）。</summary>
    public IReadOnlyList<AppAliasRow> ReadAppAliases()
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT raw_key, app_key FROM app_alias ORDER BY raw_key ASC;";

        var list = new List<AppAliasRow>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(new AppAliasRow(reader.GetString(0), reader.GetString(1)));
        return list;
    }

    /// <summary>读全部软件元信息（只含用户改过的行）。</summary>
    public IReadOnlyList<AppMetaRow> ReadAppMeta()
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT app_key, display_name, category FROM app_meta ORDER BY app_key ASC;";

        var list = new List<AppMetaRow>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new AppMetaRow(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return list;
    }

    /// <summary>读全部分类（按 sort_order、再按名字）。</summary>
    public IReadOnlyList<CategoryRow> ReadCategories()
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, sort_order FROM categories ORDER BY sort_order ASC, name ASC;";

        var list = new List<CategoryRow>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(new CategoryRow(reader.GetString(0), reader.GetInt64(1)));
        return list;
    }

    /// <summary>登记/更新一个别名（原始键 → 归一键）。</summary>
    public void SetAppAlias(string rawKey, string appKey)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO app_alias (raw_key, app_key) VALUES ($r, $a)
            ON CONFLICT(raw_key) DO UPDATE SET app_key = excluded.app_key;
            """;
        cmd.Parameters.AddWithValue("$r", rawKey ?? string.Empty);
        cmd.Parameters.AddWithValue("$a", appKey ?? string.Empty);
        cmd.ExecuteNonQuery();
    }

    /// <summary>删除一个别名；返回是否确实删掉了一行。</summary>
    public bool RemoveAppAlias(string rawKey)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM app_alias WHERE raw_key = $r;";
        cmd.Parameters.AddWithValue("$r", rawKey ?? string.Empty);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 设置某软件的分类（空串 = 未分类）。
    /// 只改 category 一列 —— **不动显示名**（同一行的两个独立设置，互相不该覆盖）。
    /// </summary>
    public void SetAppCategory(string appKey, string category)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO app_meta (app_key, display_name, category) VALUES ($k, '', $c)
            ON CONFLICT(app_key) DO UPDATE SET category = excluded.category;
            """;
        cmd.Parameters.AddWithValue("$k", appKey ?? string.Empty);
        cmd.Parameters.AddWithValue("$c", category ?? string.Empty);
        cmd.ExecuteNonQuery();
    }

    /// <summary>设置某软件的显示名覆盖（空串 = 恢复系统默认名）。只改 display_name 一列。</summary>
    public void SetAppDisplayName(string appKey, string displayName)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO app_meta (app_key, display_name, category) VALUES ($k, $d, '')
            ON CONFLICT(app_key) DO UPDATE SET display_name = excluded.display_name;
            """;
        cmd.Parameters.AddWithValue("$k", appKey ?? string.Empty);
        cmd.Parameters.AddWithValue("$d", displayName ?? string.Empty);
        cmd.ExecuteNonQuery();
    }

    /// <summary>新建分类（已存在则只更新排序，不报错 —— 建重名分类不该是个错误）。</summary>
    public void UpsertCategory(string name, long sortOrder = 0)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO categories (name, sort_order) VALUES ($n, $s)
            ON CONFLICT(name) DO UPDATE SET sort_order = excluded.sort_order;
            """;
        cmd.Parameters.AddWithValue("$n", name ?? string.Empty);
        cmd.Parameters.AddWithValue("$s", sortOrder);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 删除分类；返回是否确实删掉了。
    /// 三件事必须一起做，否则会留下“看不见却仍然生效”的东西：
    ///  ① 该分类下的软件回到**未分类**（留着悬空分类名，界面会显示一个已经不存在的分类）；
    ///  ② 删掉该分类的**限额**（M9-2 用；现在就处理，避免限额悄悄继续触发通知）；
    ///  ③ 删分类本身。
    /// </summary>
    public bool DeleteCategory(string name)
    {
        string category = name ?? string.Empty;
        if (category.Length == 0) return false;

        using SqliteConnection conn = Open();
        using SqliteTransaction tx = conn.BeginTransaction();

        using (SqliteCommand clear = conn.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "UPDATE app_meta SET category = '' WHERE category = $c;";
            clear.Parameters.AddWithValue("$c", category);
            clear.ExecuteNonQuery();
        }

        using (SqliteCommand limits = conn.CreateCommand())
        {
            limits.Transaction = tx;
            limits.CommandText = "DELETE FROM limits WHERE scope = 'category' AND target = $c;";
            limits.Parameters.AddWithValue("$c", category);
            limits.ExecuteNonQuery();
        }

        int affected;
        using (SqliteCommand drop = conn.CreateCommand())
        {
            drop.Transaction = tx;
            drop.CommandText = "DELETE FROM categories WHERE name = $c;";
            drop.Parameters.AddWithValue("$c", category);
            affected = drop.ExecuteNonQuery();
        }

        tx.Commit();
        return affected > 0;
    }

    /// <summary>
    /// 把 <paramref name="fromKey"/> 的历史行**并入** <paramref name="toKey"/>（同一事务）。返回被并入的天数。
    ///
    /// 为什么必须重写库内行：合并可能发生在一天的中途，而今天早些时候已按**原始键**落库了。
    /// 若不重写，今天会出现“vscode 1:20 + code 0:40”两行 —— 用户会以为合并没生效，
    /// 而限额（M9-2）也会只统计到一半（**不报错、只是数字偏小**，正是本项目最忌的那类缺陷）。
    ///
    /// 语义（与用户决策一致，界面与文档都要写明）：
    ///  * 合并**不可拆分** —— 原始键的行被并入归一键，历史里不再保留“哪一秒属于哪个原始进程名”；
    ///    但**原始活动日志**（JSONL，逐拍记录原始进程名/类名/标题）不受影响，追溯能力不丢；
    ///  * <c>settings</c>（卡片形态/坐标）与 <c>limits</c> 不受影响。
    /// </summary>
    public int MergeAppKey(string fromKey, string toKey)
    {
        string from = fromKey ?? string.Empty;
        string to = toKey ?? string.Empty;
        if (from.Length == 0 || to.Length == 0 || string.Equals(from, to, StringComparison.Ordinal)) return 0;

        using SqliteConnection conn = Open();
        using SqliteTransaction tx = conn.BeginTransaction();

        // ① 先读出被合并键在各天的秒数（读出来再逐天累加，比一条 INSERT…SELECT…ON CONFLICT 更好读、也更好排错）
        var days = new List<(string Day, long Seconds)>();
        using (SqliteCommand read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT date, seconds FROM app_usage WHERE app_name = $a;";
            read.Parameters.AddWithValue("$a", from);
            using SqliteDataReader reader = read.ExecuteReader();
            while (reader.Read()) days.Add((reader.GetString(0), reader.GetInt64(1)));
        }

        // ② 逐天并入目标键：目标键在该天可能已有行 → 必须**累加**而不是覆盖
        foreach ((string day, long seconds) in days)
        {
            if (seconds == 0) continue;

            using SqliteCommand add = conn.CreateCommand();
            add.Transaction = tx;
            add.CommandText = """
                INSERT INTO app_usage (date, app_name, seconds) VALUES ($d, $a, $s)
                ON CONFLICT(date, app_name) DO UPDATE SET seconds = seconds + excluded.seconds;
                """;
            add.Parameters.AddWithValue("$d", day);
            add.Parameters.AddWithValue("$a", to);
            add.Parameters.AddWithValue("$s", seconds);
            add.ExecuteNonQuery();
        }

        // ③ 删掉被合并键的行
        using (SqliteCommand dropRows = conn.CreateCommand())
        {
            dropRows.Transaction = tx;
            dropRows.CommandText = "DELETE FROM app_usage WHERE app_name = $a;";
            dropRows.Parameters.AddWithValue("$a", from);
            dropRows.ExecuteNonQuery();
        }

        // ④ 既有别名改指：凡是指向 from 的，一律改指 to（维持“单跳”语义，避免 a→b→c 的链）
        using (SqliteCommand rePoint = conn.CreateCommand())
        {
            rePoint.Transaction = tx;
            rePoint.CommandText = "UPDATE app_alias SET app_key = $to WHERE app_key = $from;";
            rePoint.Parameters.AddWithValue("$to", to);
            rePoint.Parameters.AddWithValue("$from", from);
            rePoint.ExecuteNonQuery();
        }

        // ⑤ 登记 from → to
        using (SqliteCommand alias = conn.CreateCommand())
        {
            alias.Transaction = tx;
            alias.CommandText = """
                INSERT INTO app_alias (raw_key, app_key) VALUES ($r, $a)
                ON CONFLICT(raw_key) DO UPDATE SET app_key = excluded.app_key;
                """;
            alias.Parameters.AddWithValue("$r", from);
            alias.Parameters.AddWithValue("$a", to);
            alias.ExecuteNonQuery();
        }

        // ⑥ 元信息搬家：把 from 的分类/显示名补给 to（**仅在 to 为空时**，不静默覆盖用户已有选择），再删 from 的行。
        using (SqliteCommand meta = conn.CreateCommand())
        {
            meta.Transaction = tx;
            meta.CommandText = """
                INSERT INTO app_meta (app_key, display_name, category)
                SELECT $to, display_name, category FROM app_meta WHERE app_key = $from
                ON CONFLICT(app_key) DO UPDATE SET
                    display_name = CASE WHEN app_meta.display_name = '' THEN excluded.display_name ELSE app_meta.display_name END,
                    category     = CASE WHEN app_meta.category     = '' THEN excluded.category     ELSE app_meta.category     END;
                """;
            meta.Parameters.AddWithValue("$to", to);
            meta.Parameters.AddWithValue("$from", from);
            meta.ExecuteNonQuery();
        }

        using (SqliteCommand dropMeta = conn.CreateCommand())
        {
            dropMeta.Transaction = tx;
            dropMeta.CommandText = "DELETE FROM app_meta WHERE app_key = $a;";
            dropMeta.Parameters.AddWithValue("$a", from);
            dropMeta.ExecuteNonQuery();
        }

        tx.Commit();
        return days.Count;
    }

    // ================================================================ 读取

    /// <summary>读某一天的“每软件秒数”，按秒数降序（相同则按键升序，保证稳定）。</summary>
    public IReadOnlyList<AppUsageRow> ReadDay(DateOnly day)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT date, app_name, seconds FROM app_usage
            WHERE date = $d
            ORDER BY seconds DESC, app_name ASC;
            """;
        cmd.Parameters.AddWithValue("$d", FormatDay(day));

        var list = new List<AppUsageRow>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new AppUsageRow(
                ParseDay(reader.GetString(0)),
                reader.GetString(1),
                reader.GetInt64(2)));
        }
        return list;
    }

    /// <summary>读某天某软件的秒数；不存在返回 0。</summary>
    public long SecondsOf(DateOnly day, string appName)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT seconds FROM app_usage WHERE date = $d AND app_name = $a;";
        cmd.Parameters.AddWithValue("$d", FormatDay(day));
        cmd.Parameters.AddWithValue("$a", appName ?? string.Empty);
        object? v = cmd.ExecuteScalar();
        return v is null or DBNull ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    /// <summary>某一天的总秒数（所有软件之和）。</summary>
    public long TotalSecondsOfDay(DateOnly day)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(seconds), 0) FROM app_usage WHERE date = $d;";
        cmd.Parameters.AddWithValue("$d", FormatDay(day));
        object? v = cmd.ExecuteScalar();
        return v is null or DBNull ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    /// <summary>读某天的“不计入使用时长 / 无前台·未知”秒数；没有记录时返回全 0 行。</summary>
    public DayActivityRow ReadDayActivity(DateOnly day)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT filtered_seconds, unattributed_seconds FROM daily_activity WHERE date = $d;";
        cmd.Parameters.AddWithValue("$d", FormatDay(day));

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (reader.Read())
            return new DayActivityRow(day, reader.GetInt64(0), reader.GetInt64(1));
        return new DayActivityRow(day, 0, 0);
    }

    /// <summary>
    /// 读一段日期范围内（**含首尾**）的全部软件行（M10 的 7 天图用）。
    ///
    /// 为什么不让调用方循环 <see cref="ReadDay"/>：7 天就是 7 次连接 + 7 次查询，
    /// 而图表是**定期刷新**的（已落库数据，最多滞后约 15 秒）。一次查回来更省、也更不容易写错边界。
    ///
    /// 排序：先按日期升序（图要从旧到新），同日按秒数降序（便于调用方直接取"该日 Top"）。
    /// </summary>
    public IReadOnlyList<AppUsageRow> ReadUsageRange(DateOnly from, DateOnly to)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT date, app_name, seconds FROM app_usage
            WHERE date >= $f AND date <= $t
            ORDER BY date ASC, seconds DESC, app_name ASC;
            """;
        cmd.Parameters.AddWithValue("$f", FormatDay(from));
        cmd.Parameters.AddWithValue("$t", FormatDay(to));

        var list = new List<AppUsageRow>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new AppUsageRow(
                ParseDay(reader.GetString(0)),
                reader.GetString(1),
                reader.GetInt64(2)));
        }
        return list;
    }

    /// <summary>读一段日期范围内（**含首尾**）的全部"不计入使用时长 / 无前台·未知"行（M10）。</summary>
    public IReadOnlyList<DayActivityRow> ReadActivityRange(DateOnly from, DateOnly to)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT date, filtered_seconds, unattributed_seconds FROM daily_activity
            WHERE date >= $f AND date <= $t
            ORDER BY date ASC;
            """;
        cmd.Parameters.AddWithValue("$f", FormatDay(from));
        cmd.Parameters.AddWithValue("$t", FormatDay(to));

        var list = new List<DayActivityRow>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new DayActivityRow(
                ParseDay(reader.GetString(0)),
                reader.GetInt64(1),
                reader.GetInt64(2)));
        }
        return list;
    }

    /// <summary>读设置；不存在返回 null。</summary>
    public string? GetSetting(string key)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key ?? string.Empty);
        object? v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 删除设置（M9-2）。用于清掉限额的去重标记：用户改了限额之后必须能**再次**被提醒，
    /// 否则“把限额调低却再也不提醒”会让人以为功能坏了。
    /// </summary>
    public bool DeleteSetting(string key)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM settings WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key ?? string.Empty);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>读全部限额。</summary>
    public IReadOnlyList<LimitRow> ReadLimits()
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT scope, target, seconds FROM limits ORDER BY scope ASC, target ASC;";

        var list = new List<LimitRow>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new LimitRow(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        }
        return list;
    }

    /// <summary>列出用户表（自检用）。</summary>
    public IReadOnlyList<string> ListTables()
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";

        var list = new List<string>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetString(0));
        return list;
    }

    /// <summary><c>PRAGMA integrity_check</c>；正常返回 "ok"。</summary>
    public string IntegrityCheck()
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        object? v = cmd.ExecuteScalar();
        return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "(无返回)";
    }

    /// <summary>SQLite 库版本（<c>SELECT sqlite_version()</c>）。</summary>
    public string SqliteVersion()
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version();";
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "(未知)";
    }

    /// <summary>当前库的 <c>PRAGMA user_version</c>（schema 版本）。自检用。</summary>
    public int UserVersion()
    {
        using SqliteConnection conn = Open();
        return ReadUserVersion(conn);
    }

    // ================================================================ 内部

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        // 连接级属性，必须每次重设。
        Execute(conn, $"PRAGMA busy_timeout={_options.BusyTimeoutMs};");
        Execute(conn, "PRAGMA synchronous=NORMAL;");
        return conn;
    }

    private static int ReadUserVersion(SqliteConnection conn)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string FormatDay(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly ParseDay(string text) =>
        DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None);

    /// <summary>
    /// 清空连接池。**在删除数据库文件之前必须调用**：
    /// 池化连接会持有文件句柄，Windows 上会导致 <c>File.Delete</c> 失败。自检用。
    /// </summary>
    public static void ClearPools() => SqliteConnection.ClearAllPools();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // 本类每次操作都是短连接 + using，无长连接需要释放；
        // 池由 ClearPools/进程退出回收。这里不做额外动作，保持幂等。
    }
}
