using System;
using System.Drawing;
using ScreenSpy.Interop;
using ScreenSpy.Rendering;

namespace ScreenSpy.Desktop;

/// <summary>
/// 桌面卡片的对外宿主：把“顶层分层窗口 + z 序自锁 + 显示桌面复位 + GDI+ 渲染”组装成一个可用对象。
///
/// 对应 M0 验证里的“路线 B+”。它必须运行在**带消息循环的线程**上
/// （SetWinEventHook 的回调依赖消息泵），典型用法：
/// <code>
/// var thread = new Thread(() => { using var card = new DesktopCardHost(x, y); card.Start(); card.RunMessageLoop(); });
/// </code>
/// </summary>
internal sealed class DesktopCardHost : IDisposable
{
    public const int DefaultWidth = 400;
    public const int DefaultHeight = 360;

    /// <summary>平时采样间隔；进入“显示桌面”后加密到 100ms（对应 Rainmeter 的 INTERVAL_RESTOREWINDOWS）。</summary>
    private const int PollNormalMs = 250;
    private const int PollShowDesktopMs = 100;

    private const int DefaultRedrawMs = 1000;

    /// <summary>
    /// 客观判定用的不透明标记色：ULW 在 alpha=255 时是“直接覆盖”，
    /// 因此能在截屏里被精确定位 —— 这是“像素真的上了屏”的唯一证据。
    /// 仅在调试 / 自检时绘制，产品默认不画。
    /// </summary>
    public static readonly Color DebugMarkerColor = Color.FromArgb(255, 255, 0, 170);

    public static readonly Rectangle DefaultDebugMarker = new(10, 10, 24, 24);

    private readonly int _x;
    private readonly int _y;
    private readonly int _width;
    private readonly int _height;
    private readonly Rectangle? _debugMarker;
    private readonly CardRenderer _renderer = new();

    private NativeWindowHost? _host;
    private DesktopZOrderManager? _zorder;
    private int _tick;
    private int _sinceRedrawMs;
    private int _redrawMs = DefaultRedrawMs;
    private int _frames;

    public DesktopCardHost(int x, int y, int width = DefaultWidth, int height = DefaultHeight,
                           Rectangle? debugMarker = null)
    {
        _x = x;
        _y = y;
        _width = width;
        _height = height;
        _debugMarker = debugMarker;
    }

    /// <summary>卡片数据来源。参数为刷新计数（每秒 +1）。缺省为空白卡片。</summary>
    public Func<int, CardModel>? ModelFactory { get; set; }

    public IntPtr Handle => _host?.Handle ?? IntPtr.Zero;
    public bool IsStarted => _host is not null;
    public bool ShowDesktop => _zorder?.ShowDesktop ?? false;
    public bool Elevated => _zorder?.Elevated ?? false;
    public bool HookInstalled => _zorder?.HookInstalled ?? false;
    public int Polls => _zorder?.Polls ?? 0;
    public int Transitions => _zorder?.Transitions ?? 0;
    public int Frames => _frames;

    public int X => _x;
    public int Y => _y;
    public int Width => _width;
    public int Height => _height;

    /// <summary>创建卡片窗口、首次绘制并启动 z 序管理。必须在拥有消息循环的线程上调用。</summary>
    /// <param name="autoCloseSeconds">大于 0 时，窗口在 N 秒后自动关闭（便于自动化验证）。</param>
    /// <param name="redrawMs">重绘间隔（毫秒）。</param>
    public void Start(int autoCloseSeconds = 0, int redrawMs = DefaultRedrawMs)
    {
        if (_host is not null) throw new InvalidOperationException("DesktopCardHost 已启动。");

        _redrawMs = redrawMs <= 0 ? DefaultRedrawMs : redrawMs;

        // 关键样式：
        //   LAYERED       —— 使用 UpdateLayeredWindow 做逐像素 alpha；
        //   TRANSPARENT   —— 鼠标穿透；
        //   NOACTIVATE    —— 不抢焦点；
        //   TOOLWINDOW    —— 不出现在任务栏与 Alt+Tab。
        int exStyle = NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT
                    | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;

        var host = new NativeWindowHost("ScreenSpyDesktopCard", "ScreenSpy 桌面卡片",
                                        exStyle, NativeMethods.WS_POPUP, _width, _height, lockZOrder: true);
        host.Create();
        _host = host;

        // 顶层分层窗口用 pptDst 定位，这里先把窗口摆到目标位置并显示（不给 WS_VISIBLE，故需 SHOWWINDOW）。
        NativeMethods.SetWindowPos(host.Handle, NativeMethods.HWND_BOTTOM, _x, _y, _width, _height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        Draw();

        var zorder = new DesktopZOrderManager(host);
        _zorder = zorder;
        zorder.Start();

        // 1) 采样 + 维护 z 序；2) 按“实际间隔累计”触发重绘（不依赖定时器精度）。
        host.OnTick += () =>
        {
            zorder.Poll();

            _sinceRedrawMs += zorder.ShowDesktop ? PollShowDesktopMs : PollNormalMs;
            if (_sinceRedrawMs >= _redrawMs)
            {
                _sinceRedrawMs = 0;
                _tick++;
                Draw();
            }
        };

        // 进入 / 退出“显示桌面”时切换采样密度。
        bool lastShow = zorder.ShowDesktop;
        host.OnTick += () =>
        {
            if (zorder.ShowDesktop != lastShow)
            {
                lastShow = zorder.ShowDesktop;
                host.SetTickInterval(lastShow ? PollShowDesktopMs : PollNormalMs);
            }
        };

        host.StartTimers(PollNormalMs, autoCloseSeconds);
    }

    private void Draw()
    {
        NativeWindowHost? host = _host;
        if (host is null) return;

        CardModel model = ModelFactory?.Invoke(_tick) ?? new CardModel();
        model.Tick = _tick;

        _renderer.Draw(host.Surface.Graphics, host.Surface.Size, model);

        if (_debugMarker is { } marker)
        {
            using var brush = new SolidBrush(DebugMarkerColor);
            host.Surface.Graphics.FillRectangle(brush, marker);
        }

        host.Surface.Present(host.Handle, _x, _y);
        _frames++;
    }

    public void RunMessageLoop() => _host?.RunMessageLoop();

    public void RequestClose() => _host?.RequestClose();

    public string DescribeExStyle()
    {
        IntPtr h = Handle;
        if (h == IntPtr.Zero) return "(未创建)";
        int ex = NativeMethods.GetWindowLong(h, NativeMethods.GWL_EXSTYLE);
        return $"0x{ex:X8}（{NativeMethods.DescribeExStyle(ex)}）";
    }

    public void Dispose()
    {
        try { _zorder?.Dispose(); } catch { /* 忽略销毁期异常 */ }
        try { _host?.Dispose(); } catch { /* 忽略销毁期异常 */ }
        _zorder = null;
        _host = null;
    }
}
