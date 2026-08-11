using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using HephaestusWorkbench.App.ViewModels;
using HephaestusWorkbench.App.Views;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.PluginSDK;
using WorkbenchApp = HephaestusWorkbench.App.App;

namespace HephaestusWorkbench.Tests;

[CollectionDefinition("WPF UI", DisableParallelization = true)]
public sealed class WpfUiCollection;

[Collection("WPF UI")]
public sealed class MarketplacePluginsPageTests
{
    [Fact]
    public void InstalledPluginCard_RendersReadOnlyFieldsWithoutBindingException()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            WorkbenchApp? app = null;
            try
            {
                app = new WorkbenchApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                var page = new MarketplacePluginsPage
                {
                    DataContext = new PluginPageData()
                };

                // DataTemplate 只有进入布局阶段才会实例化；这里真实触发插件卡片渲染，
                // 用于覆盖 Run.Text 对只读属性误用双向绑定导致的生产闪退。
                page.Measure(new Size(1200, 720));
                page.Arrange(new Rect(0, 0, 1200, 720));
                page.UpdateLayout();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                app?.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "WPF 插件页面渲染测试超时。");
        Assert.Null(failure);
    }

    private sealed class PluginPageData
    {
        public ObservableCollection<InstalledPluginItem> InstalledItems { get; } = new()
        {
            new InstalledPluginItem
            {
                Manifest = new PluginManifest
                {
                    Id = "log-analyzer",
                    Name = "日志分析插件",
                    Version = "1.50",
                    Type = PluginType.Exe,
                    Entry = "log_analyzer.exe"
                },
                Source = PluginInstallSource.Bundled,
                Enabled = true,
                IsDefault = true
            }
        };

        public ObservableCollection<OnlinePluginItem> OnlineItems { get; } = new();
        public ObservableCollection<string> Issues { get; } = new();
        public string Message => "测试";
        public string LastRefreshText => "测试";
        public bool ShowIssues => false;
        public bool ShowInstalledEmpty => false;
        public bool ShowOnlineEmpty => true;
        public ICommand RefreshCommand { get; } = ApplicationCommands.NotACommand;
        public ICommand InstallCommand { get; } = ApplicationCommands.NotACommand;
        public ICommand SetDefaultCommand { get; } = ApplicationCommands.NotACommand;
        public ICommand ToggleEnabledCommand { get; } = ApplicationCommands.NotACommand;
        public ICommand UninstallCommand { get; } = ApplicationCommands.NotACommand;
        public ICommand OpenPluginDirectoryCommand { get; } = ApplicationCommands.NotACommand;
        public ICommand OpenDocumentationCommand { get; } = ApplicationCommands.NotACommand;
    }
}
