using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenSpy.Interop;

namespace ScreenSpy.Rendering;

/// <summary>
/// 一块“内存 DC + 32bpp DIB + GDI+ 绘图表面”，可直接交给 UpdateLayeredWindow 做逐像素合成。
///
/// 关键点：
///  * DIB 为自顶向下（biHeight 为负）的 32bpp BI_RGB，字节序 BGRA；
///  * GDI+ 侧使用 <see cref="PixelFormat.Format32bppPArgb"/>（预乘 alpha），
///    因为 UpdateLayeredWindow 在 AC_SRC_ALPHA 下要求预乘结果；
///  * 每帧把 GDI+ 画好的位图拷进 DIB，然后 Present。
///
/// 来源：M0 验证（Spike）中经实测有效的实现，原样移植。
/// </summary>
internal sealed class LayeredSurface : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private readonly IntPtr _screenDc;
    private readonly IntPtr _memDc;
    private readonly IntPtr _dib;
    private readonly IntPtr _oldBitmap;
    private readonly IntPtr _bits;
    private readonly Bitmap _bitmap;
    private readonly Graphics _graphics;
    private bool _disposed;

    /// <summary>供调用方绘制卡片内容（坐标系原点在卡片左上角）。</summary>
    public Graphics Graphics => _graphics;

    public Size Size => new(_width, _height);

    public LayeredSurface(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "尺寸必须为正数");

        _width = width;
        _height = height;

        _screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (_screenDc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "GetDC(NULL) 失败");

        _memDc = NativeMethods.CreateCompatibleDC(_screenDc);
        if (_memDc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateCompatibleDC 失败");

        var bmi = new NativeMethods.BITMAPINFO
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,            // 负值 = 自顶向下，避免逐行翻转
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB,
            },
        };

        _dib = NativeMethods.CreateDIBSection(_screenDc, ref bmi, NativeMethods.DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero || _bits == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateDIBSection 失败");

        _oldBitmap = NativeMethods.SelectObject(_memDc, _dib);

        _bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        _graphics = Graphics.FromImage(_bitmap);
        _graphics.SmoothingMode = SmoothingMode.AntiAlias;
        _graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        _graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        _graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
    }

    /// <summary>把当前位图内容提交到分层窗口。</summary>
    /// <param name="hwnd">分层窗口句柄。</param>
    /// <param name="x">
    /// 目标位置（屏幕坐标）。<c>null</c> 表示“子窗口模式”：
    /// 位置由 SetWindowPos 决定，UpdateLayeredWindow 的 pptDst 传 NULL。
    /// （B+ 方案为顶层窗口，通常传入具体坐标。）
    /// </param>
    /// <param name="y">同 <paramref name="x"/>。</param>
    public void Present(IntPtr hwnd, int? x, int? y)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        BlitToDib();

        var size = new NativeMethods.SIZE(_width, _height);
        var src = new NativeMethods.POINT(0, 0);
        var blend = new NativeMethods.BLENDFUNCTION
        {
            BlendOp = NativeMethods.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = NativeMethods.AC_SRC_ALPHA,
        };

        bool ok;
        if (x.HasValue && y.HasValue)
        {
            var dst = new NativeMethods.POINT(x.Value, y.Value);
            ok = NativeMethods.UpdateLayeredWindow(hwnd, _screenDc, ref dst, ref size, _memDc, ref src, 0, ref blend, NativeMethods.ULW_ALPHA);
        }
        else
        {
            ok = NativeMethods.UpdateLayeredWindowNoPos(hwnd, _screenDc, IntPtr.Zero, ref size, _memDc, ref src, 0, ref blend, NativeMethods.ULW_ALPHA);
        }

        if (!ok)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateLayeredWindow 失败");
    }

    private void BlitToDib()
    {
        _graphics.Flush(FlushIntention.Sync);

        var rect = new Rectangle(0, 0, _width, _height);
        BitmapData data = _bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            int rowBytes = _width * 4;
            unsafe
            {
                for (int row = 0; row < _height; row++)
                {
                    void* srcPtr = (byte*)data.Scan0 + (long)row * data.Stride;
                    void* dstPtr = (byte*)_bits + (long)row * rowBytes;
                    Buffer.MemoryCopy(srcPtr, dstPtr, rowBytes, rowBytes);
                }
            }
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _graphics.Dispose();
        _bitmap.Dispose();

        if (_oldBitmap != IntPtr.Zero && _memDc != IntPtr.Zero)
            NativeMethods.SelectObject(_memDc, _oldBitmap);
        if (_dib != IntPtr.Zero) NativeMethods.DeleteObject(_dib);
        if (_memDc != IntPtr.Zero) NativeMethods.DeleteDC(_memDc);
        if (_screenDc != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, _screenDc);
    }
}
