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
        if (e.PropertyName == nameof(ReportsWorkspaceViewModel.SelectedTab)) ShowSelectedViewer();
    }

    /// <summary>每个 Tab 只创建一个查看器；切换时复用，关闭时由 DisposeRequested 精确释放。</summary>
    private void ShowSelectedViewer()
    {
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
