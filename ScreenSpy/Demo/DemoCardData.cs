using System;
using ScreenSpy.Rendering;

namespace ScreenSpy.Demo;

/// <summary>
/// 演示用假数据（**固定内容**，与真实统计无关）：只为打通并**随时复现**
/// “渲染 → 逐像素上屏 → 客观判定”这条链路（截屏像素判定的基准）。
///
/// 真实数据不经过这里：产品形态由 <c>Widget/CardData.cs</c> 消费
/// <c>ProductRuntime.Snapshot</c> 取数。本入口因此刻意保持**确定性**——
/// 画面不变，像素判定才有意义。
/// </summary>
internal static class DemoCardData
{
    public static CardModel Build(int tick)
    {
        int baseSeconds = 3 * 3600 + 42 * 60 + tick;   // 3:42 + tick 秒
        double limitSeconds = 5 * 3600;

        var m = new CardModel
        {
            Title = "ScreenSpy（演示数据）",
            TotalTime = Format(baseSeconds),
            LimitText = $"{Format(baseSeconds)} / 5:00",
            LimitRatio = Math.Min(1.0, baseSeconds / limitSeconds),
            CurrentApp = "devenv.exe",
        };

        m.TopApps.Add(("devenv.exe", "1:12", 0.32));
        m.TopApps.Add(("chrome.exe", "0:58", 0.26));
        m.TopApps.Add(("explorer.exe", "0:41", 0.18));
        m.TopApps.Add(("Code.exe", "0:33", 0.15));
        m.TopApps.Add(("msedge.exe", "0:18", 0.08));
        return m;
    }

    private static string Format(int seconds) => $"{seconds / 3600}:{(seconds % 3600) / 60:00}";
}
