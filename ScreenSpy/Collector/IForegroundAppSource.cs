using System;

namespace ScreenSpy.Collector;

/// <summary>
/// “此刻前台是哪个软件”的采样源（开发文档 §5.4）。
///
/// 抽象出来是为了让 <see cref="AppUsageTracker"/> 的累计逻辑，
/// 以及“切换软件 → 时长按时归属”的整条链路，能在不依赖真实前台窗口的前提下被确定性推演
/// （自检里注入脚本化的假采样源）。
/// </summary>
internal interface IForegroundAppSource
{
    /// <summary>
    /// 采样一次当前前台窗口。**实现不得抛异常**：取不到进程名时返回
    /// <see cref="ForegroundAppKind.Unknown"/>，没有前台窗口时返回 <see cref="ForegroundAppKind.None"/>。
    /// 每个心跳调用一次，实现必须足够轻量（1Hz，但不得阻塞）。
    /// </summary>
    ForegroundAppSample Sample();

    /// <summary>数据源是否可用（<c>GetForegroundWindow</c> 本身可调用）。</summary>
    bool IsAvailable { get; }

    /// <summary>最近一次错误的描述（正常为 null）。</summary>
    string? LastError { get; }
}
