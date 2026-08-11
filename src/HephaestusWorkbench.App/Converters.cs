using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using HephaestusWorkbench.Core.Models;
using AnalysisTaskStatus = HephaestusWorkbench.Core.Models.TaskStatus;

namespace HephaestusWorkbench.App;

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}

public sealed class StepVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => int.TryParse(parameter?.ToString(), out var expected) && value is int actual && actual == expected
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}

/// <summary>统一案例与任务状态的中文显示，避免各页面自行解释枚举。</summary>
public sealed class StatusTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        CaseStatus.Running or AnalysisTaskStatus.Running => "分析中",
        CaseStatus.Completed or AnalysisTaskStatus.Completed => "已完成",
        CaseStatus.Failed or AnalysisTaskStatus.Failed => "失败",
        CaseStatus.Ready or CaseStatus.Created or AnalysisTaskStatus.Waiting => "等待",
        AnalysisTaskStatus.Cancelled => "已取消",
        _ => value?.ToString() ?? "未知"
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}

public sealed class StatusBrushConverter : IValueConverter
{
    private readonly Dictionary<string, SolidColorBrush> _brushes = new(StringComparer.Ordinal);

    public StatusBrushConverter() => Refresh();

    /// <summary>
    /// 主题切换后更新已有画刷实例，而不是创建新实例，确保已显示的状态标签立即同步颜色。
    /// </summary>
    public void Refresh()
    {
        var application = System.Windows.Application.Current;
        if (application is null) return;

        foreach (var key in new[]
        {
            "WorkbenchStatusRunningBrush",
            "WorkbenchStatusCompletedBrush",
            "WorkbenchStatusFailedBrush",
            "WorkbenchStatusWaitingBrush",
            "WorkbenchStatusUnknownBrush"
        })
        {
            if (application.TryFindResource(key) is not SolidColorBrush source) continue;
            if (!_brushes.TryGetValue(key, out var target))
            {
                target = new SolidColorBrush(source.Color);
                _brushes[key] = target;
            }
            else
            {
                target.Color = source.Color;
            }
        }
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            CaseStatus.Running or AnalysisTaskStatus.Running => "WorkbenchStatusRunningBrush",
            CaseStatus.Completed or AnalysisTaskStatus.Completed => "WorkbenchStatusCompletedBrush",
            CaseStatus.Failed or AnalysisTaskStatus.Failed => "WorkbenchStatusFailedBrush",
            CaseStatus.Ready or CaseStatus.Created or AnalysisTaskStatus.Waiting => "WorkbenchStatusWaitingBrush",
            _ => "WorkbenchStatusUnknownBrush"
        };
        return _brushes.TryGetValue(key, out var brush) ? brush : System.Windows.Media.Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}
