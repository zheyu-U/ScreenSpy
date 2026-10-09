using SkiaSharp;

namespace ScreenSpy.Rendering;

/// <summary>
/// 卡片配色与投影参数（"外观升级"引入，唯一配色来源）。
///
/// ── 为什么单独成类 ──
/// 这些值以前散在渲染器里、且被 <c>--m13-selfcheck</c> 当作字面量断言过。
/// 外观升级把"配色"变成会经常调的东西，于是：
/// <list type="bullet">
///   <item>渲染器只**引用**它们；</item>
///   <item>自检**从它们推导期望值**（预乘后的字节），再与实测像素对比——
///         于是"改了配色但忘记同步断言"不会变成一条悄悄失效的检查。</item>
/// </list>
///
/// ── 配色意图（对外观升级的交代）──
/// <list type="bullet">
///   <item>底板：**垂直微渐变**（顶亮底暗）而不是单一填充色——单色在深色桌面上像一块补丁，
///         微渐变给出"有厚度"的错觉；alpha 保持 222（比 M13 的 205 更实一点，字更清楚）。</item>
///   <item>强调色：**唯一**（进度条填充、迷你条）。M13 之前强调色与文字/描边混用，视觉上没有焦点。</item>
///   <item>危险色：从 (236,92,92) **柔化**到 (232,110,108)——超限是提醒，不是报警。</item>
///   <item>投影：纯黑 alpha 120、下偏移 4px、sigma 6。**不要**调大到边距装不下（自检 A6 会绊住）。</item>
/// </list>
/// </summary>
internal static class CardTheme
{
    // ---- 底板（垂直微渐变的两端；alpha 必须相同，否则"渐变"会变成"透明度渐变"）----

    /// <summary>底板渐变顶部色。</summary>
    public static readonly SKColor BodyTop = new(26, 30, 40, BodyAlpha);

    /// <summary>底板渐变底部色。</summary>
    public static readonly SKColor BodyBottom = new(13, 15, 21, BodyAlpha);

    /// <summary>底板不透明度（两端相同）。自检 A2 直接断言这个数字。</summary>
    public const byte BodyAlpha = 222;

    /// <summary>顶部内辉光（"磨砂层次"）：从顶部向下淡出，只铺满卡片高度的 <see cref="GlowSpanRatio"/>。</summary>
    public static readonly SKColor GlowTop = new(255, 255, 255, 18);

    /// <summary>内辉光的终点（全透明，否则会看到一条硬边）。</summary>
    public static readonly SKColor GlowFade = new(255, 255, 255, 0);

    /// <summary>内辉光覆盖的高度比例。</summary>
    public const float GlowSpanRatio = 0.45f;

    /// <summary>边缘高光（1px 内描边）：让卡片边界从深色桌面上"立"起来。</summary>
    public static readonly SKColor EdgeHighlight = new(255, 255, 255, 38);

    // ---- 文字 ----

    /// <summary>主要文字（今日总时长、数值、当前软件）。</summary>
    public static readonly SKColor White = new(245, 246, 250, 255);

    /// <summary>次要文字（标题、软件名、限额文案、页脚标签）。</summary>
    public static readonly SKColor Dim = new(168, 176, 190, 190);

    // ---- 强调色（唯一）----

    /// <summary>强调色：进度条与迷你条的填充起点。</summary>
    public static readonly SKColor Accent = new(118, 196, 255, 255);

    /// <summary>强调色的渐变终点（进度条填充从左到右微亮，纯装饰）。</summary>
    public static readonly SKColor AccentLight = new(164, 218, 255, 255);

    /// <summary>超限色（柔化）。</summary>
    public static readonly SKColor Danger = new(232, 110, 108, 255);

    // ---- 图形 ----

    /// <summary>进度条 / 迷你条的轨道底色。</summary>
    public static readonly SKColor TrackBackground = new(255, 255, 255, 46);

    /// <summary>分隔线。</summary>
    public static readonly SKColor Divider = new(255, 255, 255, 34);

    // ---- 外侧柔和投影 ----

    /// <summary>投影颜色（纯黑 + alpha）。</summary>
    public static readonly SKColor ShadowColor = new(0, 0, 0, 120);

    /// <summary>投影垂直偏移（向下）。</summary>
    public const float ShadowOffsetY = 4f;

    /// <summary>投影模糊 sigma。调大就要同步调大 <see cref="CardGeometry.ShadowMargin"/>。</summary>
    public const float ShadowSigma = 6f;

    /// <summary>
    /// 底板渐变在比例 <paramref name="t"/>（0 = 顶，1 = 底）处的**未预乘**颜色。
    ///
    /// 存在的唯一理由是让自检能把"实测像素"与"设计意图"对上：Skia 的渐变着色器把颜色写进
    /// 预乘位图，而这里给出的是原色——自检自己做预乘再比对，于是这条断言同时验证了
    /// **渐变方向、渐变端点、以及预乘**三件事（预乘写错会整体偏离，不是差 1 个 8bit 单位）。
    /// 刻意自己写插值而不用 <c>SKColor.Lerp</c>：这里要的是"可独立推导的期望值"，
    /// 不能依赖被检查的那套库的另一条代码路径。
    /// </summary>
    public static SKColor BodyAt(float t)
    {
        if (t < 0f) t = 0f;
        else if (t > 1f) t = 1f;

        return new SKColor(
            Lerp(BodyTop.Red, BodyBottom.Red, t),
            Lerp(BodyTop.Green, BodyBottom.Green, t),
            Lerp(BodyTop.Blue, BodyBottom.Blue, t),
            BodyAlpha);
    }

    private static byte Lerp(byte from, byte to, float t) => (byte)System.Math.Round(from + (to - from) * t);
}
