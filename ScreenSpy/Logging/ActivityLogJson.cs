using System;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using ScreenSpy.Collector;

namespace ScreenSpy.Logging;

/// <summary>
/// 把 <see cref="ActivityLogRecord"/> 写成一行 JSON（JSONL）。
///
/// 用 <see cref="Utf8JsonWriter"/> 而不是手写字符串拼接：转义（引号、反斜杠、换行、控制字符）
/// 由库负责，窗口标题是最容易踩坑的自由文本，手写拼接几乎必然写坏。
///
/// 编码器用 <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>：中文标题保持可读，
/// 而不是被转成 <c>\uXXXX</c>。这里的 “Unsafe” 指的是**HTML 注入场景**不安全，
/// 本文件是本地 JSONL、不以任何形式嵌入 HTML，因此可读性收益大于风险。
/// </summary>
internal static class ActivityLogJson
{
    public static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        SkipValidation = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Write(Utf8JsonWriter w, in ActivityLogRecord r, bool includeWindowTitle)
    {
        w.WriteStartObject();

        w.WriteString("ts", r.StartedLocal.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
        w.WriteString("day", r.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        w.WriteNumber("dur_ms", r.DurationMs);
        w.WriteNumber("ticks", r.Ticks);
        w.WriteNumber("seq_from", r.SequenceFrom);
        w.WriteNumber("seq_to", r.SequenceTo);
        w.WriteBoolean("counted", r.Counted);
        w.WriteString("reason", ReasonName(r.Reason));
        w.WriteString("kind", KindName(r.Kind));

        // 原始证据：进程名/类名/句柄/pid —— 这是本日志存在的理由，始终记录。
        w.WriteString("proc", r.ProcessName);
        w.WriteString("cls", r.ClassName);
        w.WriteNumber("pid", r.ProcessId);
        w.WriteString("hwnd", "0x" + r.Hwnd.ToInt64().ToString("X", CultureInfo.InvariantCulture));

        // 标题可因隐私关闭（字段一并省略，避免“有键无值”的歧义）。
        if (includeWindowTitle)
        {
            w.WriteString("title", r.Title);
            w.WriteString("title_last", r.TitleLast);
            w.WriteNumber("title_changes", r.TitleChanges);
        }

        w.WriteString("display", r.DisplayName);
        w.WriteString("merge_key", r.MergeKey);

        if (r.Diagnostic is { Length: > 0 } diag)
            w.WriteString("diag", diag);

        w.WriteEndObject();
    }

    public static string ReasonName(ActivityLogReason reason) => reason switch
    {
        ActivityLogReason.Active => "active",
        ActivityLogReason.Idle => "idle",
        ActivityLogReason.Paused => "paused",
        _ => "gap",
    };

    public static string KindName(ForegroundAppKind kind) => kind switch
    {
        ForegroundAppKind.None => "none",
        ForegroundAppKind.App => "app",
        ForegroundAppKind.Self => "self",
        ForegroundAppKind.Desktop => "desktop",
        ForegroundAppKind.Shell => "shell",
        ForegroundAppKind.LockScreen => "lockscreen",
        ForegroundAppKind.UwpHost => "uwp_host",
        _ => "unknown",
    };
}
