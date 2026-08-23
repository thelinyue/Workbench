using System.IO;
using System.Windows;
using HephaestusWorkbench.App.ViewModels;
using Forms = System.Windows.Forms;

namespace HephaestusWorkbench.App;

/// <summary>首次运行配置向导窗口，只负责路径选择和显示，不直接操作数据库。</summary>
public partial class FirstRunWizard : Window
{
    private readonly FirstRunWizardViewModel _viewModel;

    public FirstRunWizard(
        string defaultDataPath,
        Func<string, IReadOnlyList<string>, IProgress<string>, Task> initializeAsync)
    {
        InitializeComponent();
        _viewModel = new FirstRunWizardViewModel(
            defaultDataPath,
            initializeAsync,
            BrowseDataPath,
            BrowseMonitorPath);
        _viewModel.Finished += OnFinished;
        DataContext = _viewModel;
    }

    private void BrowseDataPath()
    {
        using var dialog = CreateFolderDialog("请选择 Hephaestus工作台数据目录", _viewModel.DataPath);
        if (dialog.ShowDialog() == Forms.DialogResult.OK) _viewModel.DataPath = dialog.SelectedPath;
    }

    private void BrowseMonitorPath()
    {
        using var dialog = CreateFolderDialog("请选择日志监控目录", _viewModel.DataPath);
        if (dialog.ShowDialog() == Forms.DialogResult.OK) _viewModel.NewMonitorPath = dialog.SelectedPath;
    }

    private void OnFinished(object? sender, EventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private static Forms.FolderBrowserDialog CreateFolderDialog(string description, string selectedPath)
        => new()
        {
            Description = description,
            UseDescriptionForTitle = true,
            SelectedPath = selectedPath,
            ShowNewFolderButton = true
        };
}
