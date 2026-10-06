using System;
using System.IO;

namespace ScreenSpy.Logging;

/// <summary>
/// 原始活动日志（JSONL）的配置。
///
/// 定位：这是**原始证据层**，与 M4 的 SQLite（聚合数据）分工不同。
/// 它保留“最原始的进程名 / 窗口类名 / 窗口标题 / pid / hwnd”，
/// 用于事后回答聚合数据无法回答的问题（例如“这条 ‘未知应用’ 到底是什么”、
/// “那两个同名 python.exe 是不是同一个”、“UWP 宿主下真正跑的是谁”）。
///
/// 默认策略（经确认）：**产品默认开启、可关闭**。
/// 粒度：**仅变化时记录**（前台软件或“是否计入”发生变化时落一行），因此日常只有几百行/天。
/// </summary>
internal sealed class ActivityLogOptions
{
    /// <summary>是否启用。产品默认为 true。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>日志目录。产品默认 <c>%LOCALAPPDATA%\ScreenSpy\logs</c>。</summary>
    public string Directory { get; set; } = DefaultDirectory;

    /// <summary>
    /// 是否记录窗口标题。默认 true。
    ///
    /// ⚠️ 隐私提示：窗口标题可能包含网页标题、文档名、聊天对象名等敏感内容，
    /// 而本文件是**明文**存储。需要时可关闭（不影响进程名/类名等其余字段）。
    /// </summary>
    public bool IncludeWindowTitle { get; set; } = true;

    /// <summary>保留天数（按文件名中的日期清理更早的分片）。最小 1，默认 30。</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>写入队列容量。满则丢弃并计数 —— 宁可丢日志，绝不拖慢心跳。</summary>
    public int QueueCapacity { get; set; } = 4096;

    /// <summary>文件名前缀（最终形如 <c>app-activity-20261005.jsonl</c>）。</summary>
    public string FilePrefix { get; set; } = "app-activity";

    /// <summary>产品默认目录（M5 起由产品启动流程使用）。</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScreenSpy", "logs");

    public static ActivityLogOptions CreateDefault() => new ActivityLogOptions();

    public static ActivityLogOptions CreateDisabled() => new ActivityLogOptions { Enabled = false };

    /// <summary>把越界值收敛到可用范围（不做静默失败）。</summary>
    public ActivityLogOptions Normalize()
    {
        if (string.IsNullOrWhiteSpace(Directory)) Directory = DefaultDirectory;
        if (string.IsNullOrWhiteSpace(FilePrefix)) FilePrefix = "app-activity";
        if (QueueCapacity < 16) QueueCapacity = 16;
        if (RetentionDays < 1) RetentionDays = 30;
        return this;
    }

    public string Describe()
        => (Enabled ? "开启" : "关闭") +
           $"，目录={Directory}，保留={RetentionDays}天，标题={(IncludeWindowTitle ? "记录" : "不记录")}";
}
