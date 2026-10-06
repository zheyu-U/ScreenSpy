using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ScreenSpy;

/// <summary>
/// 「自定义软件名称」对话框（M9-1b）。
///
/// ────────────────────────────────────────────────────────────────────────
/// 为什么用对话框，而不是在主界面那一行内联一个文本框
/// ────────────────────────────────────────────────────────────────────────
///  ① 有地方把话说清楚：这是**纯显示名**，不影响任何统计数字 —— 内联一行塞不下这句，
///     而用户很可能以为"改名会影响统计口径"；
///  ② 重名是**必须当场解释**的失败：对话框能**保留用户输入**、只把原因写在下面让他改；
///     内联文本框失败了只能把值拉回去，用户会以为"点了没反应"；
///  ③ 「恢复默认名」是一个**不同**的动作（清空自定义名），单独一个按钮比多一行文字更不容易误点。
///
/// ────────────────────────────────────────────────────────────────────────
/// 它自己**不做任何判断**
/// ────────────────────────────────────────────────────────────────────────
/// 校验规则由调用方以 <paramref name="validate"/> 传入（最终落到 <c>AppNamingRules.Validate</c>）。
/// 这样"什么算重名"全项目只有**一处**定义 —— 否则早晚出现
/// "对话框说能改、保存时却说不能"的分叉，那是最让用户恼火的一种不一致。
///
/// ⚠️ 本类**无法被自检覆盖**（它需要真的显示窗口）。因此它被刻意写得很薄：
/// 只有一个循环与三个按钮，所有判断都在外面。见 <c>docs/M9-1</c> 的"已知局限"。
/// </summary>
internal static class AppRenameDialog
{
    /// <summary>
    /// 弹出改名对话框。
    /// </summary>
    /// <param name="owner">父窗口（用于居中；可为 null）。</param>
    /// <param name="appKey">软件的归一键（只用于展示，让用户知道改的是哪一个）。</param>
    /// <param name="currentName">当前显示名（文本框预填）。</param>
    /// <param name="defaultName">系统默认名（用于说明「恢复默认名」会变成什么）。</param>
    /// <param name="validate">
    /// 校验用户输入：参数是**要保存的值**（空串 = 恢复默认名），返回错误说明或 <c>null</c>。
    /// </param>
    /// <returns>要保存的值（空串 = 恢复默认名）；用户取消返回 <c>null</c>。</returns>
    public static string? Show(Window? owner, string appKey, string currentName, string defaultName,
                               Func<string, string?> validate)
    {
        var window = new Window
        {
            Title = "自定义软件名称",
            Width = 480,
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
            Text = $"软件：{currentName}（{appKey}）",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        root.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = Brushes.Gray,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Text = "这里改的是**显示名**：只影响界面上显示的文字（榜单 / 卡片 / 将来的图表），" +
                   "不改变任何统计数字，也不改变软件的身份。\n" +
                   "名字不能与别的软件重复 —— 两行同名时分不清谁是谁，而它们的时长是各算各的。",
        });

        var box = new TextBox
        {
            Text = currentName,
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(4, 3, 4, 3),
        };
        root.Children.Add(box);

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
        var restoreButton = new Button
        {
            Content = "恢复默认名",
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 4, 12, 4),
        };
        var cancelButton = new Button
        {
            Content = "取消",
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(16, 4, 16, 4),
            IsCancel = true,
        };

        buttons.Children.Add(okButton);
        buttons.Children.Add(restoreButton);
        buttons.Children.Add(cancelButton);
        root.Children.Add(buttons);

        root.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = Brushes.Gray,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Text = $"「恢复默认名」会清掉自定义名，回到系统名（当前默认：{defaultName}）。\n" +
                   "改名会作用于**所有日期**的记录显示（库里只存软件身份，名字是显示时现算的），" +
                   "因此不需要回填历史。",
        });

        window.Content = root;

        string result = string.Empty;
        bool accepted = false;

        void TryAccept(string candidate)
        {
            string? problem;
            try
            {
                problem = validate(candidate);
            }
            catch (Exception ex)
            {
                problem = "校验失败：" + ex.GetType().Name + ": " + ex.Message;
            }

            if (problem is not null)
            {
                // 刻意**不关闭**窗口：保留用户已输入的内容，只把原因显示出来让他改。
                error.Text = problem;
                error.Visibility = Visibility.Visible;
                return;
            }

            result = candidate;
            accepted = true;
            window.DialogResult = true;   // 关闭模态窗口
        }

        okButton.Click += (_, _) => TryAccept((box.Text ?? string.Empty).Trim());
        restoreButton.Click += (_, _) => TryAccept(string.Empty);

        // 一改输入就把上一次的错误说明收起来（否则用户会以为改了也没用）。
        box.TextChanged += (_, _) => error.Visibility = Visibility.Collapsed;

        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

        window.ShowDialog();
        return accepted ? result : null;
    }
}
