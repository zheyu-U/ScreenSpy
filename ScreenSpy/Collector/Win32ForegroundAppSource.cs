using System;
using System.Diagnostics;
using System.Threading;
using ScreenSpy.Interop;

namespace ScreenSpy.Collector;

/// <summary>
/// Windows 实现：<c>GetForegroundWindow</c> → <c>GetWindowThreadProcessId</c> → 进程名（§5.4 的技术方案）。
///
/// 若干刻意的取舍：
///  * **不加进程名缓存**。心跳只有 1Hz，<see cref="Process.GetProcessById(int)"/> 的开销可以忽略；
///    而缓存会引入“pid 复用导致张冠李戴”的风险（把时长记到错误的软件头上），
///    这与本项目“宁可少计、不可错计”的取向相悖。
///  * **取不到进程名时不猜测**：归为 <see cref="ForegroundAppKind.Unknown"/> 并单独计数，
///    绝不按窗口标题/类名硬凑一个软件名（那会污染统计且难以察觉）。
///  * 自身的判定用 **pid 比较**（<see cref="Environment.ProcessId"/>），比比较进程名更可靠。
/// </summary>
internal sealed class Win32ForegroundAppSource : IForegroundAppSource
{
    private readonly int _selfProcessId;

    private long _samples;
    private long _processLookupFailures;
    private long _selfSamples;
    private string? _fatalError;

    public Win32ForegroundAppSource(int? selfProcessId = null)
    {
        _selfProcessId = selfProcessId ?? Environment.ProcessId;
    }

    /// <summary>采样总次数。</summary>
    public long Samples => Interlocked.Read(ref _samples);

    /// <summary>“拿到了窗口但取不到进程名”的次数（进程已退出 / 拒绝访问 / 系统进程）。</summary>
    public long ProcessLookupFailures => Interlocked.Read(ref _processLookupFailures);

    /// <summary>采样到“前台是本程序”的次数（正常情况下应≈0：卡片是 NOACTIVATE 的）。</summary>
    public long SelfSamples => Interlocked.Read(ref _selfSamples);

    public bool IsAvailable => _fatalError is null;

    public string? LastError { get; private set; }

    public ForegroundAppSample Sample()
    {
        Interlocked.Increment(ref _samples);

        IntPtr hwnd;
        try
        {
            hwnd = NativeMethods.GetForegroundWindow();
        }
        catch (Exception ex)
        {
            // 正常不会发生；记录为“致命”（数据源不可用），但绝不抛给心跳线程。
            _fatalError = ex.GetType().Name + ": " + ex.Message;
            LastError = _fatalError;
            Interlocked.Increment(ref _processLookupFailures);
            return ForegroundAppRules.Failed(IntPtr.Zero, 0, null, "GetForegroundWindow 抛异常：" + ex.Message);
        }

        // 没有任何窗口在前台（切换过程中、或前台被系统接管）：按“无前台”处理，不继承上一拍。
        if (hwnd == IntPtr.Zero)
            return ForegroundAppRules.Create(IntPtr.Zero, 0, null, null, null, isSelfProcess: false);

        string className = NativeMethods.ClassNameOf(hwnd);
        string title = NativeMethods.TitleOf(hwnd);

        int pid = 0;
        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _processLookupFailures);
            LastError = "GetWindowThreadProcessId: " + ex.Message;
            return ForegroundAppRules.Failed(hwnd, 0, className, LastError);
        }

        if (pid <= 0)
        {
            Interlocked.Increment(ref _processLookupFailures);
            LastError = $"GetWindowThreadProcessId 返回 pid={pid}";
            return ForegroundAppRules.Failed(hwnd, pid, className, LastError);
        }

        if (pid == _selfProcessId)
        {
            Interlocked.Increment(ref _selfSamples);
            return ForegroundAppRules.Create(hwnd, pid, "ScreenSpy", className, title, isSelfProcess: true);
        }

        string processName;
        try
        {
            using Process p = Process.GetProcessById(pid);
            processName = p.ProcessName;
        }
        catch (Exception ex)
        {
            // 常见且无害：进程在两次调用之间退出了 / 权限受限的系统进程。
            Interlocked.Increment(ref _processLookupFailures);
            LastError = $"{ex.GetType().Name}（pid={pid}）：{ex.Message}";
            return ForegroundAppRules.Failed(hwnd, pid, className, LastError);
        }

        if (processName.Length == 0)
        {
            Interlocked.Increment(ref _processLookupFailures);
            LastError = $"进程名为空（pid={pid}）";
            return ForegroundAppRules.Failed(hwnd, pid, className, LastError);
        }

        LastError = null;
        return ForegroundAppRules.Create(hwnd, pid, processName, className, title, isSelfProcess: false);
    }
}
