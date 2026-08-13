using HephaestusWorkbench.App.ViewModels;
using System.Windows;
using System.Windows.Input;

namespace HephaestusWorkbench.App.Views;

/// <summary>应用商店页面的轻量交互适配器，仅负责把卡片点击转交给 ViewModel。</summary>
public partial class MarketplacePluginsPage : System.Windows.Controls.UserControl
{
    public MarketplacePluginsPage() => InitializeComponent();

    private void OnlineCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OnlinePluginItem item }
            && DataContext is MarketplacePluginsViewModel viewModel
            && viewModel.SelectOnlinePluginCommand.CanExecute(item))
        {
            viewModel.SelectOnlinePluginCommand.Execute(item);
            e.Handled = true;
        }
    }
}
