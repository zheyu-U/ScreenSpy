using System.ComponentModel;
using System.Windows;
using ScreenSpy.AppHost;
using ScreenSpy.Demo;
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

            // 不使用 StartupUri，改为显式创建主窗口（WPF 会自动把它设为 Application.MainWindow）。
            var window = new MainWindow(_runtime);
            _window = window;

            // ---- 卡片（M6）：默认随产品启动显示（决策③）。
            // 卡片是**纯展示件**：创建/启动失败只留痕，绝不打断统计、托盘与主界面 ——
            // 所以这里连警告弹窗都不弹（为了一个装饰件去打断启动是不划算的）。
            if (startupOptions.CardEnabled)
            {
                var card = new CardWindow(BuildCardModel);
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

            // ---- 托盘（M5b）：有托盘之后，“关闭窗口”不再退出，而是隐藏到托盘。
            _tray = TrayIconHost.TryCreate(
                snapshot: () => _runtime!.Snapshot(),
                setUserPaused: paused => _runtime!.UserPaused = paused,
                openMainWindow: ShowMainWindow,
                exit: RequestExit,
                isCardVisible: () => _card?.IsVisible ?? false,
                setCardVisible: SetCardVisible);

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

            window.Show();
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
            try { _runtime?.Dispose(); } catch { /* 退出路径不再抛异常 */ }
            _runtime = null;

            base.OnExit(e);
        }
    }
}
