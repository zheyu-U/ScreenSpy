using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenSpy.Analytics;
using ScreenSpy.AppHost;
using ScreenSpy.Charts;
using ScreenSpy.Collector;
using ScreenSpy.Limits;

namespace ScreenSpy;

/// <summary>
/// 主窗口：**运行状态面板**（本次接入的可见形态）。
///
/// 目的很单纯 —— 让“M1–M4 是否真的在跑、数据到底写去哪了”一眼可见。
/// 上层再逐步长出**按需重建**的交互区：「软件与分类」（M9-1）、「限额」（M9-2）、「近 7 天柱状图」（M10）——
/// 它们与每秒刷新的状态区严格分开，因为重建交互控件会把用户正打开的下拉框销毁掉。
///
/// 实现取巧但可靠：**每秒从 <see cref="ProductRuntime.Snapshot"/> 取一份快照再赋值给控件**，
/// 不用绑定（省掉 INotifyPropertyChanged 的一堆样板），也不直接触碰各组件，
/// 因此界面读不到撕裂的中间状态，也不会因为组件内部加锁而卡住 UI 线程。
///
/// 关闭窗口的语义由 <c>App</c> 决定（M5b 起）：托盘可用时**仅隐藏到托盘**、不退出；
/// 托盘不可用时才“关闭即退出”（此时绝不能隐藏，否则程序会失联）。
/// 无论走哪条路径，真正退出都会由 <c>App.OnExit</c> 释放运行时并 flush 尾段。
/// </summary>
public partial class MainWindow : Window
{
    private readonly ProductRuntime _runtime;
    private readonly DispatcherTimer _timer;

    /// <summary>
    /// 页脚里“关闭本窗口会怎样”的说明。由 <c>App</c> 依据**托盘是否可用**设置：
    /// 托盘可用 = 关闭仅隐藏；托盘不可用 = 关闭即退出（此时绝不能隐藏，否则程序会失联）。
    /// </summary>
    internal string CloseHint { get; set; } = "关闭本窗口即退出（退出时会 flush 尾段）";

    // ---------------------------------------------------------------- 近 7 天图表（M10）
    //
    // 图表**只读库**（用户决策），所以没必要每秒重读：库本身最多 15 秒才更新一次。
    // 于是刷新节奏与落库间隔对齐（每 15 秒），另外在"进入窗口 / 改维度 / 改目标 / 点刷新"时立即读一次。

    /// <summary>图表刷新间隔。与 <c>--flush-ms</c> 的默认 15 秒对齐：读得更勤也只是重复读同一份数据。</summary>
    private static readonly TimeSpan ChartRefreshInterval = TimeSpan.FromSeconds(15);

    /// <summary>上次读图时刻（避免每秒快照刷新都去读库）。</summary>
    private DateTime _lastChartRefresh = DateTime.MinValue;

    /// <summary>重建图表下拉框期间为 true：控件赋值会触发 SelectionChanged，必须忽略，否则界面会自己改自己。</summary>
    private bool _chartUiBusy;

    // ---------------------------------------------------------------- 卡片调整（M7）
    //
    // 四个回调都由 App 接线；**卡片是可选组件，所以都可能是 null**。
    // 为 null 时对应按钮**隐藏** —— 没有卡片却给出“调整位置”，点了没反应比不显示更糟。
    // 文案、可用性一律按**真实状态回读**（见 UpdatePositionControls），不按“我们以为点了会怎样”。

    /// <summary>卡片当前是否处于「调整位置」这个**临时**会话（常态为嵌入、为了拖动临时切浮动）。</summary>
    internal Func<bool>? IsAdjusting { get; set; }

    /// <summary>进入/结束「调整位置」。</summary>
    internal Action? ToggleAdjust { get; set; }

    /// <summary>
    /// 卡片**常态**是否是浮动窗口（用户选定的形态，而**不是**“调整中”那个临时状态）。
    ///
    /// 刻意读常态而不是当前形态：两者在“调整中”会不一致，而形态按钮说的永远是**常态**
    /// （这正是它与「调整位置」的分工 —— 一个改常态，一个是临时插曲）。
    /// </summary>
    internal Func<bool>? IsFloating { get; set; }

    /// <summary>切换卡片常态形态（嵌入 ⇄ 浮动）并持久化。</summary>
    internal Action? ToggleCardMode { get; set; }

    // ---------------------------------------------------------------- 开机自启（M12）

    /// <summary>
    /// 回读开机自启状态（由 <c>App</c> 注入；底层是注册表 Run 键）。
    ///
    /// 刻意做成"取状态"而不是"取布尔值"：界面需要知道**为什么**是这个状态
    /// （没启用 / 已启用 / 有一条指向别处的同名残留 / 注册表读写失败），
    /// 否则用户只能看到一个不听话的复选框。
    /// </summary>
    internal Func<AutoStartState>? QueryAutoStart { get; set; }

    /// <summary>设置开机自启（true = 写入 Run 键；false = 删掉）。由 <c>App</c> 注入。</summary>
    internal Action<bool>? SetAutoStart { get; set; }

    /// <summary>
    /// 用户点击「登录时自动启动」。
    ///
    /// 处理完**立刻回读**：写入失败（权限 / 策略拦下）时开关马上弹回真实值，
    /// 而不是留在屏幕上骗人 —— 与「软件与分类」保存失败拉回真实值的做法一致。
    /// </summary>
    private void AutoStartCheck_Click(object sender, RoutedEventArgs e)
    {
        bool wanted = AutoStartCheck.IsChecked == true;

        try { SetAutoStart?.Invoke(wanted); }
        catch { /* 界面动作失败不该影响统计；下面回读真实状态会纠正显示 */ }

        UpdateAutoStartControls();
    }

    /// <summary>
    /// 刷新「开机自启」开关与提示。**一律按注册表回读渲染**，不按"用户点了什么"记状态。
    ///
    /// 于是三种情况都自动对上：
    ///  * 写入失败 → 开关弹回关闭 + 提示写出原因；
    ///  * 用户在别处改过（任务管理器启动项 / 注册表编辑器）→ 这里跟着变；
    ///  * 存在同名条目但**指向别的路径** → 显示"未启用"，并说明那条残留会被覆盖。
    /// </summary>
    private void UpdateAutoStartControls()
    {
        Func<AutoStartState>? query = QueryAutoStart;
        if (query is null)
        {
            // 没接线就不给开关：点了没反应比不显示更糟（与卡片按钮同一口径）。
            AutoStartCheck.Visibility = Visibility.Collapsed;
            AutoStartHintText.Text = string.Empty;
            return;
        }

        AutoStartState state;
        try
        {
            state = query();
        }
        catch (Exception ex)
        {
            AutoStartCheck.Visibility = Visibility.Visible;
            AutoStartCheck.IsEnabled = false;
            AutoStartHintText.Text = "读取开机自启状态失败：" + ex.GetType().Name + "（" + ex.Message + "）";
            return;
        }

        AutoStartCheck.Visibility = Visibility.Visible;
        AutoStartCheck.IsEnabled = state.Available && SetAutoStart is not null;
        AutoStartCheck.IsChecked = state.Enabled;
        AutoStartHintText.Text = BuildAutoStartHint(state);
    }

    /// <summary>
    /// 开关下方那行提示（纯函数，便于自检断言它**确实会说出真实原因**）。
    /// 四种状态各说各话，因为用户接下来该做的事不同。
    /// </summary>
    internal static string BuildAutoStartHint(AutoStartState state)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));

        if (!state.Available)
            return "无法读写注册表，因此开机自启暂时不可用：" + (state.Error ?? "未知原因");

        if (state.Enabled)
            return "已启用：登录时**只进托盘**（不显示主界面），并自动开始统计。" +
                   "写入位置 = 注册表 HKCU\\" + AutoStartRules.RunKeyPath +
                   " 里的值 " + AutoStartRules.ValueName + "。";

        if (state.PointsElsewhere)
            return "未启用：注册表里有一条**同名**条目，但它指向的不是本程序（" + state.Command + "）。" +
                   "勾选会覆盖它。";

        return "未启用。勾选后会写入注册表 HKCU\\" + AutoStartRules.RunKeyPath + "，登录时只进托盘。";
    }

    private void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        try { ToggleCardMode?.Invoke(); }
        catch { /* 界面动作失败不该影响统计 */ }
        Refresh();
    }

    private void PositionButton_Click(object sender, RoutedEventArgs e)
    {
        try { ToggleAdjust?.Invoke(); }
        catch { /* 界面动作失败不该影响统计 */ }
        Refresh();
    }

    /// <summary>
    /// 「调整位置」按钮文案（纯函数，便于自检客观断言）。
    /// 显示的是**下一步动作**：当前在调整中 → 按钮写着“完成调整”。
    /// </summary>
    internal static string ComposePositionButtonText(bool adjusting)
        => adjusting ? "完成调整" : "调整位置";

    /// <summary>形态按钮文案（纯函数，同上）。同样显示**下一步动作**。</summary>
    internal static string ComposeModeButtonText(bool floating)
        => floating ? "切换为嵌入桌面层" : "切换为浮动窗口";

    /// <remarks>
    /// 构造函数刻意是 <c>internal</c>：参数 <see cref="ProductRuntime"/> 是程序集内部类型，
    /// 若构造函数为 public 会触发 CS0051（可访问性不一致）。
    /// </remarks>
    internal MainWindow(ProductRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

        InitializeComponent();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();

        Loaded += (_, _) =>
        {
            Refresh();
            // 「软件与分类」一节按需重建（它与每秒刷新的状态区互不干扰，见该节顶部说明）。
            RebuildIdentitySection();
            SetIdentityHint("列表在你点击「刷新列表」后更新；改动会立即保存。");
            // 近 7 天图表（M10）：维度下拉只建一次（选择会一直保持），目标下拉随维度重建。
            BuildChartDimensionBox();
            FillChartTargets();
            RefreshChart();
            _timer.Start();
        };

        Closed += (_, _) => _timer.Stop();
    }

    private void Refresh()
    {
        RuntimeStatus status;
        try
        {
            status = _runtime.Snapshot();
        }
        catch (Exception ex)
        {
            // 展示层异常绝不该让程序崩掉；把错误摆到脸上即可。
            StateText.Text = "状态读取失败：" + ex.GetType().Name + ": " + ex.Message;
            return;
        }

        HeaderText.Text = "ScreenSpy · 正在统计";
        OptionsText.Text = status.OptionsDescription;

        TodayText.Text = Format(status.TodayTotal);
        StateText.Text = DescribeState(status);
        NonAppText.Text = DescribeNonApp(status);
        UpdatePositionControls();
        UpdateLimitStatusText();
        // 开机自启（M12）：每秒回读注册表，于是"在别处改过启动项"也会在 1 秒内反映到界面上。
        // 这里只改一个复选框与一行文字，不重建任何列表，因此不会踩到"销毁用户正打开的控件"那个坑。
        UpdateAutoStartControls();

        // 近 7 天图表（M10）：每 15 秒读一次库（与落库间隔对齐）。这里**只重画图表**，
        // 绝不重建上面那两个下拉框 —— 那会把用户正打开的列表销毁掉，表现为"点了没反应"。
        if (DateTime.Now - _lastChartRefresh >= ChartRefreshInterval) RefreshChart();

        SessionText.Text = DescribeSession(status);
        SourceText.Text = DescribeSources(status);

        CurrentAppText.Text = status.CurrentApp;

        // 榜单 = 软件行（Top 5，占比来自榜单：占**已归因软件时长**）
        //      + 末尾一条「不计入统计」（非软件活跃，**占比列留空**）。
        // 该行刻意不给占比：它的分母是“今日真实活跃”，而软件行的分母是“已归因软件时长”，
        // 两者同列并排会让人以为可以相加、相减 —— 与其给一个含义与邻行不同的百分数，不如留空。
        // （空位宽度与软件行一致，行名因此仍与软件行左对齐；见 ShareCell / BlankShareCell。）
        TopList.Items.Clear();

        int topRows = 0;
        foreach (AppUsageEntry entry in status.Top)
        {
            TopList.Items.Add(ComposeAppTopRow(entry));
            topRows++;
        }

        if (ComposeNonAppTopRow(status) is { } nonAppRow)
        {
            TopList.Items.Add(nonAppRow);
            topRows++;
        }

        // 一个可显示条目都没有时才给占位文案（有非软件行就不算“空”）。
        if (topRows == 0)
        {
            TopList.Items.Add(status.SeededApps > 0
                ? "（今日暂无本次运行的记录；已从库中续算 " + status.SeededApps + " 条）"
                : "（暂无）");
        }

        // 刻意把“本次运行”写在守恒里：守恒等式只描述本次运行（库中基线不计入 AttributedTotal），
        // 而上面的大字号“今日真实活跃”是今天一整天。不写明会被误读成两处矛盾。
        //
        // 四桶相加才是“本次运行的被计入活跃”：锁屏/登录界面按口径**完全不计入**，
        // 因此它单独成一项（而不是混进“不计入使用时长”，那样会让总数把锁屏也算进去）。
        ConservationText.Text =
            $"时间守恒（本次运行）：已归因 {Format(status.Attributed)} + 不计入使用时长 {Format(status.Filtered)} + " +
            $"无前台/未知 {Format(status.Unattributed)} + 锁屏剔除 {Format(status.LockExcluded)} = " +
            $"{Format(status.Attributed + status.Filtered + status.Unattributed + status.LockExcluded)}" +
            $"（应等于本次运行活跃 {Format(status.TodayActive)}）；" +
            $"今日真实活跃 {Format(status.TodayTotal)}" +
            $"（= 本次运行 {Format(status.TodayActive)} − 锁屏剔除 {Format(status.LockExcluded)} + 库中续算基线 {Format(status.Seeded)}），" +
            $"软件条目 {status.AppCount}";

        StoreText.Text = DescribeStore(status);
        LogText.Text = DescribeLog(status);

        FooterText.Text =
            $"快照 {status.SampledAt:HH:mm:ss} · 每秒刷新 · {CloseHint}";

        UpdateWarnings(status);
    }

    /// <summary>
    /// 刷新「调整位置」「切换形态」两个按钮与提示。
    ///
    /// 三条都按**真实情况**回读，而不是按“我们以为点了会怎样”：
    ///  * 按钮是否存在 —— 看回调是否接线（没卡片就不该有这个按钮，点了没反应比不显示更糟）；
    ///  * 按钮文案 —— 看卡片**真实的**调整状态与常态形态，
    ///    因此用托盘菜单进入调整、再用这里退出（或反之）都不会出现文案骗人的情况；
    ///  * 按钮可用性 —— 两按钮**互斥**（见 <see cref="ComposePositionControlAvailability"/>）。
    /// </summary>
    private void UpdatePositionControls()
    {
        bool adjustWired = ToggleAdjust is not null;
        bool modeWired = ToggleCardMode is not null;

        if (!adjustWired && !modeWired)
        {
            PositionButton.Visibility = Visibility.Collapsed;
            ModeButton.Visibility = Visibility.Collapsed;
            PositionHintText.Text = string.Empty;
            return;
        }

        bool adjusting = false;
        bool floating = false;
        try { adjusting = IsAdjusting?.Invoke() ?? false; } catch { /* 读形态失败按“未调整”显示 */ }
        try { floating = IsFloating?.Invoke() ?? false; } catch { /* 读形态失败按“嵌入”显示 */ }

        PositionButton.Visibility = adjustWired ? Visibility.Visible : Visibility.Collapsed;
        ModeButton.Visibility = modeWired ? Visibility.Visible : Visibility.Collapsed;

        (bool positionEnabled, bool modeEnabled) = ComposePositionControlAvailability(adjusting, floating);

        PositionButton.Content = ComposePositionButtonText(adjusting);
        PositionButton.IsEnabled = positionEnabled;
        ModeButton.Content = ComposeModeButtonText(floating);
        ModeButton.IsEnabled = modeEnabled;

        PositionHintText.Text = BuildPositionHint(adjusting, floating);
    }

    /// <summary>
    /// 两个按钮的**可用性**（纯函数，便于自检客观断言，不必去读真实 WPF 控件）。
    ///
    /// 规则只有两条，但各自堵住一个真实的坑：
    ///
    ///  1. **调整中 → 禁用形态按钮**。否则两个按钮会同时指向“嵌入”：调整中的形态本来就是
    ///     临时的浮动，而那个按钮走的是“改常态”那条路（而不是「完成调整」的
    ///     “先取坐标再上报”）—— 于是最后一次拖动可能还没写进库（最多约 1 秒的静默偏差），
    ///     且当常态为嵌入时，在调整中点它还会把**临时**的浮动变成**永久**的常态。
    ///  2. **常态为浮动 → 禁用调整按钮**。那时卡片随处可拖、每拍自动记位置，
    ///     “调整”没有可做的事（点了没反应比禁用更糟）。
    ///
    /// 注：<paramref name="adjusting"/> 与 <paramref name="floating"/> 同时为真的是**不可能状态**
    /// （调整中意味着常态非浮动），这里仍按“以调整中为准”给出确定答案，而不是抛异常 ——
    /// 界面刷新路径不该因为一个读取竞争而崩掉。
    /// </summary>
    internal static (bool PositionEnabled, bool ModeEnabled) ComposePositionControlAvailability(
        bool adjusting, bool floating)
    {
        bool modeEnabled = !adjusting;
        bool positionEnabled = adjusting || !floating;
        return (positionEnabled, modeEnabled);
    }

    /// <summary>
    /// 卡片区域那行提示（纯函数，便于自检断言）。
    ///
    /// 三种状态各说各话，因为它们的“下一步能做什么”确实不同 ——
    /// 说成同一句会让人以为嵌入形态下也能直接拖。
    /// </summary>
    internal static string BuildPositionHint(bool adjusting, bool floating)
    {
        if (adjusting)
            return "调整中：卡片已变为**可拖动**的普通窗口（在最前）。用鼠标拖到想要的位置，" +
                   "然后点「完成调整」——位置会被记住，下次启动仍在原处。" +
                   "（此时「切换为浮动窗口 / 嵌入桌面层」按钮已禁用：请先完成调整，" +
                   "否则最后一次拖动可能还没被存下来。）";

        return floating
            ? "卡片当前是**浮动窗口**：像普通窗口一样可拖动（拖完自动记住位置），也会被其它窗口盖住。" +
              "（「调整位置」按钮已禁用 —— 无需临时调整：现在直接拖即可。）"
            : "卡片当前**嵌入桌面层**：会被其它窗口盖住、鼠标穿透、不会抢焦点。" +
              "要移动它点「调整位置」；想让它像普通窗口一样随时可拖，点「切换为浮动窗口」。";
    }

    // ---------------------------------------------------------------- 软件与分类（M9-1）
    //
    // 这一节与上面的状态区**刷新方式不同**：状态区每秒重建文本（无交互、无副作用），
    // 而这里含下拉框与文本框 —— 若跟着每秒刷新重建，用户正在打开的下拉框会在半秒后被销毁，
    // 表现为“点了没反应”（正是本项目最忌的“看起来能用、其实不能用”）。
    // 因此改成**按需重建**：进窗口时、点「刷新列表」时、以及每次改动之后。

    /// <summary>「未分类」在下拉框里的显示文字。它**不是一个分类**，只是“没设”。</summary>
    internal const string UncategorizedLabel = "(未分类)";

    /// <summary>重建期间为 true：控件赋值会触发 SelectionChanged，此时必须忽略，否则界面会自己改自己的数据。</summary>
    private bool _identityUiBusy;

    /// <summary>
    /// 下拉框里的一项：既显示给人看，也带着**真实的键**（不能靠解析显示文本来还原键）。
    /// 内部可见：M10 自检要断言"维度下拉的键确实能被解析成枚举"（键写错会让维度**静默**退回总量）。
    /// </summary>
    internal sealed class IdentityChoice
    {
        public IdentityChoice(string key, string display)
        {
            Key = key;
            Display = display;
        }

        public string Key { get; }
        public string Display { get; }

        public override string ToString() => Display;
    }

    private void ReloadAppsButton_Click(object sender, RoutedEventArgs e)
    {
        RebuildIdentitySection();
        SetIdentityHint($"列表已刷新（{DateTime.Now:HH:mm:ss}）。");
    }

    private void NewCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        string name = (NewCategoryBox.Text ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            SetIdentityHint("请先在左边的输入框里填写分类名（例如：工作 / 娱乐 / 学习）。");
            return;
        }

        if (!_runtime.CreateCategory(name))
        {
            SetIdentityHint("新建分类失败：存储不可用（见上方警告），改动未生效。");
            return;
        }

        NewCategoryBox.Text = string.Empty;
        RebuildIdentitySection();
        SetIdentityHint($"已新建分类「{name}」。现在可以在上面的软件行里为每个软件选择它。");
    }

    private void DeleteCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeleteCategoryBox.SelectedItem is not string name)
        {
            SetIdentityHint("请先选择要删除的分类。");
            return;
        }

        bool ok = _runtime.DeleteCategory(name, out bool removed);
        RebuildIdentitySection();

        SetIdentityHint(!ok
            ? "删除分类失败：存储不可用（见上方警告），改动未生效。"
            : removed
                ? $"已删除分类「{name}」：原本属于它的软件已回到「{UncategorizedLabel}」，该分类的限额（若有）也已一并删除。"
                : $"分类「{name}」不存在（可能已被删除）。");
    }

    private void MergeButton_Click(object sender, RoutedEventArgs e)
    {
        if (MergeFromBox.SelectedItem is not IdentityChoice from || MergeToBox.SelectedItem is not IdentityChoice to)
        {
            SetIdentityHint("请先在上面的两个下拉框里选择「要合并的软件」与「合并到哪个软件」。");
            return;
        }

        if (string.Equals(from.Key, to.Key, StringComparison.Ordinal))
        {
            SetIdentityHint("源与目标是同一个软件，无需合并。");
            return;
        }

        bool ok = _runtime.MergeApps(from.Key, to.Key);
        RebuildIdentitySection();

        SetIdentityHint(ok
            ? $"已把「{from.Display}」合并到「{to.Display}」：库中历史与今日时长已一并并入，今后采样会自动归属到「{to.Display}」。"
            : "合并失败（存储不可用或参数不合法）——**未做任何改动**。");
    }

    private void UnmergeButton_Click(object sender, RoutedEventArgs e)
    {
        if (UnmergeBox.SelectedItem is not IdentityChoice choice)
        {
            SetIdentityHint("当前没有可取消的合并。");
            return;
        }

        bool ok = _runtime.UnmergeApp(choice.Key, out bool removed);
        RebuildIdentitySection();

        SetIdentityHint(!ok
            ? "取消合并失败：存储不可用（见上方警告），改动未生效。"
            : removed
                ? $"已取消合并「{choice.Key}」：**今后**这个进程名不再并入目标软件。" +
                  "注意：已经并入的历史秒数不会退回（库里的行已经合并过了），原始活动日志里仍保留原始进程名。"
                : $"别名「{choice.Key}」不存在（可能已被取消）。");
    }

    private void SetIdentityHint(string text) => IdentityHintText.Text = text;

    /// <summary>
    /// 重建「软件与分类」一节（按需调用，见本节顶部说明）。
    /// 全程用 <see cref="_identityUiBusy"/> 抑制控件赋值引发的事件，避免"界面改界面"的递归。
    /// </summary>
    private void RebuildIdentitySection()
    {
        if (_identityUiBusy) return;
        _identityUiBusy = true;

        try
        {
            IReadOnlyList<string> categories = _runtime.Categories;

            // 「删除分类」下拉框
            string? previousDelete = DeleteCategoryBox.SelectedItem as string;
            DeleteCategoryBox.Items.Clear();
            foreach (string category in categories) DeleteCategoryBox.Items.Add(category);
            if (previousDelete is not null && categories.Contains(previousDelete)) DeleteCategoryBox.SelectedItem = previousDelete;
            else if (DeleteCategoryBox.Items.Count > 0) DeleteCategoryBox.SelectedIndex = 0;

            // 「取消合并」下拉框：列出现存的全部别名（原始键 → 目标键）
            UnmergeBox.Items.Clear();
            foreach (KeyValuePair<string, string> alias in _runtime.Aliases)
                UnmergeBox.Items.Add(new IdentityChoice(alias.Key, $"{alias.Key} → {alias.Value}"));
            if (UnmergeBox.Items.Count > 0) UnmergeBox.SelectedIndex = 0;

            // 软件行 + 合并下拉框
            AppRowsPanel.Children.Clear();
            MergeFromBox.Items.Clear();
            MergeToBox.Items.Clear();

            int rows = 0;
            foreach (AppUsageEntry app in _runtime.AppsToday())
            {
                AppRowsPanel.Children.Add(BuildAppRow(app, categories));

                var choice = new IdentityChoice(app.MergeKey, $"{app.DisplayName}（{app.MergeKey}）");
                MergeFromBox.Items.Add(choice);
                MergeToBox.Items.Add(new IdentityChoice(choice.Key, choice.Display));
                rows++;
            }

            if (rows == 0)
            {
                AppRowsPanel.Children.Add(new TextBlock
                {
                    Text = "（今日还没有软件记录 —— 等统计跑一会儿再点「刷新列表」）",
                    Foreground = Brushes.Gray,
                    FontSize = 11,
                });
            }

            if (MergeFromBox.Items.Count > 0) MergeFromBox.SelectedIndex = 0;
            if (MergeToBox.Items.Count > 0) MergeToBox.SelectedIndex = Math.Min(1, MergeToBox.Items.Count - 1);

            // 限额那一段的分类下拉框与「已设限额」列表也依赖分类/软件集合，因此一并重建
            // （它内部有自己的 try/catch，不会把本节的重建带崩）。
            RebuildLimitSection();
        }
        catch (Exception ex)
        {
            SetIdentityHint("读取软件与分类失败：" + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            _identityUiBusy = false;
        }

        // 身份层一变（改名 / 合并 / 改分类 / 新建删除分类），图表的**候选与标题**都必须跟着重建：
        // 否则会出现"软件行已是新名、图表标题还是旧名"，或者刚合并掉的软件仍在下拉里可选。
        // 这里不放在每秒刷新里 —— 重建下拉框会销毁用户正打开的列表（见本节顶部说明）。
        FillChartTargets();
        RefreshChart();
    }

    /// <summary>一条软件行：显示名 + 今日时长 + 分类下拉框。</summary>
    private FrameworkElement BuildAppRow(AppUsageEntry app, IReadOnlyList<string> categories)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };

        row.Children.Add(new TextBlock
        {
            Text = app.DisplayName,
            Width = 200,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        row.Children.Add(new TextBlock
        {
            Text = Format(app.Time),
            Width = 76,
            FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var combo = new ComboBox { Width = 140, VerticalAlignment = VerticalAlignment.Center };
        combo.Items.Add(UncategorizedLabel);
        foreach (string category in categories) combo.Items.Add(category);

        string current = _runtime.CategoryOf(app.MergeKey);
        combo.SelectedItem = current.Length == 0 || !categories.Contains(current) ? UncategorizedLabel : current;

        string key = app.MergeKey;
        string display = app.DisplayName;

        // 当前**真实**的显示值（保存失败时要回到它）。声明在 lambda 外，闭包共享。
        string currentSelection = combo.SelectedItem as string ?? UncategorizedLabel;
        bool reverting = false;

        combo.SelectionChanged += (_, _) =>
        {
            if (_identityUiBusy || reverting) return;

            string selected = combo.SelectedItem as string ?? UncategorizedLabel;
            string category = string.Equals(selected, UncategorizedLabel, StringComparison.Ordinal)
                ? string.Empty
                : selected;

            bool ok = _runtime.SetAppCategory(key, category);
            SetIdentityHint(ok
                ? $"已把「{display}」归到「{(category.Length == 0 ? UncategorizedLabel : category)}」。"
                : "保存分类失败：存储不可用（见上方警告），改动未生效（界面显示的值会在下次刷新时回到原样）。");

            if (ok)
            {
                // 记下新的“真实值”，后续失败时回到这里 —— 而不是无条件回到「未分类」（那会显示一个错的值）。
                currentSelection = selected;
            }
            else
            {
                // 失败就把显示值拉回真实值，避免屏幕上的值与库里不一致（界面在骗人）。
                reverting = true;
                combo.SelectedItem = currentSelection;
                reverting = false;
            }
        };

        row.Children.Add(combo);

        // 「重命名…」（M9-1b）：自定义显示名。放在分类下拉框之后 —— 两者都是"给这个软件加备注性设置"，
        // 且都只在点击时动作，不会跟着每秒状态刷新重建（见本节顶部说明）。
        var renameButton = new Button
        {
            Content = "重命名…",
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(10, 2, 10, 2),
        };
        renameButton.Click += (_, _) => PromptRenameApp(key, display);
        row.Children.Add(renameButton);

        // 「限额…」（M9-2）：给这个软件设单软件限额。与「重命名…」同为“给这个软件加设置”，
        // 都只在点击时动作，因此同样不参与每秒刷新。
        var limitButton = new Button
        {
            Content = "限额…",
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(10, 2, 10, 2),
        };
        limitButton.Click += (_, _) => PromptAppLimit(key, display);
        row.Children.Add(limitButton);

        return row;
    }

    /// <summary>
    /// 「重命名…」：弹出对话框给这个软件起一个自定义显示名（M9-1b）。
    ///
    /// 三件事刻意如此：
    ///  ① **校验规则不在这里**：对话框每次点「保存」都回调 <see cref="ProductRuntime.ValidateRename"/>，
    ///     而它内部只调 <c>AppNamingRules</c> —— 因此"什么算重名"全项目只有一处定义；
    ///  ② 校验失败时对话框**不关闭、保留输入**，只把原因写在下面（见 <see cref="AppRenameDialog"/>）；
    ///  ③ 保存成功后**重建列表**：名字是立刻生效的（榜单与卡片同源），
    ///     列表若不刷新，就会出现"上面榜单已是新名、下面这一行还是旧名"的自相矛盾。
    /// </summary>
    private void PromptRenameApp(string appKey, string currentDisplay)
    {
        try
        {
            string defaultName = _runtime.DefaultDisplayName(appKey);

            string? chosen = AppRenameDialog.Show(
                this, appKey, currentDisplay, defaultName,
                candidate => _runtime.ValidateRename(appKey, candidate));

            if (chosen is null)
            {
                SetIdentityHint("已取消改名（未做任何改动）。");
                return;
            }

            AppRenameResult result = _runtime.RenameApp(appKey, chosen, out string conflictAppKey);
            RebuildIdentitySection();

            SetIdentityHint(result switch
            {
                AppRenameResult.Ok => chosen.Length == 0
                    ? $"已把「{currentDisplay}」恢复为系统默认名「{defaultName}」。显示名只影响文字，不影响任何统计数字。"
                    : $"已把「{currentDisplay}」改名为「{chosen}」。显示名只影响文字，不影响任何统计数字；" +
                      "它作用于所有日期的显示（库里只存软件身份）。",
                AppRenameResult.Conflict =>
                    $"改名失败：与「{_runtime.DescribeApp(conflictAppKey)}」重名（禁止重名）——**未做任何改动**。",
                AppRenameResult.Invalid => "改名失败：名字不合法（空 / 过长 / 含控制字符）——未做任何改动。",
                _ => "改名失败：存储不可用（见上方警告），改动未生效。",
            });
        }
        catch (Exception ex)
        {
            SetIdentityHint("改名失败：" + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// 「限额…」（M9-2）：给这个软件设一条单软件限额。
    ///
    /// 判定与反馈都委托给 <see cref="ApplyLimitEdit"/> —— 与总限额 / 分类限额**同一条路径**，
    /// 因此三种限额的“取消 / 清除 / 成功 / 存储不可用”四种反馈天然一致，不会各说各话。
    /// </summary>
    private void PromptAppLimit(string appKey, string display)
    {
        try
        {
            TimeSpan current = FindLimit(LimitKind.App, appKey);
            LimitEditResult result = LimitEditDialog.Show(this, $"「{display}」的限额",
                "达到设定时长时会用托盘气泡提醒（只提醒，不阻止使用）。\n" +
                "用量按**今天一整天**算（重启不清零），与榜单上的时长是同一个数。",
                current);

            ApplyLimitEdit(LimitKind.App, appKey, $"「{display}」", result);
        }
        catch (Exception ex)
        {
            SetLimitHint("设置限额失败：" + ex.GetType().Name + ": " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- 限额（M9-2）
    //
    // 与「软件与分类」同一套刷新策略：状态文字每秒刷新（纯文本、无交互），
    // 下拉框与「已设限额」列表**按需重建**（重建会清掉正在打开的下拉框）。

    private void SetLimitHint(string text) => LimitHintText.Text = text;

    /// <summary>每秒刷新：把各条限额的当前进度写出来（纯文本、无交互控件，可安全每秒重写）。</summary>
    private void UpdateLimitStatusText()
    {
        try
        {
            IReadOnlyList<LimitStatus> statuses = _runtime.LimitStatuses();
            if (statuses.Count == 0)
            {
                LimitStatusText.Text = "（未设置任何限额）";
                return;
            }

            var builder = new StringBuilder();
            foreach (LimitStatus status in statuses)
            {
                string name = status.Kind == LimitKind.Total
                    ? "总限额"
                    : $"{LimitRules.KindName(status.Kind)}·{status.DisplayName}";

                builder.Append(name)
                       .Append("：")
                       .Append(LimitRules.Format(status.Used))
                       .Append(" / ")
                       .Append(LimitRules.Format(status.Limit))
                       .Append($"（{status.Ratio * 100:F0}%）");

                if (status.Exceeded) builder.Append("  ← 已超出");
                builder.AppendLine();
            }

            LimitStatusText.Text = builder.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            LimitStatusText.Text = "读取限额失败：" + ex.GetType().Name + ": " + ex.Message;
        }
    }

    /// <summary>
    /// 重建限额这一节的**交互控件**（分类下拉框 + 已设限额列表 + 阈值输入框）。
    ///
    /// 阈值输入框在用户**正在输入**时不动它 —— 否则点一下「刷新列表」就把人打了一半的字冲掉，
    /// 那正是本节顶部所说的“看起来能用、其实不能用”的一种。
    /// </summary>
    private void RebuildLimitSection()
    {
        try
        {
            IReadOnlyList<string> categories = _runtime.Categories;

            string? previous = CategoryLimitBox.SelectedItem as string;
            CategoryLimitBox.Items.Clear();
            foreach (string category in categories) CategoryLimitBox.Items.Add(category);
            if (previous is not null && categories.Contains(previous)) CategoryLimitBox.SelectedItem = previous;
            else if (CategoryLimitBox.Items.Count > 0) CategoryLimitBox.SelectedIndex = 0;

            if (!ThresholdsBox.IsKeyboardFocusWithin)
                ThresholdsBox.Text = _runtime.LimitThresholdsText;

            LimitRowsPanel.Children.Clear();

            IReadOnlyList<LimitStatus> statuses = _runtime.LimitStatuses();
            if (statuses.Count == 0)
            {
                LimitRowsPanel.Children.Add(new TextBlock
                {
                    Text = "（还没有限额。可用上面的按钮或每个软件行上的「限额…」添加。）",
                    Foreground = Brushes.Gray,
                    FontSize = 11,
                });
                return;
            }

            foreach (LimitStatus status in statuses)
                LimitRowsPanel.Children.Add(BuildLimitRow(status));
        }
        catch (Exception ex)
        {
            SetLimitHint("读取限额失败：" + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>「已设限额」里的一行：说明 + 「删除」。</summary>
    private FrameworkElement BuildLimitRow(LimitStatus status)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };

        string name = status.Kind == LimitKind.Total
            ? "总限额"
            : $"{LimitRules.KindName(status.Kind)}·{status.DisplayName}";

        row.Children.Add(new TextBlock
        {
            Text = $"{name}　{LimitRules.Format(status.Limit)}",
            Width = 260,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var remove = new Button { Content = "删除", Padding = new Thickness(10, 2, 10, 2) };

        LimitKind kind = status.Kind;
        string target = status.Target;
        remove.Click += (_, _) =>
        {
            bool ok = _runtime.RemoveLimit(kind, target, out bool removed);
            RebuildLimitSection();
            SetLimitHint(!ok
                ? "删除限额失败：存储不可用（见上方警告），改动未生效。"
                : removed ? $"已删除{name}。" : $"{name}本来就没有设置。");
        };

        row.Children.Add(remove);
        return row;
    }

    private void SetTotalLimitButton_Click(object sender, RoutedEventArgs e)
    {
        TimeSpan current = FindLimit(LimitKind.Total, string.Empty);
        LimitEditResult result = LimitEditDialog.Show(this, "设置今日总限额",
            "达到设定时长时会用托盘气泡提醒（只提醒，不阻止使用）。\n" +
            "口径是「今日真实活跃」（今天一整天、重启不清零），与主界面大字号是同一个数。",
            current);

        ApplyLimitEdit(LimitKind.Total, string.Empty, "总限额", result);
    }

    private void SetCategoryLimitButton_Click(object sender, RoutedEventArgs e)
    {
        if (CategoryLimitBox.SelectedItem is not string category)
        {
            SetLimitHint("请先选择要设限额的分类（分类在上面的「软件与分类」一节里创建）。");
            return;
        }

        TimeSpan current = FindLimit(LimitKind.Category, category);
        LimitEditResult result = LimitEditDialog.Show(this, $"分类「{category}」的限额",
            "达到设定时长时会用托盘气泡提醒（只提醒，不阻止使用）。\n" +
            "用量 = 该分类下**各软件今日时长之和**（分类口径：一个软件只属于一个分类）。",
            current);

        ApplyLimitEdit(LimitKind.Category, category, $"分类「{category}」", result);
    }

    /// <summary>把对话框结果落到组合根，并给出**准确**的反馈（取消 / 清除 / 成功 / 存储不可用）。</summary>
    private void ApplyLimitEdit(LimitKind kind, string target, string display, LimitEditResult result)
    {
        if (result.Outcome == LimitEditOutcome.Cancelled)
        {
            SetLimitHint("已取消设置限额（未做任何改动）。");
            return;
        }

        if (result.Outcome == LimitEditOutcome.Cleared)
        {
            bool ok = _runtime.RemoveLimit(kind, target, out bool removed);
            RebuildLimitSection();
            SetLimitHint(!ok
                ? "删除限额失败：存储不可用（见上方警告），改动未生效。"
                : removed ? $"已删除{display}的限额。" : $"{display}本来就没有限额。");
            return;
        }

        bool saved = _runtime.SetLimit(kind, target, result.Value);
        RebuildLimitSection();
        SetLimitHint(saved
            ? $"已把{display}设为 {LimitRules.Format(result.Value)}。达到阈值时只提醒一次（改限额或换一天会重新提醒）。"
            : "保存限额失败：存储不可用（见上方警告），改动未生效。");
    }

    private void SaveThresholdsButton_Click(object sender, RoutedEventArgs e)
    {
        bool ok = _runtime.SetLimitThresholds(ThresholdsBox.Text, out string? error);
        RebuildLimitSection();
        SetLimitHint(ok
            ? $"已保存提醒阈值：{_runtime.LimitThresholdsText}（百分比）。"
            : "保存阈值失败：" + (error ?? "原因未知"));
    }

    /// <summary>当前某条限额的时长（没有则返回 <see cref="TimeSpan.Zero"/>）。</summary>
    private TimeSpan FindLimit(LimitKind kind, string target)
    {
        foreach (LimitRule rule in _runtime.Limits)
            if (rule.Kind == kind && string.Equals(rule.Target, target, StringComparison.Ordinal))
                return rule.Limit;
        return TimeSpan.Zero;
    }

    private void UpdateWarnings(RuntimeStatus status)
    {
        // 运行期追加的警告也要能出现（例如日志写盘报错），因此每次都重新拼。
        var builder = new StringBuilder();
        foreach (string warning in status.Warnings)
            builder.AppendLine("• " + warning);

        if (status.SchedulerError is { } schedulerError)
            builder.AppendLine("• 心跳处理异常：" + schedulerError);

        if (status.SamplingFailures > 0)
            builder.AppendLine($"• 前台采样异常 {status.SamplingFailures} 次（实现缺陷，非环境问题）");

        string text = builder.ToString().TrimEnd();
        if (text.Length == 0)
        {
            WarnBorder.Visibility = Visibility.Collapsed;
            WarnText.Text = string.Empty;
        }
        else
        {
            WarnBorder.Visibility = Visibility.Visible;
            WarnText.Text = text;
        }
    }

    private static string DescribeState(RuntimeStatus status)
    {
        if (!status.Running) return "未运行";

        if (status.Paused)
        {
            string reason = status.UserPaused
                ? (status.Suspended ? "托盘手动暂停 + 睡眠/休眠"
                                    : status.Locked ? "托盘手动暂停 + 锁屏" : "托盘手动暂停")
                : status.Suspended ? "睡眠/休眠" : status.Locked ? "锁屏" : "会话暂停";
            return $"已暂停（{reason}）—— 暂停期间的时长不计入";
        }

        if (!status.LastActive)
            return status.IdleSourceAvailable
                ? $"挂机中（空闲 {status.LastIdle.TotalSeconds:F0}s ≥ 阈值 {status.IdleThreshold.TotalSeconds:F0}s）—— 不计入"
                : "无法判定活跃（空闲数据源不可用）";

        return "活跃中 —— 正在计入";
    }

    /// <summary>
    /// “不计入使用时长 / 无前台·未知”的今日标记。
    ///
    /// 两者**都计入“今日真实活跃”**，只是不归属到任何软件 —— 这句话是刻意加的：
    /// 否则看到“不计入”三个字会以为它们被排除在总数之外（而它们并没有被排除）。
    /// </summary>
    private static string DescribeNonApp(RuntimeStatus status)
    {
        return $"不计入使用时长 {Format(status.DayFiltered)}（桌面/外壳/自身） · " +
               $"无前台/未知 {Format(status.DayUnattributed)}——两者均计入「今日真实活跃」，只是不计入软件；" +
               $"榜单末尾的「{NonAppRowName}」行即二者合计（该行不给占比）";
    }

    /// <summary>
    /// 「Top 5（今日）」末尾那条非软件行的名称。
    /// 做成常量是为了让自检断言同一个字面量，而不是在测试里另抄一份（抄的那份永远会“通过”）。
    /// </summary>
    internal const string NonAppRowName = "不计入统计";

    /// <summary>
    /// 拼出「Top 5（今日）」末尾的**非软件活跃**行；没有可显示内容时返回 <c>null</c>（连空行也不插）。
    ///
    /// 三个刻意的口径选择：
    ///  1. 时长 = <see cref="RuntimeStatus.DayFiltered"/> + <see cref="RuntimeStatus.DayUnattributed"/>，
    ///     即**今天一整天**（本次运行 + 库中基线）—— 用“本次运行”口径会让重启后这一行凭空变小；
    ///  2. **不含锁屏/睡眠剔除**：它按已确认口径“完全不计入”，若算进来本行就与“今日真实活跃”对不上账；
    ///  3. **占比列留空**（不写数字、连 <c>%</c> 都不写）：软件行的占比是“占**已归因软件**时长”，
    ///     本行是“非软件活跃”，两者分母不同 —— 同列并排容易让人以为可以相加、相减，
    ///     所以宁可留空，也不给出一个含义与邻行不同的百分数。
    ///     （空位由 <see cref="BlankShareCell"/> 产出，宽度与软件行的占比列一致，行名因此与软件行对齐。）
    /// </summary>
    internal static string? ComposeNonAppTopRow(RuntimeStatus status)
    {
        TimeSpan nonApp = status.DayFiltered + status.DayUnattributed;
        if (nonApp <= TimeSpan.Zero) return null;

        return $"{Format(nonApp),10}  {BlankShareCell()}{NonAppRowName}";
    }

    /// <summary>
    /// 拼出一条**软件榜行**（时长 + 占比 + 名称）。
    ///
    /// 抽成函数是为了让自检能拿**产品自己产出的**软件行去比对「不计入统计」行的行名起始列，
    /// 从而客观断言两者对齐 —— 而不是在测试里另抄一份空格数（抄的那份永远会“通过”）。
    /// </summary>
    internal static string ComposeAppTopRow(AppUsageEntry entry)
        => $"{Format(entry.Time),10}  {ShareCell(entry.Share)}{entry.DisplayName}";

    /// <summary>
    /// 榜单“占比”一列的宽度：<c>{(share * 100),5:F1}</c> 为 5 个字符，加百分号共 6 个。
    /// 做成常量是因为它是**对齐契约**：留空的那一行必须用同样的宽度，行名才会与软件行对齐。
    /// </summary>
    internal const int ShareCellWidth = 6;

    /// <summary>列与列之间的分隔（宽度同样属于对齐契约，故只写一次）。</summary>
    private const string ColumnSeparator = "  ";

    /// <summary>软件行的“占比”列：数字 + <c>%</c> + 列分隔。</summary>
    private static string ShareCell(double share) => $"{(share * 100),5:F1}%" + ColumnSeparator;

    /// <summary>
    /// 「不计入统计」行的“占比”列：**整列留空**（宽度与软件行一致，因此行名仍与其他行对齐）。
    /// 为什么不给占比：它的分母是“今日真实活跃”，软件行的分母是“已归因软件时长”，
    /// 两者同列并排会让人以为可以相加、相减。
    /// </summary>
    private static string BlankShareCell() => new string(' ', ShareCellWidth) + ColumnSeparator;

    private static string DescribeSession(RuntimeStatus status)
    {
        if (!status.SessionAttached)
            return "锁屏/睡眠监听：未启用（见上方警告）";

        string hooked = status.SessionHooked ? "已订阅" : "订阅失败";
        string probe = status.SessionProbeError is { } error ? "探测异常：" + error : "探测 " + status.SessionProbeText;

        return $"锁屏/睡眠：锁屏={Yn(status.Locked)} 睡眠={Yn(status.Suspended)} 暂停={Yn(status.Paused)}" +
               $"（事件{hooked}，{probe}）";
    }

    private static string DescribeSources(RuntimeStatus status)
    {
        return $"心跳：第 {status.Sequence} 拍（定时器回调 {status.TimerFirings} 次，时间空洞 {status.GapTicks} 次）· " +
               $"间隔 {status.Heartbeat.TotalMilliseconds:F0}ms · 当前空闲 {status.LastIdle.TotalSeconds:F1}s · " +
               $"空闲源 {(status.IdleSourceAvailable ? "可用" : "不可用")} · " +
               $"前台源 {(status.AppSourceAvailable ? "可用" : "不可用")}";
    }

    private static string DescribeStore(RuntimeStatus status)
    {
        if (!status.StoreActive)
            return "已关闭（见上方警告或启动参数 --no-store）\n库：" + status.DatabasePath;

        var builder = new StringBuilder();
        builder.AppendLine("库：" + status.DatabasePath);
        builder.AppendLine($"已落库 {status.FlushedSeconds}s · 待写 {status.PendingMilliseconds}ms（断电最多丢这么多）");
        builder.Append($"flush 共 {status.Flushes} 次（其中强制 {status.ForcedFlushes}）· 间隔 {status.FlushInterval.TotalSeconds:F0}s");
        if (status.StoreDropped > 0) builder.Append($" · 队列丢弃 {status.StoreDropped}");
        if (status.StoreFailures > 0) builder.Append($" · 写盘失败 {status.StoreFailures}（数据仍在内存重试）");
        if (status.StoreError is { } error) builder.Append("\n最近错误：" + error);
        return builder.ToString();
    }

    private static string DescribeLog(RuntimeStatus status)
    {
        if (!status.LogActive)
            return "已关闭（见上方警告或启动参数 --no-raw-log）\n目录：" + status.LogDirectory;

        var builder = new StringBuilder();
        builder.AppendLine("目录：" + status.LogDirectory);
        builder.Append($"已写 {status.LogWritten} 行 · 已关闭状态段 {status.LogRecords}");
        if (status.LogDropped > 0) builder.Append($" · 队列丢弃 {status.LogDropped}");
        if (status.LogError is { } error) builder.Append(" · 错误：" + error);
        if (status.LogCurrentPath is { } path) builder.Append("\n当前分片：" + path);
        return builder.ToString();
    }

    // ================================================================ 近 7 天柱状图（M10）

    /// <summary>「看什么」下拉里的三项文案（做成常量：界面与自检用同一份字面量）。</summary>
    internal const string ChartTotalLabel = "每天总量";
    internal const string ChartAppLabel = "某个软件";
    internal const string ChartCategoryLabel = "某个分类";

    /// <summary>
    /// 维度下拉的内容。只建一次：用户选过之后一直保持（重建会莫名其妙地把选择弹回"每天总量"）。
    /// </summary>
    internal static IReadOnlyList<IdentityChoice> BuildChartDimensionChoices() => new[]
    {
        new IdentityChoice(nameof(ChartDimension.Total), ChartTotalLabel),
        new IdentityChoice(nameof(ChartDimension.App), ChartAppLabel),
        new IdentityChoice(nameof(ChartDimension.Category), ChartCategoryLabel),
    };

    private void BuildChartDimensionBox()
    {
        if (_chartUiBusy) return;
        _chartUiBusy = true;
        try
        {
            ChartDimensionBox.Items.Clear();
            foreach (IdentityChoice choice in BuildChartDimensionChoices()) ChartDimensionBox.Items.Add(choice);
            ChartDimensionBox.SelectedIndex = 0;   // 默认"每天总量"
        }
        finally { _chartUiBusy = false; }
    }

    private ChartDimension SelectedChartDimension()
    {
        if (ChartDimensionBox.SelectedItem is IdentityChoice choice &&
            Enum.TryParse(choice.Key, out ChartDimension dimension))
            return dimension;
        return ChartDimension.Total;
    }

    /// <summary>
    /// 把"维度 + 目标"拼成一个 scope（纯函数，便于确定性断言）。
    /// 总量维度**丢弃目标**：否则切换维度时可能把上一次选的软件名带进总量维度（看起来没影响、实际留了个脏值）。
    /// </summary>
    internal static ChartScope ComposeChartScope(ChartDimension dimension, string? target)
        => new(dimension, dimension == ChartDimension.Total ? string.Empty : (target ?? string.Empty));

    /// <summary>图表下方那行说明（纯函数）。刻意明说"只读库 / 可能滞后"，不让用户对着两个数字猜。</summary>
    internal static string ComposeChartHint(ChartModel model)
    {
        if (model.Bars.Count == 0) return "（暂无数据）";
        if (!model.HasAnyData)
            return $"近 {model.Bars.Count} 天没有历史记录（图只显示已落库的数据）。";

        return $"已落库数据 · {model.Bars.Count} 天合计 {ChartText.Duration(model.TotalSeconds)}" +
               $" · 最高一天 {ChartText.Duration(model.MaxSeconds)}" +
               $" · 空槽 = 那天没有记录 · 最多滞后约 15 秒";
    }

    /// <summary>
    /// 重建「目标」下拉。软件候选来自**库**（近 7 天出现过的），分类候选来自身份层 ——
    /// 与图表本身只读库保持一致，免得出现"下拉里能选、选了却是空图"。
    /// </summary>
    private void FillChartTargets()
    {
        if (_chartUiBusy) return;
        _chartUiBusy = true;
        try
        {
            ChartDimension dimension = SelectedChartDimension();
            string keep = (ChartTargetBox.SelectedItem as IdentityChoice)?.Key ?? string.Empty;

            ChartTargetBox.Items.Clear();
            ChartTargetBox.IsEnabled = dimension != ChartDimension.Total;

            if (dimension == ChartDimension.Total)
            {
                SetChartHint("柱子 = 当天真实活跃总量（各软件 + 不计入使用时长 + 无前台/未知）。");
                return;
            }

            if (dimension == ChartDimension.Category)
            {
                IReadOnlyList<string> categories = _runtime.ChartCategories;
                foreach (string category in categories) ChartTargetBox.Items.Add(new IdentityChoice(category, category));

                SetChartHint(categories.Count == 0
                    ? "还没有分类。请先在「软件与分类」一节新建分类，并把软件指到该分类。"
                    : "选一个分类：柱子 = 该分类下各软件之和。");
            }
            else
            {
                (IReadOnlyList<string> keys, string? error) = _runtime.ReadChartApps();
                foreach (string key in keys) ChartTargetBox.Items.Add(new IdentityChoice(key, _runtime.DescribeApp(key)));

                SetChartHint(error ?? (keys.Count == 0
                    ? "近 7 天库里还没有软件记录（图只显示已落库的数据）。"
                    : "选一个软件：柱子 = 它每天的时长（只读库）。"));
            }

            // 尽量保持原选择；原来那个不在了（例如刚被合并掉）就退回第一项。
            for (int i = 0; i < ChartTargetBox.Items.Count; i++)
            {
                if (ChartTargetBox.Items[i] is IdentityChoice choice && choice.Key == keep)
                {
                    ChartTargetBox.SelectedIndex = i;
                    break;
                }
            }
            if (ChartTargetBox.SelectedIndex < 0 && ChartTargetBox.Items.Count > 0) ChartTargetBox.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            SetChartHint("读取图表候选失败：" + ex.GetType().Name + "：" + ex.Message);
        }
        finally { _chartUiBusy = false; }
    }

    /// <summary>按当前选择读一次库并重画图表。失败时把原因摆到脸上，而不是留一张空图。</summary>
    private void RefreshChart()
    {
        _lastChartRefresh = DateTime.Now;

        try
        {
            string target = (ChartTargetBox.SelectedItem as IdentityChoice)?.Key ?? string.Empty;
            ChartScope scope = ComposeChartScope(SelectedChartDimension(), target);

            ChartResult result = _runtime.ReadChart(scope);
            if (!result.Ok)
            {
                ChartTitleText.Text = string.Empty;
                ChartHost.Child = null;
                SetChartHint(result.Error ?? "图表不可用。");
                return;
            }

            ChartModel model = result.Model!;
            ChartTitleText.Text = model.Title;
            ChartHost.Child = BarChartView.Render(model);
            SetChartHint(ComposeChartHint(model));
        }
        catch (Exception ex)
        {
            // 展示层异常绝不该让程序崩掉。
            ChartTitleText.Text = string.Empty;
            ChartHost.Child = null;
            SetChartHint("图表刷新失败：" + ex.GetType().Name + "：" + ex.Message);
        }
    }

    private void SetChartHint(string text) => ChartHintText.Text = text;

    private void ChartDimensionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_chartUiBusy) return;
        FillChartTargets();
        RefreshChart();
    }

    private void ChartTargetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_chartUiBusy) return;
        RefreshChart();
    }

    private void ReloadChartButton_Click(object sender, RoutedEventArgs e)
    {
        FillChartTargets();
        RefreshChart();
    }

    private static string Format(TimeSpan value)
        => $"{(int)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}";

    private static string Yn(bool value) => value ? "是" : "否";
}
