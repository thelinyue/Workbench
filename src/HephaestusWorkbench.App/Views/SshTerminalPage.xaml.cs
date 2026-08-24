using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using HephaestusWorkbench.App.ViewModels;
using HephaestusWorkbench.Core.Models;

namespace HephaestusWorkbench.App.Views;

/// <summary>
/// SSH 工作区的 WPF 交互层。设备抽屉覆盖终端而不改变终端宿主尺寸，
/// 独立窗口仅在视觉树之间迁移同一个终端控件，绝不在迁移时重新握手或创建 PTY。
/// </summary>
public partial class SshTerminalPage : System.Windows.Controls.UserControl
{
    private ICollectionView? _savedDevicesView;
    private readonly Dictionary<TerminalTabViewModel, DetachedTerminalWindow> _detachedWindows = [];

    public SshTerminalPage()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SshTerminalViewModel oldViewModel)
            oldViewModel.ConnectionDialogRequested -= ViewModel_ConnectionDialogRequested;

        if (e.NewValue is SshTerminalViewModel viewModel)
        {
            viewModel.ConnectionDialogRequested += ViewModel_ConnectionDialogRequested;
            _savedDevicesView = CollectionViewSource.GetDefaultView(viewModel.SavedDevices);
        }
        else
        {
            _savedDevicesView = null;
        }
    }

    private void ViewModel_ConnectionDialogRequested(object? sender, EventArgs e) => ShowConnectionDialog();

    private void OpenDeviceDrawer_OnClick(object sender, RoutedEventArgs e)
    {
        DrawerScrim.Visibility = Visibility.Visible;
        DeviceDrawer.Visibility = Visibility.Visible;
        DeviceSearchBox.Text = string.Empty;
        // 布局完成后再聚焦，确保打开抽屉时读屏器与键盘均落在搜索框。
        _ = Dispatcher.BeginInvoke(DeviceSearchBox.Focus, DispatcherPriority.Input);
    }

    private void CloseDeviceDrawer_OnClick(object sender, RoutedEventArgs e) => CloseDeviceDrawer();
    private void DrawerScrim_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseDeviceDrawer();

    private void CloseDeviceDrawer()
    {
        if (DeviceDrawer.Visibility != Visibility.Visible) return;
        DeviceDrawer.Visibility = Visibility.Collapsed;
        DrawerScrim.Visibility = Visibility.Collapsed;
        DeviceButton.Focus();
    }

    private void DeviceSearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_savedDevicesView is null) return;
        var term = DeviceSearchBox.Text.Trim();
        _savedDevicesView.Filter = item => item is SshDevice device && (string.IsNullOrWhiteSpace(term) ||
            device.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            device.Host.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            device.Username.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private async void ConnectSavedDevice_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SshTerminalViewModel viewModel || sender is not System.Windows.Controls.Button { CommandParameter: SshDevice device }) return;
        if (await viewModel.ConnectDeviceAsync(device, forceNewTab: false))
            CloseDeviceDrawer();
    }

    private void OpenConnectionDialog_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SshTerminalViewModel viewModel) viewModel.BeginNewConnection();
        else ShowConnectionDialog();
    }

    private void ShowConnectionDialog()
    {
        PasswordInput.Clear();
        PassphraseInput.Clear();
        ConnectionOverlay.Visibility = Visibility.Visible;
        _ = Dispatcher.BeginInvoke(() => PasswordInput.Focus(), DispatcherPriority.Input);
    }

    private void CloseConnectionDialog_OnClick(object sender, RoutedEventArgs e) => CloseConnectionDialog();

    private void CloseConnectionDialog()
    {
        if (DataContext is SshTerminalViewModel viewModel) viewModel.CancelConnectionDialog();
        PasswordInput.Clear();
        PassphraseInput.Clear();
        ConnectionOverlay.Visibility = Visibility.Collapsed;
    }

    private async void ConnectFromDialog_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SshTerminalViewModel viewModel) return;
        await viewModel.ConnectAsync();
        if (viewModel.StatusMessage.StartsWith("已连接到", StringComparison.Ordinal) ||
            viewModel.StatusMessage.StartsWith("已重新连接到", StringComparison.Ordinal))
        {
            CloseConnectionDialog();
            CloseDeviceDrawer();
        }
    }

    private void PasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SshTerminalViewModel viewModel && sender is PasswordBox passwordBox)
            viewModel.Password = passwordBox.Password;
    }

    private void PassphraseBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SshTerminalViewModel viewModel && sender is PasswordBox passwordBox)
            viewModel.PrivateKeyPassphrase = passwordBox.Password;
    }

    private void TerminalTabs_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TerminalTabs.SelectedItem is not TerminalTabViewModel tab)
        {
            TerminalHost.Content = null;
            return;
        }

        if (tab.IsDetached)
        {
            TerminalHost.Content = null;
            if (_detachedWindows.TryGetValue(tab, out var detached))
            {
                if (detached.WindowState == System.Windows.WindowState.Minimized) detached.WindowState = System.Windows.WindowState.Normal;
                detached.Activate();
            }
            return;
        }

        if (tab.Surface is TerminalWebViewControl existing)
        {
            TerminalHost.Content = existing;
            return;
        }

        TerminalHost.Content = new TerminalWebViewControl { DataContext = tab };
    }

    private void DetachTab_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { CommandParameter: TerminalTabViewModel tab }) return;
        if (_detachedWindows.TryGetValue(tab, out var existing))
        {
            if (existing.WindowState == System.Windows.WindowState.Minimized) existing.WindowState = System.Windows.WindowState.Normal;
            existing.Activate();
            return;
        }

        if (tab.Surface is not TerminalWebViewControl terminal)
        {
            TerminalTabs.SelectedItem = tab;
            if (tab.Surface is not TerminalWebViewControl initialized) return;
            terminal = initialized;
        }

        TerminalHost.Content = null;
        tab.SetDetached(true);
        var window = new DetachedTerminalWindow(tab.Title, tab.SessionState, terminal) { Owner = Window.GetWindow(this) };
        _detachedWindows.Add(tab, window);
        window.ReturnRequested += (_, surface) => ReturnDetachedTab(tab, surface);
        window.DisconnectAndCloseRequested += async (_, _) => await DisconnectAndCloseDetachedTabAsync(tab, window);
        window.Closed += (_, _) => _detachedWindows.Remove(tab);
        window.Show();
    }

    private void ReturnDetachedTab(TerminalTabViewModel tab, TerminalWebViewControl surface)
    {
        _detachedWindows.Remove(tab);
        tab.SetDetached(false);
        if (ReferenceEquals(TerminalTabs.SelectedItem, tab))
            TerminalHost.Content = surface;
    }

    private async void CloseTab_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { CommandParameter: TerminalTabViewModel tab }) return;
        await CloseTabFromWorkspaceAsync(tab);
    }

    /// <summary>统一关闭主窗口与独立窗口中的标签，避免快捷键绕过外置窗口的视觉回收。</summary>
    private async Task CloseTabFromWorkspaceAsync(TerminalTabViewModel tab)
    {
        if (_detachedWindows.TryGetValue(tab, out var detached))
            detached.CloseForSessionDisposal();
        if (DataContext is SshTerminalViewModel viewModel)
            await viewModel.CloseTabAsync(tab);
    }

    private async Task DisconnectAndCloseDetachedTabAsync(TerminalTabViewModel tab, DetachedTerminalWindow window)
    {
        _detachedWindows.Remove(tab);
        tab.SetDetached(false);
        window.CloseForSessionDisposal();
        if (DataContext is SshTerminalViewModel viewModel)
            await viewModel.CloseTabAsync(tab);
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DeviceDrawer.Visibility == Visibility.Visible)
        {
            CloseDeviceDrawer();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && ConnectionOverlay.Visibility == Visibility.Visible)
        {
            CloseConnectionDialog();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.T)
        {
            OpenConnectionDialog_OnClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.W &&
            DataContext is SshTerminalViewModel closeViewModel && closeViewModel.SelectedTab is { } selected)
        {
            _ = CloseTabFromWorkspaceAsync(selected);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.Tab or Key.PageDown)
        {
            SelectRelativeTab(+1);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key is Key.Tab or Key.PageUp)
        {
            SelectRelativeTab(-1);
            e.Handled = true;
        }
    }

    private void SelectRelativeTab(int direction)
    {
        if (DataContext is not SshTerminalViewModel viewModel || viewModel.Tabs.Count == 0) return;
        var index = viewModel.SelectedTab is null ? 0 : viewModel.Tabs.IndexOf(viewModel.SelectedTab);
        index = (index + direction + viewModel.Tabs.Count) % viewModel.Tabs.Count;
        TerminalTabs.SelectedItem = viewModel.Tabs[index];
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 页面离开时只摘下主窗口视觉宿主；标签和独立窗口继续持有同一 SSH 会话。
        TerminalHost.Content = null;
    }
}
