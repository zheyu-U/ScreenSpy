using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ScreenSpy.Collector;
using ScreenSpy.Logging;
using ScreenSpy.Storage;

namespace ScreenSpy.AppHost;

/// <summary>
/// 产品启动参数（本次接入的“可覆盖开关”）。
///
/// 设计原则：
///  * **默认即产品形态**：不带任何参数启动时，使用产品默认路径
///    （<c>%LOCALAPPDATA%\ScreenSpy\data.db</c> 与 <c>...\logs</c>）、默认阈值与间隔。
///  * **参数只用于覆盖**：便于开发期隔离验证（例如把库与日志指到项目内目录），
///    而**不改变**默认行为。因此“不带参数跑起来就是正式形态”。
///  * **未知参数不静默吞掉**：收集到 <see cref="Unknown"/> 供上层显示为警告，
///    否则把 <c>--data-dir</c> 拼错时会安静地按默认路径写数据（很难发现）。
/// </summary>
internal sealed class StartupOptions
{
    /// <summary>是否启用 SQLite 聚合持久化。默认 true（§5.6）。</summary>
    public bool StoreEnabled { get; set; } = true;

    /// <summary>是否启用原始活动日志（JSONL）。默认 true（此前已确认“产品默认开启”）。</summary>
    public bool RawLogEnabled { get; set; } = true;

    /// <summary>
    /// 是否显示桌面卡片（M6）。默认 true —— 卡片是本项目的门面，不该需要额外操作才会出现。
    /// <c>--no-card</c> 关掉它，便于开发期跑“只有托盘”的对照形态。
    /// （卡片显示与否之后还能在托盘菜单里临时切换；本开关决定的是**启动时**的初值。）
    /// </summary>
    public bool CardEnabled { get; set; } = true;

    /// <summary>数据库文件路径。默认 <see cref="StorageOptions.DefaultDatabasePath"/>。</summary>
    public string DatabasePath { get; set; } = StorageOptions.DefaultDatabasePath;

    /// <summary>原始活动日志目录。默认 <see cref="ActivityLogOptions.DefaultDirectory"/>。</summary>
    public string LogDirectory { get; set; } = ActivityLogOptions.DefaultDirectory;

    /// <summary>空闲阈值（挂机判定）。默认 300s（§5.2）。</summary>
    public TimeSpan IdleThreshold { get; set; } = ActivityRules.DefaultIdleThreshold;

    /// <summary>心跳间隔。默认 1s（§5.1）。</summary>
    public TimeSpan Heartbeat { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>批量落库间隔。默认 15s（§5.6）。</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>会话（锁屏）探测自愈间隔。默认 5s；<see cref="TimeSpan.Zero"/> 表示关闭周期探测。</summary>
    public TimeSpan SessionProbeInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>无法识别的参数（供上层警告，不静默丢弃）。</summary>
    public List<string> Unknown { get; } = new();

    /// <summary>生成 M4 的存储配置（已收敛越界值）。</summary>
    public StorageOptions ToStorageOptions() => new StorageOptions
    {
        Enabled = StoreEnabled,
        DatabasePath = DatabasePath,
        FlushInterval = FlushInterval,
    }.Normalize();

    /// <summary>生成原始日志配置（已收敛越界值）。</summary>
    public ActivityLogOptions ToLogOptions() => new ActivityLogOptions
    {
        Enabled = RawLogEnabled,
        Directory = LogDirectory,
    }.Normalize();

    /// <summary>
    /// 解析命令行。
    ///
    /// 支持的开关（均可省略）：
    /// <code>
    ///   --data-dir=&lt;目录&gt;       库放到 &lt;目录&gt;\data.db
    ///   --db=&lt;文件&gt;            直接指定库文件
    ///   --log-dir=&lt;目录&gt;       原始日志目录
    ///   --no-store             关闭 SQLite 落库
    ///   --no-raw-log           关闭原始活动日志
    ///   --no-card              启动时不显示桌面卡片（M6）
    ///   --idle-threshold=&lt;秒&gt;  空闲阈值（默认 300）
    ///   --heartbeat=&lt;毫秒&gt;     心跳间隔（默认 1000，最小 100）
    ///   --flush-ms=&lt;毫秒&gt;      落库间隔（默认 15000，最小 1000）
    ///   --probe-ms=&lt;毫秒&gt;      会话探测间隔（默认 5000；0 = 关闭周期探测）
    /// </code>
    /// </summary>
    public static StartupOptions Parse(string[]? args)
    {
        var options = new StartupOptions();
        if (args is null) return options;

        foreach (string raw in args)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            string arg = raw.Trim();
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                options.Unknown.Add(arg);
                continue;
            }

            string key, value;
            int eq = arg.IndexOf('=');
            if (eq < 0)
            {
                key = arg;
                value = string.Empty;
            }
            else
            {
                key = arg.Substring(0, eq);
                value = arg.Substring(eq + 1);
            }

            switch (key.ToLowerInvariant())
            {
                case "--no-store":
                    options.StoreEnabled = false;
                    break;

                case "--no-raw-log":
                    options.RawLogEnabled = false;
                    break;

                case "--no-card":
                    options.CardEnabled = false;
                    break;

                case "--db":
                    if (value.Length > 0) options.DatabasePath = value;
                    else options.Unknown.Add(arg);
                    break;

                case "--data-dir":
                    if (value.Length > 0) options.DatabasePath = Path.Combine(value, "data.db");
                    else options.Unknown.Add(arg);
                    break;

                case "--log-dir":
                    if (value.Length > 0) options.LogDirectory = value;
                    else options.Unknown.Add(arg);
                    break;

                case "--idle-threshold":
                    if (TryInt(value, out int idle) && idle > 0) options.IdleThreshold = TimeSpan.FromSeconds(idle);
                    else options.Unknown.Add(arg);
                    break;

                case "--heartbeat":
                    if (TryInt(value, out int hb) && hb >= 100) options.Heartbeat = TimeSpan.FromMilliseconds(hb);
                    else options.Unknown.Add(arg);
                    break;

                case "--flush-ms":
                    if (TryInt(value, out int flush) && flush >= 1000) options.FlushInterval = TimeSpan.FromMilliseconds(flush);
                    else options.Unknown.Add(arg);
                    break;

                case "--probe-ms":
                    if (TryInt(value, out int probe) && probe >= 0)
                        options.SessionProbeInterval = probe == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(probe);
                    else options.Unknown.Add(arg);
                    break;

                default:
                    options.Unknown.Add(arg);
                    break;
            }
        }

        return options;
    }

    private static bool TryInt(string value, out int result)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    public string Describe()
        => $"库={(StoreEnabled ? DatabasePath : "关闭")}，" +
           $"日志={(RawLogEnabled ? LogDirectory : "关闭")}，" +
           $"卡片={(CardEnabled ? "显示" : "关闭")}，" +
           $"空闲阈值={IdleThreshold.TotalSeconds:F0}s，心跳={Heartbeat.TotalMilliseconds:F0}ms，" +
           $"flush={FlushInterval.TotalSeconds:F0}s，探测={SessionProbeInterval.TotalSeconds:F0}s";
}
