using System.Collections.ObjectModel;
using Wpf = System.Windows;
using System.Windows.Input;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.App.ViewModels;

public sealed class CasesViewModel : ViewModelBase
{
    private readonly CaseAnalysisService _analysis;
    private readonly Action<string, string> _openReport;
    private readonly Action<string> _openExtractDirectory;
    private AnalysisCase? _selectedCase;
    private string _newName = string.Empty;

    public CasesViewModel(CaseAnalysisService analysis, Action<string, string> openReport, Action<string> openExtractDirectory)
    {
        _analysis = analysis;
        _openReport = openReport;
        _openExtractDirectory = openExtractDirectory;
        RefreshCommand = new DelegateCommand(() => _ = LoadAsync());
        RenameCommand = new DelegateCommand(() => _ = RenameAsync(), () => SelectedCase is not null && !string.IsNullOrWhiteSpace(NewName));
        DeleteCommand = new DelegateCommand(() => _ = DeleteAsync(), () => SelectedCase is not null);
        OpenReportCommand = new DelegateCommand(() => OpenReport(), () => SelectedCase?.Status == CaseStatus.Completed && !string.IsNullOrWhiteSpace(SelectedCase.ReportPath));
        OpenExtractDirectoryCommand = new DelegateCommand(() => OpenExtractDirectory(), () => SelectedCase is not null);
        _ = LoadAsync();
    }

    public ObservableCollection<AnalysisCase> Items { get; } = new();
    public AnalysisCase? SelectedCase
    {
        get => _selectedCase;
        set
        {
            if (!SetProperty(ref _selectedCase, value)) return;
            NewName = value?.DisplayName ?? string.Empty;
            RaiseCommands();
        }
    }
    public string NewName
    {
        get => _newName;
        set { if (SetProperty(ref _newName, value)) RaiseCommands(); }
    }
    public ICommand RefreshCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand OpenReportCommand { get; }
    public ICommand OpenExtractDirectoryCommand { get; }
    public bool ShowEmptyState => Items.Count == 0;

    public async Task LoadAsync()
    {
        var cases = await _analysis.ListCasesAsync();
        Items.Clear();
        foreach (var item in cases) Items.Add(item);
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    public async Task SelectCaseAsync(string caseId)
    {
        await LoadAsync();
        SelectedCase = Items.FirstOrDefault(x => x.Id == caseId);
    }

    private async Task RenameAsync()
    {
        if (SelectedCase is null) return;
        await _analysis.RenameAsync(SelectedCase.Id, NewName);
        await LoadAsync();
    }

    private async Task DeleteAsync()
    {
        if (SelectedCase is null) return;
        if (SelectedCase.Status is CaseStatus.Running or CaseStatus.Ready)
        {
            Wpf.MessageBox.Show("案例正在排队或分析，请等待任务结束后再删除。", "无法删除", Wpf.MessageBoxButton.OK, Wpf.MessageBoxImage.Information);
            return;
        }
        var message = $"确认删除案例“{SelectedCase.DisplayName}”吗？\n\n"
            + $"原始日志：{SelectedCase.SourcePath}\n"
            + $"解压目录：{SelectedCase.ExtractPath}\n\n"
            + "案例、报告、上述原始日志和解压目录都会被删除，此操作不可恢复。";
        if (Wpf.MessageBox.Show(message, "确认删除", Wpf.MessageBoxButton.YesNo, Wpf.MessageBoxImage.Warning) != Wpf.MessageBoxResult.Yes) return;
        await _analysis.DeleteAsync(SelectedCase.Id);
        await LoadAsync();
    }

    private void OpenReport()
    {
        if (SelectedCase is { ReportPath: not null } item) _openReport(item.Id, item.ReportPath);
    }

    private void OpenExtractDirectory()
    {
        if (SelectedCase is not null) _openExtractDirectory(SelectedCase.ExtractPath);
    }

    private void RaiseCommands()
    {
        ((DelegateCommand)RenameCommand).RaiseCanExecuteChanged();
        ((DelegateCommand)DeleteCommand).RaiseCanExecuteChanged();
        ((DelegateCommand)OpenReportCommand).RaiseCanExecuteChanged();
        ((DelegateCommand)OpenExtractDirectoryCommand).RaiseCanExecuteChanged();
    }
}
