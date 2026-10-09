using System;
using System.Collections.Generic;
using System.Drawing;
using SkiaSharp;

namespace ScreenSpy.Rendering;

/// <summary>
/// SkiaSharp 版卡片渲染器（M13 换的引擎；其后"外观升级"按
/// <see cref="CardGeometry"/> / <see cref="CardTheme"/> / <see cref="CardLayout"/> 重设了观感）。
///
/// ── 路径 (b)：Skia 位图 → 拷进 DIB（决策 D88，未变）──
/// 画在 Skia 自己的 <see cref="SKBitmap"/> 上，再按行 <c>MemoryCopy</c> 进调用方的缓冲。
/// <c>Present</c>（DIB / memDC / UpdateLayeredWindow）那一整段因此一行未动 ——
/// 它才是 M0 用截屏像素判定验证过的部分，换引擎、改外观都不该碰它。
///
/// ── 像素契约（未变）──
/// <c>SKColorType.Bgra8888</c> + <c>SKAlphaType.Premul</c> + <c>colorSpace: null</c>：
/// 与 <c>UpdateLayeredWindow(AC_SRC_ALPHA)</c> 的要求逐字节一致。
/// **绝不能**传 sRGB：Skia 会做色彩管理转换，预乘 alpha 会被算错（半透明底板偏色）。
///
/// ── 外观升级改了什么 ──
/// <list type="bullet">
///   <item>缓冲比卡片大：多出 <see cref="CardGeometry.ShadowMargin"/> 一圈，专门用来画外侧柔和投影；</item>
///   <item>底板从单色改为**垂直微渐变** + 顶部内辉光 + 1px 边缘高光；</item>
///   <item>进度条 / 迷你条重做：高度一半的圆角（圆头）、更细的轨道、填充用强调色渐变；</item>
///   <item>排版：圆角 16→22、内边距与行距放宽、今日总时长 32→34（见 <see cref="CardLayout"/>）。</item>
/// </list>
///
/// ── 文本仍走 <c>SKPaint</c> 的文本 API ──
/// <c>TextSize</c> / <c>Typeface</c> / <c>MeasureText</c> / <c>FontMetrics</c> 是 **2.88 系**的形态；
/// 3.x 已改由 <c>SKFont</c> 承载且不兼容 —— 这也是版本必须钉死的原因之一。
/// </summary>
internal sealed class SkiaCardRenderer : ICardRenderer
{
    /// <summary>
    /// 字体回退链。顺序不能变：中文靠前两项，掉到 Segoe UI 就开始掉字。
    /// （M13 之前它与 GDI+ 版逐项一致；GDI+ 版移除后，这份链成为**唯一**的字体来源。）
    /// </summary>
    private static readonly string[] FontFallback =
    {
        "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Tahoma",
    };

    /// <summary>
    /// 已安装字体系列（只枚举一次）。用于避免"字体不存在就静默回退默认字体"——
    /// Skia 的 <c>FromFamilyName</c> 对不存在的族**不会失败**，那就成了"看起来正常、其实字体掉了"。
    /// </summary>
    private static readonly Lazy<HashSet<string>> InstalledFamilies =
        new(() => new HashSet<string>(SKFontManager.Default.GetFontFamilies(), StringComparer.OrdinalIgnoreCase));

    /// <summary>调试标记色：**逐字节等于** <see cref="CardMarkers.MarkerColor"/>（alpha=255，不透明覆盖）。</summary>
    private static readonly SKColor MarkerColor = new(255, 0, 170, 255);

    private readonly int _width;
    private readonly int _height;
    private readonly SKBitmap _bitmap;
    private readonly SKCanvas _canvas;
    private readonly SKImageFilter _shadow;

    private readonly SKPaint _paintTitle;
    private readonly SKPaint _paintTotal;
    private readonly SKPaint _paintMid;
    private readonly SKPaint _paintSmall;
    private readonly SKPaint _paintRowBold;

    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };

    private bool _disposed;

    /// <summary>
    /// 创建渲染器。尺寸**必须**等于 <see cref="CardGeometry"/> 推出来的窗口尺寸。
    ///
    /// 为什么在这里硬失败：渲染器内部所有坐标都按"卡片在缓冲的哪个位置"推导，
    /// 一旦窗口尺寸与几何假定不一致，画出来的会是一张**偏移的**卡片——
    /// 不报错、只是"看着有点歪"，正是本项目最忌讳的失败模式。
    /// </summary>
    public SkiaCardRenderer(int width, int height)
    {
        if (width != CardGeometry.WindowWidth || height != CardGeometry.WindowHeight)
            throw new ArgumentException(
                $"卡片缓冲必须是 {CardGeometry.WindowWidth}×{CardGeometry.WindowHeight}" +
                $"（卡片 {CardGeometry.CardWidth}×{CardGeometry.CardHeight} + 投影边距 {CardGeometry.ShadowMargin}×2），实得 {width}×{height}。",
                nameof(width));

        _width = width;
        _height = height;

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul, null);
        _bitmap = new SKBitmap(info);
        _canvas = new SKCanvas(_bitmap);

        _shadow = SKImageFilter.CreateDropShadowOnly(
                      0f, CardTheme.ShadowOffsetY, CardTheme.ShadowSigma, CardTheme.ShadowSigma, CardTheme.ShadowColor)
                  ?? throw new InvalidOperationException("SKImageFilter.CreateDropShadowOnly 返回 null：无法绘制投影。");

        _paintTitle = CreateTextPaint(bold: false, size: CardLayout.FontTitle);
        _paintTotal = CreateTextPaint(bold: true, size: CardLayout.FontTotal);
        _paintMid = CreateTextPaint(bold: false, size: CardLayout.FontMid);
        _paintSmall = CreateTextPaint(bold: false, size: CardLayout.FontSmall);
        _paintRowBold = CreateTextPaint(bold: true, size: CardLayout.FontTitle);
    }

    /// <summary>
    /// 文字画笔：**不用 ClearType 子像素**（<c>LcdRenderText=false</c>）——
    /// 逐像素 alpha 的卡片上，子像素抗锯齿会让字形边缘带上彩色，在深色底上尤其脏。
    /// </summary>
    private static SKPaint CreateTextPaint(bool bold, float size) => new()
    {
        Typeface = ResolveTypeface(bold),
        TextSize = size,
        IsAntialias = true,
        SubpixelText = false,
        LcdRenderText = false,
    };

    /// <summary>按回退链取字体；**只认真正安装了的族**（见 <see cref="InstalledFamilies"/>）。</summary>
    private static SKTypeface ResolveTypeface(bool bold)
    {
        HashSet<string> installed = InstalledFamilies.Value;
        SKFontStyle style = bold ? SKFontStyle.Bold : SKFontStyle.Normal;

        foreach (string name in FontFallback)
        {
            if (!installed.Contains(name)) continue;
            SKTypeface? tf = SKTypeface.FromFamilyName(name, style);
            if (tf is not null) return tf;
        }

        return SKTypeface.CreateDefault();
    }

    public void Draw(IntPtr bits, int width, int height, int stride, CardModel model, Rectangle? debugMarker)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CardRenderTarget.Validate(bits, width, height, stride);

        if (width != _width || height != _height)
            throw new ArgumentException($"渲染器按 {_width}×{_height} 创建，不能画 {width}×{height}。", nameof(width));

        DrawContent(model, debugMarker);
        CopyTo(bits, stride);
    }

    /// <summary>把 Skia 位图按行拷进调用方的缓冲（**路径 (b) 的那一次拷贝**）。</summary>
    private void CopyTo(IntPtr bits, int stride)
    {
        _canvas.Flush();

        int rowBytes = _width * 4;
        IntPtr source = _bitmap.GetPixels();
        int sourceStride = _bitmap.RowBytes;

        unsafe
        {
            for (int row = 0; row < _height; row++)
            {
                void* src = (byte*)source + (long)row * sourceStride;
                void* dst = (byte*)bits + (long)row * stride;
                Buffer.MemoryCopy(src, dst, rowBytes, rowBytes);
            }
        }
    }

    // ================================================================ 绘制

    private void DrawContent(CardModel m, Rectangle? debugMarker)
    {
        _canvas.Clear(SKColors.Transparent);

        SKRect body = CardGeometry.BodyRect;

        DrawShadow(body);
        DrawBody(body);

        float left = CardGeometry.BufferX(CardLayout.Pad);
        float right = CardGeometry.BufferX(CardGeometry.CardWidth - CardLayout.Pad);

        float y = CardLayout.TitleTop;

        // ---- 顶栏：标题 ----
        DrawText(m.Title, _paintTitle, CardTheme.Dim, left, CardGeometry.BufferY(y));

        // ---- 大字号今日总时长（今天一整天：本次运行 + 库中基线）----
        y += CardLayout.TitleToTotal;
        DrawText($"今日 {m.TotalTime}", _paintTotal, CardTheme.White, left, CardGeometry.BufferY(y));

        // ---- 限额进度条 ----
        // 当 LimitText 为空时**整块不画**（含进度条与文案），而不是画一个假限额；
        // 用"间距"而非"绝对 y"排版，于是下面的内容自然上移（M13 之前的既有行为，本次保留）。
        y += CardLayout.TotalToBar;
        if (!string.IsNullOrEmpty(m.LimitText))
        {
            bool over = m.LimitRatio >= 1.0;

            DrawBar(CardGeometry.BufferRect(CardLayout.Pad, y, CardGeometry.CardWidth - CardLayout.Pad, y + CardLayout.BarHeight),
                    m.LimitRatio,
                    over ? CardTheme.Danger : CardTheme.Accent,
                    gradient: !over,
                    minFill: 4f);

            y += CardLayout.BarToLimit;
            DrawText($"限额 {m.LimitText}", _paintSmall, CardTheme.Dim, left, CardGeometry.BufferY(y));
        }

        // ---- 当前软件 ----
        y += CardLayout.LimitToCurrent;
        DrawText($"正在使用：{m.CurrentApp}", _paintMid, CardTheme.White, left, CardGeometry.BufferY(y));

        // ---- 分隔线 ----
        y += CardLayout.CurrentToDivider + 7;
        _stroke.Shader = null;
        _stroke.Color = CardTheme.Divider;
        _canvas.DrawLine(left, CardGeometry.BufferY(y), right, CardGeometry.BufferY(y), _stroke);

        // ---- 榜单（最多 5 行）----
        y += CardLayout.DividerToRows;
        foreach ((string name, string time, double ratio) in m.TopApps)
        {
            DrawText(name, _paintTitle, CardTheme.Dim, left, CardGeometry.BufferY(y));

            // 右对齐：宽度按**画这一行用的那支画笔**量（量错画笔 = 右缘必然不齐）
            float timeWidth = Measure(time, _paintRowBold);
            DrawText(time, _paintRowBold, CardTheme.White, right - timeWidth, CardGeometry.BufferY(y));

            DrawBar(CardGeometry.BufferRect(CardLayout.Pad, y + CardLayout.RowBarOffset,
                                            CardGeometry.CardWidth - CardLayout.Pad, y + CardLayout.RowBarOffset + CardLayout.RowBarHeight),
                    ratio,
                    CardTheme.Accent,
                    gradient: false,
                    minFill: 3f);

            y += CardLayout.RowStep;
        }

        // ---- 非软件活跃（两行小字，空串 = 整行不画）----
        if (m.NonAppTime.Length > 0)
            DrawTrailerRow("不计入使用时长", m.NonAppTime, right, ref y);
        if (m.NoWindowTime.Length > 0)
            DrawTrailerRow("无前台/未知", m.NoWindowTime, right, ref y);

        // ---- 调试标记：**最后画**（不透明覆盖）----
        // 注意它画在**缓冲坐标**里（= 窗口左上角 + (10,10)），也就是落在投影边距区——这是刻意的：
        // 于是"命中包围盒 = 窗口位置 + (10,10)、尺寸 24×24"这条用了 M0/M13 的证据链完全不变。
        if (debugMarker is { } marker)
        {
            _fill.Shader = null;
            _fill.ImageFilter = null;
            _fill.Color = MarkerColor;
            _canvas.DrawRect(new SKRect(marker.Left, marker.Top, marker.Right, marker.Bottom), _fill);
        }
    }

    /// <summary>
    /// 外侧柔和投影。<c>CreateDropShadowOnly</c> 只产出"影子"本身、不含形状 ⇒ 随后画底板天然叠对。
    ///
    /// **刻意把卡片本体那一块裁掉**（<see cref="SKClipOperation.Difference"/>）：
    /// 影子是"以卡片形状为源"模糊出来的，不裁的话它也铺在卡片**底下**，
    /// 而卡片是半透明的（alpha 222），于是：
    ///   <list type="bullet">
    ///     <item>卡片内部被自己的影子压暗，且越靠边越暗（实测 alpha 222 → 235 不等）；</item>
    ///     <item>"底板 alpha 恰好 222" 这条本可在像素里观测的设计常量，就再也测不到了。</item>
    ///   </list>
    /// 裁掉之后卡片内部是**均匀**的声明色、影子只出现在卡片之外 —— 这才与
    /// <see cref="CardTheme.BodyAlpha"/> 的声明一致（自检 A1/A2/B2b 直接量这个数字）。
    /// </summary>
    private void DrawShadow(SKRect body)
    {
        using var clip = new SKPath();
        clip.AddRoundRect(body, CardGeometry.Radius, CardGeometry.Radius);

        _canvas.Save();
        _canvas.ClipPath(clip, SKClipOperation.Difference, antialias: true);

        _fill.Shader = null;
        _fill.ImageFilter = _shadow;
        _fill.Color = SKColors.White;   // 影子颜色由 filter 决定，这里只是"给 filter 一点东西去投影"
        _canvas.DrawRoundRect(body, CardGeometry.Radius, CardGeometry.Radius, _fill);
        _fill.ImageFilter = null;

        _canvas.Restore();
    }

    private void DrawBody(SKRect body)
    {
        // ---- 底板：垂直微渐变（顶亮底暗，给出"厚度"）----
        using (SKShader shader = SKShader.CreateLinearGradient(
            new SKPoint(body.Left, body.Top),
            new SKPoint(body.Left, body.Bottom),
            new[] { CardTheme.BodyTop, CardTheme.BodyBottom },
            SKShaderTileMode.Clamp))
        {
            UseShader(shader);
            _canvas.DrawRoundRect(body, CardGeometry.Radius, CardGeometry.Radius, _fill);
            ClearShader();
        }

        // ---- 磨砂层次：顶部内辉光 ----
        // 必须**裁剪在圆角内**：否则辉光矩形会从圆角外面漏出两个亮角（没被圆角切掉的白色楔形）。
        float glowBottom = body.Top + CardGeometry.CardHeight * CardTheme.GlowSpanRatio;
        using (SKShader glow = SKShader.CreateLinearGradient(
            new SKPoint(body.Left, body.Top),
            new SKPoint(body.Left, glowBottom),
            new[] { CardTheme.GlowTop, CardTheme.GlowFade },
            SKShaderTileMode.Clamp))
        using (var clip = new SKPath())
        {
            clip.AddRoundRect(body, CardGeometry.Radius, CardGeometry.Radius);

            _canvas.Save();
            _canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
            UseShader(glow);
            _canvas.DrawRect(new SKRect(body.Left, body.Top, body.Right, glowBottom), _fill);
            ClearShader();
            _canvas.Restore();
        }

        // ---- 边缘高光：1px 内描边（半像素内缩，让它落在像素正中而不是跨两个像素）----
        _stroke.Shader = null;
        _stroke.Color = CardTheme.EdgeHighlight;
        _canvas.DrawRoundRect(
            new SKRect(body.Left + 0.5f, body.Top + 0.5f, body.Right - 0.5f, body.Bottom - 0.5f),
            CardGeometry.Radius - 0.5f,
            CardGeometry.Radius - 0.5f,
            _stroke);
    }

    /// <summary>
    /// 进度条 / 迷你条：一条圆角轨道 + 一段按比例填充。
    /// 用条纹高度一半的圆角 ⇒ 两端是半圆（M13 之前是 8px 高配 4px 圆角，观感更"方"）。
    /// </summary>
    private void DrawBar(SKRect rect, double ratio, SKColor color, bool gradient, float minFill)
    {
        float radius = rect.Height / 2f;

        _fill.Shader = null;
        _fill.ImageFilter = null;
        _fill.Color = CardTheme.TrackBackground;
        _canvas.DrawRoundRect(rect, radius, radius, _fill);

        float fillWidth = (float)(rect.Width * Math.Clamp(ratio, 0.0, 1.0));
        if (fillWidth < minFill) return;   // 太小就只留轨道，避免出现一个"孤立的圆点"

        var filled = new SKRect(rect.Left, rect.Top, rect.Left + fillWidth, rect.Bottom);

        if (gradient)
        {
            using SKShader shader = SKShader.CreateLinearGradient(
                new SKPoint(filled.Left, filled.Top),
                new SKPoint(filled.Right, filled.Top),
                new[] { color, CardTheme.AccentLight },
                SKShaderTileMode.Clamp);

            UseShader(shader);
            _canvas.DrawRoundRect(filled, radius, radius, _fill);
            ClearShader();
        }
        else
        {
            _fill.Color = color;
            _canvas.DrawRoundRect(filled, radius, radius, _fill);
        }
    }

    /// <summary>
    /// 让填充画笔改用着色器。**必须同时把画笔颜色置为不透明白**：
    /// Skia 的画笔 alpha 会**乘**到着色器的输出上，上一步绘制留下的 alpha 会把渐变整体压暗。
    /// —— 这不是理论风险，是实测抓到的：画轨道时的 <c>alpha=46</c> 让进度条填充从
    /// <c>(118,196,255)</c> 变成 <c>(76,91,107)</c>（正好被压到 46/255 的量级），
    /// 而"填充是强调色"这件事只能靠读像素发现（看上去只是"颜色有点脏"）。
    /// </summary>
    private void UseShader(SKShader shader)
    {
        _fill.Color = SKColors.White;
        _fill.Shader = shader;
    }

    /// <summary>撤销 <see cref="UseShader"/>：留着 <c>Color</c> 的不透明白，让后续实心填充是显式的。</summary>
    private void ClearShader()
    {
        _fill.Shader = null;
        _fill.Color = SKColors.White;
    }

    /// <summary>画一行"标签 …… 数值"的页脚（左侧暗色标签、右侧白色数值）。</summary>
    private void DrawTrailerRow(string name, string text, float right, ref float y)
    {
        DrawText(name, _paintSmall, CardTheme.Dim, CardGeometry.BufferX(CardLayout.Pad), CardGeometry.BufferY(y));

        float textWidth = Measure(text, _paintSmall);
        DrawText(text, _paintSmall, CardTheme.White, right - textWidth, CardGeometry.BufferY(y));

        y += CardLayout.TrailerStep;
    }

    private void DrawText(string text, SKPaint paint, SKColor color, float x, float top)
    {
        paint.Color = color;
        _canvas.DrawText(text, x, Baseline(paint, top), paint);
    }

    /// <summary>
    /// "行框上沿 → 基线"换算。<c>SKPaint.FontMetrics.Ascent</c> 是负数，故为 <c>top - Ascent</c>。
    ///
    /// 为什么留一个显式常量：<see cref="CardLayout"/> 里的 y 是"行框上沿"，
    /// 而肉眼看到的是**墨迹顶边**，两者之间差了 (ascent − 字形实际顶端) 那段空白。
    /// 该差值随字号变化、无法用公式推出来（字体度量是浮点，落到像素取整上会 0~2px 不等），
    /// 因此取一个**实测标定**的统一补偿，并由 <c>--m13-selfcheck</c> B6 持续盯着：
    /// 改坏它（比如改回 0）会立刻让墨迹位置超出 ±2px 容差。
    /// </summary>
    private const float TextTopAdjust = 1f;

    private static float Baseline(SKPaint paint, float top) => top - paint.FontMetrics.Ascent + TextTopAdjust;

    private static float Measure(string text, SKPaint paint) => paint.MeasureText(text);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _canvas.Dispose();
        _bitmap.Dispose();
        _paintTitle.Dispose();
        _paintTotal.Dispose();
        _paintMid.Dispose();
        _paintSmall.Dispose();
        _paintRowBold.Dispose();
        _fill.Dispose();
        _stroke.Dispose();
        _shadow.Dispose();
        // 刻意不 Dispose Typeface：它由 Skia 的字体管理器返回并缓存，不是本对象的私有资源。
    }
}
