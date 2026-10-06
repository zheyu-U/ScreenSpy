using System;
using ScreenSpy.Interop;

namespace ScreenSpy.Collector;

/// <summary>
/// Windows 实现：用 <c>GetLastInputInfo</c> 取“最后一次用户输入”距现在多久。
///
/// 关键点（开发文档 §5.2 已提示的坑）：<c>LASTINPUTINFO.dwTime</c> 与 <c>GetTickCount</c>
/// 同为 **32 位 tick**，约 49.7 天回绕一次。这里刻意 **不做有符号相减**，
/// 而是先取 <see cref="Environment.TickCount64"/> 的低 32 位，再做**无符号减法**：
/// 回绕发生时结果依然正确（真实空闲时长只要小于 49.7 天）。
///
/// 反例：<c>(int)(now - last)</c> 在回绕附近会得到负数，从而被误判成“刚有输入”。
/// </summary>
internal sealed class Win32IdleClock : IIdleClock
{
    private bool _failed;

    public bool IsAvailable => !_failed;

    public TimeSpan IdleTime
    {
        get
        {
            var info = new NativeMethods.LASTINPUTINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LASTINPUTINFO>(),
            };

            if (!NativeMethods.GetLastInputInfo(ref info))
            {
                // 失败时保守地返回 0（等价于“刚刚有输入”），并由 IsAvailable 暴露事实，
                // 避免上层把“取不到数据”误解成“长时间挂机”而漏计。
                _failed = true;
                return TimeSpan.Zero;
            }

            _failed = false;

            // TickCount64 的低 32 位与 GetTickCount 一致，因此可与 dwTime 直接做无符号减法。
            // 回绕处理集中在 ActivityRules.IdleFromTickCount32（唯一落点，可被确定性检查覆盖）。
            uint now = unchecked((uint)Environment.TickCount64);
            return ActivityRules.IdleFromTickCount32(now, info.dwTime);
        }
    }
}
