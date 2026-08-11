using System.Collections.ObjectModel;
using System.Windows.Input;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Services;
using AnalysisTaskStatus = HephaestusWorkbench.Core.Models.TaskStatus;

namespace HephaestusWorkbench.App.ViewModels;

public sealed class TasksViewModel : ViewModelBase
{
    private readonly CaseAnalysisService _analysis;
    private AnalysisTask? _selectedTask;

    public TasksViewModel(CaseAnalysisService analysis)
    {
        _analysis = analysis;
        RefreshCommand = new DelegateCommand(() => _ = LoadAsync());
        CancelCommand = new DelegateCommand(() => _ = CancelAsync(), () => SelectedTask is { Status: AnalysisTaskStatus.Waiting or AnalysisTaskStatus.Running });
        _ = LoadAsync();
    }

    public ObservableCollection<AnalysisTask> Items { get; } = new();
    public AnalysisTask? SelectedTask
    {
        get => _selectedTask;
        set { if (SetProperty(ref _selectedTask, value)) ((DelegateCommand)CancelCommand).RaiseCanExecuteChanged(); }
    }
    public ICommand RefreshCommand { get; }
    public ICommand CancelCommand { get; }
    public bool ShowEmptyState => Items.Count == 0;

    public async Task LoadAsync()
    {
        var tasks = await _analysis.ListTasksAsync();
        Items.Clear();
        foreach (var item in tasks) Items.Add(item);
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private async Task CancelAsync()
    {
        if (SelectedTask is null) return;
        await _analysis.CancelAsync(SelectedTask.Id);
        await LoadAsync();
    }
}
