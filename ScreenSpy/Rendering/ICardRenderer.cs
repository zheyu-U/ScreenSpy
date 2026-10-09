using System;
using System.Drawing;

namespace ScreenSpy.Rendering;

/// <summary>
/// 卡片渲染器接缝（M13 建立，决策 D84/D85/D88）。
///
/// ── 为什么接口是"给我一块像素缓冲"，而不是"给你一块画布" ──
/// M13 之前 <c>LayeredSurface</c> 对外交出的是 <c>Graphics</c>（GDI+ 画布）：
/// 那等于把"用哪块内存、什么像素格式"的决定权交给了渲染器 —— 于是
/// "画成什么样"与"怎么上屏"搅在一起，连"渲染器有没有把整块缓冲写完"都无从断言。
///
/// 改成传 <c>bits</c> 之后，接缝本身就把输出格式钉死了：
/// <list type="bullet">
///   <item>**自顶向下**（顶行在前，与 <c>CreateDIBSection</c> 的 <c>biHeight</c> 为负一致）；</item>
///   <item>**32bpp、字节序 BGRA、预乘 alpha** —— 这是 <c>UpdateLayeredWindow(AC_SRC_ALPHA)</c> 的唯一要求；</item>
///   <item><c>stride = width * 4</c>。</item>
/// </list>
/// 于是自检可以**直接读像素**判事（预乘对不对、圆角外是否透明、投影有没有、文字落在哪），
/// 不必依赖任何"看起来对"。
///
/// ── 只有一个实现，为什么还留接口 ──
/// 因为 <c>CardWindow</c> 因此**不认识 Skia**：换引擎、改外观都只影响"谁往缓冲里画"。
/// 这条隔离是 M15 拆工程（UI 进程化）的前提，不是为"多实现"准备的。
///
/// ── 为什么是 IDisposable、按尺寸创建 ──
/// 路径 (b) 需要一块**常驻的**引擎侧位图（<c>SKBitmap</c>）。绝不能每帧新建
/// —— 卡片每秒一帧，常驻对象比每帧分配划算得多。因此实现都是有状态的，
/// 生命周期由 <c>CardWindow</c> 负责（在卡片线程上创建、在卡片线程上释放）。
/// </summary>
internal interface ICardRenderer : IDisposable
{
    /// <summary>
    /// 把 <paramref name="model"/> 画进调用方给出的像素缓冲（**调用方与被调用方都不持有对方的缓冲**）。
    /// </summary>
    /// <param name="bits">目标缓冲首字节（自顶向下 32bpp 预乘 BGRA）。</param>
    /// <param name="width">宽度（像素）。</param>
    /// <param name="height">高度（像素）。</param>
    /// <param name="stride">行距（字节）；约定 <c>width * 4</c>。</param>
    /// <param name="model">要画的纯数据（<see cref="CardModel"/> 契约自 M13 建立后一行未改）。</param>
    /// <param name="debugMarker">
    /// 调试标记矩形（<see cref="CardMarkers.MarkerColor"/> 的不透明实心块），<c>null</c> = 不画。
    ///
    /// **它必须是接口的一等参数**，而不是某个实现的私事：它是 M0 用截屏像素换来的、
    /// "卡片是否真的上了屏"的**唯一客观锚点**。外观升级后卡片外面多了一圈投影边距，
    /// 这条锚点仍然按**缓冲坐标**画（= 窗口左上角 + 偏移），
    /// 因此"命中包围盒 = 窗口位置 + (10,10)、24×24"这条证据链完全没变。
    /// </param>
    void Draw(IntPtr bits, int width, int height, int stride, CardModel model, Rectangle? debugMarker);
}

/// <summary>
/// 像素缓冲的契约常量与**入参校验**（渲染实现共用）。
///
/// 校验刻意做成**硬失败**：画错位置/错格式的像素不会报错，只会"看起来怪怪的"，
/// 那正是本项目最忌讳的一类错误（M0 的教训：API 全绿、像素不上屏）。宁可当场抛异常。
/// 自检里还有断言专门盯着它（改坏校验 → 必须命中 FAIL）。
/// </summary>
internal static class CardRenderTarget
{
    /// <summary>每像素字节数（32bpp）。</summary>
    public const int BytesPerPixel = 4;

    /// <summary>按契约推算的行距（<c>width * 4</c>）。</summary>
    public static int StrideFor(int width) => checked(width * BytesPerPixel);

    /// <summary>校验调用方给出的目标缓冲；不合法直接抛（不静默画到别处）。</summary>
    public static void Validate(IntPtr bits, int width, int height, int stride)
    {
        if (bits == IntPtr.Zero) throw new ArgumentException("像素缓冲为空。", nameof(bits));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "宽度必须为正数。");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "高度必须为正数。");

        int min = StrideFor(width);
        if (stride < min)
            throw new ArgumentOutOfRangeException(nameof(stride), stride, $"行距不足：至少 {min} 字节（width*4）。");
    }
}
