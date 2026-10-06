using System;
using System.Threading;
using ScreenSpy.Desktop;
using ScreenSpy.Interop;
using ScreenSpy.Rendering;

namespace ScreenSpy.Widget;

/// <summary>
/// 组件卡片窗口（M6）：把「原生分层窗口 + GDI+ 渲染」装成一个**可交互的普通顶层窗口**。
///
/// 与 M0 的 <see cref="DesktopCardHost"/> 的关键差别（这正是 M6 与 M7 的分界）：
/// <list type="table">
///   <item><term>M6（本类）</term><description>可交互普通窗口：**不**加
///     <c>WS_EX_TRANSPARENT</c>／<c>WS_EX_NOACTIVATE</c>，**不加** z 序自锁，
///     命中测试返回 <c>HTCAPTION</c> → 可以拖动、可以被其它窗口覆盖。</description></item>
///   <item><term>M7（DesktopCardHost）</term><description>桌面分层形态：鼠标穿透、不抢焦点、
///     z 序自锁、显示桌面复位。</description></item>
/// </list>
/// 两者共用同一套渲染层（<see cref="CardRenderer"/> + <see cref="LayeredSurface"/> + <see cref="CardModel"/>），
/// 因此 M7 只是换窗口行为，不需要重做卡片 UI。
///
/// ── 线程模型（重要）──
/// 卡片必须在**自己带消息循环的 STA 线程**上创建与运行（WM_TIMER 与拖动都依赖消息泵）。
/// 因此 <see cref="Start"/> 会拉起线程并等到首帧画完才返回；<see cref="Dispose"/> 负责请求关闭并等待线程退出。
///
/// ── 位置（重要）──
/// 交互形态下**不能用 <c>UpdateLayeredWindow</c> 的 pptDst 定位**：
/// 那样每帧重绘都会把用户拖动后的窗口拽回原位。这里改为
/// <c>SetWindowPos</c> 定位一次 + 每帧 <c>pptDst = NULL</c> 只更新内容，位置完全归用户拖动所有。
/// 位置的持久化属 M8（存 <c>settings.widget_pos_x/y</c>）。
/// </summary>
internal sealed class CardWindow : IDisposable
{
    public const int DefaultWidth = 400;
    public const int DefaultHeight = 360;

    /// <summary>
    /// 默认坐标。取 M0 演示入口实测可见的位置（1920×1200 上不会被任务栏或屏幕边缘裁掉）；
    /// 真正的“记住上次位置”属 M8。
    /// </summary>
    public const int DefaultX = 200;
    public const int DefaultY = 700;

    /// <summary>重绘间隔。与 M1 心跳同频：数据每秒变一次，再快也只是白画。</summary>
    public const int RedrawMs = 1000;

    private readonly Func<CardModel> _modelFactory;
    private readonly CardRenderer _renderer = new();
    private readonly int _x;
    private readonly int _y;
    private readonly int _width;
    private readonly int _height;
    private readonly ManualResetEventSlim _firstFrame = new(false);

    private Thread? _thread;
    private NativeWindowHost? _host;
    private volatile bool _stopRequested;
    private volatile string? _error;
    private int _tick;
    private int _frames;
    private int _disposed;

    /// <param name="modelFactory">
    /// 取数函数（**卡片的“绑定”就在这里**）：每秒被调用一次，返回一份纯数据
    /// <see cref="CardModel"/>。它在卡片线程上执行，因此实现必须线程安全，且**不应该抛异常**
    /// （本类会兜底，但抛异常意味着那一帧画的是空卡片）。
    /// </param>
    public CardWindow(Func<CardModel> modelFactory,
                      int x = DefaultX, int y = DefaultY,
                      int width = DefaultWidth, int height = DefaultHeight)
    {
        _modelFactory = modelFactory ?? throw new ArgumentNullException(nameof(modelFactory));
        _x = x;
        _y = y;
        _width = width;
        _height = height;
    }

    public int X => _x;
    public int Y => _y;
    public int Width => _width;
    public int Height => _height;

    /// <summary>卡片窗口句柄；未创建或已销毁时为 <see cref="IntPtr.Zero"/>。</summary>
    public IntPtr Handle => _host?.Handle ?? IntPtr.Zero;

    /// <summary>是否已创建（消息循环仍在跑）。</summary>
    public bool IsStarted => _host is not null;

    /// <summary>
    /// 当前是否可见。**直接问窗口**（而不是读我们以为的状态），
    /// 这样托盘菜单的文案与勾选就不会与真实情况不符。
    /// </summary>
    public bool IsVisible
    {
        get
        {
            IntPtr handle = Handle;
            return handle != IntPtr.Zero && NativeMethods.IsWindowVisible(handle);
        }
    }

    /// <summary>已绘制的帧数（自检用：&gt; 0 表示“渲染 → 逐像素上屏”这条链路真的跑起来了）。</summary>
    public int Frames => Volatile.Read(ref _frames);

    /// <summary>启动或运行期的失败原因（正常为 null）。卡片是纯展示件，失败不得影响统计。</summary>
    public string? LastError => _error;

    /// <summary>
    /// 在独立 STA 线程上创建窗口、画首帧并启动 1s 重绘；返回时首帧已完成。
    /// 失败返回 false（并把原因记在 <see cref="LastError"/>）。
    /// </summary>
    public bool Start()
    {
        if (_thread is not null) throw new InvalidOperationException("CardWindow 已启动。");

        var thread = new Thread(ThreadMain)
        {
            // 后台线程：即便卡片的关闭路径出问题，也不能拖住进程退出。
            IsBackground = true,
            Name = "screenspy-card",
        };
        try { thread.SetApartmentState(ApartmentState.STA); } catch { /* 平台不支持时忽略 */ }

        _thread = thread;
        thread.Start();

        if (!_firstFrame.Wait(TimeSpan.FromSeconds(10)))
        {
            _error ??= "10 秒内未完成卡片首帧。";
            return false;
        }

        return Handle != IntPtr.Zero;
    }

    /// <summary>显示卡片（不抢焦点）。</summary>
    public bool Show()
    {
        NativeWindowHost? host = _host;
        if (host is null) return false;
        NativeMethods.ShowWindow(host.Handle, NativeMethods.SW_SHOWNOACTIVATE);
        return true;
    }

    /// <summary>隐藏卡片（不销毁窗口，重绘照常，重新显示时不会看到旧内容）。</summary>
    public bool Hide()
    {
        NativeWindowHost? host = _host;
        if (host is null) return false;
        NativeMethods.ShowWindow(host.Handle, NativeMethods.SW_HIDE);
        return true;
    }

    /// <summary>供自检/诊断读取真实扩展样式（客观判据，不靠肉眼）。</summary>
    public string DescribeExStyle()
    {
        IntPtr handle = Handle;
        if (handle == IntPtr.Zero) return "(未创建)";
        int ex = NativeMethods.GetWindowLong(handle, NativeMethods.GWL_EXSTYLE);
        return $"0x{ex:X8}（{NativeMethods.DescribeExStyle(ex)}）";
    }

    private void ThreadMain()
    {
        NativeWindowHost? host = null;
        try
        {
            // 关键样式（M6 可交互形态）：
            //   LAYERED     —— 逐像素 alpha 的唯一途径（UpdateLayeredWindow 要求）；
            //   TOOLWINDOW  —— 不进任务栏、不进 Alt+Tab（组件不该像文档窗口那样占位）。
            // **故意不加**：TRANSPARENT（鼠标穿透）、NOACTIVATE（不可激活）、z 序自锁。
            // 少了这三个才有“普通窗口”的行为：可点、可拖、会被别的窗口盖住。
            int exStyle = NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW;

            host = new NativeWindowHost("ScreenSpyCard", "ScreenSpy 卡片",
                                        exStyle, NativeMethods.WS_POPUP, _width, _height,
                                        lockZOrder: false, interactive: true);
            host.Create();
            _host = host;

            // 定位一次（此后位置归拖动所有；用 SetWindowPos 而非 pptDst，原因见类注释）。
            NativeMethods.SetWindowPos(host.Handle, NativeMethods.HWND_TOP, _x, _y, _width, _height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

            Draw();

            host.OnTick += () =>
            {
                _tick++;
                Draw();
            };
            host.StartTimers(RedrawMs, autoCloseSeconds: 0);

            // 首帧之后才放行 Start()：这样调用方拿到 Handle 时卡片确实已经画出来过。
            _firstFrame.Set();

            // 首帧期间就被请求关闭（启动/退出赛跑）—— 别再跑消息循环。
            if (_stopRequested) host.RequestClose();

            host.RunMessageLoop();
        }
        catch (Exception ex)
        {
            _error = ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            _firstFrame.Set();   // 失败路径也要放行，否则 Start() 会白等 10 秒
            _host = null;
            try { host?.Dispose(); } catch { /* 销毁期异常忽略 */ }
        }
    }

    private void Draw()
    {
        NativeWindowHost? host = _host;
        if (host is null) return;

        CardModel model;
        try
        {
            model = _modelFactory();
        }
        catch (Exception ex)
        {
            // 取数失败不该把窗口线程打死（那样卡片会永远停在最后一帧，且无从察觉）。
            model = new CardModel { CurrentApp = "取数失败：" + ex.GetType().Name };
        }

        model.Tick = _tick;
        _renderer.Draw(host.Surface.Graphics, host.Surface.Size, model);

        // pptDst = null：只更新内容、不改位置 —— 拖动后的位置不会被下一帧拽回去。
        host.Surface.Present(host.Handle, null, null);
        _frames++;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _stopRequested = true;

        NativeWindowHost? host = _host;
        if (host is not null) host.RequestClose();

        Thread? thread = _thread;
        if (thread is not null && thread.IsAlive)
            thread.Join(TimeSpan.FromSeconds(5));

        _thread = null;
        _firstFrame.Dispose();
    }
}
