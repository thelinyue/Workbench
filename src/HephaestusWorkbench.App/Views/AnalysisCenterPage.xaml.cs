using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HephaestusWorkbench.App.ViewModels;

namespace HephaestusWorkbench.App.Views;

/// <summary>分析中心页面，复用单一 WebView2 宿主承载当前报告标签。</summary>
public partial class AnalysisCenterPage : System.Windows.Controls.UserControl
{
    private readonly Dictionary<string, ReportViewerControl> _viewers = new();
    private ReportsWorkspaceViewModel? _workspace;

    public AnalysisCenterPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => ShowSelectedViewer();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_workspace is not null) _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        _workspace = (e.NewValue as AnalysisCenterViewModel)?.Reports;
        if (_workspace is not null) _workspace.PropertyChanged += OnWorkspacePropertyChanged;
        ShowSelectedViewer();
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ReportsWorkspaceViewModel.SelectedTab)
            or nameof(ReportsWorkspaceViewModel.IsAnalysisListVisible))
            ShowSelectedViewer();
    }

    /// <summary>返回分析中心列表时卸载查看器视觉宿主，但保留 Tab 对应缓存以便再次切换。</summary>
    private void OnShowAnalysisListClick(object sender, RoutedEventArgs e)
    {
        if (_workspace is null) return;
        _workspace.IsAnalysisListVisible = true;
        ShowSelectedViewer();
    }

    /// <summary>每个报告 Tab 只创建一个 WebView2 查看器，关闭 Tab 时由 Tab 状态精确释放。</summary>
    private void ShowSelectedViewer()
    {
        if (_workspace?.IsAnalysisListVisible != false || _workspace.SelectedTab is null)
        {
            ViewerHost.Content = null;
            return;
        }

        var tab = _workspace.SelectedTab;
        if (!_viewers.TryGetValue(tab.Report.Id, out var viewer))
        {
            viewer = new ReportViewerControl { DataContext = tab, Logger = _workspace.Logger };
            _viewers.Add(tab.Report.Id, viewer);
            tab.DisposeRequested += (_, _) =>
            {
                if (ReferenceEquals(ViewerHost.Content, viewer)) ViewerHost.Content = null;
                _viewers.Remove(tab.Report.Id);
            };
        }
        ViewerHost.Content = viewer;
    }

    /// <summary>
    /// 双击日志主体直接打开最新报告。按钮和菜单拥有各自命令，必须排除这些交互控件，
    /// 否则用户双击“打开解压目录”等按钮时会同时触发报告跳转。
    /// </summary>
    private void OnLogRowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2
            || sender is not FrameworkElement { DataContext: AnalysisLogGroupViewModel item }
            || DataContext is not AnalysisCenterViewModel viewModel)
            return;

        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(source) is not null
            || FindAncestor<MenuItem>(source) is not null
            || FindAncestor<System.Windows.Controls.Primitives.TextBoxBase>(source) is not null)
            return;

        // 即使当前没有可用报告也执行命令，由 ViewModel 输出明确的非模态提示。
        viewModel.OpenRowReportCommand.Execute(item);
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }
}
