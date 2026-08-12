using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using HephaestusWorkbench.App.ViewModels;
using HephaestusWorkbench.App.Views;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;
using WorkbenchApp = HephaestusWorkbench.App.App;

namespace HephaestusWorkbench.Tests;

[CollectionDefinition("WPF UI", DisableParallelization = true)]
public sealed class WpfUiCollection;

[Collection("WPF UI")]
public sealed class MarketplacePluginsPageTests
{
    [Fact]
    public void OnlinePluginItem_UsesCanonicalSegmentedVersionsForUpdateState()
    {
        var update = new OnlinePluginItem
        {
            Plugin = new MarketplacePlugin
            {
                Id = "log-analyzer",
                Name = "日志分析插件",
                Version = "1.60",
                Type = PluginType.Exe,
                PackageUrl = "https://example.com/plugin.zip",
                Sha256 = new string('a', 64),
                PackageSize = 1
            },
            InstalledVersion = "1.50",
            IsCompatible = true
        };

        Assert.True(update.HasUpdate);
        Assert.True(update.CanInstall);
        Assert.Equal("更新", update.ActionText);

        var olderOnline = new OnlinePluginItem
        {
            Plugin = update.Plugin with { Version = "1.50" },
            InstalledVersion = "1.60",
            IsCompatible = true
        };

        Assert.False(olderOnline.HasUpdate);
        Assert.False(olderOnline.CanInstall);
        Assert.Equal("已是最新", olderOnline.ActionText);
    }

    [Fact]
    public void CriticalPages_RenderWithoutBindingRegressionAndKeepAnalysisViewsExclusive()
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

                var analysisData = new AnalysisPageData();
                var analysisPage = new AnalysisCenterPage { DataContext = analysisData };
                analysisPage.Measure(new Size(1200, 720));
                analysisPage.Arrange(new Rect(0, 0, 1200, 720));
                analysisPage.UpdateLayout();

                var analysisList = Assert.IsAssignableFrom<FrameworkElement>(analysisPage.FindName("AnalysisListHost"));
                var reportWorkspace = Assert.IsAssignableFrom<FrameworkElement>(analysisPage.FindName("ReportWorkspaceHost"));
                // 复现生产截图中的覆盖问题：列表与报告工作区必须始终严格互斥。
                Assert.Equal(Visibility.Visible, analysisList.Visibility);
                Assert.Equal(Visibility.Collapsed, reportWorkspace.Visibility);

                analysisData.Reports.IsLibraryVisible = false;
                analysisPage.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, analysisList.Visibility);
                Assert.Equal(Visibility.Visible, reportWorkspace.Visibility);
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
        public double ProgressValue => 0;
        public bool IsProgressIndeterminate => false;
        public bool IsBusy => false;
        public bool CanUploadRules => false;
        public string UploadRulesHint => "测试";
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
        public ICommand UseRuleEditorCommand { get; } = ApplicationCommands.NotACommand;
        public ICommand ImportRuleCommand { get; } = ApplicationCommands.NotACommand;
        public ICommand UploadRuleCommand { get; } = ApplicationCommands.NotACommand;
        public ICommand OpenRulesDirectoryCommand { get; } = ApplicationCommands.NotACommand;
    }

    private sealed class AnalysisPageData
    {
        public ReportWorkspaceData Reports { get; } = new();
    }

    private sealed class ReportWorkspaceData : INotifyPropertyChanged
    {
        private bool _isLibraryVisible = true;

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool IsLibraryVisible
        {
            get => _isLibraryVisible;
            set
            {
                if (_isLibraryVisible == value) return;
                _isLibraryVisible = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLibraryVisible)));
            }
        }

        public bool HasOpenTabs => false;
        public object Library { get; } = new();
        public ObservableCollection<object> OpenTabs { get; } = new();
        public ICommand ShowLibraryCommand { get; } = ApplicationCommands.NotACommand;
        public ICommand OpenTabCommand { get; } = ApplicationCommands.NotACommand;
        public ICommand CloseTabCommand { get; } = ApplicationCommands.NotACommand;
        public ICommand OpenSelectedExtractDirectoryCommand { get; } = ApplicationCommands.NotACommand;
    }
}
