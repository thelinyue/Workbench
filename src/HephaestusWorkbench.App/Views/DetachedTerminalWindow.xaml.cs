using System.ComponentModel;
using System.Windows;

namespace HephaestusWorkbench.App.Views;

/// <summary>
/// 一个独立窗口只承载一个已存在的终端控件。关闭窗口或 Alt+F4 只会把同一控件移回主工作区；
/// 真正断开会话必须从“更多”菜单明确触发，避免误关窗口导致远程 Shell 被释放。
/// </summary>
public partial class DetachedTerminalWindow : Window
{
    private bool _returned;

    public DetachedTerminalWindow(string title, string state, TerminalWebViewControl terminal)
    {
        InitializeComponent();
        Title = $"{title} — SSH — Hephaestus Workbench";
        SessionTitle.Text = title;
        SessionState.Text = state;
        TerminalHost.Content = terminal;
    }

    public event EventHandler<TerminalWebViewControl>? ReturnRequested;
    public event EventHandler? DisconnectAndCloseRequested;

    /// <summary>主工作区已决定销毁标签时调用，防止窗口关闭时把已释放的终端表面重新挂回主窗口。</summary>
    internal void CloseForSessionDisposal()
    {
        _returned = true;
        TerminalHost.Content = null;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        ReturnToMain();
        base.OnClosing(e);
    }

    private void ReturnToMain_OnClick(object sender, RoutedEventArgs e) => Close();
    private void DisconnectAndClose_OnClick(object sender, RoutedEventArgs e) => DisconnectAndCloseRequested?.Invoke(this, EventArgs.Empty);

    private void ReturnToMain()
    {
        if (_returned || TerminalHost.Content is not TerminalWebViewControl terminal) return;
        _returned = true;
        TerminalHost.Content = null;
        ReturnRequested?.Invoke(this, terminal);
    }
}
