using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using ScreenSpy.Interop;

namespace ScreenSpy.Diagnostics;

/// <summary>
/// 截屏 + 像素判定工具。
///
/// 存在的理由（M0 最贵的一课）：
///   路线 A 的“自动检测”曾经全绿 —— 所有 API 都报告成功，屏幕上却什么都没有。
///   因此凡是“是否真的显示了”这类判断，一律以**截屏像素**为准，不以 API 返回值为准。
///   本项目后续所有“窗口是否可见”的自动化断言都应走这里。
/// </summary>
internal static class ScreenCapture
{
    public readonly struct ColorHit
    {
        public int Count { get; init; }
        public Rectangle Bounds { get; init; }
        public bool Visible => Count > 200;
    }

    public static Rectangle VirtualScreenBounds()
    {
        int x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        if (w <= 0 || h <= 0)
        {
            x = 0;
            y = 0;
            w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
            h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        }

        return new Rectangle(x, y, w, h);
    }

    public static string Capture(string path)
    {
        Rectangle v = VirtualScreenBounds();

        string full = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var bmp = new Bitmap(v.Width, v.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
            g.CopyFromScreen(v.X, v.Y, 0, 0, new Size(v.Width, v.Height), CopyPixelOperation.SourceCopy);

        bmp.Save(full, ImageFormat.Png);
        return full;
    }

    /// <summary>在截屏里统计指定颜色的像素数与包围盒（容差按通道绝对值）。</summary>
    public static ColorHit Analyze(string path, Color color, int tolerance = 6)
    {
        if (!File.Exists(path)) return new ColorHit { Count = 0, Bounds = Rectangle.Empty };

        using var bmp = new Bitmap(path);
        int w = bmp.Width;
        int h = bmp.Height;

        BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        int count = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;

        try
        {
            unsafe
            {
                int stride = data.Stride;
                int abs = Math.Abs(stride);

                for (int y = 0; y < h; y++)
                {
                    byte* row = (byte*)data.Scan0 + (long)(stride >= 0 ? y : (h - 1 - y)) * abs;
                    for (int x = 0; x < w; x++)
                    {
                        byte* px = row + (long)x * 4;
                        byte b = px[0];
                        byte g = px[1];
                        byte r = px[2];

                        if (Math.Abs(r - color.R) > tolerance ||
                            Math.Abs(g - color.G) > tolerance ||
                            Math.Abs(b - color.B) > tolerance)
                            continue;

                        count++;
                        if (x < minX) minX = x;
                        if (y < minY) minY = y;
                        if (x > maxX) maxX = x;
                        if (y > maxY) maxY = y;
                    }
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        return new ColorHit
        {
            Count = count,
            Bounds = count > 0 ? Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1) : Rectangle.Empty,
        };
    }

    /// <summary>
    /// 找出“覆盖指定屏幕点”的最上层非本进程窗口（按 z 序，返回描述带 z 下标）。
    /// 用途：区分“卡片没画出来”与“卡片被别的窗口盖住了”。
    /// 注意：这是**启发式**（按矩形覆盖判断），会受未绘制的空窗口干扰；
    /// 判定仍以像素为准，本方法只作解释性参考。
    /// </summary>
    public static string TopWindowOver(Point screenPoint)
    {
        int self = Environment.ProcessId;
        string found = "（无：该点没有被其它窗口覆盖）";

        int i = -1;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            i++;

            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            if (NativeMethods.IsIconic(hwnd)) return true;

            NativeMethods.GetWindowThreadProcessId(hwnd, out int pid);
            if (pid == self) return true;

            string cls = NativeMethods.ClassNameOf(hwnd);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
                or "WindowsDashboard" or "XamlExplorerHostIslandWindow") return true;

            if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT r)) return true;
            if (screenPoint.X < r.Left || screenPoint.X >= r.Right ||
                screenPoint.Y < r.Top || screenPoint.Y >= r.Bottom) return true;

            found = $"类={cls} 标题=\"{NativeMethods.TitleOf(hwnd)}\" z={i}";
            return false;
        }, IntPtr.Zero);

        return found;
    }
}
