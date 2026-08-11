using System.Windows.Controls;
using HephaestusWorkbench.App.ViewModels;
using Forms = System.Windows.Forms;

namespace HephaestusWorkbench.App.Views;

public partial class SettingsPage : System.Windows.Controls.UserControl
{
    public SettingsPage() => InitializeComponent();

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
