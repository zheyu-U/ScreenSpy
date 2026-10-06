using System;
using System.Threading;
using ScreenSpy.Collector;

namespace ScreenSpy.Logging;

/// <summary>
/// 接线点：把 M3 的 <see cref="AppUsageBridge.Observed"/>（逐拍观测）接到原始活动日志上。
///
/// 与 M2 的 <c>SessionPauseBridge</c> 同样的策略：**旁听，不修改** M1/M2/M3 的既有逻辑。
/// 因此日志功能可以随时关掉而不影响计时与归属。
///
/// 线程模型：回调在**心跳线程**上，只做“折叠 + 入队”，不做任何 IO
/// （真正的写盘在 <see cref="ActivityLogWriter"/> 的后台线程上）。
///
/// <see cref="Dispose"/> 会把最后一段未关闭的状态刷出去 —— 否则每次退出都会丢掉尾段。
/// </summary>
internal sealed class AppActivityLogger : IDisposable
{
    private readonly AppUsageBridge _bridge;
    private readonly ActivityLogStateTracker _tracker;
    private readonly bool _subscribed;
    private int _disposed;

    private long _records;
    private long _failures;

    public AppActivityLogger(AppUsageBridge bridge, ActivityLogOptions? options = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        options = (options ?? ActivityLogOptions.CreateDefault()).Normalize();

        Writer = new ActivityLogWriter(options);
        _tracker = new ActivityLogStateTracker(options.IncludeWindowTitle);

        if (Writer.IsEnabled)
        {
            _bridge.Observed += OnObserved;
            _subscribed = true;
        }
    }

    public ActivityLogWriter Writer { get; }

    public bool IsEnabled => Writer.IsEnabled;

    /// <summary>已产出的记录数（= 已关闭的状态段数，不含尚未关闭的当前段）。</summary>
    public long Records => Interlocked.Read(ref _records);

    /// <summary>折叠过程中发生的异常次数（正常应为 0）。</summary>
    public long Failures => Interlocked.Read(ref _failures);

    public string? LastError { get; private set; }

    private void OnObserved(AppUsageObservation observation)
    {
        try
        {
            ActivityLogRecord? closed = _tracker.Observe(observation);
            if (closed is { } record) Emit(record);
        }
        catch (Exception ex)
        {
            // 日志异常绝不允许冒泡到心跳线程（那会影响计时）。
            Interlocked.Increment(ref _failures);
            LastError ??= ex.GetType().Name + ": " + ex.Message;
            Writer.RecordError(ex);
        }
    }

    private void Emit(in ActivityLogRecord record)
    {
        Interlocked.Increment(ref _records);
        Writer.TryWrite(record);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        if (_subscribed)
        {
            try { _bridge.Observed -= OnObserved; } catch { /* 忽略 */ }
        }

        try
        {
            ActivityLogRecord? tail = _tracker.Flush();
            if (tail is { } record) Emit(record);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failures);
            LastError ??= ex.GetType().Name + ": " + ex.Message;
        }

        Writer.Dispose();
    }
}
