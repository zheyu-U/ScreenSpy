using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ScreenSpy.Interop;
using ScreenSpy.Rendering;

namespace ScreenSpy.Desktop;

/// <summary>
/// 极简原生窗口宿主：注册窗口类、创建窗口、跑消息循环、按定时器触发重绘。
///
/// 为什么不用 WPF/WinForms 的窗口（《开发文档》§3.2）：
/// 桌面卡片必须是**顶层**分层窗口并逐像素半透明，且要能自锁 z 序；
/// 这些都需要直接操作 Win32 窗口样式与 UpdateLayeredWindow。
///
/// 来源：M0 验证（Spike）中经实测有效的实现，移植时移除了嵌入/诊断专用参数
/// （父窗口、WS_CHILD 转换、纯色背景刷、彩色探针）。
/// </summary>
internal sealed class NativeWindowHost : IDisposable
{
    private static readonly HashSet<string> RegisteredClasses = new(StringComparer.Ordinal);
    private static readonly object RegisterSync = new();

    private static readonly IntPtr TimerTick = new(1);
    private static readonly IntPtr TimerAutoClose = new(2);

    private readonly string _className;
    private readonly string _title;
    private readonly int _style;
    private readonly int _width;
    private readonly int _height;
    private readonly bool _createSurface;
    private readonly bool _quitOnDestroy;

    /// <summary>必须作为字段保持强引用，否则委托会在 RegisterClassEx 之后被 GC 回收。</summary>
    private readonly NativeMethods.WndProc _wndProc;

    /// <summary>
    /// 注册时真正使用的窗口类名：<c>基名#实例序号</c>。
    ///
    /// ⚠️ 为什么必须带序号（本轮修掉的一个隐患）：
    /// 窗口类一旦注册，**类过程就与“那一个实例的委托”绑死了**（<c>lpfnWndProc</c> 是实例方法指针）。
    /// 多个实例若复用同一个类名，第二个实例的所有消息都会走到**第一个实例**的 WndProc 上 ——
    /// 于是第二个窗口的 <c>OnTick</c>、命中测试、z 序自锁全部静默失效（不报错，只是不工作）。
    /// 单卡片的产品形态看不出来，但 M7「调整位置」会**反复创建 z 序探针**，必然踩到。
    /// 每实例独占一个类名，从结构上消除这一类错误。
    /// </summary>
    private readonly string _registeredClass;

    /// <summary>创建时的扩展样式（仅供诊断；运行期可被 <see cref="SetInteraction"/> 改写）。</summary>
    private int _exStyle;

    /// <summary>z 序自锁：外力（含系统）试图改变本窗口 z 序时，强制加上 SWP_NOZORDER。**可运行时切换**。</summary>
    private volatile bool _lockZOrder;

    /// <summary>
    /// 交互模式（M6 可拖动卡片）：
    /// 命中测试整窗返回 <c>HTCAPTION</c>，于是按住任意位置都能拖动窗口（拖动由系统代劳）。
    /// 关闭时（默认，桌面卡片形态）命中测试返回 <c>HTTRANSPARENT</c>，即鼠标穿透。
    /// **可运行时切换**（M7 的「调整位置」就是临时把它打开）。
    /// </summary>
    private volatile bool _interactive;

    /// <summary>窗口类名实例序号（进程内单调递增）。</summary>
    private static int _classSerial;

    /// <summary>一次性放行标志：<see cref="MoveZOrder"/> 执行期间允许真正改变 z 序。</summary>
    private bool _allowZOrder;

    private bool _disposed;

    /// <summary>窗口类**基名**（不含实例序号）。仅供诊断显示，不要拿它去匹配窗口。</summary>
    public string ClassBaseName => _className;

    public IntPtr Handle { get; private set; }

    public LayeredSurface Surface { get; private set; } = null!;

    /// <summary>定时器 tick 回调（在窗口线程上执行）。</summary>
    public Action? OnTick { get; set; }

    /// <summary>
    /// 自定义消息（<see cref="NativeMethods.WM_APP"/>）回调，在窗口线程上执行。
    /// 跨线程请求“改样式 / 切模式”走这条路，而不是从别的线程直接动窗口。
    /// </summary>
    public Action? OnUserMessage { get; set; }

    /// <summary>当前命中测试是否为“可拖动”（= 交互模式）。</summary>
    public bool Interactive => _interactive;

    /// <summary>当前是否开着 z 序自锁。</summary>
    public bool ZOrderLocked => _lockZOrder;

    public bool Closed { get; private set; }

    public NativeWindowHost(string className, string title, int exStyle, int style, int width, int height,
                            bool createSurface = true, bool lockZOrder = false, bool quitOnDestroy = true,
                            bool interactive = false)
    {
        _className = className;
        _title = title;
        _exStyle = exStyle;
        _style = style;
        _width = width;
        _height = height;
        _createSurface = createSurface;
        _lockZOrder = lockZOrder;
        _quitOnDestroy = quitOnDestroy;
        _interactive = interactive;
        _registeredClass = _className + "#" + System.Threading.Interlocked.Increment(ref _classSerial)
                                              .ToString(System.Globalization.CultureInfo.InvariantCulture);
        _wndProc = WndProcCore;
    }

    public void Create()
    {
        EnsureClassRegistered();

        Handle = NativeMethods.CreateWindowEx(
            _exStyle, _registeredClass, _title, _style,
            0, 0, _width, _height,
            IntPtr.Zero, IntPtr.Zero, NativeMethods.GetModuleHandle(null), IntPtr.Zero);

        if (Handle == IntPtr.Zero)
            throw new InvalidOperationException("CreateWindowEx 失败，Win32 错误码 " + Marshal.GetLastWin32Error());

        if (_createSurface)
            Surface = new LayeredSurface(_width, _height);
    }

    private void EnsureClassRegistered()
    {
        lock (RegisterSync)
        {
            if (RegisteredClasses.Contains(_registeredClass)) return;

            var wc = new NativeMethods.WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = NativeMethods.GetModuleHandle(null),
                hCursor = NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_ARROW),
                hbrBackground = IntPtr.Zero,
                lpszClassName = _registeredClass,
            };

            ushort atom = NativeMethods.RegisterClassEx(ref wc);
            if (atom == 0)
            {
                int err = Marshal.GetLastWin32Error();
                const int ERROR_CLASS_ALREADY_EXISTS = 1410;
                if (err != ERROR_CLASS_ALREADY_EXISTS)
                    throw new InvalidOperationException("RegisterClassEx 失败，错误码 " + err);
            }

            RegisteredClasses.Add(_registeredClass);
        }
    }

    /// <summary>
    /// 运行时切换交互形态（M7 的「嵌入 ⇄ 浮动」本质上就是这一件事）。
    ///
    /// 做三件事，配套不可省：
    ///  1. 翻转命中测试（<c>HTCAPTION</c> 可拖 ⇄ <c>HTTRANSPARENT</c> 穿透）与 z 序自锁两个开关；
    ///  2. 加/去 <c>WS_EX_TRANSPARENT</c> + <c>WS_EX_NOACTIVATE</c>（穿透 + 不抢焦点）；
    ///  3. <c>SWP_FRAMECHANGED</c> 让系统重读样式 —— **少了这一步，样式位已经变了但窗口行为没变**
    ///     （“API 全绿 ≠ 行为正确”，M0 的教训）。
    ///
    /// 刻意带 <c>SWP_NOZORDER</c>：z 序由 <see cref="MoveZOrder"/> 或外部的 z 序管理器单独负责，
    /// 改样式不该顺带把窗口挪到别处；也因此不会与自锁互相打架。
    /// </summary>
    public void SetInteraction(bool interactive, bool lockZOrder, bool clickThrough)
    {
        _interactive = interactive;
        _lockZOrder = lockZOrder;

        if (Handle == IntPtr.Zero) return;

        int ex = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
        const int mask = NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE;
        ex = clickThrough ? (ex | mask) : (ex & ~mask);
        _exStyle = ex;
        NativeMethods.SetWindowLong(Handle, NativeMethods.GWL_EXSTYLE, ex);

        NativeMethods.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED |
            NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_NOSENDCHANGING);
    }

    /// <summary>投递一条自定义消息（<see cref="NativeMethods.WM_APP"/>），由窗口线程回调 <see cref="OnUserMessage"/>。</summary>
    public void PostUserMessage()
    {
        if (Handle != IntPtr.Zero)
            NativeMethods.PostMessage(Handle, NativeMethods.WM_APP, IntPtr.Zero, IntPtr.Zero);
    }

    public void StartTimers(int tickMs, int autoCloseSeconds)
    {
        if (Handle == IntPtr.Zero) return;
        if (tickMs > 0) NativeMethods.SetTimer(Handle, TimerTick, (uint)tickMs, IntPtr.Zero);
        if (autoCloseSeconds > 0) NativeMethods.SetTimer(Handle, TimerAutoClose, (uint)(autoCloseSeconds * 1000), IntPtr.Zero);
    }

    /// <summary>重设 tick 间隔（SetTimer 同 ID 即重置间隔）。</summary>
    public void SetTickInterval(int tickMs)
    {
        if (Handle == IntPtr.Zero || tickMs <= 0) return;
        NativeMethods.SetTimer(Handle, TimerTick, (uint)tickMs, IntPtr.Zero);
    }

    /// <summary>
    /// 显式改变 z 序（临时放行自锁）。
    /// 带 SWP_NOSENDCHANGING，因此不会触发 WM_WINDOWPOSCHANGING 中的自锁逻辑。
    /// </summary>
    /// <param name="insertAfter">HWND_BOTTOM / HWND_TOP / HWND_TOPMOST / HWND_NOTOPMOST / 某窗口句柄。</param>
    public void MoveZOrder(IntPtr insertAfter)
    {
        if (Handle == IntPtr.Zero) return;

        bool previous = _allowZOrder;
        _allowZOrder = true;
        try
        {
            NativeMethods.SetWindowPos(Handle, insertAfter, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_NOSENDCHANGING);
        }
        finally
        {
            _allowZOrder = previous;
        }
    }

    public void RunMessageLoop()
    {
        while (true)
        {
            int ret = NativeMethods.GetMessage(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0);
            if (ret == 0) break;      // WM_QUIT
            if (ret == -1) break;     // 错误

            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }
    }

    public void RequestClose()
    {
        if (Handle != IntPtr.Zero)
            NativeMethods.PostMessage(Handle, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (Handle != IntPtr.Zero)
        {
            NativeMethods.KillTimer(Handle, TimerTick);
            NativeMethods.KillTimer(Handle, TimerAutoClose);
            NativeMethods.DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }

        Surface?.Dispose();
    }

    private IntPtr WndProcCore(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_TIMER:
                if (wParam == TimerAutoClose)
                {
                    RequestClose();
                    return IntPtr.Zero;
                }
                try
                {
                    OnTick?.Invoke();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[tick] " + ex);
                }
                return IntPtr.Zero;

            case NativeMethods.WM_APP:
                // 上层（别的线程）请求窗口线程做一件事（M7：切换嵌入/浮动模式）。
                try
                {
                    OnUserMessage?.Invoke();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[user-message] " + ex);
                }
                return IntPtr.Zero;

            case NativeMethods.WM_NCHITTEST:
                // 桌面卡片形态（默认）：命中测试一律返回“透明”，让点击落到下面的桌面图标上。
                // 与 WS_EX_TRANSPARENT 双重保险（不同场景下二者生效顺序不同）。
                //
                // 交互形态（M6 可拖动卡片）：整窗返回 HTCAPTION —— 按住任意位置即可拖动，
                // 这是无边框窗口最省事也最标准的拖动实现（拖动由系统代劳，无需自己跟 WM_MOUSEMOVE）。
                return new IntPtr(_interactive ? NativeMethods.HTCAPTION : NativeMethods.HTTRANSPARENT);

            case NativeMethods.WM_WINDOWPOSCHANGING:
                // z 序自锁：任何外力都无法把卡片从它所在层挤走。
                // 关键机制之一，来自对 Rainmeter 的求证（见 docs/M0-B+验证.md）。
                if (_lockZOrder && !_allowZOrder)
                {
                    var wp = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
                    wp.flags |= NativeMethods.SWP_NOZORDER;
                    Marshal.StructureToPtr(wp, lParam, false);
                    return IntPtr.Zero;
                }
                break;

            case NativeMethods.WM_CLOSE:
                NativeMethods.DestroyWindow(hWnd);
                return IntPtr.Zero;

            case NativeMethods.WM_DESTROY:
                Closed = true;
                if (_quitOnDestroy)
                    NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }
}
