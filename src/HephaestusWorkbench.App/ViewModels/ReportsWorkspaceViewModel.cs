using System.Collections.ObjectModel;
using System.Windows.Input;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Services;
using Wpf = System.Windows;

namespace HephaestusWorkbench.App.ViewModels;

/// <summary>分析中心内嵌的报告查看工作区，仅维护当前进程中的临时 Tab。</summary>
public sealed class ReportsWorkspaceViewModel : ViewModelBase, IDisposable
{
    private readonly ReportService _reports;
    private readonly SettingsService _settings;
    private readonly WorkbenchLogger _logger;
    private readonly Action<string> _openExtractDirectory;
    private readonly Func<string, bool> _confirmCloseOldest;
    private ReportTabViewModel? _selectedTab;
    private bool _isAnalysisListVisible = true;

    public ReportsWorkspaceViewModel(
        ReportService reports,
        SettingsService settings,
        Action<string> openExtractDirectory,
        WorkbenchLogger logger,
        Func<string, bool>? confirmCloseOldest = null)
    {
        _reports = reports;
        _settings = settings;
        _logger = logger;
        _openExtractDirectory = openExtractDirectory;
        _confirmCloseOldest = confirmCloseOldest ?? (message => Wpf.MessageBox.Show(message, "报告数量已达上限", Wpf.MessageBoxButton.YesNo, Wpf.MessageBoxImage.Question) == Wpf.MessageBoxResult.Yes);
        ShowAnalysisListCommand = new DelegateCommand(() => IsAnalysisListVisible = true);
        OpenTabCommand = new DelegateCommand(parameter => { if (parameter is ReportTabViewModel tab) ActivateTab(tab); });
        CloseTabCommand = new DelegateCommand(parameter => { if (parameter is ReportTabViewModel tab) CloseTab(tab); });
        OpenSelectedExtractDirectoryCommand = new DelegateCommand(OpenSelectedExtractDirectory, () => SelectedTab is not null);
    }

    public WorkbenchLogger Logger => _logger;
    public ObservableCollection<ReportTabViewModel> OpenTabs { get; } = new();
    public ICommand ShowAnalysisListCommand { get; }
    public ICommand OpenTabCommand { get; }
    public ICommand CloseTabCommand { get; }
    public ICommand OpenSelectedExtractDirectoryCommand { get; }
    public int OpenTabCount => OpenTabs.Count;
    public bool HasOpenTabs => OpenTabs.Count > 0;
    public bool IsAnalysisListVisible { get => _isAnalysisListVisible; set => SetProperty(ref _isAnalysisListVisible, value); }
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
            IsAnalysisListVisible = false;
            ((DelegateCommand)OpenSelectedExtractDirectoryCommand).RaiseCanExecuteChanged();
        }
    }

    /// <summary>激活报告标签；相同报告只保留一个 Tab。</summary>
    private void ActivateTab(ReportTabViewModel tab)
    {
        if (ReferenceEquals(SelectedTab, tab))
        {
            IsAnalysisListVisible = false;
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
        var tab = new ReportTabViewModel(report);
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
            IsAnalysisListVisible = true;
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

    private void OpenSelectedExtractDirectory()
    {
        if (SelectedTab is not null) _openExtractDirectory(SelectedTab.Report.ExtractPath);
    }

    private void RaiseTabProperties()
    {
        OnPropertyChanged(nameof(OpenTabCount));
        OnPropertyChanged(nameof(HasOpenTabs));
    }

    public void Dispose()
    {
        foreach (var tab in OpenTabs) tab.RequestDispose();
    }
}