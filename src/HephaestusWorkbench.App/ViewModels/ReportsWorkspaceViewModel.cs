using System.Collections.ObjectModel;
using System.Windows.Input;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Services;
using Wpf = System.Windows;

namespace HephaestusWorkbench.App.ViewModels;

/// <summary>分析中心内的报告查看工作区，仅维护当前进程中的临时 Tab。</summary>
public sealed class ReportsWorkspaceViewModel : ViewModelBase, IDisposable
{
    private readonly ReportService _reports;
    private readonly SettingsService _settings;
    private readonly WorkbenchLogger _logger;
    private readonly Func<string, bool> _confirmDelete;
    private readonly Action<string> _openExtractDirectory;
    private readonly Func<string, bool> _confirmCloseOldest;
    private ReportTabViewModel? _selectedTab;
    private bool _isLibraryVisible = true;

    public ReportsWorkspaceViewModel(
        ReportService reports,
        SettingsService settings,
        Action<string> openCase,
        Action<string> openExtractDirectory,
        WorkbenchLogger logger,
        Func<string, bool>? confirmCloseOldest = null,
        Func<string, bool>? confirmDelete = null)
    {
        _reports = reports;
        _settings = settings;
        _logger = logger;
        _confirmDelete = confirmDelete ?? (message => Wpf.MessageBox.Show(message, "确认删除案例和报告", Wpf.MessageBoxButton.YesNo, Wpf.MessageBoxImage.Warning) == Wpf.MessageBoxResult.Yes);
        _openExtractDirectory = openExtractDirectory;
        _confirmCloseOldest = confirmCloseOldest ?? (message => Wpf.MessageBox.Show(message, "报告数量已达上限", Wpf.MessageBoxButton.YesNo, Wpf.MessageBoxImage.Question) == Wpf.MessageBoxResult.Yes);
        // 报告页仍以工作区作为根 DataContext，Library 是实际承载查询、筛选和操作命令的模型。
        Library = new ReportsViewModel(reports, OpenReportAsync, openCase, openExtractDirectory, DeleteReportAsync);
        ShowLibraryCommand = new DelegateCommand(() => IsLibraryVisible = true);
        OpenTabCommand = new DelegateCommand(parameter => { if (parameter is ReportTabViewModel tab) ActivateTab(tab); });
        CloseTabCommand = new DelegateCommand(parameter => { if (parameter is ReportTabViewModel tab) CloseTab(tab); });
        OpenSelectedExtractDirectoryCommand = new DelegateCommand(OpenSelectedExtractDirectory, () => SelectedTab is not null);
    }

    public ReportsViewModel Library { get; }
    public WorkbenchLogger Logger => _logger;
    public ObservableCollection<ReportTabViewModel> OpenTabs { get; } = new();
    public ICommand ShowLibraryCommand { get; }
    public ICommand OpenTabCommand { get; }
    public ICommand CloseTabCommand { get; }
    public ICommand OpenSelectedExtractDirectoryCommand { get; }
    public int OpenTabCount => OpenTabs.Count;
    public bool HasOpenTabs => OpenTabs.Count > 0;
    public bool IsLibraryVisible { get => _isLibraryVisible; set => SetProperty(ref _isLibraryVisible, value); }
    public ReportTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (ReferenceEquals(_selectedTab, value)) return;
            if (_selectedTab is not null) _selectedTab.IsActive = false;
            if (!SetProperty(ref _selectedTab, value)) return;
            if (value is null)
            {
                ((DelegateCommand)OpenSelectedExtractDirectoryCommand).RaiseCanExecuteChanged();
                return;
            }
            value.IsActive = true;
            IsLibraryVisible = false;
            ((DelegateCommand)OpenSelectedExtractDirectoryCommand).RaiseCanExecuteChanged();
        }
    }

    public async Task InitializeAsync()
    {
        await RefreshLibraryAsync();
    }

    /// <summary>刷新报告库，供分析中心在后台任务状态变化后同步最新报告。</summary>
    public Task RefreshLibraryAsync(CancellationToken cancellationToken = default)
        => Library.LoadAsync(cancellationToken);

    /// <summary>
    /// 激活报告标签。即使点击的是当前标签，也必须退出报告库回到查看器，
    /// 因为用户可能刚刚通过“报告列表”暂时隐藏了当前查看器。
    /// </summary>
    private void ActivateTab(ReportTabViewModel tab)
    {
        if (ReferenceEquals(SelectedTab, tab))
        {
            IsLibraryVisible = false;
            return;
        }
        SelectedTab = tab;
    }

    public async Task<bool> OpenCaseReportAsync(string caseId)
    {
        var report = await _reports.GetLatestForCaseAsync(caseId);
        if (report is null)
        {
            _logger.Error($"案例没有可打开的报告：{caseId}");
            return false;
        }
        await OpenReportAsync(report);
        return SelectedTab?.Report.Id == report.Id;
    }

    public async Task OpenReportAsync(ReportSummary report)
    {
        var existing = OpenTabs.FirstOrDefault(x => x.Report.Id == report.Id);
        if (existing is not null)
        {
            SelectedTab = existing;
            return;
        }
        if (!report.IsAvailable)
        {
            Wpf.MessageBox.Show("报告文件不存在，无法打开。", "报告不可用", Wpf.MessageBoxButton.OK, Wpf.MessageBoxImage.Information);
            return;
        }
        var maximum = await _settings.GetReportMaxTabsAsync();
        if (OpenTabs.Count >= maximum)
        {
            var oldest = OpenTabs.OrderBy(x => x.LastOpenTime).First();
            if (!_confirmCloseOldest($"已打开 {maximum} 个报告。是否关闭最早打开的报告“{oldest.Title}”？")) return;
            CloseTab(oldest);
        }
        var tab = CreateTab(report);
        OpenTabs.Add(tab);
        RaiseTabProperties();
        SelectedTab = tab;
    }

    public void CloseTab(ReportTabViewModel tab)
    {
        var index = OpenTabs.IndexOf(tab);
        if (index < 0) return;
        tab.RequestDispose();
        OpenTabs.RemoveAt(index);
        if (ReferenceEquals(SelectedTab, tab))
            SelectedTab = OpenTabs.Count == 0 ? null : OpenTabs[Math.Min(index, OpenTabs.Count - 1)];
        if (OpenTabs.Count == 0)
        {
            _selectedTab = null;
            OnPropertyChanged(nameof(SelectedTab));
            IsLibraryVisible = true;
        }
        ((DelegateCommand)OpenSelectedExtractDirectoryCommand).RaiseCanExecuteChanged();
        RaiseTabProperties();
    }

    /// <summary>生命周期删除前关闭所有关联报告，确保 WebView2 不再占用即将删除的报告文件。</summary>
    public void CloseCaseTabs(IEnumerable<string> caseIds)
    {
        var ids = caseIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var tab in OpenTabs.Where(x => ids.Contains(x.Report.CaseId)).ToArray()) CloseTab(tab);
    }

    private ReportTabViewModel CreateTab(ReportSummary report)
    {
        return new ReportTabViewModel(report);
    }

    private void OpenSelectedExtractDirectory()
    {
        if (SelectedTab is not null) _openExtractDirectory(SelectedTab.Report.ExtractPath);
    }

    private async Task DeleteReportAsync(ReportSummary report)
    {
        var analysisCase = await _reports.GetCaseAsync(report.CaseId);
        var artifactDetails = analysisCase is null
            ? "无法读取原始日志和解压目录路径，请谨慎确认。"
            : $"原始日志：{analysisCase.SourcePath}\n解压目录：{analysisCase.ExtractPath}";
        var message = $"报告“{report.CaseName}”属于分析案例。\n\n{artifactDetails}\n\n"
            + "继续将删除该案例、报告、原始日志和解压目录，此操作不可恢复。";
        if (!_confirmDelete(message)) return;

        CloseCaseTabs(new[] { report.CaseId });
        try
        {
            await _reports.DeleteReportAndCaseAsync(report);
        }
        catch (Exception ex)
        {
            _logger.Error("删除报告和案例失败", ex);
            Wpf.MessageBox.Show($"删除失败：{ex.Message}", "删除失败", Wpf.MessageBoxButton.OK, Wpf.MessageBoxImage.Error);
        }
    }

    public Task SaveNowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private void RaiseTabProperties()
    {
        OnPropertyChanged(nameof(OpenTabCount));
        OnPropertyChanged(nameof(HasOpenTabs));
    }

    public void Dispose()
    {
        foreach (var tab in OpenTabs) tab.RequestDispose();
        Library.Dispose();
    }
}
