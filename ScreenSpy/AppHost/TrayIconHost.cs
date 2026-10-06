using System;
using System.Drawing;
using System.Threading;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ScreenSpy.AppHost;

/// <summary>
/// 托盘图标宿主（M5b）：把 <see cref="Forms.NotifyIcon"/> 与右键菜单包起来，
/// 对外只暴露「创建 / 释放」两件事。
///
/// 三条实现约定：
///  * **创建失败返回 null 而不抛异常** —— 上层必须据此决定“关闭窗口是否等于退出”。
///    没有托盘还把窗口藏起来，程序就会变成“看不见也关不掉”。
///  * **菜单动作绝不把异常抛进消息循环** —— 统一走 <c>Safe</c>。
///  * 图标由 <see cref="TrayIconFactory"/> 程序化生成，并按「统计中 / 已暂停」两态切换。
///
/// 菜单结构（本次确认）：打开主界面 / 暂停统计 / 调整组件位置（M8，禁用占位）/
/// 设置（M8/M9，禁用占位）/ 退出。占位项**故意禁用** —— 点了没反应比“看起来能点”诚实。
///
/// 线程：必须在 WPF 的 UI 线程（STA、有消息循环）上创建与释放。
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
    private readonly Icon _normalIcon;
    private readonly Icon _pausedIcon;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _openItem;
    private readonly Forms.ToolStripMenuItem _pauseItem;
    private readonly Forms.ToolStripMenuItem? _cardItem;
    private readonly Forms.ToolStripMenuItem _exitItem;
    private readonly DispatcherTimer _timer;
    private readonly Func<RuntimeStatus> _snapshot;
    private readonly Action<bool> _setUserPaused;
    private readonly Func<bool>? _isCardVisible;
    private readonly Action<bool>? _setCardVisible;
    private readonly Action _exit;

    private bool _lastPaused;
    private bool _lastCardVisible;
    private int _disposed;

    private TrayIconHost(Func<RuntimeStatus> snapshot,
                         Action<bool> setUserPaused,
                         Action openMainWindow,
                         Action exit,
                         Func<bool>? isCardVisible,
                         Action<bool>? setCardVisible)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _setUserPaused = setUserPaused ?? throw new ArgumentNullException(nameof(setUserPaused));
        _isCardVisible = isCardVisible;
        _setCardVisible = setCardVisible;
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));

        _normalIcon = TrayIconFactory.Create(paused: false);
        _pausedIcon = TrayIconFactory.Create(paused: true);

        _menu = new Forms.ContextMenuStrip();

        _openItem = new Forms.ToolStripMenuItem("打开主界面");
        _openItem.Click += (_, _) => Safe(openMainWindow);
        _menu.Items.Add(_openItem);

        _menu.Items.Add(new Forms.ToolStripSeparator());

        _pauseItem = new Forms.ToolStripMenuItem("暂停统计");
        _pauseItem.Click += (_, _) => TogglePause();
        _menu.Items.Add(_pauseItem);

        // 卡片开关（M6）：只在**接线了卡片回调**时才出现 ——
        // 这样不带卡片的宿主（例如 M5b 自检）看到的菜单与 M5b 当时完全一致，形态不被悄悄改动。
        if (_setCardVisible is not null)
        {
            _cardItem = new Forms.ToolStripMenuItem("显示卡片");
            _cardItem.Click += (_, _) => ToggleCard();
            _menu.Items.Add(_cardItem);
        }

        _menu.Items.Add(new Forms.ToolStripSeparator());

        // M8 / M9 的入口先摆出来但禁用（已确认的决策），并明确标注“未实现”。
        _menu.Items.Add(new Forms.ToolStripMenuItem("调整组件位置（M8 未实现）") { Enabled = false });
        _menu.Items.Add(new Forms.ToolStripMenuItem("设置（M8/M9 未实现）") { Enabled = false });

        _menu.Items.Add(new Forms.ToolStripSeparator());

        _exitItem = new Forms.ToolStripMenuItem("退出");
        _exitItem.Click += (_, _) => Safe(_exit);
        _menu.Items.Add(_exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _normalIcon,
            Text = "ScreenSpy",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _notifyIcon.DoubleClick += (_, _) => Safe(openMainWindow);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Refresh();
    }

    /// <summary>
    /// 尝试创建（不带卡片开关；菜单里不会出现卡片项）。
    /// 保留此重载是为了让 M5b 时代的调用与自检形态**原样不变**。
    /// </summary>
    public static TrayIconHost? TryCreate(Func<RuntimeStatus> snapshot,
                                          Action<bool> setUserPaused,
                                          Action openMainWindow,
                                          Action exit)
        => TryCreate(snapshot, setUserPaused, openMainWindow, exit, isCardVisible: null, setCardVisible: null);

    /// <summary>尝试创建（M6：附带卡片的显示状态与开关）。任何失败都返回 null（调用方据此走降级路径）。</summary>
    public static TrayIconHost? TryCreate(Func<RuntimeStatus> snapshot,
                                          Action<bool> setUserPaused,
                                          Action openMainWindow,
                                          Action exit,
                                          Func<bool>? isCardVisible,
                                          Action<bool>? setCardVisible)
    {
        try
        {
            return new TrayIconHost(snapshot, setUserPaused, openMainWindow, exit, isCardVisible, setCardVisible);
        }
        catch
        {
            return null;
        }
    }

    private void TogglePause()
    {
        bool next = !_lastPaused;
        Safe(() => _setUserPaused(next));
        Refresh();
    }

    /// <summary>切换卡片显示：把**期望状态**交给上层（由上层真正 Show/Hide），随后以实际状态回读刷新菜单。</summary>
    private void ToggleCard()
    {
        if (_setCardVisible is null) return;

        bool next = !_lastCardVisible;
        Safe(() => _setCardVisible(next));
        Refresh();
    }

    private void Refresh()
    {
        try
        {
            RuntimeStatus status = _snapshot();
            bool userPaused = status.UserPaused;

            if (userPaused != _lastPaused)
            {
                _lastPaused = userPaused;
                _notifyIcon.Icon = userPaused ? _pausedIcon : _normalIcon;
            }

            _pauseItem.Checked = userPaused;
            _pauseItem.Text = userPaused ? "继续统计" : "暂停统计";

            // 卡片项：文案与勾选都以**实际可见状态**为准（而不是我们以为的状态）——
            // 这样即使上层 Show/Hide 失败，菜单也不会撒谎。
            if (_cardItem is not null)
            {
                bool visible = _isCardVisible?.Invoke() ?? false;
                _lastCardVisible = visible;
                _cardItem.Checked = visible;
                _cardItem.Text = visible ? "隐藏卡片" : "显示卡片";
            }

            _notifyIcon.Text = ComposeTooltip(status.TodayTotal, status.Paused, userPaused);
        }
        catch
        {
            // 托盘显示失败不得影响统计。
        }
    }

    /// <summary>
    /// 托盘 tooltip 文案（纯函数，便于自检客观断言，不必去读真实 <c>NotifyIcon</c>）。
    ///
    /// <paramref name="todayTotal"/> 是**今天一整天**的口径
    /// （<see cref="RuntimeStatus.TodayTotal"/> = 本次运行 + 库中基线），
    /// 与主界面大字号、桌面卡片使用同一个口径 —— 重启后不会掉回 0:00。
    ///
    /// 参数是**两个独立的口径**：<paramref name="paused"/> 是“生效暂停”
    /// （<see cref="RuntimeStatus.Paused"/>，锁屏/睡眠 或 用户手动，任一成立），
    /// <paramref name="userPaused"/> 只用于说明**原因**。
    ///
    /// 曾经的真实缺陷：<see cref="RuntimeStatus.Paused"/> 只被填成“锁屏/睡眠”，
    /// 于是手动暂停时这里既不显示「已暂停」也不显示「锁屏/睡眠」，tooltip 安静地少了信息。
    /// </summary>
    internal static string ComposeTooltip(TimeSpan todayTotal, bool paused, bool userPaused)
    {
        string text = "ScreenSpy · 今日 " + Format(todayTotal);
        if (paused) text += userPaused ? "（已暂停）" : "（锁屏/睡眠）";

        // NotifyIcon.Text 有长度上限（超出会抛异常），这里显式截断。
        return text.Length > 63 ? text.Substring(0, 63) : text;
    }

    private static string Format(TimeSpan value)
        => $"{(int)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}";

    private static void Safe(Action action)
    {
        try { action(); } catch { /* 托盘动作不冒泡异常 */ }
    }

    // ------------------------------------------------------------ 自检 / 诊断

    /// <summary>
    /// **仅供自检与诊断**：导出菜单当前形态（标签 + 禁用标记 + 勾选标记）。
    ///
    /// 读的就是真实菜单对象，因此能客观反映“菜单是否长成了预期样子”
    /// （含 M8/M9 占位项确实被禁用），而不是靠人去肉眼看。
    /// </summary>
    internal string[] DescribeMenuForDiagnostics()
    {
        var list = new System.Collections.Generic.List<string>(_menu.Items.Count);
        foreach (Forms.ToolStripItem item in _menu.Items)
        {
            string text = item is Forms.ToolStripSeparator ? "---" : item.Text ?? string.Empty;
            if (!item.Enabled) text += " [禁用]";
            if (item is Forms.ToolStripMenuItem menuItem && menuItem.Checked) text += " [已勾选]";
            list.Add(text);
        }
        return list.ToArray();
    }

    /// <summary>**仅供自检与诊断**：走与真实点击「打开主界面」完全相同的路径。</summary>
    internal void InvokeOpenForDiagnostics() => _openItem.PerformClick();

    /// <summary>**仅供自检与诊断**：走与真实点击「暂停统计」完全相同的路径。</summary>
    internal void InvokePauseForDiagnostics() => TogglePause();

    /// <summary>
    /// **仅供自检与诊断**：走与真实点击「显示卡片 / 隐藏卡片」完全相同的路径。
    /// 卡片项未接线（4 参重载）时返回 false，便于自检断言“菜单里根本没有这一项”。
    /// </summary>
    internal bool InvokeCardToggleForDiagnostics()
    {
        if (_cardItem is null) return false;
        _cardItem.PerformClick();
        return true;
    }

    /// <summary>
    /// **仅供自检与诊断**：走与真实点击「退出」完全相同的路径。
    ///
    /// 为什么值得单独验证：菜单项真被点到时才走这条路，而自检不能真的退出进程，
    /// 于是很容易留下“退出菜单没接线”这个从不被检验的缺口。这里用
    /// <c>PerformClick</c> 触发真实 Click 事件（不是直接调回调）。
    /// </summary>
    internal void InvokeExitForDiagnostics() => _exitItem.PerformClick();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { _timer.Stop(); } catch { /* 忽略 */ }
        try { _notifyIcon.Visible = false; } catch { /* 忽略 */ }
        try { _notifyIcon.Dispose(); } catch { /* 忽略 */ }
        try { _menu.Dispose(); } catch { /* 忽略 */ }
        try { _normalIcon.Dispose(); } catch { /* 忽略 */ }
        try { _pausedIcon.Dispose(); } catch { /* 忽略 */ }
    }
}
