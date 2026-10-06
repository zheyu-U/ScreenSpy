using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32;
using ScreenSpy.AppHost;

namespace ScreenSpy.Demo;

/// <summary>
/// M12 自检：**开机自启**（开发文档 §5.12）。
///
/// 分三组：
///  * <b>A 纯逻辑（AutoStartRules）</b>：命令行构造（含空格路径**必须加引号**）、解析（带/不带引号、
///    无参数、半个引号、null、空白）、匹配（必须**同时**满足"指向本程序"与"带 --autorun"）、
///    路径规范化（大小写 / 短名 / 非法路径不抛）、以及"自启是否显示主界面"的两条规则。
///  * <b>B 注册表真实往返</b>：在一个**真实的 HKCU 临时键**上做 写 → 读 → 改 → 删 的完整往返。
///    不对着 mock 断言：mock 只能验证"我以为的注册表语义"，验证不了真实读写权限与键值类型。
///    其中一条最有价值：把值改成**裸路径（没有 --autorun）**，断言它被判成"未启用" ——
///    只比路径的实现会在这里 FAIL，而那正是"界面说已启用、登录却弹出主界面"的缺陷形态。
///  * <b>C 组合根与接线</b>：<c>--autorun</c> 被正确解析（且**不会**落进"未知参数"警告）、
///    真实 <see cref="ProductRuntime"/> 带 <c>--autorun</c> 照常启动统计、开关提示文案四态各说各话。
///
/// 为什么 A 组要用**字面量**钉死键路径与值名：它们是"与用户机器上已存在的注册表内容"之间的兼容契约，
/// 拿常量自比自会变成同义反复（M7 变异测试的教训）。
/// </summary>
internal static class M12SelfCheck
{
    /// <summary>自检专用的注册表键（**绝不能**是生产 Run 键）。</summary>
    private const string TestKeyPath = @"Software\ScreenSpy\SelfCheck\Run";

    private static readonly StringBuilder LogBuffer = new();
    private static string? _logPath;
    private static string? _dir;

    public static bool IsRequested(string[]? args) => HasFlag(args, "--m12-selfcheck");

    public static int Run(string[] args)
    {
        _logPath = GetString(args, "--log=");
        string dir = GetString(args, "--dir=") ?? Path.Combine("artifacts", "m12");
        _dir = Path.GetFullPath(dir);

        TextWriter original = Console.Out;
        try { Console.SetOut(new TeeWriter(original, LogBuffer)); } catch { /* 忽略 */ }

        int failed = 0;
        try
        {
            Console.WriteLine("M12 自检：开机自启（注册表 Run 键）");
            Console.WriteLine("目录：" + _dir);
            Console.WriteLine();

            try { Directory.CreateDirectory(_dir); } catch { /* 忽略 */ }

            Console.WriteLine("A. 纯逻辑（命令行构造 / 解析 / 匹配 / 窗口策略）");
            failed += RuleChecks();

            Console.WriteLine();
            Console.WriteLine("B. 注册表真实往返（HKCU 临时键）");
            failed += RegistryChecks();

            Console.WriteLine();
            Console.WriteLine("C. 组合根与接线");
            failed += WiringChecks(Path.Combine(_dir, "m12.db"));

            Console.WriteLine();
            Console.WriteLine(failed == 0
                ? "M12 自检：全部通过。"
                : $"M12 自检：有 {failed} 项失败。");
        }
        catch (Exception ex)
        {
            Console.WriteLine("自检自身异常：" + ex);
            failed++;
        }
        finally
        {
            try { Console.SetOut(original); } catch { /* 忽略 */ }
            WriteLogFile();
        }

        return failed == 0 ? 0 : 1;
    }

    // ================================================================ A 纯逻辑

    private static int RuleChecks()
    {
        int failed = 0;

        const string plain = @"C:\Program Files\ScreenSpy\ScreenSpy.exe";
        const string spaced = @"C:\Users\Zhang San\AppData\Local\ScreenSpy\ScreenSpy.exe";

        // 不含空格的路径：**只用于"不带引号"的解析用例**。
        // 用含空格的路径去测"不带引号"是自相矛盾的 —— 那时按第一个空格切开**正是正确行为**
        // （第一版自检就踩了这个：拿 plain 去测不带引号，结果两条都 FAIL，而产品是对的）。
        const string bare = @"C:\Tools\ScreenSpy\ScreenSpy.exe";

        // ---- A1 命令行构造 ----
        failed += Check("构造：路径一律加引号",
            AutoStartRules.BuildCommand(plain) == "\"" + plain + "\" --autorun");
        failed += Check("构造：含空格路径同样加引号（否则系统会把它拆成两段，且不报错）",
            AutoStartRules.BuildCommand(spaced) == "\"" + spaced + "\" --autorun");
        failed += Check("构造：传入已带引号的路径不会写出双引号",
            AutoStartRules.BuildCommand("\"" + plain + "\"") == "\"" + plain + "\" --autorun");
        failed += Check("构造：路径前后空白被裁掉",
            AutoStartRules.BuildCommand("  " + plain + "  ") == "\"" + plain + "\" --autorun");

        // ---- A2 解析 ----
        failed += Check("解析：带引号 + --autorun",
            AutoStartRules.TryParse("\"" + spaced + "\" --autorun", out string p1, out bool a1)
            && p1 == spaced && a1);
        failed += Check("解析：带引号但**没有**参数 → autoRun=false",
            AutoStartRules.TryParse("\"" + spaced + "\"", out string p2, out bool a2)
            && p2 == spaced && !a2);
        failed += Check("解析：不带引号的裸路径也能解析出 exe（该路径本身不含空格）",
            AutoStartRules.TryParse(bare, out string p3, out bool a3)
            && p3 == bare && !a3);
        failed += Check("解析：不带引号 + 参数（路径不含空格时按第一个空格切是对的）",
            AutoStartRules.TryParse(bare + " --autorun", out string p4, out bool a4)
            && p4 == bare && a4);
        failed += Check("解析：不带引号的**含空格**路径必然被切断 —— 这正是必须加引号的原因",
            AutoStartRules.TryParse(plain, out string p5, out bool a5b)
            && p5 != plain && !a5b);
        failed += Check("解析：参数顺序无关（--autorun 在后）",
            AutoStartRules.TryParse("\"" + plain + "\" --something --autorun", out _, out bool a5) && a5);
        failed += Check("解析：参数大小写不敏感（--AUTORUN）",
            AutoStartRules.TryParse("\"" + plain + "\" --AUTORUN", out _, out bool a6) && a6);
        failed += Check("解析：只有一个开引号 → 失败（无法确定路径，绝不猜）",
            !AutoStartRules.TryParse("\"" + plain, out _, out _));
        failed += Check("解析：null / 空白 → 失败且不抛",
            !AutoStartRules.TryParse(null, out _, out _)
            && !AutoStartRules.TryParse("   ", out _, out _)
            && !AutoStartRules.TryParse("", out _, out _));

        // ---- A3 匹配：三条必须同时成立 ----
        string expected = AutoStartRules.BuildCommand(plain);
        failed += Check("匹配：本程序 + --autorun → 已启用",
            AutoStartRules.PointsToThisApp(expected, plain));
        failed += Check("匹配：**只比路径不算数** —— 裸路径（无 --autorun）判为未启用",
            !AutoStartRules.PointsToThisApp("\"" + plain + "\"", plain));
        failed += Check("匹配：指向别的程序 → 未启用",
            !AutoStartRules.PointsToThisApp(AutoStartRules.BuildCommand(@"C:\Other\Other.exe"), plain));
        failed += Check("匹配：路径大小写不同仍算同一个程序",
            AutoStartRules.PointsToThisApp(AutoStartRules.BuildCommand(plain.ToUpperInvariant()), plain));
        failed += Check("匹配：值不是命令行（例如一个数字）→ 未启用且不抛",
            !AutoStartRules.PointsToThisApp("12345", plain));
        failed += Check("匹配：null → 未启用且不抛",
            !AutoStartRules.PointsToThisApp(null, plain));

        // ---- A4 路径比较 ----
        failed += Check("路径比较：忽略大小写", AutoStartRules.PathsEqual(@"C:\A\b.exe", @"c:\a\B.EXE"));
        failed += Check("路径比较：反斜杠结尾视为同一路径",
            AutoStartRules.PathsEqual(@"C:\A\b.exe\", @"C:\A\b.exe"));
        failed += Check("路径比较：非法路径不抛异常（退回原样比较）",
            !AutoStartRules.PathsEqual("::::", @"C:\A\b.exe"));
        failed += Check("路径比较：任一为空 → false（不抛）",
            !AutoStartRules.PathsEqual(null, @"C:\A\b.exe")
            && !AutoStartRules.PathsEqual(@"C:\A\b.exe", ""));

        // ---- A5 自启时的窗口策略 ----
        failed += Check("窗口：自启 + 托盘可用 → **不**显示主界面（只进托盘）",
            !AutoStartRules.ShouldShowMainWindow(autoRun: true, trayAvailable: true));
        failed += Check("窗口：自启 + 托盘不可用 → **必须**显示（否则看不见也关不掉）",
            AutoStartRules.ShouldShowMainWindow(autoRun: true, trayAvailable: false));
        failed += Check("窗口：非自启（手动双击）→ 显示主界面",
            AutoStartRules.ShouldShowMainWindow(autoRun: false, trayAvailable: true)
            && AutoStartRules.ShouldShowMainWindow(autoRun: false, trayAvailable: false));

        // ---- A6 契约：键路径与值名用**字面量**钉死（改名 = 让旧条目变孤儿） ----
        var prod = new AutoStartRegistration();
        failed += Check("契约：生产键路径 = HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run",
            prod.KeyPath == @"Software\Microsoft\Windows\CurrentVersion\Run");
        failed += Check("契约：生产值名 = ScreenSpy",
            prod.ValueName == "ScreenSpy");
        failed += Check("契约：写入的命令行以 --autorun 结尾（注册表里存的必须是这个形态）",
            prod.ExpectedCommand.EndsWith(" " + "--autorun", StringComparison.Ordinal));

        return failed;
    }

    // ================================================================ B 注册表真实往返

    private static int RegistryChecks()
    {
        int failed = 0;

        // 清掉上次残留，保证本次结论确定（自检失败中断时也不会污染下一次）。
        AutoStartRegistration.TryRemoveTestKey(TestKeyPath);

        var reg = new AutoStartRegistration(keyPath: TestKeyPath);

        AutoStartState initial = reg.Query();
        if (!initial.Available)
        {
            Console.WriteLine("   [SKIP] 注册表读写不可用，跳过本组：" + initial.Error);
            Console.WriteLine("   小计：0 项失败（跳过）");
            return 0;
        }

        failed += Check("初始：临时键不存在 → 未启用（Available=true）",
            !initial.Enabled && initial.Command is null);

        // ---- 启用 ----
        AutoStartState afterEnable = reg.Enable();
        failed += Check("启用：写完回读 → 已启用", afterEnable.Enabled);
        failed += Check("启用：回读到的命令行 == 期望命令行（不是“我以为写进去了”）",
            afterEnable.Command == reg.ExpectedCommand);
        failed += Check("启用：注册表里真的能读到该值（直接读原始值核对）",
            ReadRawValue(TestKeyPath) == reg.ExpectedCommand);
        failed += Check("启用：回读值能被解析成“本程序 + --autorun”",
            AutoStartRules.PointsToThisApp(afterEnable.Command, reg.ExePath));

        // ---- 幂等 ----
        AutoStartState again = reg.Enable();
        failed += Check("幂等：重复启用不报错、值不变",
            again.Enabled && again.Command == reg.ExpectedCommand);

        // ---- 关键：裸路径（缺 --autorun）必须判"未启用" ----
        WriteRawValue(TestKeyPath, "\"" + reg.ExePath + "\"");
        AutoStartState bare = reg.Query();
        failed += Check("关键：值存在但**缺 --autorun** → 判为未启用（否则界面会承诺错误的行为）",
            !bare.Enabled);
        failed += Check("关键：这种“同名但不对”的情况会被标出来（供界面解释）",
            bare.PointsElsewhere && bare.Available);
        failed += Check("关键：裸路径不算已启用，因此 **Enable 必须能把它修正回去**",
            reg.Enable().Command == reg.ExpectedCommand);

        // ---- 值不是字符串类型时也不能抛 ----
        using (RegistryKey? key = Registry.CurrentUser.CreateSubKey(TestKeyPath, writable: true))
        {
            key?.SetValue(reg.ValueName, 12345, RegistryValueKind.DWord);
        }
        AutoStartState weird = reg.Query();
        failed += Check("健壮：值是 DWORD（被别的工具写过）→ 不抛异常、判为未启用",
            weird.Available && !weird.Enabled);
        failed += Check("健壮：非字符串值仍会被带出来供界面解释（不会被静默丢掉）",
            weird.Command is not null);

        // ---- 关闭 ----
        AutoStartState afterDisable = reg.Disable();
        failed += Check("关闭：删值后回读 → 未启用", !afterDisable.Enabled);
        failed += Check("关闭：值已不存在（直接读原始值为 null）", ReadRawValue(TestKeyPath) is null);

        AutoStartState disableAgain = reg.Disable();
        failed += Check("幂等：重复关闭不报错（用户点两次不该看到错误）",
            !disableAgain.Enabled && disableAgain.Available);

        // ---- 守卫：绝不删真实 Run 键 ----
        failed += Check("守卫：TryRemoveTestKey(真实 Run 键) 必须被**拒绝**",
            !AutoStartRegistration.TryRemoveTestKey(AutoStartRules.RunKeyPath));
        failed += Check("守卫：TryRemoveTestKey(别的前缀) 也被拒绝",
            !AutoStartRegistration.TryRemoveTestKey(@"Software\SomeOtherApp"));
        failed += Check("清理：自检自己的临时键可以被删掉",
            AutoStartRegistration.TryRemoveTestKey(TestKeyPath));

        AutoStartState cleaned = reg.Query();
        failed += Check("清理后：状态回到未启用（不留残留）",
            !cleaned.Enabled && cleaned.Command is null);

        return failed;
    }

    // ================================================================ C 组合根与接线

    private static int WiringChecks(string databasePath)
    {
        int failed = 0;

        // ---- C1 命令行解析：--autorun 必须被识别（否则会变成一条"未知参数"警告） ----
        failed += Check("解析：--autorun → AutoRun=true",
            StartupOptions.Parse(new[] { "--autorun" }).AutoRun);
        failed += Check("解析：--AUTORUN（大小写不敏感）→ AutoRun=true",
            StartupOptions.Parse(new[] { "--AUTORUN" }).AutoRun);
        failed += Check("解析：不带参数 → AutoRun=false（手动双击的形态）",
            !StartupOptions.Parse(Array.Empty<string>()).AutoRun
            && !StartupOptions.Parse(null).AutoRun);
        failed += Check("解析：--autorun 不会落进“未知参数”（否则界面会一直挂着一条警告）",
            StartupOptions.Parse(new[] { "--autorun" }).Unknown.Count == 0);

        // ---- C2 真实组合根带 --autorun 照常启动统计 ----
        TryDelete(databasePath);
        TryDelete(databasePath + "-wal");
        TryDelete(databasePath + "-shm");

        StartupOptions options = StartupOptions.Parse(new[]
        {
            "--data-dir=" + Path.GetDirectoryName(databasePath),
            "--no-raw-log", "--no-card", "--probe-ms=0", "--autorun",
        });

        HostStartResult start = ProductRuntime.Start(options);
        if (start.Outcome == HostStartOutcome.AlreadyRunning)
        {
            Console.WriteLine("   [SKIP] 已有 ScreenSpy 实例在运行，无法独占单实例互斥体 —— 跳过。");
        }
        else if (start.Outcome != HostStartOutcome.Started || start.Runtime is null)
        {
            failed += Check("组合根：带 --autorun 能正常启动（" + start.Message + "）", false);
        }
        else
        {
            ProductRuntime runtime = start.Runtime;
            try
            {
                failed += Check("组合根：带 --autorun 能正常启动", true);

                RuntimeStatus status = runtime.Snapshot();
                if (status is null)
                {
                    // 组合根的 Snapshot 契约是"永不返回 null"，走到这里说明契约被破坏了。
                    failed += Check("组合根：快照可用（自启形态不影响统计链路）", false);
                }
                else
                {
                    failed += Check("组合根：快照可用（自启形态不影响统计链路）", true);

                    bool autorunWarning = false;
                    foreach (string w in status.Warnings)
                    {
                        if (w.IndexOf("autorun", StringComparison.OrdinalIgnoreCase) >= 0) autorunWarning = true;
                    }
                    failed += Check("组合根：没有任何一条警告提到 autorun（它是已知参数，不是拼错的参数）",
                        !autorunWarning);
                }
            }
            finally
            {
                try { runtime.Dispose(); } catch { /* 忽略 */ }
            }
        }

        // ---- C3 界面提示文案：四态各说各话（用**字面量**断言，避免与实现同义反复） ----
        string enabledHint = MainWindow.BuildAutoStartHint(
            new AutoStartState(true, "\"X\" --autorun", null, "X"));
        failed += Check("提示：已启用 → 明说“只进托盘”（用户承诺的形态）",
            enabledHint.Contains("只进托盘") && enabledHint.Contains("不显示主界面"));
        failed += Check("提示：已启用 → 写出真实注册表位置（便于用户自己核查）",
            enabledHint.Contains(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run")
            && enabledHint.Contains("ScreenSpy"));

        string offHint = MainWindow.BuildAutoStartHint(new AutoStartState(false, null, null, "X"));
        failed += Check("提示：未启用 → 说明勾选后会写到哪里、登录时的形态",
            offHint.Contains("未启用") && offHint.Contains("只进托盘"));

        string staleHint = MainWindow.BuildAutoStartHint(
            new AutoStartState(false, "\"C:\\Old\\ScreenSpy.exe\" --autorun", null, "X"));
        failed += Check("提示：同名残留指向别处 → 明说“指向的不是本程序”，并给出那条值",
            staleHint.Contains("指向的不是本程序") && staleHint.Contains("C:\\Old\\ScreenSpy.exe"));

        string errHint = MainWindow.BuildAutoStartHint(
            new AutoStartState(false, null, "UnauthorizedAccessException: 拒绝访问", "X"));
        failed += Check("提示：注册表不可用 → 明说原因（而不是显示一个关不掉的开关）",
            errHint.Contains("无法读写注册表") && errHint.Contains("拒绝访问"));

        return failed;
    }

    // ================================================================ 注册表小工具（仅自检用）

    private static string? ReadRawValue(string keyPath)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
            return key?.GetValue(AutoStartRules.ValueName) as string;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteRawValue(string keyPath, string value)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            key?.SetValue(AutoStartRules.ValueName, value, RegistryValueKind.String);
        }
        catch { /* 忽略：写入失败会在随后的断言里暴露 */ }
    }

    // ================================================================ 基础设施

    private static int Check(string label, bool ok)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        return ok ? 0 : 1;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }

    private static bool HasFlag(string[]? args, string flag)
    {
        if (args is null) return false;
        foreach (string a in args)
            if (a.Equals(flag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string? GetString(string[]? args, string prefix)
    {
        if (args is null) return null;
        foreach (string a in args)
            if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return a.Substring(prefix.Length);
        return null;
    }

    private static void WriteLogFile()
    {
        // 注意：_dir 已经**就是**日志目录（默认 artifacts\m12），不要再拼一次 "m12"——
        // 那会写出 artifacts\m12\m12\...（第一版就是这样，日志落到多一层目录里）。
        string path = _logPath ?? Path.Combine(_dir ?? Path.Combine("artifacts", "m12"), "m12-selfcheck.log");
        try
        {
            string full = Path.GetFullPath(path);
            string? dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(full, LogBuffer.ToString(), new UTF8Encoding(false));
            try { Console.WriteLine($"[日志] 全部输出已写入：{full}"); } catch { }
        }
        catch { /* 忽略 */ }
    }

    /// <summary>把输出同时写到原控制台与内存缓冲区（用于"控制台看不到"时的文件兜底）。</summary>
    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _console;
        private readonly StringBuilder _buffer;

        public TeeWriter(TextWriter console, StringBuilder buffer)
        {
            _console = console;
            _buffer = buffer;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            try { _console.Write(value); } catch { /* 忽略 */ }
            _buffer.Append(value);
        }

        public override void Write(string? value)
        {
            try { _console.Write(value); } catch { /* 忽略 */ }
            _buffer.Append(value);
        }

        public override void WriteLine(string? value)
        {
            try { _console.WriteLine(value); } catch { /* 忽略 */ }
            _buffer.Append(value).Append('\n');
        }

        public override void WriteLine()
        {
            try { _console.WriteLine(); } catch { /* 忽略 */ }
            _buffer.Append('\n');
        }
    }
}
