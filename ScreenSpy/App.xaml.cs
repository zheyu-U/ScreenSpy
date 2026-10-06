using System;
using System.ComponentModel;
using System.Windows;
using ScreenSpy.AppHost;
using ScreenSpy.Demo;
using ScreenSpy.Diagnostics;
using ScreenSpy.Limits;
using ScreenSpy.Rendering;
using ScreenSpy.Widget;

namespace ScreenSpy
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        /// <summary>产品运行时（无参数启动时建立；退出时释放并 flush 尾段）。</summary>
        private ProductRuntime? _runtime;

        /// <summary>主窗口（托盘可用时可能被隐藏；托盘「打开主界面」要用）。</summary>
        private Window? _window;

        /// <summary>托盘图标宿主；创建失败为 null（此时关闭窗口即退出）。</summary>
        private TrayIconHost? _tray;

        /// <summary>
        /// 桌面卡片（M6）。它在自己的 STA 线程上跑消息循环，通过 <see cref="BuildCardModel"/>
        /// 每秒读一次运行时快照。
        /// </summary>
        private CardWindow? _card;

        /// <summary>是否已请求退出（用于区分“关闭窗口”与“真正退出”）。</summary>
        private bool _exiting;

        protected override void OnStartup(StartupEventArgs e)
        {
            // M1 自检入口（控制台打印，跑完即退出）：
            //   ScreenSpy.exe --m1-selfcheck [--seconds=20] [--idle-threshold=300] [--heartbeat=1000]
            if (M1SelfCheck.IsRequested(e.Args))
            {
                Shutdown(M1SelfCheck.Run(e.Args));
                return;
            }

            // M2 自检入口（锁屏/睡眠监听；控制台打印 + 日志文件兜底，跑完即退出）：
            //   ScreenSpy.exe --m2-selfcheck [--seconds=20] [--logic-only] [--probe-interval=5000]
            //                                [--lock-test] [--sleep-test] [--sleep-seconds=20]
            if (M2SelfCheck.IsRequested(e.Args))
            {
                Shutdown(M2SelfCheck.Run(e.Args));
                return;
            }

            // M3 自检入口（前台进程识别 + 过滤；控制台打印 + 日志文件兜底，跑完即退出）：
            //   ScreenSpy.exe --m3-selfcheck [--seconds=20] [--logic-only] [--top=5] [--log=<path>]
            if (M3SelfCheck.IsRequested(e.Args))
            {
                Shutdown(M3SelfCheck.Run(e.Args));
                return;
            }

            // M4 自检入口（SQLite 存储 + 批量 flush；控制台打印 + 日志文件兜底，跑完即退出）：
            //   ScreenSpy.exe --m4-selfcheck [--seconds=20] [--logic-only] [--dir=<目录>]
            //                                 [--flush-ms=15000] [--queue=8192] [--log=<path>]
            if (M4SelfCheck.IsRequested(e.Args))
            {
                Shutdown(M4SelfCheck.Run(e.Args));
                return;
            }

            // M5b 自检入口（托盘图标与菜单；控制台打印，跑完即退出）：
            //   ScreenSpy.exe --m5-selfcheck
            if (M5SelfCheck.IsRequested(e.Args))
            {
                Shutdown(M5SelfCheck.Run(e.Args));
                return;
            }

            // M6 自检入口（组件卡片 UI；控制台打印，跑完即退出）：
            //   ScreenSpy.exe --m6-selfcheck
            if (M6SelfCheck.IsRequested(e.Args))
            {
                Shutdown(M6SelfCheck.Run(e.Args));
                return;
            }

            // M7 自检入口（卡片双形态 + 位置持久化；控制台打印，跑完即退出）：
            //   ScreenSpy.exe --m7-selfcheck
            if (M7SelfCheck.IsRequested(e.Args))
            {
                Shutdown(M7SelfCheck.Run(e.Args));
                return;
            }

            // M9-1 自检入口（软件身份层：合并 + 分类；控制台打印 + 日志文件兜底，跑完即退出）：
            //   ScreenSpy.exe --m9-selfcheck [--dir=<目录>] [--log=<文件>]
            if (M9SelfCheck.IsRequested(e.Args))
            {
                Shutdown(M9SelfCheck.Run(e.Args));
                return;
            }

            // M10 自检入口（近 7 天柱状图：口径 / 布局 / 范围查询 / 组合根与降级）：
            //   ScreenSpy.exe --m10-selfcheck [--dir=<目录>] [--log=<文件>]
            if (M10SelfCheck.IsRequested(e.Args))
            {
                Shutdown(M10SelfCheck.Run(e.Args));
                return;
            }

            // M11 取证工具（UWP 窗口实地勘察）：确认"应用本体到底在哪里"。
            //   ScreenSpy.exe --diag-uwp
            if (UwpWindowDiagnostics.IsRequested(e.Args))
            {
                Shutdown(UwpWindowDiagnostics.Run());
                return;
            }

            // M11 自检入口（UWP 真实名修正：解析规则 / 键→名一致性 / 真实 Win32）：
            //   ScreenSpy.exe --m11-selfcheck [--dir=<目录>] [--log=<文件>]
            if (M11SelfCheck.IsRequested(e.Args))
            {
                Shutdown(M11SelfCheck.Run(e.Args));
                return;
            }

            // M12 自检入口（开机自启：纯逻辑 / 注册表真实往返 / 组合根与接线）：
            //   ScreenSpy.exe --m12-selfcheck [--dir=<目录>] [--log=<文件>]
            if (M12SelfCheck.IsRequested(e.Args))
            {
                Shutdown(M12SelfCheck.Run(e.Args));
                return;
            }

            // 桌面卡片演示入口（M0 结论落地用；**长期保留** —— 卡片接入产品（M6/M7）之前，
            // 它是唯一能观测桌面层行为的手段）：
            //   ScreenSpy.exe --demo-card [--seconds=8] [--win-d] [--no-marker]
            // 命中时不显示主窗口，改在独立 STA 线程上跑桌面卡片，跑完即退出。
            if (DesktopCardDemo.IsRequested(e.Args))
            {
                int exitCode = DesktopCardDemo.Run(e.Args);
                Shutdown(exitCode);
                return;
            }

            base.OnStartup(e);

            // ---------------- 产品启动（无参数即正式形态）----------------
            // M1–M4 在这里被装配成一条真实运行链路；主窗口显示运行状态。
            // 选项只解析一次：卡片开关（--no-card）等也要在启动流程里用到。
            StartupOptions startupOptions = StartupOptions.Parse(e.Args);
            HostStartResult start = ProductRuntime.Start(startupOptions);

            if (start.Outcome == HostStartOutcome.AlreadyRunning)
            {
                // 明确告知，而不是静默退出 —— 否则“双击了却没反应”会让人以为是坏了。
                MessageBox.Show(start.Message ?? "ScreenSpy 已经在运行。",
                    "ScreenSpy", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(0);
                return;
            }

            if (start.Outcome == HostStartOutcome.Failed || start.Runtime is null)
            {
                MessageBox.Show("ScreenSpy 启动失败：" + (start.Message ?? "未知原因"),
                    "ScreenSpy", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }

            _runtime = start.Runtime;

            // 限额提醒（M9-2）：引擎在心跳线程上判定，这里负责把它送到托盘气泡。
            _runtime.LimitNotified += OnLimitNotified;

            // 不使用 StartupUri，改为显式创建主窗口（WPF 会自动把它设为 Application.MainWindow）。
            var window = new MainWindow(_runtime);
            _window = window;

            // ---- 卡片（M6/M7）：默认随产品启动显示（决策③）。
            // 形态与位置**从库中读取**：默认嵌入桌面层、默认坐标。
            // 卡片是**纯展示件**：创建/启动失败只留痕，绝不打断统计、托盘与主界面 ——
            // 所以这里连警告弹窗都不弹（为了一个装饰件去打断启动是不划算的）。
            if (startupOptions.CardEnabled)
            {
                ProductRuntime runtime = _runtime;
                WidgetPlacement placement = runtime.LoadWidgetPlacement();

                var card = new CardWindow(
                    BuildCardModel,
                    placement.Mode,
                    placement.X,
                    placement.Y,
                    // 位置变化（「完成调整」或被拖动后）→ 立刻落库。
                    // 回调可能来自卡片线程：SqliteStore 每次操作都是短连接，因此是安全的。
                    onPositionChanged: (x, y) => runtime.SaveWidgetPosition(x, y));

                if (card.Start())
                {
                    _card = card;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[card] 启动失败：" + (card.LastError ?? "未知原因"));
                    try { card.Dispose(); } catch { /* 忽略 */ }
                }
            }

            // ---- 卡片位置/形态调整（M7）：主界面按钮与托盘菜单项是**同一个动作**。
            // 只在真有卡片时接上 —— 没有卡片却给出“调整位置”，点了没反应比不显示更糟。
            if (_card is not null)
            {
                window.IsAdjusting = () => _card?.IsAdjusting ?? false;
                window.ToggleAdjust = ToggleCardAdjust;

                // 形态按钮说的是**常态**（用户选定的形态），不是当前形态：
                // 调整中当前形态虽是浮动，但常态仍是嵌入 —— 读当前形态会让两个按钮
                // 同时指向“嵌入”，把两者的分工搅在一起。
                window.IsFloating = () => _card?.RestingMode == WidgetMode.Floating;
                window.ToggleCardMode = ToggleCardMode;
            }

            // ---- 托盘（M5b）：有托盘之后，“关闭窗口”不再退出，而是隐藏到托盘。
            _tray = TrayIconHost.TryCreate(
                snapshot: () => _runtime!.Snapshot(),
                setUserPaused: paused => _runtime!.UserPaused = paused,
                openMainWindow: ShowMainWindow,
                exit: RequestExit,
                isCardVisible: () => _card?.IsVisible ?? false,
                setCardVisible: SetCardVisible,
                toggleAdjust: _card is null ? null : ToggleCardAdjust,
                isAdjusting: () => _card?.IsAdjusting ?? false,
                // 常态已是浮动时“调整位置”无事可做 → 托盘菜单项也**禁用**（而不是点了没反应）。
                isAdjustAvailable: () => _card?.CanAdjust ?? false,
                // 「设置…」（M9-2）：限额与「软件与分类」都在主界面里，因此它就是打开主界面。
                openSettings: ShowMainWindow);

            if (_tray is null)
            {
                // 降级：没有托盘就**绝不能**隐藏窗口，否则程序会变成“看不见也关不掉”。
                window.CloseHint = "关闭本窗口即退出（托盘不可用；退出时会 flush 尾段）";
                MessageBox.Show(
                    "托盘图标创建失败：关闭主窗口将直接退出程序，也无法从托盘退出。",
                    "ScreenSpy", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                window.CloseHint = "关闭本窗口仅隐藏到托盘；退出请用托盘菜单「退出」（退出时会 flush 尾段）";
            }

            // 关闭策略放在 App 而不是 MainWindow：窗口不该关心“自己是不是常驻”。
            //
            // 这里刻意**不改 ShutdownMode**：取消关闭后窗口从未真正关闭，进程自然不会退出；
            // 而托盘不可用时不取消，窗口关闭即触发默认的 OnLastWindowClose → 正常退出，
            // 正好就是我们要的降级行为。
            window.Closing += OnMainWindowClosing;

            // ---- 开机自启（M12）：**注册表是唯一事实来源**，界面每次回读。
            // 不往 settings 表里再存一份"期望状态"：两份状态一旦不一致（用户在任务管理器删掉、
            // 被策略拦下、被别的工具覆盖），界面就会显示"已启用"而实际没有。
            var autoStart = new AutoStartRegistration();
            window.QueryAutoStart = autoStart.Query;
            window.SetAutoStart = enabled =>
            {
                if (enabled) autoStart.Enable();
                else autoStart.Disable();
            };

            // 自启拉起时**只进托盘、不显示主界面**（用户决策）——
            // 登录时弹一个窗口打断用户，不是"常驻小工具"该有的样子。
            // 例外：**托盘不可用时必须显示窗口**，否则没有任何入口能呼出界面，
            // 程序会变成"看不见也关不掉"（M5b 降级契约在自启场景下的延续）。
            if (AutoStartRules.ShouldShowMainWindow(startupOptions.AutoRun, _tray is not null))
            {
                window.Show();
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[startup] --autorun：只进托盘，不显示主界面。");
            }
        }

        /// <summary>关闭主窗口：托盘可用时隐藏（不退出），否则放行（退出）。</summary>
        private void OnMainWindowClosing(object? sender, CancelEventArgs e)
        {
            if (_tray is null || _exiting) return;   // 无托盘 或 明确退出 → 允许关闭

            e.Cancel = true;
            if (sender is Window window) window.Hide();
        }

        /// <summary>从托盘打开主界面（窗口可能隐藏，也可能被最小化）。</summary>
        private void ShowMainWindow()
        {
            Window? window = _window;
            if (window is null) return;

            try
            {
                if (!window.IsVisible) window.Show();
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
            }
            catch { /* 界面显示失败不该影响统计 */ }
        }

        /// <summary>从托盘退出：置位后走正常退出路径，由 OnExit 负责 flush 尾段。</summary>
        private void RequestExit()
        {
            _exiting = true;
            Shutdown(0);
        }

        /// <summary>
        /// 限额提醒（M9-2）。回调在**心跳线程**上，因此必须 marshal 到 UI 线程再弹气泡
        /// （<c>NotifyIcon</c> 属于 UI 线程）。
        ///
        /// 托盘不可用（降级运行）时只留一行调试痕迹 —— 通知是可选能力，不该为了它弹错误框
        /// 去打断用户。
        /// </summary>
        private void OnLimitNotified(LimitNotification note)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    TrayIconHost? tray = _tray;
                    if (tray is null)
                    {
                        System.Diagnostics.Debug.WriteLine("[limit] " + note.Message);
                        return;
                    }

                    tray.ShowNotification(note.Title, note.Message);
                }));
            }
            catch { /* 通知失败不影响统计 */ }
        }

        /// <summary>
        /// 卡片取数函数（**卡片的“绑定”**）：在卡片线程上每秒被调用一次。
        ///
        /// 复用组合根快照，于是卡片与主界面、托盘看到的是同一份口径；
        /// <see cref="ProductRuntime.Snapshot"/> 本身永不抛异常，这里再兜一层是为了
        /// “任何异常都不该让卡片线程停摆”这条底线。
        /// </summary>
        private CardModel BuildCardModel()
        {
            ProductRuntime? runtime = _runtime;
            if (runtime is null) return new CardModel();

            try
            {
                return CardData.FromStatus(runtime.Snapshot());
            }
            catch
            {
                return new CardModel();
            }
        }

        /// <summary>托盘「显示卡片 / 隐藏卡片」（M6）。卡片不可用（或 --no-card）时静默忽略。</summary>
        private void SetCardVisible(bool visible)
        {
            CardWindow? card = _card;
            if (card is null) return;

            if (visible) card.Show();
            else card.Hide();
        }

        /// <summary>
        /// 「调整位置 / 完成调整」（M7）：主界面按钮与托盘菜单项是**同一个动作**。
        ///
        /// 这是**临时**进入浮动形态：结束时保存坐标并回到用户选定的常态形态。
        /// </summary>
        private void ToggleCardAdjust()
        {
            CardWindow? card = _card;
            if (card is null) return;

            if (card.IsAdjusting) card.EndAdjust();
            else card.BeginAdjust();
        }

        /// <summary>
        /// 切换**常态形态**（嵌入桌面层 ⇄ 浮动窗口）并存库（M7）。
        ///
        /// 与「调整位置」的分工：调整是临时的（用完回到这个常态），这里改的是常态本身。
        ///
        /// 两处保护：
        ///  * **调整中先结束调整**。主界面那个按钮在调整中是**禁用**的，但这条路仍可能
        ///    被别的入口调到（自检、将来新增的菜单项）。若不先结束调整，就会绕过
        ///    「完成调整」的“取当前坐标再上报”，于是最后一次拖动可能还没写进库
        ///    （最多约 1 秒的静默偏差）——正是本项目最忌的那类边界偏差。
        ///    （自检断言：调整中切常态后，库里的坐标必须是**拖动后**的位置。）
        ///  * **只有切换真的成功才写库** —— 否则库里的形态与屏幕上的形态会不一致，
        ///    而下次启动就会照着库里的错值走。
        ///
        /// 读取用**常态**而不是当前形态：调整中当前形态是浮动的，若据此取反，就会把常态
        /// 从嵌入直接推到浮动（用户只是结束了一次调整，却把常态改掉了）。
        /// </summary>
        private void ToggleCardMode()
        {
            CardWindow? card = _card;
            if (card is null) return;

            // 先结束调整（把当前坐标上报上去），再改常态。
            if (card.IsAdjusting) card.EndAdjust();

            WidgetMode next = card.RestingMode == WidgetMode.Floating
                ? WidgetMode.Embedded
                : WidgetMode.Floating;

            if (!card.SwitchMode(next)) return;

            card.SetRestingMode(next);
            _runtime?.SaveWidgetMode(next);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 释放顺序（重要）：**卡片 → 托盘 → 运行时**。
            // 卡片每秒都要读运行时的快照，所以必须最先停 —— 否则会出现
            // “运行时已开始释放、卡片仍在取数”的窗口期。
            try { _card?.Dispose(); } catch { /* 忽略 */ }
            _card = null;

            // 再收托盘（避免退出瞬间还留着图标）。
            try { _tray?.Dispose(); } catch { /* 忽略 */ }
            _tray = null;

            // 退出时释放运行时：停心跳 → 刷日志尾段与落库尾段。漏掉这一步每次退出都会丢尾段。
            try { if (_runtime is not null) _runtime.LimitNotified -= OnLimitNotified; } catch { /* 忽略 */ }
            try { _runtime?.Dispose(); } catch { /* 退出路径不再抛异常 */ }
            _runtime = null;

            base.OnExit(e);
        }
    }
}
