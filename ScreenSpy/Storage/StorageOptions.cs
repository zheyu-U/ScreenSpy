using System;
using System.IO;

namespace ScreenSpy.Storage;

/// <summary>
/// M4 的存储配置（开发文档 §5.6）。
///
/// 与 <c>ActivityLogOptions</c>（原始 JSONL 证据层）分工不同：
/// 这里是**聚合持久层** —— 只存“每天 × 每软件”的累计秒数、设置与限额，
/// 用于重启后继续累计、供卡片/主界面/限额引擎查询。
///
/// 关键参数是 <see cref="FlushInterval"/>：文档明确要求**不要每秒写盘**
/// （会疯狂写 SSD），改为每 15 秒批量落库一次。
/// </summary>
internal sealed class StorageOptions
{
    /// <summary>是否启用持久化。产品默认为 true。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 数据库文件路径。默认 <c>%LOCALAPPDATA%\ScreenSpy\data.db</c>。
    ///
    /// 注意：刻意用 **Local**（而非 Roaming）。开发文档 §5.6 原文写的是 <c>%AppData%</c>，
    /// 但 Roaming 在域账户环境会被同步到服务器 —— 一个不断被写入的 SQLite 文件
    /// 被漫游同步既有损坏风险、也无必要；且本项目原始日志目录用的也是 Local，保持一致。
    /// （2026-10-05 已确认改为 Local，并同步修订文档。）
    /// </summary>
    public string DatabasePath { get; set; } = DefaultDatabasePath;

    /// <summary>批量落库间隔。默认 15 秒（§5.6）。</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>落库队列容量。满则丢弃并计数 —— 宁可丢数据，绝不拖慢心跳。</summary>
    public int QueueCapacity { get; set; } = 8192;

    /// <summary>SQLite 忙等待超时（毫秒）。多连接并发时避免直接抛 SQLITE_BUSY。</summary>
    public int BusyTimeoutMs { get; set; } = 5000;

    /// <summary>是否启用 WAL 日志模式（读写并发更友好，且崩溃后易恢复）。</summary>
    public bool UseWal { get; set; } = true;

    /// <summary>产品默认数据库路径。</summary>
    public static string DefaultDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScreenSpy", "data.db");

    public static StorageOptions CreateDefault() => new StorageOptions();

    public static StorageOptions CreateDisabled() => new StorageOptions { Enabled = false };

    /// <summary>把越界值收敛到可用范围。</summary>
    public StorageOptions Normalize()
    {
        if (string.IsNullOrWhiteSpace(DatabasePath)) DatabasePath = DefaultDatabasePath;

        if (FlushInterval < TimeSpan.FromSeconds(1)) FlushInterval = TimeSpan.FromSeconds(1);
        if (FlushInterval > TimeSpan.FromMinutes(10)) FlushInterval = TimeSpan.FromMinutes(10);

        if (QueueCapacity < 16) QueueCapacity = 16;

        if (BusyTimeoutMs < 0) BusyTimeoutMs = 0;
        if (BusyTimeoutMs > 60000) BusyTimeoutMs = 60000;

        return this;
    }

    public string Describe()
        => (Enabled ? "开启" : "关闭") +
           $"，库={DatabasePath}，flush={FlushInterval.TotalSeconds:F0}s，WAL={(UseWal ? "是" : "否")}";
}
