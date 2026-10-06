using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScreenSpy.Limits;

namespace ScreenSpy;

/// <summary>对话框的三种结局（分开表达，调用方才能给出准确反馈，而不是把“取消”也报成“失败”）。</summary>
internal enum LimitEditOutcome
{
    /// <summary>用户取消 —— 一个字都不该写。</summary>
    Cancelled,

    /// <summary>用户点了「清除限额」。</summary>
    Cleared,

    /// <summary>用户点了「保存」，且时长合法。</summary>
    Saved,
}

/// <summary>对话框结果。</summary>
internal readonly struct LimitEditResult
{
    public LimitEditResult(LimitEditOutcome outcome, TimeSpan value)
    {
        Outcome = outcome;
        Value = value;
    }

    public LimitEditOutcome Outcome { get; }
    public TimeSpan Value { get; }

    public static LimitEditResult Cancelled() => new(LimitEditOutcome.Cancelled, TimeSpan.Zero);
    public static LimitEditResult Cleared() => new(LimitEditOutcome.Cleared, TimeSpan.Zero);
    public static LimitEditResult Saved(TimeSpan value) => new(LimitEditOutcome.Saved, value);
}

/// <summary>
/// 「设置限额」对话框（M9-2）：总限额 / 软件限额 / 分类限额共用同一个对话框。
///
/// ────────────────────────────────────────────────────────────────────────
/// 为什么用对话框，而不是在列表里内联几个输入框
/// ────────────────────────────────────────────────────────────────────────
///  ① **有地方把话说清楚**：限额按“今天一整天”算、只提醒不阻止、达到阈值只提醒一次 ——
///     这三句话塞不进一行文本框旁边；
///  ② 时长输入有两个字段（小时 / 分钟），内联会把列表挤爆；
///  ③ 「清除限额」是一个**不同**的动作，单独一个按钮比让用户“把数字填成 0”清楚得多
///     （填 0 会被判为非法，反而让人以为程序坏了）。
///
/// ⚠️ 本类**无法被自检覆盖**（需要真的显示窗口）。因此它被刻意写得很薄：
/// 只有一个循环与三个按钮，判定规则全在 <see cref="LimitRules.TryParseDuration"/>（唯一一处定义）。
/// </summary>
internal static class LimitEditDialog
{
    public static LimitEditResult Show(Window? owner, string title, string description, TimeSpan current)
    {
        bool hasCurrent = current > TimeSpan.Zero;
        TimeSpan initial = hasCurrent ? current : TimeSpan.FromHours(1);

        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = false,
        };

        var root = new StackPanel { Margin = new Thickness(16) };

        root.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        root.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = Brushes.Gray,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Text = description,
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };

        var hoursBox = new TextBox
        {
            Text = ((int)initial.TotalHours).ToString(),
            Width = 60,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 3, 4, 3),
        };
        var minutesBox = new TextBox
        {
            Text = initial.Minutes.ToString(),
            Width = 60,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 3, 4, 3),
        };

        row.Children.Add(hoursBox);
        row.Children.Add(new TextBlock { Text = "小时", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(minutesBox);
        row.Children.Add(new TextBlock { Text = "分钟", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(row);

        var error = new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = Brushes.Firebrick,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        root.Children.Add(error);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var okButton = new Button { Content = "保存", Padding = new Thickness(16, 4, 16, 4), IsDefault = true };
        var clearButton = new Button
        {
            Content = "清除限额",
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 4, 12, 4),
            Visibility = hasCurrent ? Visibility.Visible : Visibility.Collapsed,
        };
        var cancelButton = new Button
        {
            Content = "取消",
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(16, 4, 16, 4),
            IsCancel = true,
        };

        buttons.Children.Add(okButton);
        buttons.Children.Add(clearButton);
        buttons.Children.Add(cancelButton);
        root.Children.Add(buttons);

        window.Content = root;

        LimitEditResult result = LimitEditResult.Cancelled();

        okButton.Click += (_, _) =>
        {
            // 判定规则只有一处定义：LimitRules.TryParseDuration。
            if (!LimitRules.TryParseDuration(hoursBox.Text, minutesBox.Text, out TimeSpan value, out string? problem))
            {
                // 刻意**不关闭**窗口：保留用户已输入的内容，只把原因显示出来让他改。
                error.Text = problem ?? "时长不合法。";
                error.Visibility = Visibility.Visible;
                return;
            }

            result = LimitEditResult.Saved(value);
            window.DialogResult = true;
        };

        clearButton.Click += (_, _) =>
        {
            result = LimitEditResult.Cleared();
            window.DialogResult = true;
        };

        // 一改输入就把上一次的错误说明收起来（否则用户会以为改了也没用）。
        hoursBox.TextChanged += (_, _) => error.Visibility = Visibility.Collapsed;
        minutesBox.TextChanged += (_, _) => error.Visibility = Visibility.Collapsed;

        window.Loaded += (_, _) =>
        {
            hoursBox.Focus();
            hoursBox.SelectAll();
        };

        window.ShowDialog();
        return result;
    }
}
