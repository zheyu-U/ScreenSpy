namespace ScreenSpy.Rendering;

/// <summary>
/// 卡片渲染器的**唯一选择点**（M13 建立）。
///
/// ── 现在为什么还留着这个类（只有一个实现）──
/// "默认用哪个引擎"只能有**一个**答案。散在多处（产品、演示、自检各写各的）必然出现
/// "演示里是 Skia、产品里还是别的"这种双份漂移 —— 正是这个项目一直在躲的那类错。
/// 更重要的是：<c>CardWindow</c> 通过它拿到 <see cref="ICardRenderer"/>，
/// 于是**窗口层永远不认识具体引擎** —— 这是 M15 把 UI 拆出去时"渲染器跟着核心走、
/// 不跟着 WPF 界面走"的前提。
///
/// ── 回退方案变了（外观升级时用户决定）──
/// M13 时的回退是"把这一行换回 <c>GdiCardRenderer</c>"，因此当时保留了 GDI+ 实现当回归基线。
/// 外观升级把卡片重设了一遍，GDI+ 实现再也无法与新版对照（它做不出渐变底板、柔和投影），
/// 于是 <c>GdiCardRenderer</c> 被**移除**，回退改由 **git** 承担（回到 M13 之后的提交即可）。
/// 代价是失去"一行切回旧观"，换来的是不再维护一份必然漂移的平行实现。
///
/// 刻意**不做**"运行时自动回退"（Skia 失败就悄悄换别的）：那属于被否决的路径 (c) 的同类问题
/// —— **静默掩盖风险**。失败就让它失败得看得见。
/// </summary>
internal static class CardRendererFactory
{
    /// <summary>创建默认渲染器（当前唯一实现：SkiaSharp，见 <see cref="SkiaCardRenderer"/>）。</summary>
    public static ICardRenderer Create(int width, int height) => new SkiaCardRenderer(width, height);
}
