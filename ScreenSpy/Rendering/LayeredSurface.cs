using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using ScreenSpy.Interop;

namespace ScreenSpy.Rendering;

/// <summary>
/// 一块“内存 DC + 32bpp DIB”，可直接交给 UpdateLayeredWindow 做逐像素合成。
///
/// ── M13 瘦身（决策 D85）──
/// 本类**不再持有 GDI+ 的 Bitmap / Graphics，也不再负责把画面拷进 DIB**
/// （<c>_bitmap</c>、<c>_graphics</c>、<c>BlitToDib()</c> 整段删除，157 行 → 约 110 行）。
/// 现在它只做两件事，且**与“用哪个引擎画”彻底无关**：
/// <list type="number">
///   <item>提供目标缓冲：渲染器把像素画进 <see cref="Bits"/>（自顶向下 32bpp 预乘 BGRA，行距 <see cref="Stride"/>）；</item>
///   <item>提交：<see cref="Present"/> 把这块缓冲交给分层窗口。</item>
/// </list>
/// 于是渲染器与"像素怎么上屏"彻底解耦：换引擎、改外观都只影响"谁往缓冲里画"。
/// （M13 时这里曾用来让 GDI+ 与 Skia 两个实现互 diff；GDI+ 实现已在外观升级时移除，
/// 自检改为直接对**外观不变量**下断言 —— 见 `--m13-selfcheck`。）
///
/// ── 一行未动的部分（关键）──
/// DIB 的格式（自顶向下 32bpp <c>BI_RGB</c>、字节序 BGRA）与 <see cref="Present"/> 的整条
/// <c>UpdateLayeredWindow</c> 路径，都是 M0/M6 用**截屏像素**判定验证过的，M13 不碰：
/// 换引擎只换“谁往缓冲里画”，不换“缓冲怎么上屏”。
///
/// 来源：M0 验证（Spike）中经实测有效的实现。
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
    private bool _disposed;

    public Size Size => new(_width, _height);

    public int Width => _width;

    public int Height => _height;

    /// <summary>
    /// DIB 像素首字节 —— **渲染器的绘制目标**。
    /// 格式：自顶向下（顶行在前）、32bpp、字节序 BGRA、**预乘 alpha**。
    /// 该契约与 <see cref="CardRenderTarget"/> 是同一份（两者若不一致，像素会在屏幕上错位或偏色）。
    /// </summary>
    public IntPtr Bits => _bits;

    /// <summary>DIB 行距（字节）。32bpp 下恒为 <c>width * 4</c>（DIB 无额外对齐填充）。</summary>
    public int Stride => CardRenderTarget.StrideFor(_width);

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
    }

    /// <summary>
    /// 把当前缓冲内容提交到分层窗口（<see cref="Bits"/> 里的像素必须已经画好）。
    /// </summary>
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_oldBitmap != IntPtr.Zero && _memDc != IntPtr.Zero)
            NativeMethods.SelectObject(_memDc, _oldBitmap);
        if (_dib != IntPtr.Zero) NativeMethods.DeleteObject(_dib);
        if (_memDc != IntPtr.Zero) NativeMethods.DeleteDC(_memDc);
        if (_screenDc != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, _screenDc);
    }
}
