using System;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using ScreenSpy.AppHost;
using ScreenSpy.Collector;

namespace ScreenSpy;

/// <summary>
/// 主窗口：**运行状态面板**（本次接入的可见形态）。
///
/// 目的很单纯 —— 让“M1–M4 是否真的在跑、数据到底写去哪了”一眼可见。
/// 因此这里不做设置、不做图表（那属 M8/M10），只显示实时状态。
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

    private static string Format(TimeSpan value)
        => $"{(int)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}";

    private static string Yn(bool value) => value ? "是" : "否";
}
