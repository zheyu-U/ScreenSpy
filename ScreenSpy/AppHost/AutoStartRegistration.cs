using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace ScreenSpy.AppHost;

/// <summary>
/// 开机自启的**当前状态**（一律由**注册表回读**得到，而不是"我们以为写进去了"）。
/// </summary>
internal sealed class AutoStartState
{
    public AutoStartState(bool enabled, string? command, string? error, string exePath)
    {
        Enabled = enabled;
        Command = command;
        Error = error;
        ExePath = exePath;
    }

    /// <summary>是否已启用（Run 键里存在指向**本程序**且带 <c>--autorun</c> 的条目）。</summary>
    public bool Enabled { get; }

    /// <summary>注册表里**实际存着的**命令行（不存在为 null）。用于向用户解释"为什么显示这个状态"。</summary>
    public string? Command { get; }

    /// <summary>读/写失败的原因（正常为 null）。</summary>
    public string? Error { get; }

    /// <summary>本程序的可执行文件路径（写入时用的那个）。</summary>
    public string ExePath { get; }

    /// <summary>注册表可读可写（false 时界面应禁用开关并显示 <see cref="Error"/>）。</summary>
    public bool Available => Error is null;

    /// <summary>存在一条同名条目、但它**不是**本程序（例如指向旧版本路径或被别的工具覆盖）。</summary>
    public bool PointsElsewhere => Command is not null && !Enabled;
}

/// <summary>
/// 开机自启的注册表层（§5.12）：<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>
/// 下的一个字符串值。
///
/// 四条实现约定（每一条都对应一个真实的坑）：
///
///  1. **注册表是唯一事实来源**，不再往 <c>settings</c> 表里存一份"期望状态"。
///     两份状态一旦不一致（用户用任务管理器删掉、被策略拦下、被别的工具覆盖），
///     界面就会显示"已启用"而实际没有 —— 这正是本项目最忌的"不报错的错"。
///     因此 <see cref="Query"/> 每次都**直接回读**，开关也按回读结果渲染。
///  2. **写操作一律回读**：<see cref="Enable"/> / <see cref="Disable"/> 不假设写入成功。
///     写入"看起来成功"但被重定向 / 被策略拦掉时，回读是**唯一**的判据。
///  3. **只碰 HKCU**（当前用户），不需要管理员权限，也不影响其他用户。
///  4. **删除只删值，绝不删键**：真实的 Run 键是 Windows 自己的，任何情况下都不能动它；
///     <see cref="TryRemoveTestKey"/> 因此带前缀守卫，只允许删自检自己的临时键。
///
/// 键路径与值名可注入，于是自检能在一个**真实的注册表键**上做完整的"写 → 读 → 删"往返，
/// 而不是对着 mock 断言（那样只能验证"我以为的注册表语义"）。
/// </summary>
internal sealed class AutoStartRegistration
{
    private readonly string _keyPath;
    private readonly string _valueName;

    /// <param name="keyPath">注册表键路径；默认 <see cref="AutoStartRules.RunKeyPath"/>（生产值）。</param>
    /// <param name="valueName">值名；默认 <see cref="AutoStartRules.ValueName"/>（生产值）。</param>
    /// <param name="exePath">要登记的 exe 路径；默认取当前进程的真实路径。</param>
    public AutoStartRegistration(string? keyPath = null, string? valueName = null, string? exePath = null)
    {
        _keyPath = string.IsNullOrWhiteSpace(keyPath) ? AutoStartRules.RunKeyPath : keyPath;
        _valueName = string.IsNullOrWhiteSpace(valueName) ? AutoStartRules.ValueName : valueName;
        ExePath = string.IsNullOrWhiteSpace(exePath) ? ResolveExePath() : exePath;
    }

    /// <summary>本程序的可执行文件路径（写进 Run 键的就是它）。</summary>
    public string ExePath { get; }

    /// <summary>实际的键路径（自检用它断言"生产默认值就是 Run 键"）。</summary>
    public string KeyPath => _keyPath;

    /// <summary>实际的值名（自检用它断言生产默认值）。</summary>
    public string ValueName => _valueName;

    /// <summary>本程序应当写进 Run 键的命令行（纯逻辑生成，见 <see cref="AutoStartRules.BuildCommand"/>）。</summary>
    public string ExpectedCommand => AutoStartRules.BuildCommand(ExePath);

    /// <summary>
    /// 回读当前状态。**永不抛异常**（展示层要直接用它渲染开关）。
    /// </summary>
    public AutoStartState Query()
    {
        try
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false))
            {
                object? value = key?.GetValue(_valueName);
                string? command = value as string;

                // 值存在但不是字符串（被别的工具写成了别的类型）→ 当作"不是我们写的"，
                // 但把原始内容以文本形式带出去，好让界面能解释清楚。
                if (value is not null && command is null) command = value.ToString();

                bool enabled = AutoStartRules.PointsToThisApp(command, ExePath);
                return new AutoStartState(enabled, command, null, ExePath);
            }
        }
        catch (Exception ex)
        {
            return new AutoStartState(false, null, Describe(ex), ExePath);
        }
    }

    /// <summary>
    /// 启用开机自启（写 Run 键）。**写完回读**，并把失败原因一并带出。
    /// </summary>
    public AutoStartState Enable()
    {
        string? error = null;
        try
        {
            using (RegistryKey? key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true))
            {
                if (key is null) throw new InvalidOperationException("无法打开注册表键：" + _keyPath);
                key.SetValue(_valueName, ExpectedCommand, RegistryValueKind.String);
            }
        }
        catch (Exception ex)
        {
            error = Describe(ex);
        }

        return MergeWithRealState(error);
    }

    /// <summary>
    /// 关闭开机自启（删 Run 键下的**值**）。
    /// 值本来就不存在时**不算失败**（幂等）—— 用户点两次不该看到错误。
    /// </summary>
    public AutoStartState Disable()
    {
        string? error = null;
        try
        {
            // 只为写而打开；键不存在时 OpenSubKey 返回 null —— 那就等于"本来就没启用"，无需创建。
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true))
            {
                key?.DeleteValue(_valueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            error = Describe(ex);
        }

        return MergeWithRealState(error);
    }

    /// <summary>回读真实状态；若发生过错误，把错误附加上去（状态仍以真实回读为准）。</summary>
    private AutoStartState MergeWithRealState(string? error)
    {
        AutoStartState real = Query();
        return error is null ? real : new AutoStartState(real.Enabled, real.Command, error, ExePath);
    }

    /// <summary>
    /// 仅供自检：把注入的**测试键**整棵删除。
    ///
    /// 两道守卫（缺一不可）：
    ///  * 拒绝生产 Run 键路径 —— 那是 Windows 自己的键，删掉会影响其它自启项；
    ///  * 只接受自检专用前缀 —— 即使调用方传了别的键，也删不到东西。
    /// </summary>
    internal static bool TryRemoveTestKey(string? keyPath)
    {
        if (string.IsNullOrWhiteSpace(keyPath)) return false;
        if (keyPath.Equals(AutoStartRules.RunKeyPath, StringComparison.OrdinalIgnoreCase)) return false;
        if (!keyPath.StartsWith(@"Software\ScreenSpy\SelfCheck", StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 取当前进程的可执行文件路径。
    ///
    /// 优先 <see cref="Environment.ProcessPath"/>（这是 .NET 6+ 的官方途径）；
    /// 退回主模块文件名。两者都拿不到时给出一个**明确可读的**占位路径 ——
    /// 宁可让开关报错，也不要写一个空字符串进注册表。
    /// </summary>
    private static string ResolveExePath()
    {
        try
        {
            string? path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path)) return path;
        }
        catch { /* 忽略，走下面的兜底 */ }

        try
        {
            string? path = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path)) return path;
        }
        catch { /* 忽略 */ }

        return string.Empty;
    }

    private static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message;
}
