using System.Collections.Generic;

namespace ScreenSpy.Rendering;

/// <summary>
/// 桌面卡片要显示的数据模型。
///
/// 这是**纯数据**：无逻辑、无数据来源 —— 卡片每帧从 <c>ModelFactory</c> 取一份新的把它交给
/// <see cref="CardRenderer"/> 画出来。因此“绑定”在本项目里就是一个返回本对象的委托 + 每秒拉取，
/// 没有 WPF 数据绑定，也没有属性通知。
///
/// M6 起 <see cref="LimitText"/> 为空表示“还没有限额数据”（M9 才有）：
/// 渲染器会**整块跳过**限额区域，而不是显示一个假限额。
/// </summary>
internal sealed class CardModel
{
    /// <summary>卡片标题。</summary>
    public string Title = "ScreenSpy";

    /// <summary>今日总时长（已格式化，如 "3:42"）。M6 起口径为“今天一整天”（本次运行 + 库中基线）。</summary>
    public string TotalTime = "0:00";

    /// <summary>限额文案，如 "3:42 / 5:00"。**空字符串 = 隐藏整块限额区域**（M9 之前）。</summary>
    public string LimitText = "";

    /// <summary>限额进度 0.0 ~ 1.0。</summary>
    public double LimitRatio;

    /// <summary>当前前台软件名。</summary>
    public string CurrentApp = "（无）";

    /// <summary>
    /// “不计入使用时长”（桌面 / 外壳 / 自身）今日合计，已格式化（如 "1:23"）。
    /// **空字符串 = 不显示这一行**（值为 0 时不占版面）。
    /// </summary>
    public string NonAppTime = "";

    /// <summary>
    /// “无前台 / 未知”今日合计，已格式化。**空字符串 = 不显示这一行**。
    /// </summary>
    public string NoWindowTime = "";

    /// <summary>
    /// 刷新计数（每秒 +1）。
    /// **仅供演示入口与自检使用**：M6 起卡片不再把它画到界面上（M0 阶段的调试秒针已移除）。
    /// </summary>
    public int Tick;

    /// <summary>Top 软件排行：(名称, 时长文案, 占比 0.0 ~ 1.0)。</summary>
    public List<(string Name, string Time, double Ratio)> TopApps = new();
}
