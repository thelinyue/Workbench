using System.Windows;
using System.Windows.Controls;
using HephaestusWorkbench.App.ViewModels;

namespace HephaestusWorkbench.App.Views;

/// <summary>承载固定 SSH 页面，并确保每个标签复用自己的隔离 WebView2 终端表面。</summary>
public partial class SshTerminalPage : System.Windows.Controls.UserControl
{
    public SshTerminalPage()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    private void PasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SshTerminalViewModel viewModel && sender is System.Windows.Controls.PasswordBox passwordBox)
            viewModel.Password = passwordBox.Password;
    }

    private void PassphraseBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SshTerminalViewModel viewModel && sender is System.Windows.Controls.PasswordBox passwordBox)
            viewModel.PrivateKeyPassphrase = passwordBox.Password;
    }

    private void TerminalTabs_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TerminalTabs.SelectedItem is not TerminalTabViewModel tab)
        {
            TerminalHost.Content = null;
            return;
        }

        if (tab.Surface is TerminalWebViewControl existing)
        {
            TerminalHost.Content = existing;
            return;
        }

        TerminalHost.Content = new TerminalWebViewControl { DataContext = tab };
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 导航离开 SSH 页面时只从视觉树摘下 WebView；会话仍由标签持有，返回页面后继续复用。
        TerminalHost.Content = null;
    }
}
