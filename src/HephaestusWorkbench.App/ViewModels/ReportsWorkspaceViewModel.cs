using System.Collections.ObjectModel;
using System.Windows.Input;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Services;
using Wpf = System.Windows;

namespace HephaestusWorkbench.App.ViewModels;

/// <summary>分析中心内的报告查看工作区，统一维护 Tab 上限和可恢复会话。</summary>
public sealed class ReportsWorkspaceViewModel : ViewModelBase, IDisposable
{
    private readonly ReportService _reports;
    private readonly SettingsService _settings;
    private readonly WorkbenchLogger _logger;
    private readonly Action<string> _openExtractDirectory;
    private readonly Func<string, bool> _confirmCloseOldest;
    private CancellationTokenSource? _saveCancellation;
    private ReportTabViewModel? _selectedTab;
    private bool _isLibraryVisible = true;
    private bool _isRestoring;

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
        ShowLibraryCommand = new DelegateCommand(() => IsLibraryVisible = true);
        OpenTabCommand = new DelegateCommand(parameter => { if (parameter is ReportTabViewModel tab) SelectedTab = tab; });
        CloseTabCommand = new DelegateCommand(parameter => { if (parameter is ReportTabViewModel tab) CloseTab(tab); });
        OpenSelectedExtractDirectoryCommand = new DelegateCommand(OpenSelectedExtractDirectory, () => SelectedTab is not null);
    }

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
            if (!SetProperty(ref _selectedTab, value) || value is null) return;
            value.LastOpenTime = DateTime.Now;
            IsLibraryVisible = false;
            ((DelegateCommand)OpenSelectedExtractDirectoryCommand).RaiseCanExecuteChanged();
            ScheduleSave();
        }
    }

    public async Task InitializeAsync()
    {
        if (!await _settings.GetReportRestoreEnabledAsync()) return;
        _isRestoring = true;
        try
        {
            var maximum = await _settings.GetReportMaxTabsAsync();
            var sessions = (await _reports.LoadSessionAsync()).OrderBy(x => x.OrderIndex).Take(maximum).ToArray();
            foreach (var session in sessions)
            {
                var summary = await _reports.GetSummaryAsync(session.ReportId);
                if (summary is null || !summary.IsAvailable)
                {
                    _logger.Error($"跳过无法恢复的报告：{session.ReportId}");
                    continue;
                }
                var tab = CreateTab(summary, session.Id);
                tab.ScrollPosition = session.ScrollPosition;
                tab.LastOpenTime = session.LastOpenTime;
                OpenTabs.Add(tab);
                if (session.IsActive) _selectedTab = tab;
            }
            if (_selectedTab is null) _selectedTab = OpenTabs.LastOrDefault();
            if (_selectedTab is not null)
            {
                OnPropertyChanged(nameof(SelectedTab));
                IsLibraryVisible = false;
            }
            ((DelegateCommand)OpenSelectedExtractDirectoryCommand).RaiseCanExecuteChanged();
            RaiseTabProperties();
        }
        finally
        {
            _isRestoring = false;
            await SaveNowAsync();
        }
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
        tab.ScrollPositionChanged -= OnScrollPositionChanged;
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
        ScheduleSave();
    }

    /// <summary>生命周期删除前关闭所有关联报告，确保 WebView2 不再占用即将删除的报告文件。</summary>
    public void CloseCaseTabs(IEnumerable<string> caseIds)
    {
        var ids = caseIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var tab in OpenTabs.Where(x => ids.Contains(x.Report.CaseId)).ToArray()) CloseTab(tab);
    }

    private ReportTabViewModel CreateTab(ReportSummary report, string? sessionId = null)
    {
        var tab = new ReportTabViewModel(report, sessionId);
        tab.ScrollPositionChanged += OnScrollPositionChanged;
        return tab;
    }

    private void OpenSelectedExtractDirectory()
    {
        if (SelectedTab is not null) _openExtractDirectory(SelectedTab.Report.ExtractPath);
    }

    private void OnScrollPositionChanged(object? sender, EventArgs e) => ScheduleSave();

    private void ScheduleSave()
    {
        if (_isRestoring) return;
        _saveCancellation?.Cancel();
        _saveCancellation?.Dispose();
        _saveCancellation = new CancellationTokenSource();
        _ = DelaySaveAsync(_saveCancellation.Token);
    }

    private async Task DelaySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(500, cancellationToken);
            await SaveNowAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.Error("保存报告工作区失败", ex); }
    }

    public Task SaveNowAsync(CancellationToken cancellationToken = default)
    {
        var sessions = OpenTabs.Select((tab, index) => new ReportSession
        {
            Id = tab.SessionId,
            ReportId = tab.Report.Id,
            OrderIndex = index,
            IsActive = ReferenceEquals(tab, SelectedTab),
            ScrollPosition = tab.ScrollPosition,
            LastOpenTime = tab.LastOpenTime
        }).ToArray();
        return _reports.SaveSessionAsync(sessions, cancellationToken);
    }

    private void RaiseTabProperties()
    {
        OnPropertyChanged(nameof(OpenTabCount));
        OnPropertyChanged(nameof(HasOpenTabs));
    }

    public void Dispose()
    {
        _saveCancellation?.Cancel();
        _saveCancellation?.Dispose();
        foreach (var tab in OpenTabs) tab.RequestDispose();
    }
}
