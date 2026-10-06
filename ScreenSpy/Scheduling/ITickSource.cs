using System;

namespace ScreenSpy.Scheduling;

/// <summary>
/// 单调毫秒时钟的抽象。
///
/// 存在的意义与 <see cref="ScreenSpy.Collector.IIdleClock"/> 相同：把 <c>Environment.TickCount64</c>
/// 这类系统调用隔离出来，使“按差值累计”“挂机不计时”“跨天重置”等逻辑可以**完全确定性地推演**
/// （当前计划不含测试工程，改由 <c>--m1-selfcheck</c> 内嵌的确定性检查覆盖）。
/// </summary>
internal interface ITickSource
{
    /// <summary>单调递增的毫秒计数（应保证单调，不受系统时间调整影响）。</summary>
    long Milliseconds { get; }
}

/// <summary>默认实现：<see cref="Environment.TickCount64"/>（系统启动以来的毫秒数）。</summary>
internal sealed class SystemTickSource : ITickSource
{
    public static readonly SystemTickSource Instance = new();

    public long Milliseconds => Environment.TickCount64;
}
