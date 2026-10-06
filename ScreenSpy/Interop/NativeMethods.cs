using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenSpy.Interop;

/// <summary>
/// 运行期所需的 Win32 互操作声明（M0 验证结论落地后的最小集合）。
///
/// 设计说明：
///  * 只保留“桌面卡片”真正用到的 API：原生窗口宿主、z 序读取、分层窗口逐像素合成
///    （UpdateLayeredWindow）、显示桌面检测（SetWinEventHook）。
///  * 已剔除 M0 阶段的嵌入 / 诊断专用 API（SetParent、0x052C、纯色探针、键鼠模拟等）：
///    路线 A（真嵌入）已在 Windows 11 26H2 上实测证伪，见 docs/M0验证结论.md。
/// </summary>
internal static class NativeMethods
{
    // ---------------------------------------------------------------- 常量

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    // 窗口基本样式
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_VISIBLE = 0x10000000;

    // 扩展样式
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_NOPARENTNOTIFY = 0x00000004;
    public const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    public const int WS_EX_COMPOSITED = 0x02000000;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    // SetWindowPos
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const uint SWP_NOSENDCHANGING = 0x0400;

    // 消息
    public const int WM_DESTROY = 0x0002;
    public const int WM_CLOSE = 0x0010;
    public const int WM_QUIT = 0x0012;
    public const int WM_WINDOWPOSCHANGING = 0x0046;
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_TIMER = 0x0113;

    /// <summary>
    /// 自定义消息起点（<c>WM_APP</c>）。用于**跨线程请求窗口做一件事**：
    /// 卡片在浮动/嵌入之间切换必须发生在卡片自己那条线程上（改样式、启停 z 序管理器、
    /// <c>SetWinEventHook</c> 都要消息泵），所以上层只投递一条消息，由窗口线程去执行。
    /// </summary>
    public const int WM_APP = 0x8000;

    public const int HTTRANSPARENT = -1;

    /// <summary>
    /// 命中测试结果“标题栏”。无边框窗口把它从 <c>WM_NCHITTEST</c> 返回，
    /// 就等于告诉系统“这一整块都是可拖动区域”，拖动由系统代劳（M6 可交互卡片）。
    /// </summary>
    public const int HTCAPTION = 2;

    // 显示
    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;

    // 系统度量
    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    // UpdateLayeredWindow
    public const int ULW_ALPHA = 0x00000002;
    public const byte AC_SRC_OVER = 0x00;
    public const byte AC_SRC_ALPHA = 0x01;

    // DIB
    public const uint DIB_RGB_COLORS = 0x0000;
    public const int BI_RGB = 0x0000;

    public const int IDC_ARROW = 32512;

    // WinEvent（“显示桌面”检测加速）
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    // DWM
    public const uint DWMWA_CLOAKED = 14;

    // 睡眠测试（M2 自检 --sleep-test）：唤醒定时器 + SE_SHUTDOWN_NAME 权限
    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    public const uint TOKEN_QUERY = 0x0008;
    public const uint SE_PRIVILEGE_ENABLED = 0x0002;
    public const uint WAIT_OBJECT_0 = 0x00000000;
    public const uint WAIT_TIMEOUT = 0x00000102;
    public const int ERROR_NOT_ALL_ASSIGNED = 1300;
    public const int ERROR_PRIVILEGE_NOT_HELD = 1314;
    public const string SE_SHUTDOWN_PRIVILEGE = "SeShutdownPrivilege";

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2</summary>
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    public static readonly IntPtr HWND_BOTTOM = new(1);
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    // ---------------------------------------------------------------- 结构体

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx;
        public int cy;
        public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    /// <summary>必须 Pack=1，共 4 字节。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    /// <summary>32bpp BI_RGB 情况下不需要色表；保留一个占位字段即可。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    /// <summary>
    /// GetLastInputInfo 的结果。cbSize 必须先填结构体大小；
    /// dwTime 是最后一次输入时的 32 位 tick（与 GetTickCount 同源，约 49.7 天回绕）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    /// <summary>LUID（用于 AdjustTokenPrivileges）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    /// <summary>
    /// TOKEN_PRIVILEGES 的“单个权限”形态。
    /// 只启一个权限时可以直接用这个简化布局（PrivilegeCount=1 + 一个 LUID_AND_ATTRIBUTES）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    // ---------------------------------------------------------------- 委托

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public delegate IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>WinEvent 回调。注意必须保持强引用，否则会被 GC 回收导致回调地址失效。</summary>
    public delegate void WinEventProc(IntPtr hWinEventHook, uint eventId, IntPtr hwnd,
                                      int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    // ---------------------------------------------------------------- user32

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    /// <summary>
    /// 改窗口样式。**运行时切换卡片模式靠它**（加/去 <c>WS_EX_TRANSPARENT</c> / <c>WS_EX_NOACTIVATE</c>）。
    ///
    /// ⚠️ 只调它是不够的：扩展样式改动必须紧跟一次带 <c>SWP_FRAMECHANGED</c> 的
    /// <c>SetWindowPos</c>，否则会出现“<c>GetWindowLong</c> 已经变了、窗口行为没变”的假成功
    /// —— 正是 M0 踩过的那类“只校验记账层”的坑。
    /// </summary>
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string? lpWindowName, int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>句柄是否仍然有效（窗口已销毁则返回 false）。自检用它确认 Dispose 真的销毁了窗口。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool IsWindow(IntPtr hWnd);

    /// <summary>
    /// 同步发送窗口消息（等对方处理完才返回）。
    /// 自检用它客观探测窗口行为（例如 <c>WM_NCHITTEST</c> 是否返回 <c>HTCAPTION</c>）。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool IsIconic(IntPtr hWnd);

    /// <summary>
    /// 锁定工作站（等价于 Win+L）。M2 自检的 <c>--lock-test</c> 用它验证真实锁屏链路；
    /// **必须显式开启**，绝不作为默认行为（解锁需要用户自己输入凭据）。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    /// <summary>
    /// 当前前台窗口（M3 §5.4 的入口）。返回 <see cref="IntPtr.Zero"/> 表示此刻没有任何窗口处于前台
    /// （例如切换过程中、或桌面被完全接管时）——调用方必须按"无前台"处理，而不是当成"上一个软件还在用"。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetForegroundWindow();

    /// <summary>窗口的根（桌面层级判定用）：<c>GA_ROOT</c>。</summary>
    public const uint GA_PARENT = 1;
    public const uint GA_ROOT = 2;
    public const uint GA_ROOTOWNER = 3;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetDpiForSystem();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>取得“最后一次用户输入”的时间戳（32 位 tick）。M1 空闲检测的数据源。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "UpdateLayeredWindow")]
    public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    /// <summary>子窗口场景：位置由 SetWindowPos 决定，pptDst 传 NULL。</summary>
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "UpdateLayeredWindow")]
    public static extern bool UpdateLayeredWindowNoPos(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
                                               WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("shcore.dll", SetLastError = true)]
    public static extern int SetProcessDpiAwareness(int value);

    // ---------------------------------------------------------------- dwmapi

    [DllImport("dwmapi.dll", SetLastError = true)]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute, out int pvAttribute, int cbAttribute);

    // ---------------------------------------------------------------- gdi32

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    // ---------------------------------------------------------------- kernel32

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    // ---------------------------------------------------------------- wtsapi32（M2 锁屏探测）

    /// <summary>WTS_CURRENT_SERVER_HANDLE</summary>
    public static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;

    /// <summary>WTS_CURRENT_SESSION</summary>
    public const uint WTS_CURRENT_SESSION = 0xFFFFFFFF;

    /// <summary>WTS_INFO_CLASS.WTSSessionId —— 返回一个 DWORD 会话号。</summary>
    public const uint WTS_INFO_SESSION_ID = 4;

    /// <summary>WTS_INFO_CLASS.WTSConnectState —— 返回一个 DWORD（WTS_CONNECTSTATE_CLASS），用作交叉校验。</summary>
    public const uint WTS_INFO_CONNECT_STATE = 8;

    /// <summary>WTS_INFO_CLASS.WTSSessionInfoEx —— 返回 WTSINFOEX（含 SessionFlags 锁屏标志）。</summary>
    public const uint WTS_INFO_SESSION_INFO_EX = 25;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSQuerySessionInformation(IntPtr hServer, uint sessionId, uint wtsInfoClass,
                                                         out IntPtr ppBuffer, out uint pBytesReturned);

    /// <summary>释放 WTSQuerySessionInformation 返回的缓冲区（不可用 FreeHGlobal）。</summary>
    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern void WTSFreeMemory(IntPtr pMemory);

    // ---------------------------------------------------------------- 睡眠测试（M2 自检 --sleep-test）

    /// <summary>
    /// 使系统进入睡眠 / 休眠。会**真实挂起本机**，仅供 <c>--sleep-test</c> 显式开启时使用。
    ///
    /// 参数约定：<paramref name="bHibernate"/>=false 表示“睡眠优先”（由电源策略决定实际是睡眠还是休眠）；
    /// <paramref name="bForce"/>=false 表示不强制（允许其它程序推迟）；
    /// <paramref name="bWakeupEventsDisabled"/>=<b>false</b> —— 必须保持唤醒事件可用，否则我们设的唤醒定时器无法唤醒系统。
    ///
    /// 权限：按文档要求调用方需具备 <c>SE_SHUTDOWN_NAME</c>（见 <see cref="TryEnableShutdownPrivilege"/>）。
    /// </summary>
    [DllImport("powrprof.dll", SetLastError = true)]
    public static extern bool SetSuspendState(bool bHibernate, bool bForce, bool bWakeupEventsDisabled);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWaitableTimer(IntPtr lpTimerAttributes, bool bManualReset, string? lpTimerName);

    /// <summary>
    /// <paramref name="pDueTime"/>：正数 = 绝对 UTC 时间（FILETIME，1601 起 100ns）；负数 = 相对时间。
    /// <paramref name="fResume"/>=<b>true</b> 是关键 —— 定时器到期时唤醒系统（配合 <see cref="SetSuspendState"/> 使用）。
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetWaitableTimer(IntPtr hTimer, ref long pDueTime, int lPeriod,
                                               IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, bool fResume);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
                                                    ref TOKEN_PRIVILEGES newState, uint bufferLength,
                                                    IntPtr previousState, IntPtr returnLength);

    /// <summary>
    /// 尽力为当前进程启用 <c>SE_SHUTDOWN_NAME</c>（<see cref="SetSuspendState"/> 的前置条件）。
    ///
    /// 注意 <see cref="AdjustTokenPrivileges"/> 的一个陷阱：即使权限分配失败它也可能返回 <c>true</c>，
    /// 必须再检查 <c>GetLastError() == ERROR_NOT_ALL_ASSIGNED</c> 才能判定是否真的启用成功。
    /// 失败不抛异常 —— 调用方仍可尝试 <see cref="SetSuspendState"/>，由返回码给出最终结论。
    /// </summary>
    public static bool TryEnableShutdownPrivilege(out string message)
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token))
            {
                message = $"OpenProcessToken 失败（{Marshal.GetLastWin32Error()}）";
                return false;
            }

            if (!LookupPrivilegeValue(null, SE_SHUTDOWN_PRIVILEGE, out LUID luid))
            {
                message = $"LookupPrivilegeValue 失败（{Marshal.GetLastWin32Error()}）";
                return false;
            }

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SE_PRIVILEGE_ENABLED,
            };

            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
            {
                message = $"AdjustTokenPrivileges 失败（{Marshal.GetLastWin32Error()}）";
                return false;
            }

            int last = Marshal.GetLastWin32Error();
            if (last == ERROR_NOT_ALL_ASSIGNED)
            {
                message = "权限不在当前令牌中（通常需以管理员身份运行）";
                return false;
            }

            message = "已启用";
            return true;
        }
        catch (Exception ex)
        {
            message = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    // ---------------------------------------------------------------- 便捷辅助

    /// <summary>把扩展样式位展开成可读名字，便于日志与“Alt+Tab 是否隐藏”之类的断言。</summary>
    public static string DescribeExStyle(int ex)
    {
        var parts = new List<string>();
        if ((ex & WS_EX_LAYERED) != 0) parts.Add("LAYERED");
        if ((ex & WS_EX_TRANSPARENT) != 0) parts.Add("TRANSPARENT");
        if ((ex & WS_EX_NOACTIVATE) != 0) parts.Add("NOACTIVATE");
        if ((ex & WS_EX_TOOLWINDOW) != 0) parts.Add("TOOLWINDOW");
        if ((ex & WS_EX_APPWINDOW) != 0) parts.Add("APPWINDOW");
        if ((ex & WS_EX_TOPMOST) != 0) parts.Add("TOPMOST");
        if ((ex & WS_EX_NOREDIRECTIONBITMAP) != 0) parts.Add("NOREDIRECTIONBITMAP");
        if ((ex & WS_EX_COMPOSITED) != 0) parts.Add("COMPOSITED");
        if ((ex & WS_EX_NOPARENTNOTIFY) != 0) parts.Add("NOPARENTNOTIFY");
        return parts.Count == 0 ? "(none)" : string.Join("|", parts);
    }

    public static string ClassNameOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "(null)";
        var sb = new StringBuilder(256);
        int n = GetClassName(hwnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString() : "(unknown)";
    }

    public static string TitleOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var sb = new StringBuilder(256);
        int n = GetWindowText(hwnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString() : "";
    }

    /// <summary>
    /// 顶层窗口在 z 序表中的下标：<c>0</c> = 最前（最上层），越大越靠后。找不到返回 <c>-1</c>。
    /// 这是“显示桌面”检测的唯一判据来源（不依赖任何推断）。
    /// </summary>
    public static int ZOrderIndex(IntPtr target)
    {
        if (target == IntPtr.Zero) return -1;

        int index = -1;
        int i = -1;
        EnumWindows((hwnd, _) =>
        {
            i++;
            if (hwnd == target)
            {
                index = i;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return index;
    }

    /// <summary>
    /// DWM 遮盖状态。<c>0</c> = 未被遮盖（会被 DWM 合成到屏幕）；非 0 = 不会被合成。
    /// 供“窗口被完全遮挡时暂停渲染”之类的后续优化使用。
    /// </summary>
    public static string CloakedOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "(null)";

        int v;
        int hr;
        try
        {
            hr = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out v, sizeof(int));
        }
        catch (Exception ex)
        {
            return "查询失败：" + ex.Message;
        }

        if (hr != 0) return $"查询失败 hr=0x{hr:X8}";
        if (v == 0) return "0（未遮盖 → 会被合成）";

        var parts = new List<string>();
        if ((v & 0x1) != 0) parts.Add("APP");
        if ((v & 0x2) != 0) parts.Add("SHELL");
        if ((v & 0x4) != 0) parts.Add("INHERITED(随父/所有者)");
        return $"{v}（被遮盖：{string.Join("|", parts)}）";
    }
}
