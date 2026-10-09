using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ScreenSpy.Interop;
using ScreenSpy.Diagnostics;
using ScreenSpy.Rendering;
using ScreenSpy.Widget;
using SkiaSharp;

namespace ScreenSpy.Demo;

/// <summary>
/// M13 的控制台自检入口（内嵌入口，**长期保留**）。
///
/// 用法：ScreenSpy.exe --m13-selfcheck
///
/// 覆盖四类**可以在进程内客观判定**的事实：
///
///  【A 像素语义】缓冲的物理属性：
///          * 半透明底板出来的字节**是预乘的**（而不是"看起来对"）；
///          * 断言本身**能区分**预乘与非预乘（否则是一条空断言 —— 本项目最忌讳的形式）；
///          * 卡片四角是圆角（角上不是底板）；alpha 通道没被色彩管理改过；
///          * 渲染器确实写进了**调用方给的那块缓冲**（哨兵字节被全部覆盖）；
///          * 投影边距够宽：窗口最外一圈像素仍是透明的（投影没被窗口边缘切断）；
///          * 输出可落盘成 PNG（供人眼离线比对）。
///
///  【B 外观不变量】"这次外观升级到底做了什么"必须能被断言，而不是只能靠眼睛：
///          * 外侧柔和投影存在、是中性暗色、且朝窗口边缘衰减；
///          * 底板是**垂直微渐变**（顶亮底暗），两端 alpha 相同；
///          * 圆角半径**实测**（量卡片顶边的填充起点，而不是读常量）；
///          * 进度条：填充宽度 = 条宽 × 比例、条带高度、圆头；
///          * 排版锚点：用"同一段文字有/无 ink 两次渲染相减"量出**真实墨迹包围盒**，
///            与声明的行位对齐（右对齐右缘、左对齐左缘、墨迹顶边）；
///          * 最坏情形（5 行榜单 + 2 行页脚）不溢出卡片底边。
///
///  【C 接缝与契约】渲染器选择单点、状态化渲染器可复用、参数校验硬失败、
///          <c>LayeredSurface</c> 暴露的缓冲契约自洽、调试标记是 <c>Draw</c> 的一等参数
///          （传 null 就没有标记 —— 证明它真的由参数驱动，而不是某个实现的私事）。
///
///  【D 窗口层回归】真实创建一张卡片（默认渲染器 = Skia），断言：
///          逐像素 alpha 的样式位、鼠标穿透、z 序"不在最顶层"这些**窗口层行为一字未变**，
///          并用**截屏像素**确认卡片真的上了屏（M0 的判据）。
///
/// ── 与 M13 时的差别（外观升级带来的判据变更）──
/// M13 的 B 组是"与 GDI+ 基线逐像素等价"（15 项）：外观升级把卡片重设了一遍，
/// 那个前提自然作废，GDI+ 实现也已移除。取而代之的是**对卡片自己下断言**（上面 B 组）：
/// 不再问"和旧版一样吗"，而问"声明的几何/配色/排版，像素里是不是真的那样"。
///
/// **本自检不覆盖**（需要人眼）：
///  * 卡片**好不好看**（只能人眼比对 artifacts 里的 PNG）；
///  * 鼠标真的拖一下、Win+D 不最小化、"被别的窗口盖住"的观感。
///
/// 注意：运行期间会**短暂出现一张真实卡片窗口**（创建后立即销毁），并会在
/// <c>artifacts/m13/</c> 下写出 PNG（**保留**，供人眼比对）。
/// </summary>
internal static class M13SelfCheck
{
    private const string SwitchName = "--m13-selfcheck";

    /// <summary>缓冲尺寸 = 窗口尺寸（卡片可视区 + 两侧投影边距，见 <see cref="CardGeometry"/>）。</summary>
    private const int W = CardWindow.DefaultWidth;
    private const int H = CardWindow.DefaultHeight;

    /// <summary>文字墨迹相对"声明行位"的允许偏差（像素）。</summary>
    private const int InkTolerance = 2;

    /// <summary>圆角实测允许的区间（相对卡片左边缘的填充起点，量在卡片顶边下 2px 处）。</summary>
    private const int CornerProbeMinX = 10;
    private const int CornerProbeMaxX = 20;

    /// <summary>右对齐文字的右缘允许偏差（像素）：Skia 会把文字正好放在内边距上。</summary>
    private const int RightAlignTolerance = 3;

    /// <summary>窗口最外一圈允许的最大 alpha：证明投影边距够宽（投影没在窗口边缘被切断）。</summary>
    private const int OuterRingMaxAlpha = 2;

    /// <summary>"是底板而不是别的东西"的 alpha 上限：投影在角落最多贡献几十，远低于底板 222。</summary>
    private const int NotBodyMaxAlpha = 100;

    /// <summary>
    /// 墨迹**顶边**的实测锚点（缓冲坐标，行框上沿 = 声明行位 + 字形上沿空白）。
    ///
    /// 这两条刻意写成**字面量、零容差**：行位到墨迹顶边之间的距离随字号与字体度量变化，
    /// 推不出公式（见 <c>SkiaCardRenderer.TextTopAdjust</c>）。
    /// 容差一旦放开（例如"落在行位下方 0..14px 即可"），把 <c>TextTopAdjust</c> 改成 0
    /// ——整体上移 1px——就再也抓不住，而那恰恰是这套排版最容易被改坏的一处。
    /// 同一台机器上字体光栅化是确定性的（连跑两次实测均为 50 / 83），所以这里就该钉死。
    /// 换机器或换字体导致这两条失败时：**先看图确认是否真的变丑**，再重新标定这两个数字。
    /// </summary>
    private const int TitleInkTop = 50;
    private const int TotalInkTop = 83;

    public static bool IsRequested(string[] args)
    {
        foreach (string a in args)
            if (a.Equals(SwitchName, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static int Run(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 输出被重定向时可能失败，忽略 */ }

        Console.WriteLine("============ ScreenSpy · M13 卡片渲染自检（SkiaSharp）============");
        Console.WriteLine($"缓冲 {W}×{H} = 卡片 {CardGeometry.CardWidth}×{CardGeometry.CardHeight} + 投影边距 {CardGeometry.ShadowMargin}×2；" +
                          $"(b) Skia 位图 → 拷进 DIB；默认渲染器 = {CardRendererFactory.Create(W, H).GetType().Name}");
        Console.WriteLine();

        string dir = Path.GetFullPath(Path.Combine("artifacts", "m13"));
        Directory.CreateDirectory(dir);

        int failed = 0;
        failed += PixelSemanticsChecks(dir);
        failed += AppearanceChecks(dir);
        failed += SeamChecks();
        failed += WindowLayerChecks(dir);

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "M13 自检：全部通过。" : $"M13 自检：{failed} 项失败。");
        return failed == 0 ? 0 : 1;
    }

    // ================================================================ A 像素语义

    private static int PixelSemanticsChecks(string dir)
    {
        Console.WriteLine("---- A 像素语义（预乘 / 圆角 / 投影边距 / 写入目标缓冲 / 可落盘）----");
        int failed = 0;

        using var renderer = new SkiaCardRenderer(W, H);
        using Target t = new(W, H, sentinel: 0xCD);
        // 不画调试标记：它默认落在左上角 (10,10,24,24)，会盖住下面 A3 的圆角取样点。
        // 标记本身由 C5 专门断言，这里不需要它。
        renderer.Draw(t.Bits, W, H, t.Stride, FullModel(), null);

        // ---- A1 半透明底板必须是**预乘**的 ----
        // 取样点：卡片左边缘内 5px、卡片垂直正中 —— 那里只有底板：
        //   * 内容从内边距 22 起，够不着；
        //   * 顶部内辉光只铺到卡片高度的 45%，够不着；
        //   * 该点的渐变比例恰好 0.5，期望颜色由 CardTheme.BodyAt 算出（不是抄来的字面量）。
        int probeX = CardGeometry.BodyLeft + 5;
        int probeY = CardGeometry.BodyTop + CardGeometry.CardHeight / 2;

        SKColor raw = CardTheme.BodyAt((probeY - CardGeometry.BodyTop) / (float)CardGeometry.CardHeight);
        (byte eB, byte eG, byte eR, byte eA) = Premultiply(raw);
        (byte b, byte g, byte r, byte a) = Pixel(t.Bits, t.Stride, probeX, probeY);

        bool matchesPremul = Within(r, eR, 2) && Within(g, eG, 2) && Within(b, eB, 2) && a == eA;
        bool matchesRaw = Within(r, raw.Red, 2) && Within(g, raw.Green, 2) && Within(b, raw.Blue, 2);

        Console.WriteLine($"   底板像素 ({probeX},{probeY})：B={b} G={g} R={r} A={a}；" +
                          $"预乘期望 (B={eB},G={eG},R={eR},A={eA})；未预乘原色 (B={raw.Blue},G={raw.Green},R={raw.Red})");
        failed += Check("A1 底板出来的是**预乘**字节（UpdateLayeredWindow 的唯一要求）", matchesPremul);
        failed += Check("A1b 该断言**能区分**预乘与非预乘（否则是空断言）", matchesPremul && !matchesRaw);
        failed += Check($"A2 底板 alpha 恰好 {eA}（没被色彩管理/预乘处理改过）", a == eA);

        // ---- A3 圆角：卡片四角不能是"直角" ----
        // 注意不能要求角上 alpha == 0：**投影就画在那里**。所以判据是"角上远不是底板"
        // （alpha ≤ NotBodyMaxAlpha），而卡片中间是 222 —— 半径被改成 0 立刻就会被抓住。
        (byte cb1, byte cg1, byte cr1, byte ca1) = Pixel(t.Bits, t.Stride, CardGeometry.BodyLeft, CardGeometry.BodyTop);
        (byte cb2, byte cg2, byte cr2, byte ca2) = Pixel(t.Bits, t.Stride, CardGeometry.BodyRight - 1, CardGeometry.BodyTop);
        (byte cb3, byte cg3, byte cr3, byte ca3) = Pixel(t.Bits, t.Stride, CardGeometry.BodyLeft, CardGeometry.BodyBottom - 1);
        (byte cb4, byte cg4, byte cr4, byte ca4) = Pixel(t.Bits, t.Stride, CardGeometry.BodyRight - 1, CardGeometry.BodyBottom - 1);
        Console.WriteLine($"   四角 alpha：左上 {ca1} / 右上 {ca2} / 左下 {ca3} / 右下 {ca4}（底板 {a}）");
        failed += Check($"A3 卡片四角都不是底板（圆角真的切掉了，四角 alpha 均 ≤ {NotBodyMaxAlpha}）",
            ca1 <= NotBodyMaxAlpha && ca2 <= NotBodyMaxAlpha && ca3 <= NotBodyMaxAlpha && ca4 <= NotBodyMaxAlpha);

        // 圆角半径**实测**：在卡片顶边下 2px 的整行里，找第一个"确实是底板"的像素。
        // 半径 r 的圆弧在 k 行处的内缩 = r - sqrt(r² - (r-k)²)：r=22、k=2 ⇒ 约 12.8px。
        // 半径改成 0 ⇒ 起点 0；改成 40 ⇒ 约 27 —— 都落在区间外。
        int fillStart = FirstBodyX(t.Bits, t.Stride, CardGeometry.BodyTop + 2);
        int inset = fillStart - CardGeometry.BodyLeft;
        Console.WriteLine($"   圆角实测：顶边下 2px 处填充起点 x={fillStart}（内缩 {inset}px；半径 {CardGeometry.Radius} 期望约 13）");
        failed += Check($"A3b 圆角半径实测落在 [{CornerProbeMinX},{CornerProbeMaxX}]px 内缩区间（= 半径 {CardGeometry.Radius}）",
            inset >= CornerProbeMinX && inset <= CornerProbeMaxX);

        // ---- A4 渲染器确实写进了**调用方给的**那块缓冲 ----
        int untouched = CountSentinel(t.Bits, W, H, t.Stride, 0xCD);
        failed += Check($"A4 调用方的缓冲被完整写入（哨兵残留 {untouched} 字节组，期望 0）", untouched == 0);

        // ---- A5 投影边距够宽：窗口最外一圈仍透明 ----
        // 这是"投影没被窗口边缘切断"的客观证据。把 sigma 调大（或把边距调小）会立刻失败。
        int ringMax = MaxAlphaInOuterRing(t.Bits, W, H, t.Stride);
        Console.WriteLine($"   窗口最外一圈最大 alpha：{ringMax}（期望 ≤ {OuterRingMaxAlpha}；边距 {CardGeometry.ShadowMargin}px、sigma {CardTheme.ShadowSigma}）");
        failed += Check($"A5 投影边距够宽：窗口最外一圈 alpha ≤ {OuterRingMaxAlpha}", ringMax <= OuterRingMaxAlpha);

        // ---- A6 可落盘 → 供人眼离线比对 ----
        string png = Path.Combine(dir, "card-full.png");
        SavePng(t.Bits, W, H, t.Stride, png);
        failed += Check($"A6 渲染输出可落盘成 PNG：{Path.GetFileName(png)}", File.Exists(png));

        Console.WriteLine($"   小计：{(failed == 0 ? "全部通过" : failed + " 项失败")}");
        return failed;
    }

    // ================================================================ B 外观不变量

    private static int AppearanceChecks(string dir)
    {
        Console.WriteLine("---- B 外观不变量（投影 / 渐变 / 圆角 / 进度条 / 排版锚点）----");
        int failed = 0;

        CardModel model = FullModel();

        using Target t = new(W, H);
        using (var r = new SkiaCardRenderer(W, H)) r.Draw(t.Bits, W, H, t.Stride, model, null);
        SavePng(t.Bits, W, H, t.Stride, Path.Combine(dir, "card-full.png"));

        // ---- B1 外侧柔和投影 ----
        int shadowX = CardGeometry.BodyLeft + CardGeometry.CardWidth / 2;
        (byte sb, byte sg, byte sr, byte sa) = Pixel(t.Bits, t.Stride, shadowX, CardGeometry.BodyBottom + 3);
        (byte eb, byte eg, byte er, byte ea) = Pixel(t.Bits, t.Stride, shadowX, H - 2);
        Console.WriteLine($"   投影像素：卡片下缘 +3 → B={sb} G={sg} R={sr} A={sa}；窗口下缘 -2 → B={eb} G={eg} R={er} A={ea}");
        failed += Check($"B1 卡片下缘外确有投影（A={sa} > 0）", sa > 0);
        failed += Check($"B1b 投影是半透明而不是实心块（0 < A={sa} < 底板 {CardTheme.BodyAlpha}）", sa < CardTheme.BodyAlpha);
        failed += Check($"B1c 投影是中性暗色（R/G/B 互差 ≤ 2，实得 {sr}/{sg}/{sb}）",
            Within(sr, sg, 2) && Within(sg, sb, 2));
        failed += Check($"B1d 投影朝窗口边缘衰减（下缘 +3 处 {sa} > 窗口下缘处 {ea}）", sa > ea);

        // ---- B2 底板是垂直微渐变（顶亮底暗），两端 alpha 相同 ----
        // 取样必须在**内辉光覆盖范围之外**（顶部 45%），否则量到的是辉光不是底板。
        // 同时要**离左右边与底边都够远**（≥ 圆角半径 22）：贴着边取样会落到圆角外面，
        // 量到的就不是底板而是影子 —— 这正是第一轮自检踩到的坑（y=417 在左下圆角之外）。
        int gradTop = CardGeometry.BodyTop + (int)(CardGeometry.CardHeight * (CardTheme.GlowSpanRatio + 0.05f));
        int gradBottom = CardGeometry.BodyBottom - 30;
        int gradX = CardGeometry.BodyLeft + 30;
        (byte tb, byte tg, byte tr, byte ta) = Pixel(t.Bits, t.Stride, gradX, gradTop);
        (byte bb, byte bg, byte br, byte ba) = Pixel(t.Bits, t.Stride, gradX, gradBottom);
        Console.WriteLine($"   底板渐变：y={gradTop} → B={tb} G={tg} R={tr} A={ta}；y={gradBottom} → B={bb} G={bg} R={br} A={ba}");
        failed += Check($"B2 底板是垂直渐变（上端明显更亮：{tr + tg + tb} > {br + bg + bb} + 8）",
            tr + tg + tb > br + bg + bb + 8);
        failed += Check($"B2b 渐变两端 alpha 相同（都是 {CardTheme.BodyAlpha}：{ta} / {ba}）",
            ta == CardTheme.BodyAlpha && ba == CardTheme.BodyAlpha);

        // ---- B3 限额进度条（几何 + 比例映射）----
        // 行位由 CardLayout 的间距累加得出；下面同时把**字面量**打出来，
        // 于是"改了间距但忘记重新核对锚点"会以数字的形式暴露在输出里。
        int barTop = CardGeometry.BodyTop + (int)(CardLayout.TitleTop + CardLayout.TitleToTotal + CardLayout.TotalToBar);
        int barBottom = barTop + (int)CardLayout.BarHeight;
        int barLeft = CardGeometry.BodyLeft + (int)CardLayout.Pad;
        int barRight = CardGeometry.BodyRight - (int)CardLayout.Pad;

        int fillRight = RightmostAccentX(t.Bits, t.Stride, barLeft, barRight, barTop, barBottom);
        int expectedFillRight = barLeft + (int)((barRight - barLeft) * model.LimitRatio);
        Console.WriteLine($"   进度条：条带 y={barTop}..{barBottom}、x={barLeft}..{barRight}（字面量，布局改了要重新核对）；" +
                          $"填充右端实测 {fillRight}，期望 ≈ {expectedFillRight}（比例 {model.LimitRatio:P0}）");
        failed += Check("B3 填充宽度 = 条宽 × 比例（±3px）", Math.Abs(fillRight - expectedFillRight) <= 3);
        failed += Check("B3b 条带高度锁死：带内最下一行仍是填充、带外下一行已不是填充",
            IsAccentAt(t.Bits, t.Stride, barLeft + 4, barBottom - 1) &&
            !IsAccentAt(t.Bits, t.Stride, barLeft + 4, barBottom + 2));
        failed += Check("B3c 条带上方一格不是填充（高度没有向上溢出）",
            !IsAccentAt(t.Bits, t.Stride, barLeft + 4, barTop - 1));
        failed += Check("B3d 未填充的右端是**轨道**（比底板亮、但仍没到强调色）= 圆头轨道存在",
            AlphaAt(t.Bits, t.Stride, barRight - 2, barTop + 2) > AlphaAt(t.Bits, t.Stride, barRight - 2, barBottom + 3) + 3 &&
            !IsAccentAt(t.Bits, t.Stride, barRight - 2, barTop + 2));

        // ---- B4 榜单迷你条（第一行）----
        int rowTop = barTop
                     + (int)CardLayout.BarToLimit + (int)CardLayout.LimitToCurrent
                     + (int)CardLayout.CurrentToDivider + (int)CardLayout.DividerToRows;
        int miniTop = rowTop + (int)CardLayout.RowBarOffset;
        int miniBottom = miniTop + (int)CardLayout.RowBarHeight;
        Console.WriteLine($"   迷你条：第一行 y={miniTop}..{miniBottom}（行首 {rowTop}）");
        failed += Check("B4 迷你条已画（行内填充是强调色），且行下方没有溢出",
            IsAccentAt(t.Bits, t.Stride, barLeft + 4, miniTop + 1) &&
            !IsAccentAt(t.Bits, t.Stride, barLeft + 4, miniBottom + 2));

        // ---- B5 排版锚点（用"有 ink / 无 ink 两次渲染相减"量真实墨迹）----
        using (var ink = new SkiaCardRenderer(W, H))
        {
            Rectangle? title = InkBounds(ink, WithTitle("MM"), BlankModel());
            Rectangle? total = InkBounds(ink, WithTotal("0:00"), BlankModel());
            Rectangle? rowTime = InkBounds(ink, WithTime("M:MM"), WithTime(""));

            float declaredTitleTop = CardGeometry.BufferY(CardLayout.TitleTop);
            float declaredTotalTop = CardGeometry.BufferY(CardLayout.TitleTop + CardLayout.TitleToTotal);
            float declaredLeft = CardGeometry.BufferX(CardLayout.Pad);
            float declaredRight = CardGeometry.BufferX(CardGeometry.CardWidth - CardLayout.Pad);

            Console.WriteLine($"   墨迹实测：标题(13px)={Describe(title)}  声明行位 top={declaredTitleTop} left={declaredLeft}");
            Console.WriteLine($"   墨迹实测：总时长(34px)={Describe(total)}  声明行位 top={declaredTotalTop} left={declaredLeft}");
            Console.WriteLine($"   墨迹实测：榜单时间(13px 粗)={Describe(rowTime)}  声明右缘={declaredRight}");

            failed += Check("B5a 三段文字都画出了墨迹（字体没掉、字形真的出来了）",
                title is not null && total is not null && rowTime is not null);

            // 左对齐：墨迹起点必须贴着声明左缘（差 ≤2px）。
            // 量的是**墨迹**而不是"画笔位置"，所以它同时证明"字真的被放在了那里"。
            //
            // 总时长那一行不能这样断言：<c>"今日 "</c> 是**固定前缀**，两次渲染都会画，
            // 相减之后只剩数字的墨迹 —— 它的起点必然在左缘**之后**（这正是这方法能精确
            // 孤立出一段文字的原因）。所以改判"数字确实排在前缀之后"。
            failed += Check($"B5b 左对齐墨迹起点 ≈ 内边距（标题 {title?.Left}，声明 {declaredLeft}）",
                title is not null && Math.Abs(title.Value.Left - declaredLeft) <= InkTolerance);
            failed += Check($"B5b2 总时长数字排在前缀之后（数字墨迹起点 {total?.Left} > 左缘 {declaredLeft} + 20）",
                total is not null && total.Value.Left > declaredLeft + 20);

            // 右对齐：墨迹右缘必须正好落在右内边距上（最容易因字体度量/量错画笔而崩的一条）。
            failed += Check($"B5c 右对齐墨迹右缘 ≈ 右内边距（实测 {rowTime?.Right}，声明 {declaredRight}，容差 {RightAlignTolerance}）",
                rowTime is not null && Math.Abs(rowTime.Value.Right - declaredRight) <= RightAlignTolerance);

            // 墨迹**顶边**：声明行位到墨迹顶边之间隔着字形上沿的空白，宽度随字号变化。
            // 这里不猜公式，而是钉实测值（见 TitleInkTop / TotalInkTop 的说明）。
            failed += Check($"B5d 标题墨迹顶边 = {TitleInkTop}（实测 {title?.Top}，声明行位 {declaredTitleTop}）",
                title is not null && title.Value.Top == TitleInkTop);
            failed += Check($"B5e 总时长墨迹顶边 = {TotalInkTop}（实测 {total?.Top}，声明行位 {declaredTotalTop}）",
                total is not null && total.Value.Top == TotalInkTop);
        }

        // ---- B6 最坏情形（5 行榜单 + 2 行页脚）不溢出卡片底边 ----
        // 页脚行位 = 行首 + 5 × 行距；它的墨迹必须落在卡片内、且确实画在靠近底边处。
        int trailerTop = rowTop + (int)(CardLayout.RowStep * CardLayout.MaxRows);
        int trailerBottom = trailerTop + (int)(CardLayout.TrailerStep * CardLayout.MaxTrailers);
        int brightFooter = CountBrightInRect(t.Bits, t.Stride,
            new Rectangle(CardGeometry.BodyLeft, trailerTop - 4, CardGeometry.CardWidth, trailerBottom - trailerTop + 6));
        int brightOverflow = CountBrightInRect(t.Bits, t.Stride,
            new Rectangle(CardGeometry.BodyLeft, CardGeometry.BodyBottom - 2, CardGeometry.CardWidth, 2));
        Console.WriteLine($"   最坏情形：页脚带 y={trailerTop}..{trailerBottom}，其内亮像素 {brightFooter}；" +
                          $"卡片底边 2px 内亮像素 {brightOverflow}");
        failed += Check("B6 页脚确实画在紧邻底边处（该带内有文字墨迹）", brightFooter > 0);
        failed += Check("B6b 内容没有溢出卡片底边（底边 2px 内无文字墨迹）", brightOverflow == 0);

        // ---- B6c 数据策略不得超过排版容量 ----
        // "显示几行"（数据策略，CardData.MaxRows）与"装得下几行"（排版容量，CardLayout.MaxRows）
        // 是两个各有一个 5 的东西。这里把它们的**关系**钉住，于是把其中任何一个调大都会被立刻发现。
        failed += Check($"B6c 榜单行数上限（数据 {CardData.MaxRows}）不超过排版容量（{CardLayout.MaxRows}）",
            CardData.MaxRows <= CardLayout.MaxRows);

        Console.WriteLine($"   小计：{(failed == 0 ? "全部通过" : failed + " 项失败")}");
        return failed;
    }

    // ================================================================ C 接缝与契约

    private static int SeamChecks()
    {
        Console.WriteLine("---- C 接缝与契约（选择单点 / 参数校验 / 状态化）----");
        int failed = 0;

        // ---- C1 渲染器选择只有一个答案 ----
        using (ICardRenderer created = CardRendererFactory.Create(W, H))
            failed += Check("C1 工厂产出 SkiaCardRenderer（决策 D84）", created is SkiaCardRenderer);

        // ---- C2 渲染器是**有状态的**：同一实例连续两帧结果一致（可复用、无累积副作用）----
        using (var renderer = new SkiaCardRenderer(W, H))
        using (Target t1 = new(W, H))
        using (Target t2 = new(W, H))
        {
            renderer.Draw(t1.Bits, W, H, t1.Stride, FullModel(), null);
            renderer.Draw(t2.Bits, W, H, t2.Stride, FullModel(), null);
            failed += Check("C2 同一实例连续两帧输出一致（有状态但幂等，可长期复用）",
                DiffInRect(t1.Bits, t2.Bits, t1.Stride, new Rectangle(0, 0, W, H)) == 0);
        }

        // ---- C3 参数校验必须**硬失败**（画到别处不会报错，只会静默变丑）----
        using (var renderer = new SkiaCardRenderer(W, H))
        using (Target t = new(W, H))
        {
            failed += Check("C3a 空缓冲 → 抛 ArgumentException", Throws<ArgumentException>(() =>
                renderer.Draw(IntPtr.Zero, W, H, t.Stride, FullModel(), null)));
            failed += Check("C3b 行距不足 → 抛 ArgumentOutOfRangeException（错行距会让图像斜切）",
                Throws<ArgumentOutOfRangeException>(() => renderer.Draw(t.Bits, W, H, W * 4 - 4, FullModel(), null)));
            failed += Check("C3c 尺寸与创建时不符 → 抛 ArgumentException",
                Throws<ArgumentException>(() => renderer.Draw(t.Bits, W - 1, H, t.Stride, FullModel(), null)));
            failed += Check("C3d 非正尺寸 → 抛 ArgumentOutOfRangeException",
                Throws<ArgumentOutOfRangeException>(() => renderer.Draw(t.Bits, 0, H, t.Stride, FullModel(), null)));
        }

        // ---- C4 渲染器只接受"几何推出来的"尺寸（否则卡片会画歪且不报错）----
        failed += Check("C4 尺寸与 CardGeometry 不符 → 构造时硬失败",
            Throws<ArgumentException>(() => { using var r = new SkiaCardRenderer(W - 1, H); }));

        // ---- C5 调试标记是 Draw 的**一等参数**（传 null 就没有标记）----
        using (Target withMarker = new(W, H))
        using (Target without = new(W, H))
        {
            using (var r = new SkiaCardRenderer(W, H)) r.Draw(withMarker.Bits, W, H, withMarker.Stride, FullModel(), CardMarkers.DefaultRect);
            using (var r = new SkiaCardRenderer(W, H)) r.Draw(without.Bits, W, H, without.Stride, FullModel(), null);

            failed += Check("C5a 传矩形 → 标记区域恰好是标记色（不透明、不被预乘/色彩管理改写）",
                Pixel(withMarker.Bits, withMarker.Stride, CardMarkers.DefaultRect.Left + 2, CardMarkers.DefaultRect.Top + 2)
                    is (170, 0, 255, 255));
            failed += Check("C5b 传 null → 全图不含标记色（证明标记由参数驱动，不是渲染器的私事）",
                CountColor(without.Bits, W, H, without.Stride, 255, 0, 170) == 0);
            failed += Check("C5c 差异只出现在标记矩形内（标记没有污染别处）",
                DiffOutsideRect(withMarker.Bits, without.Bits, W, H, withMarker.Stride, CardMarkers.DefaultRect) == 0);
        }

        // ---- C6 LayeredSurface 的缓冲契约自洽（bits/stride/size 三者一致）----
        using (var surface = new LayeredSurface(W, H))
        {
            failed += Check("C6 LayeredSurface 暴露的像素缓冲可用且契约自洽",
                surface.Bits != IntPtr.Zero && surface.Stride == CardRenderTarget.StrideFor(W) &&
                surface.Width == W && surface.Height == H && surface.Size == new Size(W, H));
        }

        // ---- C7 Dispose 幂等（卡片线程的 finally 会无条件调用）----
        bool idempotent = true;
        try
        {
            var r1 = new SkiaCardRenderer(W, H); r1.Dispose(); r1.Dispose();
        }
        catch { idempotent = false; }
        failed += Check("C7 渲染器的 Dispose 幂等（重复释放不抛）", idempotent);

        Console.WriteLine($"   小计：{(failed == 0 ? "全部通过" : failed + " 项失败")}");
        return failed;
    }

    // ================================================================ D 窗口层回归

    private static int WindowLayerChecks(string dir)
    {
        Console.WriteLine("---- D 窗口层回归（真实卡片：样式/穿透/z 序 + 截屏像素）----");
        int failed = 0;

        using var card = new CardWindow(
            () => FullModel(),
            WidgetMode.Embedded,
            x: CardWindow.DefaultX,
            y: CardWindow.DefaultY,
            debugMarker: CardMarkers.DefaultRect);

        if (!card.Start())
        {
            Console.WriteLine($"   [FAIL] 卡片启动失败：{card.LastError ?? "未知原因"}");
            Console.WriteLine("   小计：1 项失败");
            return 1;
        }

        IntPtr hwnd = card.Handle;
        failed += Check("D1 启动成功、首帧已画完、句柄有效（Skia 渲染器在卡片线程上跑通）",
            card.Frames > 0 && hwnd != IntPtr.Zero && NativeMethods.IsWindow(hwnd));

        Console.WriteLine("   扩展样式：" + card.DescribeExStyle());
        failed += Check("D2 仍是分层窗口（LAYERED：逐像素 alpha 没丢）",
            HasStyle(hwnd, NativeMethods.WS_EX_LAYERED, want: true));
        failed += Check("D3 嵌入形态仍是 穿透 + 不抢焦点（TRANSPARENT | NOACTIVATE）",
            HasStyle(hwnd, NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE, want: true));
        failed += Check($"D4 命中测试 = HTTRANSPARENT（鼠标穿透行为未变，实得 {HitTest(hwnd)}）",
            HitTest(hwnd) == NativeMethods.HTTRANSPARENT);
        failed += Check("D5 不是 topmost（B+ 语义未变）", !IsTopMost(hwnd));

        int cardZ = NativeMethods.ZOrderIndex(hwnd);
        Console.WriteLine($"   嵌入 z 序：Card={cardZ}（0 = 最顶层）· {card.DescribeZOrder()}");
        failed += Check($"D6 不在最顶层（其上确有窗口 ⇒ 仍会被盖住），CardZ={cardZ}", cardZ > 0);

        failed += Check("D7 渲染器仍在持续出帧（消息循环与定时器未被打断）", FramesAdvance(card));

        // ---- D8 截屏像素：卡片真的上了屏（M0 的判据，唯一算数的判据）----
        Thread.Sleep(1200);
        string png = ScreenCapture.Capture(Path.Combine(dir, "onscreen.png"));

        // 关键：**限定在卡片自己的矩形内**统计（M13 实测教训 —— 全屏扫描会误命中别处的相似像素）。
        Rectangle virtualScreen = ScreenCapture.VirtualScreenBounds();
        int cardLeft = card.X - virtualScreen.Left;
        int cardTop = card.Y - virtualScreen.Top;
        var cardRegion = new Rectangle(cardLeft, cardTop, card.Width, card.Height);
        var hit = ScreenCapture.Analyze(png, CardWindow.DebugMarkerColor, region: cardRegion);
        int anywhere = ScreenCapture.Analyze(png, CardWindow.DebugMarkerColor).Count;
        Console.WriteLine($"   截屏：{Path.GetFileName(png)}；卡片区域 {cardRegion}；" +
                          $"区域内标记像素 {hit.Count}（全屏共 {anywhere}），包围盒={(hit.Count > 0 ? hit.Bounds.ToString() : "(无)")}");

        if (hit.Visible)
        {
            failed += Check("D8 卡片确实上屏（**卡片自己的矩形内**找到不透明标记色块）✅", true);
        }
        else
        {
            // 桌面卡片**允许**被别的窗口盖住（B+ 的已知局限）——那是预期，不算失败；
            // 但若该点没有被任何外部窗口覆盖，却仍找不到标记，那就是"没上屏"。
            var probe = new Point(card.X + 22, card.Y + 22);
            string over = ScreenCapture.TopWindowOver(probe);
            bool covered = !over.StartsWith("（无", StringComparison.Ordinal);
            Console.WriteLine($"   该点最上层外部窗口：{over}");
            failed += Check(covered
                ? "D8 卡片不可见，但该点被其它窗口覆盖 → 属 B+ 已知局限，判为可接受"
                : "D8 卡片不可见且该点无遮挡 → 像素没上屏 ❌", covered);
        }

        failed += Check("D9 截图与自检产物已落盘", File.Exists(png));

        card.RequestClose();
        failed += Check("D10 卡片线程正常退出", card.WaitForExit(10000));
        failed += Check("D11 退出后窗口句柄已失效", !NativeMethods.IsWindow(hwnd));

        Console.WriteLine($"   小计：{(failed == 0 ? "全部通过" : failed + " 项失败")}");
        return failed;
    }

    // ================================================================ 测试数据

    /// <summary>覆盖全部绘制分支的确定性模型（限额 / Top5 / 两行页脚 / 中文）。</summary>
    private static CardModel FullModel() => new()
    {
        Title = "ScreenSpy",
        TotalTime = "3:42",
        LimitText = "3:42 / 5:00",
        LimitRatio = 0.74,
        CurrentApp = "Microsoft Edge",
        NonAppTime = "1:23",
        NoWindowTime = "0:18",
        TopApps = new List<(string Name, string Time, double Ratio)>
        {
            ("Microsoft Edge", "1:42", 0.92),
            ("Visual Studio", "0:58", 0.52),
            ("记事本", "0:21", 0.19),
            ("任务管理器", "0:12", 0.11),
            ("微信", "0:09", 0.08),
        },
    };

    /// <summary>
    /// "空白模型"：只留下固定前缀（<c>"今日 "</c> 与 <c>"正在使用："</c> 是**永远会画**的）、
    /// 底板、投影、分隔线 —— 没有任何可变内容。
    ///
    /// 用途：与"只多一段文字"的模型相减，就能把那段文字的**真实墨迹**孤立出来量尺寸。
    /// </summary>
    private static CardModel BlankModel() => new()
    {
        Title = "",
        TotalTime = "",
        LimitText = "",
        CurrentApp = "",
        NonAppTime = "",
        NoWindowTime = "",
        TopApps = new List<(string Name, string Time, double Ratio)>(),
    };

    private static CardModel WithTitle(string title)
    {
        CardModel m = BlankModel();
        m.Title = title;
        return m;
    }

    private static CardModel WithTime(string time)
    {
        CardModel m = BlankModel();
        m.TopApps = new List<(string Name, string Time, double Ratio)> { ("", time, 0.0) };
        return m;
    }

    private static CardModel WithTotal(string total)
    {
        CardModel m = BlankModel();
        m.TotalTime = total;
        return m;
    }

    // ================================================================ 像素小工具

    private static string Describe(Rectangle? r) => r?.ToString() ?? "(无)";

    private static bool Within(byte a, byte b, int tolerance) => Math.Abs(a - b) <= tolerance;

    /// <summary>把"设计意图里的原色"按预乘算成期望字节（**自检自己算**，不借助被检查的库的另一条代码路径）。</summary>
    private static (byte B, byte G, byte R, byte A) Premultiply(SKColor c)
    {
        byte a = c.Alpha;
        return ((byte)Math.Round(c.Blue * a / 255.0),
                (byte)Math.Round(c.Green * a / 255.0),
                (byte)Math.Round(c.Red * a / 255.0),
                a);
    }

    private static byte AlphaAt(IntPtr bits, int stride, int x, int y) => Pixel(bits, stride, x, y).A;

    /// <summary>该像素是不是"强调色填充"（进度条/迷你条）：不透明 + 明显偏蓝（白字与轨道都不满足）。</summary>
    private static bool IsAccentAt(IntPtr bits, int stride, int x, int y)
    {
        (byte b, byte _, byte r, byte a) = Pixel(bits, stride, x, y);
        return a == 255 && b > r + 30;
    }

    /// <summary>在一行里找第一个"确实是底板"的像素（alpha ≥ 150，把几十量级的投影排除在外）。</summary>
    private static int FirstBodyX(IntPtr bits, int stride, int y)
    {
        for (int x = 0; x < W; x++)
            if (AlphaAt(bits, stride, x, y) >= 150) return x;
        return -1;
    }

    /// <summary>在某段行区间内从右往左找最后一个强调色像素（-1 = 没有）。</summary>
    private static int RightmostAccentX(IntPtr bits, int stride, int xFrom, int xTo, int yFrom, int yTo)
    {
        for (int x = xTo; x >= xFrom; x--)
            for (int y = yFrom; y < yTo; y++)
                if (IsAccentAt(bits, stride, x, y)) return x;
        return -1;
    }

    /// <summary>窗口最外一圈的最大 alpha（投影边距是否够宽的客观判据）。</summary>
    private static int MaxAlphaInOuterRing(IntPtr bits, int w, int h, int stride)
    {
        int max = 0;
        for (int x = 0; x < w; x++)
        {
            max = Math.Max(max, AlphaAt(bits, stride, x, 0));
            max = Math.Max(max, AlphaAt(bits, stride, x, h - 1));
        }
        for (int y = 0; y < h; y++)
        {
            max = Math.Max(max, AlphaAt(bits, stride, 0, y));
            max = Math.Max(max, AlphaAt(bits, stride, w - 1, y));
        }
        return max;
    }

    /// <summary>统计矩形内"文字墨迹"像素（不透明的亮像素：白字核心；底板 alpha 222、影子更暗，都不满足）。</summary>
    private static int CountBrightInRect(IntPtr bits, int stride, Rectangle rect)
    {
        int count = 0;
        for (int y = rect.Top; y < rect.Bottom; y++)
        {
            if (y < 0 || y >= H) continue;
            for (int x = rect.Left; x < rect.Right; x++)
            {
                if (x < 0 || x >= W) continue;
                if (IsAccentAt(bits, stride, x, y)) continue;   // 强调色不是文字墨迹
                (byte b, byte g, byte r, byte a) = Pixel(bits, stride, x, y);
                if (a == 255 && Math.Min(r, Math.Min(g, b)) > 80) count++;
            }
        }
        return count;
    }

    /// <summary>
    /// 量出"某段文字"的真实墨迹包围盒：同一渲染器画两次（有 ink / 无 ink）后求差异像素的包围盒。
    /// 两次渲染之间只有那一段文字不同 —— 其余一切（投影/底板/内辉光/固定前缀）都会相互抵消。
    /// </summary>
    private static Rectangle? InkBounds(ICardRenderer renderer, CardModel withInk, CardModel blank)
    {
        using Target a = new(W, H);
        using Target b = new(W, H);
        renderer.Draw(a.Bits, W, H, a.Stride, withInk, null);
        renderer.Draw(b.Bits, W, H, b.Stride, blank, null);
        return BoundsOfDiff(a.Bits, b.Bits, W, H, a.Stride);
    }

    /// <summary>两块缓冲的差异像素包围盒；完全相同时返回 <c>null</c>。</summary>
    private static Rectangle? BoundsOfDiff(IntPtr a, IntPtr b, int w, int h, int stride)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        unsafe
        {
            for (int y = 0; y < h; y++)
            {
                byte* ra = (byte*)a + (long)y * stride;
                byte* rb = (byte*)b + (long)y * stride;
                for (int x = 0; x < w; x++)
                {
                    byte* pa = ra + (long)x * 4;
                    byte* pb = rb + (long)x * 4;
                    if (pa[0] == pb[0] && pa[1] == pb[1] && pa[2] == pb[2] && pa[3] == pb[3]) continue;

                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }
        }

        return maxX < 0 ? null : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    /// <summary>一块非托管像素缓冲（自顶向下 32bpp）。用哨兵填充，以便验证"渲染器真的写进来了"。</summary>
    private sealed class Target : IDisposable
    {
        public IntPtr Bits { get; }
        public int Width { get; }
        public int Height { get; }
        public int Stride { get; }
        private readonly int _bytes;

        public Target(int width, int height, byte sentinel = 0x00)
        {
            Width = width;
            Height = height;
            Stride = CardRenderTarget.StrideFor(width);
            _bytes = Stride * height;
            Bits = Marshal.AllocHGlobal(_bytes);

            unsafe
            {
                byte* p = (byte*)Bits;
                for (int i = 0; i < _bytes; i++) p[i] = sentinel;
            }
        }

        public void Dispose() => Marshal.FreeHGlobal(Bits);
    }

    private static (byte B, byte G, byte R, byte A) Pixel(IntPtr bits, int stride, int x, int y)
    {
        unsafe
        {
            byte* p = (byte*)bits + (long)y * stride + (long)x * 4;
            return (p[0], p[1], p[2], p[3]);
        }
    }

    /// <summary>统计仍是哨兵值（4 字节全等）的像素数——用来证明渲染器真的写了整块缓冲。</summary>
    private static int CountSentinel(IntPtr bits, int w, int h, int stride, byte sentinel)
    {
        int count = 0;
        unsafe
        {
            for (int y = 0; y < h; y++)
            {
                byte* row = (byte*)bits + (long)y * stride;
                for (int x = 0; x < w; x++)
                {
                    byte* p = row + (long)x * 4;
                    if (p[0] == sentinel && p[1] == sentinel && p[2] == sentinel && p[3] == sentinel) count++;
                }
            }
        }
        return count;
    }

    private static int CountColor(IntPtr bits, int w, int h, int stride, byte r, byte g, byte b)
    {
        int count = 0;
        unsafe
        {
            for (int y = 0; y < h; y++)
            {
                byte* row = (byte*)bits + (long)y * stride;
                for (int x = 0; x < w; x++)
                {
                    byte* p = row + (long)x * 4;
                    if (p[0] == b && p[1] == g && p[2] == r && p[3] == 255) count++;
                }
            }
        }
        return count;
    }

    /// <summary>统计两块缓冲在指定矩形内的差异像素数。</summary>
    private static int DiffInRect(IntPtr a, IntPtr b, int stride, Rectangle rect)
    {
        int count = 0;
        unsafe
        {
            for (int y = rect.Top; y < rect.Bottom; y++)
            {
                byte* ra = (byte*)a + (long)y * stride;
                byte* rb = (byte*)b + (long)y * stride;
                for (int x = rect.Left; x < rect.Right; x++)
                {
                    byte* pa = ra + (long)x * 4;
                    byte* pb = rb + (long)x * 4;
                    if (pa[0] != pb[0] || pa[1] != pb[1] || pa[2] != pb[2] || pa[3] != pb[3]) count++;
                }
            }
        }
        return count;
    }

    /// <summary>统计两块缓冲在指定矩形**之外**的差异像素数。</summary>
    private static int DiffOutsideRect(IntPtr a, IntPtr b, int w, int h, int stride, Rectangle rect)
    {
        int count = 0;
        unsafe
        {
            for (int y = 0; y < h; y++)
            {
                byte* ra = (byte*)a + (long)y * stride;
                byte* rb = (byte*)b + (long)y * stride;
                for (int x = 0; x < w; x++)
                {
                    if (x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom) continue;
                    byte* pa = ra + (long)x * 4;
                    byte* pb = rb + (long)x * 4;
                    if (pa[0] != pb[0] || pa[1] != pb[1] || pa[2] != pb[2] || pa[3] != pb[3]) count++;
                }
            }
        }
        return count;
    }

    /// <summary>
    /// 把缓冲存成 PNG（**供人眼评审外观**）。
    ///
    /// 必须做 **预乘 → 直通** 的转换：缓冲是预乘的（UpdateLayeredWindow 的要求），
    /// 而 PNG 解码器按"直通 alpha"解释 —— 直接把预乘字节拷进去，
    /// 半透明处（底板 alpha 222）会比屏幕上显示得**偏暗**，人眼据此评审就会被误导。
    /// </summary>
    private static void SavePng(IntPtr bits, int w, int h, int stride, string path)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < h; y++)
                {
                    byte* src = (byte*)bits + (long)y * stride;
                    byte* dst = (byte*)data.Scan0 + (long)y * data.Stride;
                    for (int x = 0; x < w; x++)
                    {
                        byte alpha = src[3];
                        dst[0] = Unpremultiply(src[0], alpha);
                        dst[1] = Unpremultiply(src[1], alpha);
                        dst[2] = Unpremultiply(src[2], alpha);
                        dst[3] = alpha;
                        src += 4;
                        dst += 4;
                    }
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bmp.Save(path, ImageFormat.Png);
    }

    /// <summary>预乘 → 直通（带四舍五入与上限钳制）。</summary>
    private static byte Unpremultiply(byte value, byte alpha)
    {
        if (alpha == 0) return 0;
        if (alpha == 255) return value;
        int v = (value * 255 + alpha / 2) / alpha;
        return (byte)(v > 255 ? 255 : v);
    }

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try { action(); return false; }
        catch (TException) { return true; }
        catch { return false; }
    }

    private static bool FramesAdvance(CardWindow card)
    {
        int before = card.Frames;
        Thread.Sleep(CardWindow.RedrawMs + 600);
        return card.Frames > before;
    }

    private static bool HasStyle(IntPtr hwnd, int mask, bool want)
    {
        int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        return ((ex & mask) == mask) == want;
    }

    private static bool IsTopMost(IntPtr hwnd)
        => (NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOPMOST) != 0;

    private static int HitTest(IntPtr hwnd)
        => NativeMethods.SendMessage(hwnd, NativeMethods.WM_NCHITTEST, IntPtr.Zero, IntPtr.Zero).ToInt32();

    private static int Check(string label, bool ok)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        return ok ? 0 : 1;
    }
}
