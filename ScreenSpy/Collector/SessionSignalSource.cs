using System;
using System.Threading;
using Microsoft.Win32;

namespace ScreenSpy.Collector;

/// <summary>
/// 会话 / 电源信号的来源抽象（开发文档 §5.3 的主路径）。
///
/// 抽象出来是为了让 <see cref="SessionPauseBridge"/> 的状态处理逻辑
/// 能在不锁屏、不睡眠的前提下被确定性推演（自检里注入假信号源）。
/// </summary>
internal interface ISessionSignalSource : IDisposable
{
    /// <summary>收到信号时触发。第二个参数是可读原因（用于日志与取证）。</summary>
    event Action<SessionSignal, string>? Signal;

    /// <summary>底层事件订阅是否成功。</summary>
    bool IsHooked { get; }

    /// <summary>订阅失败的原因（成功时为 null）。</summary>
    string? HookError { get; }
}

/// <summary>
/// Windows 实现：订阅 <c>SystemEvents.SessionSwitch</c>（锁屏 / 解锁）与
/// <c>PowerModeChanged</c>（睡眠 / 恢复）。
///
/// 两点说明：
///  * <c>SystemEvents</c> 自带专用消息泵线程，因此**不需要**调用方再跑消息循环；
///    回调发生在该线程上，处理必须短小（这里只做状态更新）。
///  * 订阅可能失败（极少见），此时 <see cref="IsHooked"/> 为 false，
///    而 <see cref="SessionPauseBridge"/> 的周期探测会成为唯一来源。
/// </summary>
internal sealed class SystemSessionSignalSource : ISessionSignalSource
{
    private int _disposed;

    public SystemSessionSignalSource()
    {
        try
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            IsHooked = true;
        }
        catch (Exception ex)
        {
            HookError = ex.GetType().Name + ": " + ex.Message;
            IsHooked = false;
        }
    }

    public event Action<SessionSignal, string>? Signal;

    public bool IsHooked { get; }

    public string? HookError { get; }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                Raise(SessionSignal.Locked, "SessionSwitch: SessionLock");
                break;

            case SessionSwitchReason.SessionUnlock:
                Raise(SessionSignal.Unlocked, "SessionSwitch: SessionUnlock");
                break;

            // 控制台 / 远程会话被接管或断开时，本机同样“没人在用”。
            // 注意：这类情况下 WTS 探测会返回 Unknown（会话非活动态），
            // 因此探测自愈不会与之打架（详见 Win32SessionStateProbe）。
            case SessionSwitchReason.ConsoleDisconnect:
                Raise(SessionSignal.Locked, "SessionSwitch: ConsoleDisconnect");
                break;

            case SessionSwitchReason.RemoteDisconnect:
                Raise(SessionSignal.Locked, "SessionSwitch: RemoteDisconnect");
                break;

            case SessionSwitchReason.ConsoleConnect:
                Raise(SessionSignal.Unlocked, "SessionSwitch: ConsoleConnect");
                break;

            case SessionSwitchReason.RemoteConnect:
                Raise(SessionSignal.Unlocked, "SessionSwitch: RemoteConnect");
                break;
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                Raise(SessionSignal.Suspended, "PowerModeChanged: Suspend");
                break;

            case PowerModes.Resume:
                Raise(SessionSignal.Resumed, "PowerModeChanged: Resume");
                break;

            // StatusChange（电源状态变化）与计时语义无关，忽略。
        }
    }

    private void Raise(SessionSignal signal, string reason)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Signal?.Invoke(signal, reason);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
        catch
        {
            // 进程退出阶段 SystemEvents 可能已不可用，忽略。
        }
    }
}
