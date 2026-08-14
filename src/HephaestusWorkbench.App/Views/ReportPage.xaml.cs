using System.Windows.Controls;
using System.ComponentModel;
using HephaestusWorkbench.App.ViewModels;

namespace HephaestusWorkbench.App.Views;

public partial class ReportPage : System.Windows.Controls.UserControl
{
    private readonly Dictionary<string, ReportViewerControl> _viewers = new();
    private ReportsWorkspaceViewModel? _workspace;

    public ReportPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => ShowSelectedViewer();
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (_workspace is not null) _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        _workspace = e.NewValue as ReportsWorkspaceViewModel;
        if (_workspace is not null) _workspace.PropertyChanged += OnWorkspacePropertyChanged;
        ShowSelectedViewer();
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ReportsWorkspaceViewModel.SelectedTab)
            or nameof(ReportsWorkspaceViewModel.IsLibraryVisible))
            ShowSelectedViewer();
    }

    /// <summary>
    /// 报告列表是查看器宿主的页面级切换入口。保留命令绑定的同时，在 Click 事件中直接同步工作区，
    /// 避免 WebView2 获取焦点或命令路由异常时只改变模型而没有卸载当前视觉宿主。
    /// </summary>
    private void OnShowLibraryClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_workspace is null) return;
        _workspace.IsLibraryVisible = true;
        ShowSelectedViewer();
    }

    /// <summary>每个 Tab 只创建一个查看器；切换时复用，关闭时由 DisposeRequested 精确释放。</summary>
    private void ShowSelectedViewer()
    {
        if (_workspace?.IsLibraryVisible == true)
        {
            // 返回报告库时主动卸载当前视觉宿主，但保留 _viewers 缓存，切回标签仍复用同一个查看器。
            ViewerHost.Content = null;
            return;
        }
        var tab = _workspace?.SelectedTab;
        if (tab is null)
        {
            ViewerHost.Content = null;
            return;
        }
        if (!_viewers.TryGetValue(tab.Report.Id, out var viewer))
        {
            viewer = new ReportViewerControl { DataContext = tab, Logger = _workspace?.Logger };
            _viewers.Add(tab.Report.Id, viewer);
            tab.DisposeRequested += (_, _) =>
            {
                if (ReferenceEquals(ViewerHost.Content, viewer)) ViewerHost.Content = null;
                _viewers.Remove(tab.Report.Id);
            };
        }
        ViewerHost.Content = viewer;
    }
}
