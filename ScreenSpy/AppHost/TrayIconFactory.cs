using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ScreenSpy.AppHost;

/// <summary>
/// 程序化生成托盘图标（M5b 决策：**不新增二进制资源文件**）。
///
/// 造型：圆角深蓝底板 + 白色“镜头”（外环 + 瞳孔）——对应 ScreenSpy 的“观察”含义；
/// 已暂停时底板转灰并叠加两道暂停竖条，这样即使窗口关着、菜单没展开，也能一眼看出
/// “统计被暂停了”（否则很容易忘记自己暂停过）。
///
/// 句柄管理：<c>Bitmap.GetHicon()</c> 得到的 HICON **必须显式 DestroyIcon**，
/// 否则每次创建都泄漏一个 GDI 对象；而 <c>Icon.FromHandle</c> 返回的是“借用句柄”的
/// Icon，不能直接长期持有 —— 因此这里先 Clone 出独立副本，再销毁句柄。
/// </summary>
internal static class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const int Size = 32;

    /// <summary>生成一个托盘图标（<paramref name="paused"/> 决定正常态 / 暂停态）。</summary>
    public static Icon Create(bool paused)
    {
        using var bitmap = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);

        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            Color plate = paused
                ? Color.FromArgb(255, 110, 118, 129)   // 灰：已暂停
                : Color.FromArgb(255, 30, 78, 122);    // 深蓝：统计中

            using (var brush = new SolidBrush(plate))
            using (GraphicsPath path = RoundedRect(new Rectangle(1, 1, Size - 2, Size - 2), 7))
            {
                g.FillPath(brush, path);
            }

            // “镜头”
            using (var pen = new Pen(Color.White, 2f))
                g.DrawEllipse(pen, 7f, 10.5f, 18f, 11f);

            using (var pupil = new SolidBrush(Color.White))
                g.FillEllipse(pupil, 13.5f, 13.5f, 5f, 5f);

            if (paused)
            {
                using var bar = new SolidBrush(Color.White);
                g.FillRectangle(bar, 20f, 21f, 3f, 8f);
                g.FillRectangle(bar, 25f, 21f, 3f, 8f);
            }
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
