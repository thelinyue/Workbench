using System.Collections.ObjectModel;
using System.Windows.Input;
using Wpf = System.Windows;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Services;
using AnalysisTaskStatus = HephaestusWorkbench.Core.Models.TaskStatus;

namespace HephaestusWorkbench.App.ViewModels;

public sealed class DashboardViewModel : ViewModelBase
{
    private readonly CaseAnalysisService _analysis;
    private readonly StorageService _storage;
    private readonly LogInboxService _inbox;

    public DashboardViewModel(CaseAnalysisService analysis, StorageService storage, LogInboxService inbox, Action openInbox, Action openCases, Action openSettings)
    {
        _analysis = analysis;
        _storage = storage;
        _inbox = inbox;
        OpenSettingsCommand = new DelegateCommand(openSettings);
        OpenInboxCommand = new DelegateCommand(openInbox);
        OpenCasesCommand = new DelegateCommand(openCases);
        _inbox.ConfigurationChanged += OnConfigurationChanged;
        _ = LoadAsync();
    }

    public ObservableCollection<AnalysisCase> RecentCases { get; } = new();
    public ObservableCollection<AnalysisTask> CurrentTasks { get; } = new();
    public string UsedSpace { get; private set; } = "计算中";
    public string ReleasableSpace { get; private set; } = "计算中";
    public int CaseCount { get; private set; }
    public bool ShowFirstUseGuide => _inbox.IsUsingDefaultDirectory;
    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenInboxCommand { get; }
    public ICommand OpenCasesCommand { get; }
    public bool ShowNoCases => RecentCases.Count == 0;
    public bool ShowNoTasks => CurrentTasks.Count == 0;

    private void OnConfigurationChanged(object? sender, EventArgs e)
    {
        var dispatcher = Wpf.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) OnPropertyChanged(nameof(ShowFirstUseGuide));
        else _ = dispatcher.InvokeAsync(() => OnPropertyChanged(nameof(ShowFirstUseGuide)));
    }

    public async Task LoadAsync()
    {
        var cases = await _analysis.ListCasesAsync();
        var tasks = await _analysis.ListTasksAsync();
        var summary = await _storage.GetSummaryAsync();
        RecentCases.Clear();
        foreach (var item in cases.Take(8)) RecentCases.Add(item);
        CurrentTasks.Clear();
        foreach (var item in tasks.Where(x => x.Status is not AnalysisTaskStatus.Completed and not AnalysisTaskStatus.Failed and not AnalysisTaskStatus.Cancelled).Take(8)) CurrentTasks.Add(item);
        UsedSpace = ViewModelFormatting.Size(summary.TotalBytes);
        ReleasableSpace = ViewModelFormatting.Size(summary.ReleasableBytes);
        CaseCount = summary.CaseCount;
        OnPropertyChanged(nameof(UsedSpace));
        OnPropertyChanged(nameof(ReleasableSpace));
        OnPropertyChanged(nameof(CaseCount));
        OnPropertyChanged(nameof(ShowNoCases));
        OnPropertyChanged(nameof(ShowNoTasks));
    }
}
