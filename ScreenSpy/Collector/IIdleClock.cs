using System;

namespace ScreenSpy.Collector;

/// <summary>
/// “最后一次用户输入”的时间源抽象（开发文档 §5.2）。
///
/// 存在的意义：把 <c>GetLastInputInfo</c> 这类**焊死在操作系统上的调用**隔离在一处，
/// 使“是否计入活跃时长”的判定逻辑成为可独立推演的纯逻辑（<see cref="ActivityRules"/>），
/// 也让将来平台替换 / 自动化回归有落点。
/// </summary>
internal interface IIdleClock
{
    /// <summary>自最后一次键盘 / 鼠标输入以来经过的时间。</summary>
    TimeSpan IdleTime { get; }

    /// <summary>底层数据源是否可用（例如 GetLastInputInfo 调用失败）。</summary>
    bool IsAvailable { get; }
}
