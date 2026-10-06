using System;
using System.Drawing;
using System.Threading;
using ScreenSpy.Desktop;
using ScreenSpy.Interop;
using ScreenSpy.Rendering;

namespace ScreenSpy.Widget;

/// <summary>
/// 组件卡片窗口（M6 建立，M7 扩为**双形态**）：把「原生分层窗口 + GDI+ 渲染」装成一个可选择形态的卡片。
///
/// ── 两种形态是**同一个窗口**的两种参数组合（M7 的关键决定）──
/// 单 HWND、单 STA 线程、共用一份坐标，切换时只改三样东西（全部落在窗口线程上）：
/// <list type="table">
///   <item><term>Embedded（默认，嵌入桌面层）</term><description>
///     样式加 <c>TRANSPARENT | NOACTIVATE</c>，命中测试 <c>HTTRANSPARENT</c>（鼠标穿透），
///     z 序自锁 + 压 <c>HWND_BOTTOM</c> + 「显示桌面」复位（即 M0 验证过的 B+ 形态）。
///     **别的窗口会盖住它** —— 这正是要的语义；定位沿用 <c>UpdateLayeredWindow</c> 的 pptDst。</description></item>
///   <item><term>Floating（浮动普通窗口）</term><description>
///     去掉 <c>TRANSPARENT/NOACTIVATE</c>，命中测试 <c>HTCAPTION</c>（整窗可拖），
///     解除 z 序自锁并抬到最前，供「调整位置」使用；
///     定位用 <c>SetWindowPos</c>，每帧 <c>pptDst=NULL</c>（否则拖动会被下一帧拽回）。</description></item>
/// </list>
///
/// 为什么不做成两个窗口（也不保留 M0 的 <c>DesktopCardHost</c>）：
/// 位置天然共用（字面上就是同一个窗口），切换不跳位、不闪断、无销毁重建空档，
/// 且只有一条生命周期（线程 / 句柄 / 显隐 / 释放顺序）与一套自检 ——
/// 否则必然出现“一种形态修了、另一种没修”的双份漂移。
///
/// ── 线程模型（重要）──
/// 卡片必须在**自己带消息循环的 STA 线程**上创建与运行（WM_TIMER、拖动、SetWinEventHook 都依赖消息泵）。
/// <see cref="Start"/> 拉起线程并等首帧画完才返回；<see cref="Dispose"/> 请求关闭并等待线程退出。
/// 因此**切换形态必须投递到窗口线程执行**（<see cref="SwitchMode"/> 走 <c>WM_APP</c>），
/// 不能从 UI 线程直接动窗口 —— 那既会与 z 序自锁打架，也会让 z 序管理器的钩子失去归属线程。
/// </summary>
internal sealed class CardWindow : IDisposable
{
    public const int DefaultWidth = 400;
    public const int DefaultHeight = 360;

    /// <summary>默认坐标（1920×1200 上不会被任务栏或屏幕边缘裁掉）。</summary>
    public const int DefaultX = 200;
    public const int DefaultY = 700;

    /// <summary>重绘间隔。与 M1 心跳同频：数据每秒变一次，再快也只是白画。</summary>
    public const int RedrawMs = 1000;

    /// <summary>嵌入形态的采样间隔（z 序 / 显示桌面检测）。</summary>
    public const int PollNormalMs = 250;

    /// <summary>进入「显示桌面」后的加密采样间隔（对应 Rainmeter 的 INTERVAL_RESTOREWINDOWS）。</summary>
    public const int PollShowDesktopMs = 100;

    /// <summary>
    /// 客观判定用的不透明标记色：ULW 在 alpha=255 时是“直接覆盖”，
    /// 因此能在截屏里被精确定位 —— 这是“像素真的上了屏”的唯一证据（M0 最重要的教训）。
    /// 仅演示 / 自检时绘制，产品默认不画。
    /// </summary>
    public static readonly Color DebugMarkerColor = Color.FromArgb(255, 255, 0, 170);

    public static readonly Rectangle DefaultDebugMarker = new(10, 10, 24, 24);

    /// <summary>形态切换的等待上限（自检与 UI 都用它，避免界面卡死）。</summary>
    public const int ModeSwitchTimeoutMs = 3000;

    private readonly Func<CardModel> _modelFactory;
    private readonly CardRenderer _renderer = new();
    private readonly Rectangle? _debugMarker;
    private readonly Action<int, int>? _onPositionChanged;
    private readonly ManualResetEventSlim _firstFrame = new(false);
    private readonly ManualResetEventSlim _modeApplied = new(false);

    /// <summary>当前形态（<see cref="WidgetMode"/> 的整数值，<c>volatile</c> 存取）。</summary>
    private int _mode;

    /// <summary>
    /// 「常态」形态：**不在调整中**时应当处于的形态。
    ///
    /// 与 <see cref="_mode"/> 分开是必要的：<see cref="BeginAdjust"/> 会把窗口临时切成浮动，
    /// 但用户选定的常态可能是嵌入 —— 「完成调整」要回到**用户选的那个**，而不是写死嵌入。
    /// </summary>
    private int _restingMode;

    /// <summary>
    /// 是否处于「调整位置」这个**临时**会话（= 常态为嵌入时，为了拖动而临时切成浮动）。
    ///
    /// 为什么必须与 <see cref="_mode"/> 分开记（M7 第一版把它写成 <c>Mode == Floating</c>，
    /// 那是个真实的坑）：常态本身就选浮动的用户，<c>Mode</c> **永远是浮动** ——
    /// 于是「调整位置」永远显示“完成调整”，而“调整中要禁用的另一个按钮”（形态切换）
    /// 会被**永久禁用**，用户再也切不回嵌入。所以只认“临时进入浮动”这一种情况。
    /// </summary>
    private int _adjusting;

    /// <summary>最近一次上报过的坐标（浮动形态下用它检测“用户把它拖动了”，见 <see cref="OnTick"/>）。</summary>
    private int _lastReportedX;
    private int _lastReportedY;

    /// <summary>待应用的形态请求；-1 表示无。由 <see cref="SwitchMode"/> 写入、窗口线程消费。</summary>
    private int _requestedMode = -1;

    /// <summary>卡片左上角坐标。嵌入形态下它就是 <c>pptDst</c>；两种形态**共用**同一份。</summary>
    private int _x;
    private int _y;

    private readonly int _width;
    private readonly int _height;

    private Thread? _thread;
    private NativeWindowHost? _host;
    private DesktopZOrderManager? _zorder;
    private volatile bool _stopRequested;
    private volatile string? _error;
    private int _frames;
    private int _sinceRedrawMs;
    private int _tickInterval;
    private int _disposed;

    /// <param name="modelFactory">
    /// 取数函数（**卡片的“绑定”就在这里**）：每秒被调用一次，返回一份纯数据
    /// <see cref="CardModel"/>。它在卡片线程上执行，因此实现必须线程安全，且**不应该抛异常**
    /// （本类会兜底，但抛异常意味着那一帧画的是空卡片）。
    /// </param>
    /// <param name="mode">初始形态；默认嵌入桌面层。</param>
    /// <param name="onPositionChanged">
    /// 用户调整完位置后的回调（在**调用 <see cref="EndAdjust"/> 的那条线程**上执行，
    /// 不是卡片线程）。参数为最终屏幕坐标；由上层负责持久化。
    /// </param>
    public CardWindow(Func<CardModel> modelFactory,
                      WidgetMode mode = WidgetMode.Embedded,
                      int x = DefaultX, int y = DefaultY,
                      int width = DefaultWidth, int height = DefaultHeight,
                      Action<int, int>? onPositionChanged = null,
                      Rectangle? debugMarker = null)
    {
        _modelFactory = modelFactory ?? throw new ArgumentNullException(nameof(modelFactory));
        _mode = (int)mode;
        _restingMode = (int)mode;
        _x = x;
        _y = y;
        _lastReportedX = x;
        _lastReportedY = y;
        _width = width;
        _height = height;
        _onPositionChanged = onPositionChanged;
        _debugMarker = debugMarker;
    }

    /// <summary>当前形态。</summary>
    public WidgetMode Mode => (WidgetMode)Volatile.Read(ref _mode);

    /// <summary>常态形态（非调整时应处于的形态）。</summary>
    public WidgetMode RestingMode => (WidgetMode)Volatile.Read(ref _restingMode);

    /// <summary>
    /// 设定常态形态（用户在主界面选择「嵌入桌面层 / 浮动窗口」时调用）。
    /// 由上层同时负责持久化；这里只管当前进程内的行为。
    /// </summary>
    public void SetRestingMode(WidgetMode mode) => Volatile.Write(ref _restingMode, (int)mode);

    /// <summary>
    /// 是否处于「调整位置」这个**临时**会话。界面据此决定按钮文案，以及“另一个按钮”是否禁用。
    ///
    /// 恒有：<see cref="IsAdjusting"/> 为真 ⇒ <see cref="Mode"/> 是浮动；
    /// 但**反之不成立** —— 常态为浮动时卡片本来就可拖，那不是“调整中”。
    /// </summary>
    public bool IsAdjusting => Volatile.Read(ref _adjusting) != 0;

    /// <summary>
    /// 「调整位置」是否**有意义**（= 常态不是浮动）。
    ///
    /// 常态已是浮动时，卡片随处可拖、且每拍自动记位置，“调整”入口只会让人以为它另有作用 ——
    /// 因此界面应当把它**禁用**，而不是让它点了没反应。
    /// </summary>
    public bool CanAdjust => RestingMode != WidgetMode.Floating;

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

    // ------------------------------------------------------------ z 序诊断（嵌入形态）

    public bool HookInstalled => _zorder?.HookInstalled ?? false;
    public int Polls => _zorder?.Polls ?? 0;
    public int Transitions => _zorder?.Transitions ?? 0;
    public bool ShowDesktop => _zorder?.ShowDesktop ?? false;
    public bool Elevated => _zorder?.Elevated ?? false;

    /// <summary>当前 z 序管理器读数（嵌入）/ 说明（浮动）。诊断与自检用。</summary>
    public string DescribeZOrder()
    {
        DesktopZOrderManager? zorder = _zorder;
        if (zorder is null) return "未启用（浮动形态不需要 z 序复位）";
        return $"显示桌面={zorder.ShowDesktop} 已抬升={zorder.Elevated} 采样={zorder.Polls} " +
               $"状态切换={zorder.Transitions} 钩子={(zorder.HookInstalled ? "已安装" : "未安装（仅定时器兜底）")}";
    }

    /// <summary>窗口当前的实际屏幕坐标（**直接问窗口**，不读我们以为的值）。</summary>
    public (int X, int Y) CurrentPosition
    {
        get
        {
            IntPtr handle = Handle;
            if (handle != IntPtr.Zero && NativeMethods.GetWindowRect(handle, out NativeMethods.RECT rect))
                return (rect.Left, rect.Top);
            return (_x, _y);
        }
    }

    /// <summary>
    /// 在独立 STA 线程上创建窗口、按 <see cref="Mode"/> 布好、画首帧并启动定时器；返回时首帧已完成。
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

    // ================================================================ 形态切换

    /// <summary>
    /// 请求切换形态（可在任意线程调用；真正的工作在卡片线程上做）。
    /// </summary>
    /// <param name="mode">目标形态。</param>
    /// <param name="waitMs">
    /// 大于 0 时等待切换完成（最多等这么久）；返回是否**确实**切到了目标形态。
    /// 自检与界面按钮都用等待式，因为它们紧接着就要读状态。
    /// </param>
    public bool SwitchMode(WidgetMode mode, int waitMs = ModeSwitchTimeoutMs)
    {
        if (Mode == mode) return true;

        NativeWindowHost? host = _host;
        if (host is null) return false;

        Interlocked.Exchange(ref _requestedMode, (int)mode);
        if (waitMs > 0) _modeApplied.Reset();
        host.PostUserMessage();

        if (waitMs <= 0) return true;
        if (!_modeApplied.Wait(waitMs)) return false;
        return Mode == mode;
    }

    /// <summary>
    /// 进入「调整位置」：卡片变为浮动普通窗口（可拖、在最前）。与主界面/托盘那个按钮是同一件事。
    ///
    /// 常态已是浮动时**直接返回 false**（见 <see cref="CanAdjust"/>）—— 那时没有“临时”可言。
    ///
    /// 刻意**不动** <see cref="RestingMode"/>：常态是用户选定的，调整只是临时的插曲。
    /// （早先的写法在进入调整时把常态改写成“进来之前的形态”，那样用户选了浮动常态后
    /// 一旦进入过调整，常态就会被悄悄改回嵌入 —— 一个不报错的错。）
    /// </summary>
    public bool BeginAdjust()
    {
        // 常态已是浮动 → 没有“临时”可言（卡片本来就可拖、也已经在自动记位置）。
        // 返回 false 而不是假装成功：界面据此把该入口禁用（见 CanAdjust）。
        if (!CanAdjust) return false;
        if (!SwitchMode(WidgetMode.Floating)) return false;

        // 只在**切换真的成功之后**才置位：否则“调整中”会与屏幕上的实际形态不符。
        Volatile.Write(ref _adjusting, 1);
        return true;
    }

    /// <summary>
    /// 结束「调整位置」：把**当前实际坐标**交给上层持久化，然后回到**常态形态**。
    ///
    /// 顺序刻意如此：先取坐标再切形态 —— 切形态本身不动位置（同一个窗口），
    /// 但把“取坐标”放在前面，即使切回失败也不会丢掉用户刚摆好的位置。
    /// （这也是为什么“调整中直接切常态形态”这条路径必须先把坐标取走：见 App 的 ToggleCardMode。）
    /// </summary>
    public bool EndAdjust()
    {
        (int x, int y) = CurrentPosition;
        ReportPosition(x, y);
        Volatile.Write(ref _adjusting, 0);
        return SwitchMode(RestingMode);
    }

    /// <summary>上报坐标给上层（持久化）。异常不外溢：坐标存不上不该打断形态切换或卡片线程。</summary>
    private void ReportPosition(int x, int y)
    {
        _lastReportedX = x;
        _lastReportedY = y;
        try { _onPositionChanged?.Invoke(x, y); } catch { /* 忽略 */ }
    }

    /// <summary>供自检/诊断读取真实扩展样式（客观判据，不靠肉眼）。</summary>
    public string DescribeExStyle()
    {
        IntPtr handle = Handle;
        if (handle == IntPtr.Zero) return "(未创建)";
        int ex = NativeMethods.GetWindowLong(handle, NativeMethods.GWL_EXSTYLE);
        return $"0x{ex:X8}（{NativeMethods.DescribeExStyle(ex)}）";
    }

    /// <summary>请求关闭（不阻塞）。演示入口用它复现“跑 N 秒后自关”。</summary>
    public void RequestClose()
    {
        NativeWindowHost? host = _host;
        if (host is not null) host.RequestClose();
    }

    /// <summary>等待卡片线程退出（已退出或未启动都返回 true）。</summary>
    public bool WaitForExit(int milliseconds)
    {
        Thread? thread = _thread;
        if (thread is null) return true;
        return !thread.IsAlive || thread.Join(milliseconds);
    }

    // ================================================================ 卡片线程

    private void ThreadMain()
    {
        NativeWindowHost? host = null;
        try
        {
            bool embedded = _mode == (int)WidgetMode.Embedded;

            // 关键样式：
            //   LAYERED    —— 逐像素 alpha 的唯一途径（UpdateLayeredWindow 要求）；
            //   TOOLWINDOW —— 不进任务栏、不进 Alt+Tab；
            //   嵌入形态再加 TRANSPARENT（鼠标穿透）+ NOACTIVATE（不抢焦点）。
            int exStyle = NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW;
            if (embedded) exStyle |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE;

            host = new NativeWindowHost("ScreenSpyCard", "ScreenSpy 卡片",
                                        exStyle, NativeMethods.WS_POPUP, _width, _height,
                                        lockZOrder: embedded, interactive: !embedded);
            host.Create();
            _host = host;

            // 嵌入：压到最底（贴桌面之上、其它普通窗口之下）；浮动：抬到最前，便于拖动。
            NativeMethods.SetWindowPos(host.Handle,
                embedded ? NativeMethods.HWND_BOTTOM : NativeMethods.HWND_TOP,
                _x, _y, _width, _height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

            SetTickInterval(embedded ? PollNormalMs : RedrawMs);
            Draw();

            host.OnUserMessage = ApplyRequestedMode;
            host.OnTick = OnTick;
            host.StartTimers(_tickInterval, autoCloseSeconds: 0);

            if (embedded) StartZOrder(host);

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
            StopZOrder();
            _host = null;
            try { host?.Dispose(); } catch { /* 销毁期异常忽略 */ }
        }
    }

    /// <summary>在卡片线程上消费一次形态请求。</summary>
    private void ApplyRequestedMode()
    {
        int requested = Interlocked.Exchange(ref _requestedMode, -1);
        if (requested < 0) return;

        try
        {
            ApplyMode((WidgetMode)requested);
        }
        catch (Exception ex)
        {
            _error = "切换形态失败：" + ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            _modeApplied.Set();
        }
    }

    /// <summary>
    /// 真正切换形态（**必须在卡片线程上执行**）。
    ///
    ///  切到嵌入 → 先把窗口当前坐标读回来（两形态共用同一份），再开自锁 + 切穿透样式，
    ///             然后启动 z 序管理器并立刻采一次（让 z 序马上归位），最后把采样加密到 250ms；
    ///  切到浮动 → 先停 z 序管理器并释放自锁（否则拖动会被自锁顶着），再切可交互样式并抬到最前。
    /// </summary>
    private void ApplyMode(WidgetMode mode)
    {
        NativeWindowHost? host = _host;
        if (host is null) return;

        if (mode == WidgetMode.Embedded)
        {
            ReadPositionFromWindow();
            host.SetInteraction(interactive: false, lockZOrder: true, clickThrough: true);
            DesktopZOrderManager zorder = _zorder ?? StartZOrder(host);
            zorder.Poll(force: true);
            SetTickInterval(PollNormalMs);
        }
        else
        {
            StopZOrder();
            host.SetInteraction(interactive: true, lockZOrder: false, clickThrough: false);
            host.MoveZOrder(NativeMethods.HWND_TOP);
            SetTickInterval(RedrawMs);

            // 把“已上报坐标”的基线对齐到当前位置，避免刚进浮动就白写一次库。
            (int px, int py) = CurrentPosition;
            _lastReportedX = px;
            _lastReportedY = py;
        }

        _mode = (int)mode;

        // 形态一旦落回嵌入，「调整中」就不可能成立 —— 在这里清掉它，
        // 免得任何绕过「完成调整」的路径（自检、将来的其它入口）把标志留在“调整中”，
        // 那会让“调整中”要禁用的那个按钮被永久禁用。
        if (mode == WidgetMode.Embedded) Volatile.Write(ref _adjusting, 0);

        _sinceRedrawMs = 0;

        // 立刻按新形态重画一帧：否则切换瞬间会短暂留着“旧样式下的旧内容”。
        Draw();
    }

    private DesktopZOrderManager StartZOrder(NativeWindowHost host)
    {
        var zorder = new DesktopZOrderManager(host);
        _zorder = zorder;
        zorder.Start();
        return zorder;
    }

    private void StopZOrder()
    {
        DesktopZOrderManager? zorder = _zorder;
        _zorder = null;
        try { zorder?.Dispose(); } catch { /* 忽略销毁期异常 */ }
    }

    private void SetTickInterval(int milliseconds)
    {
        _tickInterval = milliseconds <= 0 ? RedrawMs : milliseconds;
        _host?.SetTickInterval(_tickInterval);
    }

    private void ReadPositionFromWindow()
    {
        IntPtr handle = _host?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;
        if (NativeMethods.GetWindowRect(handle, out NativeMethods.RECT rect))
        {
            _x = rect.Left;
            _y = rect.Top;
        }
    }

    private void OnTick()
    {
        // 浮动形态下没有「完成」按钮可依赖：用户随时可以拖，拖完就该记住。
        // 用“每拍比一次实际坐标”来检测（而不是监听拖动消息 —— 拖动是系统代劳的，
        // 没有我们参与的消息可听）。一次 GetWindowRect 极便宜，且只在真的变了才写库。
        if (Mode == WidgetMode.Floating)
        {
            (int cx, int cy) = CurrentPosition;
            if (cx != _lastReportedX || cy != _lastReportedY) ReportPosition(cx, cy);
        }

        DesktopZOrderManager? zorder = _zorder;

        if (zorder is not null)
        {
            zorder.Poll();

            // 进入 / 退出「显示桌面」时切换采样密度（与 M0 的做法一致）。
            int wanted = zorder.ShowDesktop ? PollShowDesktopMs : PollNormalMs;
            if (wanted != _tickInterval) SetTickInterval(wanted);
        }

        _sinceRedrawMs += _tickInterval;
        if (_sinceRedrawMs < RedrawMs) return;

        _sinceRedrawMs = 0;
        Draw();
    }

    private void Draw()
    {
        NativeWindowHost? host = _host;
        if (host is null) return;

        CardModel model;
        try
        {
            model = _modelFactory() ?? new CardModel();
        }
        catch (Exception ex)
        {
            // 取数失败不该把窗口线程打死（那样卡片会永远停在最后一帧，且无从察觉）。
            model = new CardModel { CurrentApp = "取数失败：" + ex.GetType().Name };
        }

        _renderer.Draw(host.Surface.Graphics, host.Surface.Size, model);

        if (_debugMarker is { } marker)
        {
            using var brush = new SolidBrush(DebugMarkerColor);
            host.Surface.Graphics.FillRectangle(brush, marker);
        }

        // 嵌入：pptDst 定位（B+ 的既有做法，坐标就是 _x/_y）；
        // 浮动：pptDst = NULL —— 否则每帧重绘都会把用户拖动后的位置拽回原位。
        if (Mode == WidgetMode.Embedded) host.Surface.Present(host.Handle, _x, _y);
        else host.Surface.Present(host.Handle, null, null);

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
        _modeApplied.Dispose();
        _firstFrame.Dispose();
    }
}
