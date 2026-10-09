using System.Drawing;

namespace ScreenSpy.Rendering;

/// <summary>
/// 卡片上那些**跨引擎必须逐字节一致**的视觉常量。
///
/// 目前只有一样东西属于这一类：**调试标记色块**。
/// 它来自 M0 最贵的一课 —— “API 返回值全都成功、像素却从未上屏” ——
/// 因此它是判定“卡片真的上了屏”的**唯一锚点**：
/// <list type="bullet">
///   <item>alpha = 255 ⇒ <c>UpdateLayeredWindow</c> 是**直接覆盖**，不受底色影响，能在截屏里被精确定位；</item>
///   <item>所以 <c>ScreenCapture.Analyze</c> 能用它反查出卡的包围盒与可见性。</item>
/// </list>
///
/// 为什么放在 <c>Rendering</c> 而不是继续挂在 <c>CardWindow</c>（M13 的一处必要挪动）：
/// 标记现在由**渲染器**画（<see cref="ICardRenderer.Draw"/> 的参数），
/// 而渲染层不该反向依赖 <c>Widget</c> 层。<c>CardWindow</c> 保留同名转发，
/// 因此 <c>DesktopCardDemo</c> 等既有调用点一行都不用改。
///
/// 坐标是**缓冲坐标**（原点 = 窗口左上角），而不是"卡片左上角"：外观升级后窗口比卡片大一圈
/// （<see cref="CardGeometry.ShadowMargin"/>），标记因此落在左上角的投影区、跨过卡片边缘。
/// **刻意不改**：M0/M13 记录里"卡片 (200,700) ⇒ 标记包围盒 {X=210,Y=710,24×24}"这条证据
/// 因此仍然成立；改成"卡片坐标"反而会把历史证据全部作废。
/// </summary>
internal static class CardMarkers
{
    /// <summary>不透明标记色（alpha = 255）。改它等于改掉全部像素判定的基准，**不要改**。</summary>
    public static readonly Color MarkerColor = Color.FromArgb(255, 255, 0, 170);

    /// <summary>演示 / 自检用的默认标记位置与尺寸（缓冲坐标；见类说明：它故意留在窗口左上角附近）。</summary>
    public static readonly Rectangle DefaultRect = new(10, 10, 24, 24);
}
