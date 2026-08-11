using System.Windows.Controls;
using HephaestusWorkbench.App.ViewModels;
using Wpf = System.Windows;

namespace HephaestusWorkbench.App.Views;

public partial class DashboardPage : System.Windows.Controls.UserControl
{
    public DashboardPage() => InitializeComponent();

    private async void OnSelectLogClick(object sender, Wpf.RoutedEventArgs e)
    {
        if (DataContext is not DashboardViewModel viewModel || !viewModel.CanStartQuickAnalysis) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要分析的日志",
            Filter = "日志压缩包 (*.tgz)|*.tgz|所有文件 (*.*)|*.*",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog() == true) await viewModel.AnalyzeSelectedFileAsync(dialog.FileName);
    }

    private void OnQuickDragOver(object sender, Wpf.DragEventArgs e)
    {
        e.Effects = DataContext is DashboardViewModel { CanStartQuickAnalysis: true }
                    && e.Data.GetDataPresent(Wpf.DataFormats.FileDrop)
            ? Wpf.DragDropEffects.Copy
            : Wpf.DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnQuickDrop(object sender, Wpf.DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not DashboardViewModel viewModel || !viewModel.CanStartQuickAnalysis) return;
        var paths = e.Data.GetData(Wpf.DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
        await viewModel.AnalyzeDroppedFilesAsync(paths);
    }
}
