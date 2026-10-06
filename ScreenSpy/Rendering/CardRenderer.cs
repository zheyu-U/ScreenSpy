using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace ScreenSpy.Rendering;

/// <summary>
/// 把 <see cref="CardModel"/> 画成“半透明深色卡片 + 白色文字”。
/// 对应《开发文档》§5.9 的视觉规格。
///
/// 来源：M0 验证（Spike）中经实测有效的实现，原样移植；
/// 仅把页眉文案由验证用的“路线标识”改为 <see cref="CardModel.Title"/>。
///
/// M6 的两处调整（产品化）：
///  * 去掉顶栏的 <c>t=Ns</c> 秒针（M0 阶段的调试痕迹）；
///  * <see cref="CardModel.LimitText"/> 为空时整块不画限额（真实限额属 M9）。
///
/// 本次（口径统一）再补一处：
///  * 榜下方新增两行小字“不计入使用时长 / 无前台/未知”——它们在
///    <see cref="CardModel.NonAppTime"/>/<see cref="CardModel.NoWindowTime"/> 为 0 时自动隐藏。
/// </summary>
internal sealed class CardRenderer
{
    private static readonly FontFamily UiFamily = PickFamily("Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Tahoma");

    private static FontFamily PickFamily(params string[] names)
    {
        foreach (string name in names)
        {
            try
            {
                return new FontFamily(name);
            }
            catch (ArgumentException)
            {
                // 该字体不存在，继续尝试
            }
        }
        return FontFamily.GenericSansSerif;
    }

    public void Draw(Graphics g, Size size, CardModel m)
    {
        int w = size.Width;
        int h = size.Height;

        g.Clear(Color.Transparent);

        // ---- 卡片底板：半透明深色 + 1px 描边 ----
        using (var bg = new SolidBrush(Color.FromArgb(205, 18, 20, 25)))
        using (var border = new Pen(Color.FromArgb(64, 255, 255, 255), 1f))
        using (GraphicsPath path = RoundedRect(new Rectangle(0, 0, w - 1, h - 1), 16))
        {
            g.FillPath(bg, path);
            g.DrawPath(border, path);
        }

        using var white = new SolidBrush(Color.FromArgb(255, 245, 246, 250));
        using var dim = new SolidBrush(Color.FromArgb(185, 190, 195, 205));
        using var accent = new SolidBrush(Color.FromArgb(255, 118, 196, 255));
        using var barBg = new SolidBrush(Color.FromArgb(64, 255, 255, 255));

        using var fontSmall = new Font(UiFamily, 12.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var fontBig = new Font(UiFamily, 32f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fontMid = new Font(UiFamily, 15f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var fontRow = new Font(UiFamily, 13f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var fontRowBold = new Font(UiFamily, 13f, FontStyle.Bold, GraphicsUnit.Pixel);

        const int pad = 18;
        int y = 14;

        // ---- 顶栏：标题 ----
        // M6：不再画 "t=Ns" 秒针。那是 M0 阶段用来肉眼确认"卡片仍在刷新"的调试痕迹，
        // 产品卡片上留一个跳动的时间戳只会让人困惑。
        g.DrawString(m.Title, fontSmall, dim, pad, y);
        y += 24;

        // ---- 大字号今日总时长（今天一整天：本次运行 + 库中基线）----
        g.DrawString($"今日 {m.TotalTime}", fontBig, white, pad - 3, y);
        y += 48;

        // ---- 限额进度条 ----
        // M6：限额数据要 M9 才有。当 LimitText 为空时**整块不画**（含进度条与文案），
        // 而不是画一个 0:00 / 5:00 的假限额 —— 宁可少一块，也不显示不存在的约束。
        if (!string.IsNullOrEmpty(m.LimitText))
        {
            var barRect = new Rectangle(pad, y, w - pad * 2, 8);
            using (GraphicsPath p = RoundedRect(barRect, 4))
                g.FillPath(barBg, p);

            int fillW = (int)(barRect.Width * Math.Clamp(m.LimitRatio, 0.0, 1.0));
            if (fillW > 3)
            {
                using var danger = new SolidBrush(Color.FromArgb(255, 236, 92, 92));
                using GraphicsPath p = RoundedRect(new Rectangle(barRect.X, barRect.Y, fillW, barRect.Height), 4);
                g.FillPath(m.LimitRatio >= 1.0 ? danger : accent, p);
            }
            y += 20;

            g.DrawString($"限额 {m.LimitText}", fontRow, dim, pad, y);
            y += 22;
        }

        // ---- 当前软件 ----
        g.DrawString($"正在使用：{m.CurrentApp}", fontMid, white, pad, y);
        y += 30;

        // ---- 分隔线 ----
        using (var pen = new Pen(Color.FromArgb(46, 255, 255, 255), 1f))
            g.DrawLine(pen, pad, y, w - pad, y);
        y += 10;

        // ---- Top5 ----
        foreach (var (name, time, ratio) in m.TopApps)
        {
            g.DrawString(name, fontRow, dim, pad, y);
            float tw = g.MeasureString(time, fontRowBold).Width;
            g.DrawString(time, fontRowBold, white, w - pad - tw, y);

            var mini = new Rectangle(pad, y + 19, w - pad * 2, 4);
            using (GraphicsPath p = RoundedRect(mini, 2))
                g.FillPath(barBg, p);

            int fw = (int)(mini.Width * Math.Clamp(ratio, 0.0, 1.0));
            if (fw > 2)
            {
                using GraphicsPath p = RoundedRect(new Rectangle(mini.X, mini.Y, fw, mini.Height), 2);
                g.FillPath(accent, p);
            }

            y += 30;
        }

        // ---- 非软件活跃（本次统一口径）----
        // “不计入使用时长”（桌面/外壳/自身）与“无前台/未知”都**计入“今日真实活跃”**，
        // 只是不归属到任何软件。标在两行小字里，别人就不会以为这段时间被漏掉了。
        // 值为 0 时对应字段为空串，整行不画（避免“0:00”占版面）。
        y += 2;
        if (m.NonAppTime.Length > 0)
            DrawTrailerRow(g, fontSmall, dim, white, "不计入使用时长", m.NonAppTime, pad, w, ref y);
        if (m.NoWindowTime.Length > 0)
            DrawTrailerRow(g, fontSmall, dim, white, "无前台/未知", m.NoWindowTime, pad, w, ref y);
    }

    /// <summary>画一行“标签 …… 数值”的页脚（左侧暗色标签、右侧白色数值）。</summary>
    private static void DrawTrailerRow(Graphics g, Font font, Brush label, Brush value,
                                       string name, string text, int pad, int width, ref int y)
    {
        g.DrawString(name, font, label, pad, y);
        float tw = g.MeasureString(text, font).Width;
        g.DrawString(text, font, value, width - pad - tw, y);
        y += 17;
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        if (d <= 0 || r.Width <= d || r.Height <= d)
        {
            var simple = new GraphicsPath();
            simple.AddRectangle(r);
            return simple;
        }

        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
