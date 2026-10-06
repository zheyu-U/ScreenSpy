using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace ScreenSpy.Logging;

/// <summary>
/// 原始活动日志的落盘器：**有界队列 + 单个后台写线程 + 按天分片 + 保留期清理**。
///
/// 三条硬约束（都是为避免“日志拖垮计时”这一最坏结果）：
///  1. <see cref="TryWrite"/> **永不阻塞**：心跳线程只做入队；队列满就丢弃并计数（<see cref="Dropped"/>）。
///  2. **永不抛异常**：所有异常都转成计数与 <see cref="LastError"/>，绝不让 IO 问题冒泡到心跳线程。
///  3. 写线程是**后台线程**，不会阻止进程退出。
///
/// 分片按**记录自身的日期**（而不是写盘时刻的日期）命名，因此跨天时先关闭的尾段
/// 仍会落到正确的那一天文件里。
/// </summary>
internal sealed class ActivityLogWriter : IDisposable
{
    private readonly ActivityLogOptions _options;
    private readonly BlockingCollection<ActivityLogRecord>? _queue;
    private readonly Thread? _thread;
    private readonly ArrayBufferWriter<byte> _buffer = new(4096);
    private readonly object _streamGate = new();

    private FileStream? _stream;
    private DateOnly _currentDay;
    private string? _currentPath;

    private long _written;
    private long _dropped;
    private long _errors;
    private string? _lastError;
    private int _disposed;

    public ActivityLogWriter(ActivityLogOptions options)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Normalize();
        if (!_options.Enabled) return;

        _queue = new BlockingCollection<ActivityLogRecord>(_options.QueueCapacity);
        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "screenspy-activity-log",
        };
        _thread.Start();
    }

    public bool IsEnabled => _queue is not null;

    public ActivityLogOptions Options => _options;

    /// <summary>已成功写出的行数。</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>因队列满被丢弃的行数（>0 说明磁盘跟不上，日志不完整但计时未受影响）。</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>写盘过程中遇到的异常次数。</summary>
    public long Errors => Interlocked.Read(ref _errors);

    public string? LastError => _lastError;

    /// <summary>当前分片文件的完整路径（尚未写出任何行时为 null）。</summary>
    public string? CurrentPath
    {
        get { lock (_streamGate) return _currentPath; }
    }

    /// <summary>入队一行。返回 false 表示未入队（未启用或队列满）；**绝不阻塞、绝不抛异常**。</summary>
    public bool TryWrite(in ActivityLogRecord record)
    {
        BlockingCollection<ActivityLogRecord>? queue = _queue;
        if (queue is null) return false;

        try
        {
            if (queue.TryAdd(record, 0)) return true;
            Interlocked.Increment(ref _dropped);
            return false;
        }
        catch (Exception ex)
        {
            RecordError(ex);
            return false;
        }
    }

    /// <summary>供上层报告自身异常（保持“错误可见”，而不是悄悄吞掉）。</summary>
    public void RecordError(Exception ex)
    {
        Interlocked.Increment(ref _errors);
        _lastError ??= ex.GetType().Name + ": " + ex.Message;
    }

    private void Pump()
    {
        BlockingCollection<ActivityLogRecord> queue = _queue!;
        try
        {
            foreach (ActivityLogRecord record in queue.GetConsumingEnumerable())
            {
                try
                {
                    WriteOne(record);
                    Interlocked.Increment(ref _written);
                }
                catch (Exception ex)
                {
                    RecordError(ex);
                }
            }
        }
        catch (Exception ex)
        {
            RecordError(ex);
        }
    }

    private void WriteOne(in ActivityLogRecord record)
    {
        lock (_streamGate)
        {
            if (_stream is null || _currentDay != record.Day)
            {
                CloseStreamLocked();
                OpenStreamLocked(record.Day);
            }

            _buffer.Clear();
            using (var writer = new Utf8JsonWriter(_buffer, ActivityLogJson.WriterOptions))
            {
                ActivityLogJson.Write(writer, record, _options.IncludeWindowTitle);
            }

            _stream!.Write(_buffer.WrittenSpan);
            _stream.WriteByte((byte)'\n');   // JSONL 约定用 LF，便于跨工具处理
            _stream.Flush();                 // 行数很少（仅变化时），逐行刷盘换取崩溃时的可读性
        }
    }

    private void OpenStreamLocked(DateOnly day)
    {
        System.IO.Directory.CreateDirectory(_options.Directory);

        string path = Path.Combine(_options.Directory,
            _options.FilePrefix + "-" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");

        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 8192);
        _currentDay = day;
        _currentPath = path;

        PruneOldLocked(day);
    }

    private void CloseStreamLocked()
    {
        try { _stream?.Dispose(); } catch (Exception ex) { RecordError(ex); }
        _stream = null;
    }

    /// <summary>删除早于保留期的分片（按文件名中的日期判断，不依赖文件时间戳）。</summary>
    private void PruneOldLocked(DateOnly today)
    {
        try
        {
            if (!System.IO.Directory.Exists(_options.Directory)) return;

            DateOnly cutoff = today.AddDays(-_options.RetentionDays);
            string prefix = _options.FilePrefix + "-";

            foreach (string file in System.IO.Directory.EnumerateFiles(_options.Directory, _options.FilePrefix + "-*.jsonl"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                string stamp = name[prefix.Length..];
                if (stamp.Length != 8) continue;
                if (!DateOnly.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture,
                                           DateTimeStyles.None, out DateOnly day)) continue;

                if (day < cutoff)
                {
                    try { File.Delete(file); }
                    catch (Exception ex) { RecordError(ex); }
                }
            }
        }
        catch (Exception ex)
        {
            RecordError(ex);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        BlockingCollection<ActivityLogRecord>? queue = _queue;
        if (queue is not null)
        {
            try { queue.CompleteAdding(); } catch { /* 忽略 */ }
            try { _thread?.Join(TimeSpan.FromSeconds(3)); } catch { /* 忽略 */ }
        }

        lock (_streamGate)
        {
            CloseStreamLocked();   // 保留 _currentPath，供调用方在 Dispose 之后打印
        }

        try { queue?.Dispose(); } catch { /* 忽略 */ }
    }
}
