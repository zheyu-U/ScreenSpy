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
    private readonly int _exStyle;
    private readonly int _style;
    private readonly int _width;
    private readonly int _height;
    private readonly bool _createSurface;
    private readonly bool _quitOnDestroy;

    /// <summary>必须作为字段保持强引用，否则委托会在 RegisterClassEx 之后被 GC 回收。</summary>
    private readonly NativeMethods.WndProc _wndProc;

    /// <summary>z 序自锁：外力（含系统）试图改变本窗口 z 序时，强制加上 SWP_NOZORDER。</summary>
    private readonly bool _lockZOrder;

    /// <summary>
    /// 交互模式（M6 可拖动卡片）：
    /// 命中测试整窗返回 <c>HTCAPTION</c>，于是按住任意位置都能拖动窗口（拖动由系统代劳）。
    /// 关闭时（默认，桌面卡片形态）命中测试返回 <c>HTTRANSPARENT</c>，即鼠标穿透。
    /// </summary>
    private readonly bool _interactive;

    /// <summary>一次性放行标志：<see cref="MoveZOrder"/> 执行期间允许真正改变 z 序。</summary>
    private bool _allowZOrder;

    private bool _disposed;

    public IntPtr Handle { get; private set; }

    public LayeredSurface Surface { get; private set; } = null!;

    /// <summary>定时器 tick 回调（在窗口线程上执行）。</summary>
    public Action? OnTick { get; set; }

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
        _wndProc = WndProcCore;
    }

    public void Create()
    {
        EnsureClassRegistered();

        Handle = NativeMethods.CreateWindowEx(
            _exStyle, _className, _title, _style,
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
            if (RegisteredClasses.Contains(_className)) return;

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
                lpszClassName = _className,
            };

            ushort atom = NativeMethods.RegisterClassEx(ref wc);
            if (atom == 0)
            {
                int err = Marshal.GetLastWin32Error();
                const int ERROR_CLASS_ALREADY_EXISTS = 1410;
                if (err != ERROR_CLASS_ALREADY_EXISTS)
                    throw new InvalidOperationException("RegisterClassEx 失败，错误码 " + err);
            }

            RegisteredClasses.Add(_className);
        }
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
