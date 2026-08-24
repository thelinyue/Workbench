using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HephaestusWorkbench.App.ViewModels;
using Microsoft.Win32;

namespace HephaestusWorkbench.App.Views;

/// <summary>精简后的分析中心页面，只负责文件选择、拖放和列表双击交互。</summary>
public partial class AnalysisCenterPage : System.Windows.Controls.UserControl
{
    public AnalysisCenterPage() => InitializeComponent();

    private async void OnSelectLogFileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择诊断日志",
            Filter = "诊断日志 (*.tgz;*.tgz.temp)|*.tgz;*.tgz.temp|所有文件 (*.*)|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog() == true && DataContext is AnalysisCenterViewModel viewModel)
            await viewModel.AnalyzeFileAsync(dialog.FileName);
    }

    private async void OnFilesDropped(object sender, System.Windows.DragEventArgs e)
    {
        if (DataContext is not AnalysisCenterViewModel viewModel
            || !e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            || e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] { Length: > 0 } files)
            return;
        await viewModel.AnalyzeFileAsync(files[0]);
    }

    private void OnLogRowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2
            || sender is not FrameworkElement { DataContext: var item }
            || DataContext is not AnalysisCenterViewModel viewModel
            || !viewModel.OpenRowReportCommand.CanExecute(item))
            return;

        viewModel.OpenRowReportCommand.Execute(item);
        e.Handled = true;
    }
}
