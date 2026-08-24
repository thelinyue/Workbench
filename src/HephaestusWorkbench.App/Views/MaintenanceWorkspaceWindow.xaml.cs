using System.ComponentModel;
using System.Windows;
using HephaestusWorkbench.App.ViewModels;

namespace HephaestusWorkbench.App.Views;

/// <summary>
/// 受控维护窗口只承载宿主 ViewModel。关闭窗口时取消 UI 等待并释放资源，
/// 不在 code-behind 中解释计划、拼接命令或改变 Executor 的安全停止语义。
/// </summary>
public partial class MaintenanceWorkspaceWindow : Window
{
    private readonly MaintenanceWorkspaceViewModel _viewModel;

    public MaintenanceWorkspaceWindow(MaintenanceWorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnClosing(object? sender, CancelEventArgs e) => _viewModel.CancelPendingOperations();

    private void OnClosed(object? sender, EventArgs e)
    {
        Closing -= OnClosing;
        Closed -= OnClosed;
        _viewModel.Dispose();
    }
}
