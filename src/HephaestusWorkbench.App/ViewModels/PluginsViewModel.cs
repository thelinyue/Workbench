using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.App.ViewModels;

/// <summary>
/// 应用中心模型，展示本地已发现插件、扫描问题和开发入口。
/// 当前版本不负责在线安装或升级，所有插件文件仍由用户复制到工作台插件目录。
/// </summary>
public sealed class PluginsViewModel : ViewModelBase
{
    private readonly PluginCatalog _catalog;
    private readonly WorkbenchLogger _logger;
    private bool _isBusy;
    private string _message = "正在扫描插件…";
    private DateTime? _lastScanTime;

    public PluginsViewModel(PluginCatalog catalog, WorkbenchLogger logger)
    {
        _catalog = catalog;
        _logger = logger;
        RefreshCommand = new DelegateCommand(() => _ = LoadAsync(), () => !IsBusy);
        OpenPluginDirectoryCommand = new DelegateCommand(OpenPluginDirectory);
        OpenDocumentationCommand = new DelegateCommand(OpenDocumentation);
        _ = LoadAsync();
    }

    public ObservableCollection<PluginManifest> Items { get; } = new();
    public ObservableCollection<string> Issues { get; } = new();
    public ICommand RefreshCommand { get; }
    public ICommand OpenPluginDirectoryCommand { get; }
    public ICommand OpenDocumentationCommand { get; }
    public string PluginDirectory => _catalog.PluginsDirectory;
    public string DocumentationPath => Path.Combine(AppContext.BaseDirectory, "Documentation", "plugin-development.md");
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) ((DelegateCommand)RefreshCommand).RaiseCanExecuteChanged(); } }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public string LastScanText => _lastScanTime is null ? "尚未扫描" : $"最后扫描：{_lastScanTime:yyyy-MM-dd HH:mm:ss}";
    public int PluginCount => Items.Count;
    public int ExeCount => Items.Count(x => x.Type == PluginType.Exe);
    public int DllCount => Items.Count(x => x.Type == PluginType.Dll);
    public int IssueCount => Issues.Count;
    public bool ShowEmptyState => Items.Count == 0;
    public bool ShowIssues => Issues.Count > 0;

    private async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Message = "正在扫描本地插件…";
        try
        {
            var plugins = await _catalog.ScanAsync();
            Items.Clear();
            foreach (var plugin in plugins) Items.Add(plugin);
            Issues.Clear();
            foreach (var issue in _catalog.Issues) Issues.Add(issue);
            _lastScanTime = DateTime.Now;
            Message = Issues.Count == 0
                ? $"扫描完成，发现 {Items.Count} 个可用插件。"
                : $"扫描完成，发现 {Items.Count} 个可用插件，{Issues.Count} 个问题。";
        }
        catch (OperationCanceledException)
        {
            Message = "插件扫描已取消。";
        }
        catch (Exception ex)
        {
            Message = $"扫描插件失败：{ex.Message}";
            Issues.Clear();
            Issues.Add(Message);
            _logger.Error("应用中心扫描失败", ex);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(LastScanText));
            OnPropertyChanged(nameof(PluginCount));
            OnPropertyChanged(nameof(ExeCount));
            OnPropertyChanged(nameof(DllCount));
            OnPropertyChanged(nameof(IssueCount));
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(ShowIssues));
        }
    }

    private void OpenPluginDirectory()
    {
        try
        {
            Directory.CreateDirectory(PluginDirectory);
            Process.Start(new ProcessStartInfo { FileName = PluginDirectory, UseShellExecute = true });
            Message = "已打开插件目录。";
        }
        catch (Exception ex)
        {
            Message = $"打开插件目录失败：{ex.Message}";
            _logger.Error($"打开插件目录失败：{PluginDirectory}", ex);
        }
    }

    private void OpenDocumentation()
    {
        try
        {
            if (!File.Exists(DocumentationPath))
            {
                Message = $"未找到插件开发文档：{DocumentationPath}";
                _logger.Error(Message);
                return;
            }

            Process.Start(new ProcessStartInfo { FileName = DocumentationPath, UseShellExecute = true });
            Message = "已打开插件开发文档。";
        }
        catch (Exception ex)
        {
            Message = $"打开插件开发文档失败：{ex.Message}";
            _logger.Error($"打开插件开发文档失败：{DocumentationPath}", ex);
        }
    }
}
