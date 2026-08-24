using System.Diagnostics;
using System.IO;
using System.Windows;

namespace HephaestusWorkbench.App;

/// <summary>阻断旧工作区启动，只提供查看目录和退出，不执行任何数据写入。</summary>
public partial class LegacyWorkspaceWindow : Window
{
    private readonly string _dataRoot;

    public LegacyWorkspaceWindow(string dataRoot)
    {
        InitializeComponent();
        _dataRoot = Path.GetFullPath(dataRoot);
        DataContext = new { DataRoot = _dataRoot };
    }

    private void OpenDirectory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Directory.Exists(_dataRoot) is false) throw new DirectoryNotFoundException("旧工作区目录不存在。");
            Process.Start(new ProcessStartInfo(_dataRoot) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"无法打开旧工作区目录：{ex.Message}", "打开目录失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
}
