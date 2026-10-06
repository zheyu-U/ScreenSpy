using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ScreenSpy.Analytics;

namespace ScreenSpy.Charts;

/// <summary>
/// 柱状图的**绘制层**（M10）：拿一张已经算好的 <see cref="ChartModel"/>，摆出 WPF 视觉。
///
/// ────────────────────────────────────────────────────────────────────────
/// 为什么单独一个文件
/// ────────────────────────────────────────────────────────────────────────
/// 用户已明确"以后要重新做 UI"。因此这里刻意只做**一件事**（模型 → 控件），
/// 不含任何口径、比值、除法或查库：
///  * 数字与比例来自 <see cref="ChartBuilder"/>（口径）与 <see cref="BarChartLayout"/>（几何）；
///  * 换 UI 时只替换本文件，那两处的规则与确定性断言原样保留。
///
/// 刻意不用 WPF 数据绑定（与全项目一致）：一次性搭好控件树即可，图表本身是**定期整体重建**的。
/// </summary>
internal static class BarChartView
{
    /// <summary>柱区高度（像素）。太小则"0 与一点点"无法区分。</summary>
    public const double PlotHeight = 150;

    /// <summary>柱宽（像素）。固定宽度而不是撑满：7 根柱撑满会显得像"面积图"。</summary>
    public const double BarWidth = 18;

    private static readonly Brush BarBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3D, 0x6F, 0xB4)));
    private static readonly Brush TodayBarBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x17, 0x3E, 0x7A)));
    private static readonly Brush ValueBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)));
    private static readonly Brush BaselineBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)));
    private static readonly Brush EmptyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)));

    /// <summary>把模型渲染成控件树。永不抛异常（展示层不该带崩界面）。</summary>
    public static FrameworkElement Render(ChartModel? model)
    {
        var outer = new StackPanel();

        if (model is null || model.Bars.Count == 0)
        {
            outer.Children.Add(Placeholder("（暂无数据）"));
            return outer;
        }

        if (!model.HasAnyData)
        {
            outer.Children.Add(Placeholder($"近 {model.Bars.Count} 天没有历史记录。"));
            return outer;
        }

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                    // 0 数值
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PlotHeight) });          // 1 柱区
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                    // 2 日期

        long max = model.MaxSeconds;

        for (int i = 0; i < model.Bars.Count; i++)
        {
            ChartBar bar = model.Bars[i];
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            bool isToday = model.Today.HasValue && bar.Day == model.Today.Value;

            var value = new TextBlock
            {
                Text = bar.ValueText,
                FontSize = 10,
                Margin = new Thickness(0, 0, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = bar.HasData ? ValueBrush : EmptyBrush,
                FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
            };
            Grid.SetRow(value, 0);
            Grid.SetColumn(value, i);
            grid.Children.Add(value);

            // 有记录且秒数 > 0 才画柱：有记录但为 0（那天用过电脑、没用过这个软件）显示 0:00 而不画柱；
            // 无记录（那天没运行过）显示 — 也不画柱。两者在数值文字上分得很清楚。
            if (bar.HasData && bar.Seconds > 0)
            {
                var rect = new Rectangle
                {
                    Width = BarWidth,
                    Height = BarChartLayout.HeightOf(bar.Seconds, max, PlotHeight),
                    Fill = isToday ? TodayBarBrush : BarBrush,
                    RadiusX = 2,
                    RadiusY = 2,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    ToolTip = $"{bar.Day:yyyy-MM-dd}\n{bar.ValueText}",
                };
                Grid.SetRow(rect, 1);
                Grid.SetColumn(rect, i);
                grid.Children.Add(rect);
            }

            var label = new TextBlock
            {
                Text = bar.DayLabel,
                FontSize = 10,
                Margin = new Thickness(0, 3, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = isToday ? ValueBrush : LabelBrush,
                FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
            };
            Grid.SetRow(label, 2);
            Grid.SetColumn(label, i);
            grid.Children.Add(label);
        }

        // 基线：一条横线穿过所有列的底部，让"空槽"与"0 高"有共同参照。
        var baseline = new Rectangle
        {
            Height = 1,
            Fill = BaselineBrush,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        Grid.SetRow(baseline, 1);
        Grid.SetColumn(baseline, 0);
        Grid.SetColumnSpan(baseline, model.Bars.Count);
        grid.Children.Add(baseline);

        outer.Children.Add(grid);
        return outer;
    }

    private static TextBlock Placeholder(string text) => new()
    {
        Text = text,
        Foreground = EmptyBrush,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
    };

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
