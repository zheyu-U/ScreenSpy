namespace ScreenSpy.Rendering;

/// <summary>
/// 卡片的排版规格（全部是**卡片局部坐标**，即相对卡片可视区左上角；换算见 <see cref="CardGeometry.BufferY"/>）。
///
/// ── 为什么用"间距"而不是"绝对 y" ──
/// 限额块（进度条 + 文案）在 <c>LimitText</c> 为空时**整块不画**（不画假限额）。
/// 若把下面的内容写成绝对 y，不画时就会留下一个空洞；写成间距则内容自然上移——
/// 这是 M13 之前就有的行为，本次**刻意保留**。
///
/// ── 最坏情形必须装得下（本次立下的硬约束）──
/// 5 行榜单 + 2 行页脚同时出现时：
/// <code>
///   行首 196 + 5 × 32 = 356（最后一行迷你条在 196+4×32+21 = 345..349）
///   页脚 356 + 2 × 18 = 392
///   卡片高 400 → 底边留白 8px
/// </code>
/// 自检 B6 就是在最坏情形下量的墨迹位置——它不是"看起来没溢出"，而是把数字钉住了。
/// 这也是 <see cref="CardGeometry.CardHeight"/> 必须从 360 涨到 400 的原因。
/// </summary>
internal static class CardLayout
{
    /// <summary>左右内边距。</summary>
    public const float Pad = 22f;

    /// <summary>顶栏（标题）顶部。</summary>
    public const float TitleTop = 26f;

    /// <summary>标题 → 大字号今日总时长。</summary>
    public const float TitleToTotal = 28f;

    /// <summary>大字号 → 限额进度条。</summary>
    public const float TotalToBar = 62f;

    /// <summary>进度条高度（圆角 = 高度一半 ⇒ 完全圆头）。</summary>
    public const float BarHeight = 6f;

    /// <summary>进度条 → 限额文案。</summary>
    public const float BarToLimit = 16f;

    /// <summary>限额文案 → 当前软件。</summary>
    public const float LimitToCurrent = 32f;

    /// <summary>当前软件 → 分隔线。</summary>
    public const float CurrentToDivider = 22f;

    /// <summary>分隔线 → 榜单首行。</summary>
    public const float DividerToRows = 10f;

    /// <summary>榜单行距（比 M13 之前的 30 更松）。</summary>
    public const float RowStep = 32f;

    /// <summary>行内迷你条的顶部偏移（相对该行顶部）。</summary>
    public const float RowBarOffset = 21f;

    /// <summary>迷你条高度（圆角 = 高度一半）。</summary>
    public const float RowBarHeight = 4f;

    /// <summary>页脚行距。</summary>
    public const float TrailerStep = 18f;

    // ---- 字号 ----

    /// <summary>标题 / 行名的字号。</summary>
    public const float FontTitle = 13f;

    /// <summary>今日总时长（本次升级从 32 提到 34，层级更明确）。</summary>
    public const float FontTotal = 34f;

    /// <summary>"正在使用："一行。</summary>
    public const float FontMid = 15f;

    /// <summary>榜单数值 / 限额文案 / 页脚。</summary>
    public const float FontSmall = 12.5f;

    // ---- 榜单 / 页脚的行数上限（用于自检核算最坏情形）----

    /// <summary>榜单最多显示的行数（数据层已限 5，这里再声明一次，供自检对照）。</summary>
    public const int MaxRows = 5;

    /// <summary>页脚最多显示的行数（"不计入使用时长" / "无前台·未知"）。</summary>
    public const int MaxTrailers = 2;
}
