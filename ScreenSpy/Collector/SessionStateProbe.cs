using System;

namespace ScreenSpy.Collector;

/// <summary>锁屏探测的三态结果。刻意保留“不确定”，避免把“查不到”误当成“没锁屏”。</summary>
internal enum SessionLockProbeResult
{
    /// <summary>无法判定（例如当前会话不是活动会话，或 API 调用失败）。此时**不得**改动状态。</summary>
    Unknown = 0,

    /// <summary>工作站未锁定。</summary>
    Unlocked = 1,

    /// <summary>工作站已锁定。</summary>
    Locked = 2,
}

/// <summary>
/// “工作站当前是否锁定”的探测源（开发文档 §5.3 的**兜底**手段，非主路径）。
///
/// 主路径是事件（<c>SystemEvents</c>），但事件有两个天然缺口：
///  * 进程启动时若已经锁屏，事件不会补发；
///  * 万一解锁事件丢失，会**永久**少计（比多计更隐蔽）。
/// 因此加一个“尽力而为”的探测做初值与自愈。探测失败必须返回 <see cref="SessionLockProbeResult.Unknown"/>，
/// 绝不能用猜测覆盖事件给出的状态。
/// </summary>
internal interface ISessionStateProbe
{
    /// <summary>探测当前工作站的锁定状态。实现不得抛异常（异常按 Unknown 处理）。</summary>
    SessionLockProbeResult ProbeLocked();
}
