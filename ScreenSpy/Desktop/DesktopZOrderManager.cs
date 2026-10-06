using System;
using System.Collections.Generic;
using ScreenSpy.Interop;

namespace ScreenSpy.Desktop;

/// <summary>
/// “显示桌面”检测 + z 序复位管理器 —— 本方案（B+）的核心。
///
/// 机制（**不是嵌入**）：
///  1) 创建一个隐藏的顶层工具窗口作为“探针”，始终压在 HWND_BOTTOM；
///  2) 比较桌面图标宿主与探针在 z 序表中的先后，判断系统是否处于“显示桌面”状态；
///  3) 状态变化时重新安排卡片 z 序：
///       · 非显示桌面 → HWND_BOTTOM（贴桌面之上、其它普通窗口之下，正是想要的观感）；
///       · 显示桌面   → 先 HWND_TOPMOST 再 HWND_NOTOPMOST，落在“最上层非 topmost 位置”
///                      （任务栏等 topmost 窗口之下、桌面与其它普通窗口之上）；
///  4) 用 SetWinEventHook(EVENT_SYSTEM_FOREGROUND) 让检测即时响应（定时器兜底）。
///
/// 判据极性来自**实测校正**（不是照抄推断）：
///   正常态  ：桌面宿主在最底层 → HostZ 很大，ProbeZ 略小 → HostZ &gt; ProbeZ
///   显示桌面：系统把桌面宿主抬到最前 → HostZ 很小，ProbeZ 仍在底部 → HostZ &lt; ProbeZ
///   即：桌面宿主跑到“压在底部的探针”之前 ⇒ 处于“显示桌面”。
///
/// 关键事实（26H2 实测）：Win+D **并不会**最小化卡片窗口
/// （IsIconic 始终为 False）；卡片只是被抬起的桌面**盖住**了，
/// 因此正解是 z 序复位，而不是拦截最小化。详见 docs/M0-B+验证.md。
/// </summary>
internal sealed class DesktopZOrderManager : IDisposable
{
    private readonly NativeWindowHost _cardHost;
    private readonly NativeWindowHost _probe;

    /// <summary>必须保持强引用，否则 SetWinEventHook 的回调地址会被 GC 回收。</summary>
    private NativeMethods.WinEventProc? _winEventProc;

    private IntPtr _hook;
    private bool _applied;
    private bool _disposed;

    public DesktopZOrderManager(NativeWindowHost cardHost)
    {
        _cardHost = cardHost;

        // 探针：顶层、隐藏（不给 WS_VISIBLE）、工具窗口、禁用交互；不建绘图表面的 1×1 窗口。
        _probe = new NativeWindowHost(
            "ScreenSpyZProbe", "ScreenSpyZProbe",
            NativeMethods.WS_EX_TOOLWINDOW, NativeMethods.WS_POPUP,
            1, 1, createSurface: false, quitOnDestroy: false);
    }

    /// <summary>当前是否处于“显示桌面”（Win+D）状态。</summary>
    public bool ShowDesktop { get; private set; }

    /// <summary>卡片当前是否已被抬升到桌面之上的非 topmost 顶层位置。</summary>
    public bool Elevated { get; private set; }

    public int HostZ { get; private set; } = -1;
    public int ProbeZ { get; private set; } = -1;
    public int CardZ { get; private set; } = -1;

    public int Polls { get; private set; }
    public int Elevations { get; private set; }
    public int Transitions { get; private set; }
    public bool HookInstalled => _hook != IntPtr.Zero;

    /// <summary>状态切换与 z 序变化的可读轨迹（用于日志 / 报告）。</summary>
    public List<string> Trace { get; } = new();

    /// <summary>在卡片窗口所在线程上调用（该线程必须具备消息循环，WinEvent 才能送达）。</summary>
    public void Start()
    {
        _probe.Create();
        _probe.MoveZOrder(NativeMethods.HWND_BOTTOM);

        try
        {
            _winEventProc = OnWinEvent;
            _hook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _winEventProc, 0, 0,
                NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        }
        catch (Exception ex)
        {
            Trace.Add("SetWinEventHook 失败（改用定时器兜底）：" + ex.Message);
            _hook = IntPtr.Zero;
        }

        Poll(force: true);
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventId, IntPtr hwnd,
                            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (eventId != NativeMethods.EVENT_SYSTEM_FOREGROUND) return;

        try
        {
            Poll();
        }
        catch (Exception ex)
        {
            Trace.Add("WinEvent 回调中出错：" + ex.Message);
        }
    }

    /// <summary>采一次样：读 z 序 → 判定状态 → 必要时复位卡片 z 序。</summary>
    public void Poll(bool force = false)
    {
        if (_disposed) return;

        IntPtr host = DesktopShell.FindDesktopIconsHost();
        if (host == IntPtr.Zero || !NativeMethods.IsWindowVisible(host)) return;

        Polls++;

        HostZ = NativeMethods.ZOrderIndex(host);
        ProbeZ = NativeMethods.ZOrderIndex(_probe.Handle);
        CardZ = NativeMethods.ZOrderIndex(_cardHost.Handle);

        // 判据：桌面宿主跑到“压在底部的探针”之前 ⇒ 显示桌面。
        bool show = HostZ >= 0 && ProbeZ >= 0 && HostZ < ProbeZ;

        bool changed = show != ShowDesktop;
        ShowDesktop = show;

        if (changed)
        {
            Transitions++;
            Trace.Add($"状态切换 → 显示桌面={(show ? "是" : "否")}（HostZ={HostZ} ProbeZ={ProbeZ} CardZ={CardZ}）");
        }

        ApplyZOrder(force);

        if (changed || force)
        {
            Trace.Add($"   z 序快照：Host={HostZ} Probe={ProbeZ} Card={CardZ} → Elevated={Elevated}");
        }
    }

    private void ApplyZOrder(bool force)
    {
        if (!force && ShowDesktop == _applied) return;
        _applied = ShowDesktop;

        if (ShowDesktop)
        {
            // 两步走：先 HWND_TOPMOST，再 HWND_NOTOPMOST。
            // 结果落在“最上层的非 topmost 位置”——在任务栏等 topmost 窗口之下、在桌面与其它普通窗口之上。
            _cardHost.MoveZOrder(NativeMethods.HWND_TOPMOST);
            _cardHost.MoveZOrder(NativeMethods.HWND_NOTOPMOST);
            Elevated = true;
            Elevations++;
        }
        else
        {
            _cardHost.MoveZOrder(NativeMethods.HWND_BOTTOM);
            Elevated = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hook != IntPtr.Zero)
        {
            try { NativeMethods.UnhookWinEvent(_hook); } catch { /* 忽略 */ }
            _hook = IntPtr.Zero;
            _winEventProc = null;
        }

        try { _probe.Dispose(); } catch { /* 忽略销毁期异常 */ }
    }
}
