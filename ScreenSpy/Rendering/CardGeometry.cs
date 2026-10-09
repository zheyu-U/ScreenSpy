using SkiaSharp;

namespace ScreenSpy.Rendering;

/// <summary>
/// 卡片几何（"外观升级"引入）：**卡片可视区域**与**分层窗口**从此不再相等。
///
/// ── 为什么必须多出这一层 ──
/// 外侧柔和投影（<see cref="CardTheme.ShadowSigma"/>）需要**卡片以外的像素**才画得出来。
/// 如果窗口尺寸仍等于卡片尺寸，投影只会在窗口边缘被硬生生切断，
/// 看起来不像"影子"而像"描边被裁了一刀"——这是加投影时最典型的翻车方式。
/// 因此：
/// <code>
///   窗口（= 渲染缓冲） = 卡片可视区 + 2 × 投影边距
///   440 × 440          = 400 × 400   + 2 × 20
/// </code>
///
/// ── 关键：只改渲染层，窗口层行为一行未动 ──
/// 窗口依旧是"一块整窗命中测试"的普通分层窗口：
/// <list type="bullet">
///   <item>嵌入形态整窗 <c>HTTRANSPARENT</c>（鼠标穿透）——边距区连带一起穿透，没有区别；</item>
///   <item>浮动形态整窗 <c>HTCAPTION</c>（可拖）——拖动热区因此比卡片大一圈，属可接受后果；</item>
///   <item>z 序、显示桌面复位、线程模型、<c>UpdateLayeredWindow</c> 全部不涉及"卡片在哪"，不受影响。</item>
/// </list>
/// 也就是说：<b>"窗口层冻结"依然成立</b>——变的只是"渲染器往那块缓冲里画什么"。
///
/// ── 位置语义的唯一变化（已知且已接受）──
/// 窗口左上角 = 卡片可视区左上角 − 投影边距。<c>_x/_y</c> 及持久化坐标依旧指**窗口**左上角，
/// 因此默认位置 (200,700) 下卡片本体出现在 (220,720)。选这个方案是为了**不动任何坐标换算**：
/// 一旦引入"卡片=窗口−边距"的补偿，浮动拖动、位置上报、两形态共用坐标都会被牵连。
/// </summary>
internal static class CardGeometry
{
    /// <summary>投影边距（每边）。取自实测：sigma=6 的模糊在 20px 处已经衰减到 alpha ≤ 1（自检 A6 盯着）。</summary>
    public const int ShadowMargin = 20;

    /// <summary>卡片**可视**宽度（布局基准，所有排版常量都相对它）。</summary>
    public const int CardWidth = 400;

    /// <summary>
    /// 卡片**可视**高度。比 M13 之前的 360 高 40：外观升级要"留白更松"，
    /// 而 M13 之前 5 行榜单 + 2 行页脚已经把 360 撑到只剩 6px 底边距（见 <see cref="CardLayout"/> 的最坏情形核算）。
    /// </summary>
    public const int CardHeight = 410;

    /// <summary>分层窗口宽度 = 卡片 + 两侧投影边距。</summary>
    public const int WindowWidth = CardWidth + ShadowMargin * 2;

    /// <summary>分层窗口高度 = 卡片 + 上下投影边距。</summary>
    public const int WindowHeight = CardHeight + ShadowMargin * 2;

    /// <summary>卡片圆角半径（比 M13 之前的 16 更圆，属外观升级的一部分）。</summary>
    public const float Radius = 22f;

    /// <summary>卡片可视区在缓冲坐标下的左边界。</summary>
    public const int BodyLeft = ShadowMargin;

    /// <summary>卡片可视区在缓冲坐标下的上边界。</summary>
    public const int BodyTop = ShadowMargin;

    /// <summary>卡片可视区在缓冲坐标下的右边界（不含）。</summary>
    public const int BodyRight = ShadowMargin + CardWidth;

    /// <summary>卡片可视区在缓冲坐标下的下边界（不含）。</summary>
    public const int BodyBottom = ShadowMargin + CardHeight;

    /// <summary>卡片可视区域在**缓冲坐标**下的矩形（自顶向下 32bpp 预乘 BGRA）。</summary>
    public static SKRect BodyRect => new(BodyLeft, BodyTop, BodyRight, BodyBottom);

    /// <summary>卡片局部 X → 缓冲 X。</summary>
    public static float BufferX(float localX) => ShadowMargin + localX;

    /// <summary>卡片局部 Y → 缓冲 Y。</summary>
    public static float BufferY(float localY) => ShadowMargin + localY;

    /// <summary>卡片局部矩形 → 缓冲矩形。</summary>
    public static SKRect BufferRect(float left, float top, float right, float bottom)
        => new(BufferX(left), BufferY(top), BufferX(right), BufferY(bottom));
}
