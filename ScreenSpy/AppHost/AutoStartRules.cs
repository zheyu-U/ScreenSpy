using System;
using System.IO;

namespace ScreenSpy.AppHost;

/// <summary>
/// 开机自启的**纯逻辑**（命令行构造 / 解析 / 匹配 + 自启时的窗口策略）。
///
/// 为什么单独抽一层：注册表本身无法在自检里随意折腾（写真实 Run 键有副作用），
/// 而"写进注册表的命令行长什么样""算不算已启用"这些判断**恰恰是最容易写错的地方**：
///  * 路径带空格却没加引号 → 登录后什么都不发生，且**不报错**；
///  * 比较时忘了要求 <c>--autorun</c> → 界面显示"已启用"，而实际启动时是显示主界面的形态；
///  * 比较时不规范化路径（大小写 / 短名 / 斜杠） → 同一条目被判成"不是本程序"。
///
/// 把它们做成纯函数后，就能在自检里逐条钉死（见 <c>--m12-selfcheck</c> A 组）。
/// </summary>
internal static class AutoStartRules
{
    /// <summary>
    /// Run 键下的值名。
    ///
    /// **这是与"用户机器上已存在的注册表内容"之间的兼容契约**：改名等于让旧条目变成孤儿
    /// （系统仍会在登录时启动它，而本程序再也读不到、关不掉）。
    /// 因此自检里用**字面量**把它钉死，而不是拿这个常量自比自（那会变成同义反复）。
    /// </summary>
    public const string ValueName = "ScreenSpy";

    /// <summary>HKCU 的 Run 键路径（§5.12）。自检里同样用字面量钉死。</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>自启拉起时附带的参数：让程序**只进托盘、不显示主界面**（用户决策）。</summary>
    public const string AutoRunArgument = "--autorun";

    /// <summary>
    /// 生成写进 Run 键的命令行。
    ///
    /// **路径一律加引号**：安装路径或用户名含空格时，不加引号的命令行会被系统按空格拆成
    /// "程序" + "参数" 两段，表现为"登录后什么都没发生"，而且不报任何错。
    /// </summary>
    public static string BuildCommand(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            throw new ArgumentException("可执行文件路径为空，无法生成自启命令行。", nameof(exePath));

        string path = exePath.Trim();

        // 万一传进来的已经是带引号的形式，先剥掉，避免写出 ""C:\..."" 这种双引号。
        if (path.Length >= 2 && path[0] == '"' && path[path.Length - 1] == '"')
            path = path.Substring(1, path.Length - 2).Trim();

        return "\"" + path + "\" " + AutoRunArgument;
    }

    /// <summary>
    /// 解析一行命令行，取出第一个 token（可执行文件路径）与是否带 <c>--autorun</c>。
    ///
    /// 注册表里的值是**任意外来内容**（可能被人手改、被别的工具覆盖、甚至是别的类型），
    /// 因此这里对 null / 空白 / 只有半个引号 / 无引号带空格 都给出**确定结果**，绝不抛异常。
    /// </summary>
    public static bool TryParse(string? command, out string exePath, out bool autoRun)
    {
        exePath = string.Empty;
        autoRun = false;

        if (string.IsNullOrWhiteSpace(command)) return false;

        string text = command.Trim();

        if (text[0] == '"')
        {
            // 带引号：取到下一个引号为止（含空格路径的正确形态）。
            int end = text.IndexOf('"', 1);
            if (end < 0) return false;                 // 只有开头一个引号 → 无法确定路径
            exePath = text.Substring(1, end - 1).Trim();
            text = text.Substring(end + 1).Trim();
        }
        else
        {
            // 不带引号：只能按第一个空格切（这正是"含空格路径必须加引号"的原因）。
            int space = text.IndexOf(' ');
            if (space < 0)
            {
                exePath = text.Trim();
                text = string.Empty;
            }
            else
            {
                exePath = text.Substring(0, space).Trim();
                text = text.Substring(space + 1).Trim();
            }
        }

        if (exePath.Length == 0) return false;

        foreach (string token in text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Equals(AutoRunArgument, StringComparison.OrdinalIgnoreCase))
            {
                autoRun = true;
                break;
            }
        }

        return true;
    }

    /// <summary>
    /// Run 键里的命令行是否**就是本程序**、且带 <c>--autorun</c>。
    ///
    /// 三条必须同时成立：能解析出路径、带 <c>--autorun</c>、路径与本进程一致。
    /// 少任何一条都会让界面显示与实际行为不符 —— 例如只比路径不要求 <c>--autorun</c>，
    /// 界面会说"已启用开机自启"，而登录时其实会**弹出一个主界面窗口**（不是我们承诺的形态）。
    /// </summary>
    public static bool PointsToThisApp(string? command, string exePath)
    {
        if (!TryParse(command, out string stored, out bool autoRun)) return false;
        return autoRun && PathsEqual(stored, exePath);
    }

    /// <summary>
    /// 路径是否相等（忽略大小写，尽量取全路径再比）。
    ///
    /// 为什么要规范化：注册表里可能存的是短名（<c>C:\PROGRA~1\...</c>）或正斜杠形式，
    /// 直接字符串比较会把"其实指向本程序"的条目判成"不是本程序"，于是界面把开关显示成关闭，
    /// 用户再勾一次 → 又写一遍。取全路径能消掉大部分这种差异。
    /// </summary>
    public static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        string p = path.Trim().Trim('"').Trim();
        try { return Path.GetFullPath(p).TrimEnd('\\'); }
        catch { return p.TrimEnd('\\'); }   // 路径非法时退回原样比较，不抛
    }

    /// <summary>
    /// 自启拉起时**要不要显示主界面**（纯函数，用户决策 + 一条降级例外）。
    ///
    ///  * 正常：自启 → 只进托盘（登录时不弹窗口，这是"常驻小工具"该有的样子）；
    ///  * **例外：托盘不可用 → 必须显示窗口**。那时没有任何入口能呼出界面，
    ///    不显示窗口就等于"看不见也关不掉"（M5b 那条降级契约在自启场景下的延续）。
    /// </summary>
    public static bool ShouldShowMainWindow(bool autoRun, bool trayAvailable)
        => !autoRun || !trayAvailable;
}
