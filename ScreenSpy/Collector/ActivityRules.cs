using System;

namespace ScreenSpy.Collector;

/// <summary>
/// 活跃判定的纯逻辑（无 IO、无状态、无平台依赖）。
///
/// 单独成类是为了让“阈值边界”“32 位 tick 回绕”这类**人工难以复现**的场景
/// 有唯一的、可推演的落点（当前计划不含测试工程，这些结论由 <c>--m1-selfcheck</c>
/// 内嵌的确定性检查覆盖）。
/// </summary>
internal static class ActivityRules
{
    /// <summary>默认空闲阈值：停手 5 分钟即视为挂机（M1 定为 300s，可配置）。</summary>
    public const int DefaultIdleThresholdSeconds = 300;

    public static readonly TimeSpan DefaultIdleThreshold =
        TimeSpan.FromSeconds(DefaultIdleThresholdSeconds);

    /// <summary>
    /// 单拍最多可计入的时长（M2 新增的“第二道防线”）。
    ///
    /// 存在的理由：睡眠 / 休眠 / 系统长时间冻结时，进程被挂起、心跳停止，
    /// 恢复后的第一拍会看到一个**异常巨大的间隔**（可能是数小时）。
    /// 若只靠 <c>PowerModeChanged.Suspend/Resume</c> 事件，在 S0 新式待机等场景下事件可能缺失或不及时，
    /// 那一拍就可能把整段睡眠算成“活跃”。
    ///
    /// 因此再加一条与事件无关的客观护栏：**间隔超过阈值的拍，一律不计入**。
    /// 取 60s 的取舍：正常运行时心跳为 1s，60s 的偏差只可能来自“进程被挂起 / 机器睡眠 / 极端负载”，
    /// 这些场景下用户是否在用本就无从判定，按“不计入”处理符合本项目“宁可少计、不可多计”的取向。
    /// 代价是被极端负载卡住时会少计（有界、可配置）。
    /// </summary>
    public static readonly TimeSpan DefaultMaxCreditedInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 该时刻是否计入活跃：**空闲时长尚未达到阈值**即算活跃。
    /// 边界取“严格小于”，即恰好等于阈值时已算挂机（避免阈值处抖动带来的一秒两判）。
    /// </summary>
    public static bool IsActive(TimeSpan idle, TimeSpan threshold) => idle < threshold;

    /// <summary>
    /// 间隔是否可被信任并计入（M2）。边界取“或等于”即计入：
    /// 恰好等于上限时仍按正常拍处理，只在**超过**上限时才判定为时间空洞。
    /// </summary>
    public static bool IsCreditable(TimeSpan elapsed, TimeSpan maxCreditedInterval)
        => elapsed <= maxCreditedInterval;

    /// <summary>
    /// 综合判定（M2 起调度器使用此重载）：间隔可信任 **且** 空闲未达阈值。
    /// </summary>
    public static bool IsActive(TimeSpan idle, TimeSpan threshold, TimeSpan elapsed, TimeSpan maxCreditedInterval)
        => IsCreditable(elapsed, maxCreditedInterval) && IsActive(idle, threshold);

    /// <summary>64 位基准下两个 tick 的间隔；出现负值（时钟倒退）时归零。</summary>
    public static long ElapsedMs(long nowMs, long lastMs)
    {
        long d = nowMs - lastMs;
        return d < 0 ? 0 : d;
    }

    /// <summary>
    /// 由两个 **32 位** tick 求空闲时长（<c>LASTINPUTINFO.dwTime</c> 场景）。
    ///
    /// 关键：先做 **uint 无符号减法**，使 49.7 天的回绕自动正确
    /// （只要真实空闲时长小于 49.7 天，结果恒正确）。
    /// 反例：有符号相减在回绕附近会得到负数，从而被误判成“刚刚有输入”，导致挂机被计时。
    /// </summary>
    public static TimeSpan IdleFromTickCount32(uint now, uint lastInput)
        => TimeSpan.FromMilliseconds(unchecked(now - lastInput));
}
