using System.Windows.Controls;
using System.Globalization;
using HephaestusWorkbench.App.ViewModels;
using Forms = System.Windows.Forms;

namespace HephaestusWorkbench.App.Views;

/// <summary>设置页视图，负责在不同可用宽度下安排设置卡片，不承载设置业务逻辑。</summary>
public partial class SettingsPage : System.Windows.Controls.UserControl
{
    private const double CompactLayoutBreakpoint = 760;
    private bool _isCompactLayout;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
    }

    private void SettingsPage_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        UpdateLayoutMode(ActualWidth);
    }

    private void SettingsPage_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
    {
        UpdateLayoutMode(e.NewSize.Width);
    }

    /// <summary>
    /// 设置页在可用宽度充足时采用两列布局，窄窗口下切换为单列，避免控件被压缩或产生横向滚动。
    /// </summary>
    private void UpdateLayoutMode(double availableWidth)
    {
        var isCompact = availableWidth < CompactLayoutBreakpoint;
        if (_isCompactLayout == isCompact) return;

        _isCompactLayout = isCompact;
        if (isCompact)
        {
            PrimaryColumn.Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
            SecondaryColumn.Width = new System.Windows.GridLength(0);
            System.Windows.Controls.Grid.SetColumnSpan(WatchDirectoriesCard, 2);
            System.Windows.Controls.Grid.SetRow(PreferencesPanel, 1);
            System.Windows.Controls.Grid.SetColumn(PreferencesPanel, 0);
            System.Windows.Controls.Grid.SetColumnSpan(PreferencesPanel, 2);
            System.Windows.Controls.Grid.SetRow(SettingsActionsPanel, 2);
            WatchDirectoriesCard.Margin = new System.Windows.Thickness(0, 0, 0, 10);
        }
        else
        {
            PrimaryColumn.Width = new System.Windows.GridLength(2, System.Windows.GridUnitType.Star);
            SecondaryColumn.Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
            System.Windows.Controls.Grid.SetColumnSpan(WatchDirectoriesCard, 1);
            System.Windows.Controls.Grid.SetRow(PreferencesPanel, 0);
            System.Windows.Controls.Grid.SetColumn(PreferencesPanel, 1);
            System.Windows.Controls.Grid.SetColumnSpan(PreferencesPanel, 1);
            System.Windows.Controls.Grid.SetRow(SettingsActionsPanel, 1);
            WatchDirectoriesCard.Margin = new System.Windows.Thickness(0, 0, 10, 10);
        }
    }

    private void BrowseWatchDirectory_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel) return;
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "请选择日志监控目录",
            UseDescriptionForTitle = true,
            SelectedPath = viewModel.NewWatchDirectory
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) viewModel.NewWatchDirectory = dialog.SelectedPath;
    }
}

/// <summary>
/// 报告数量在输入阶段即时校验，避免用户保存后才发现范围错误。
/// 使用原始文本校验，既能拦截非数字，也能提示 1 到 10 之外的数值。
/// </summary>
public sealed class MaxOpenReportsValidationRule : ValidationRule
{
    public override ValidationResult Validate(object value, CultureInfo cultureInfo)
    {
        var text = Convert.ToString(value, cultureInfo)?.Trim();
        return int.TryParse(text, out var count) && count is >= 1 and <= 10
            ? ValidationResult.ValidResult
            : new ValidationResult(false, "请输入 1 到 10 之间的整数。");
    }
}
