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
    ///
    /// 迁移是**纯增量**的：<c>CreateSchema</c> 全部用 <c>IF NOT EXISTS</c>，
    /// 因此旧库（v1）升级只是“多建一张空表”，不会改动或丢失任何既有数据。
    /// </summary>
    public const int SchemaVersion = 2;

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
