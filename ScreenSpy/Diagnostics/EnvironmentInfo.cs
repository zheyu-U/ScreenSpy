using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using ScreenSpy.Interop;

namespace ScreenSpy.Diagnostics;

/// <summary>
/// 采集运行环境快照（操作系统版本 / .NET 版本 / DPI / 分辨率）。
/// 用途：确认问题复现时确实处于同一环境（M0 的全部结论都标注了 26H2 / build 26300）。
/// </summary>
internal static class EnvironmentInfo
{
    public static string Capture()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"采集时间            : {DateTime.Now:yyyy-MM-dd HH:mm:ss}（本地）");
        sb.AppendLine($"运行时              : {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Environment.Version : {Environment.Version}");
        sb.AppendLine($"OS 描述             : {RuntimeInformation.OSDescription}");
        sb.AppendLine($"OS 版本(OSVersion)  : {Environment.OSVersion}");
        sb.AppendLine($"进程架构            : {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"系统架构            : {RuntimeInformation.OSArchitecture}");

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key != null)
            {
                sb.AppendLine($"ProductName         : {key.GetValue("ProductName")}");
                sb.AppendLine($"DisplayVersion      : {key.GetValue("DisplayVersion")}");
                sb.AppendLine($"CurrentBuild        : {key.GetValue("CurrentBuildNumber")}.{key.GetValue("UBR")}");
                sb.AppendLine($"BuildLabEx          : {key.GetValue("BuildLabEx")}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"读取注册表失败      : {ex.Message}");
        }

        try
        {
            sb.AppendLine($"系统 DPI            : {NativeMethods.GetDpiForSystem()}（100 = 96）");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"GetDpiForSystem 失败: {ex.Message}");
        }

        sb.AppendLine($"主屏分辨率          : {NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN)} × {NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN)}");
        sb.AppendLine(
            $"虚拟屏              : ({NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN)},{NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN)}) " +
            $"{NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN)} × {NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN)}");

        return sb.ToString();
    }

    public static void Print()
    {
        Console.WriteLine("---- 运行环境 ----");
        Console.Write(Capture());
        Console.WriteLine("------------------");
        Console.WriteLine();
    }
}
