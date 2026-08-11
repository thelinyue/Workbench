using System.Collections.ObjectModel;
using Wpf = System.Windows;
using System.Windows.Input;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.App.ViewModels;

public sealed class StorageViewModel : ViewModelBase
{
    private readonly StorageService _storage;
    private readonly CaseAnalysisService _analysis;
    private AnalysisCase? _selectedCase;

    public StorageViewModel(StorageService storage, CaseAnalysisService analysis)
    {
        _storage = storage;
        _analysis = analysis;
        RefreshCommand = new DelegateCommand(() => _ = LoadAsync());
        CleanCommand = new DelegateCommand(() => _ = CleanAsync(), () => SelectedCase is not null);
        _ = LoadAsync();
    }

    public ObservableCollection<AnalysisCase> Cases { get; } = new();
    public AnalysisCase? SelectedCase
    {
        get => _selectedCase;
        set { if (SetProperty(ref _selectedCase, value)) ((DelegateCommand)CleanCommand).RaiseCanExecuteChanged(); }
    }
    public string TotalSpace { get; private set; } = "计算中";
    public string ReleasableSpace { get; private set; } = "计算中";
    public string LogSpace { get; private set; } = "计算中";
    public string ExtractSpace { get; private set; } = "计算中";
    public string ReportSpace { get; private set; } = "计算中";
    public ICommand RefreshCommand { get; }
    public ICommand CleanCommand { get; }

    public async Task LoadAsync()
    {
        var summary = await _storage.GetSummaryAsync();
        var cases = await _analysis.ListCasesAsync();
        Cases.Clear();
        foreach (var item in cases) Cases.Add(item);
        TotalSpace = ViewModelFormatting.Size(summary.TotalBytes);
        ReleasableSpace = ViewModelFormatting.Size(summary.ReleasableBytes);
        LogSpace = ViewModelFormatting.Size(summary.LogBytes);
        ExtractSpace = ViewModelFormatting.Size(summary.ExtractBytes);
        ReportSpace = ViewModelFormatting.Size(summary.ReportBytes);
        OnPropertyChanged(nameof(TotalSpace));
        OnPropertyChanged(nameof(ReleasableSpace));
        OnPropertyChanged(nameof(LogSpace));
        OnPropertyChanged(nameof(ExtractSpace));
        OnPropertyChanged(nameof(ReportSpace));
    }

    private async Task CleanAsync()
    {
        if (SelectedCase is null) return;
        var message = $"确认清理案例“{SelectedCase.DisplayName}”的数据吗？\n\n"
            + $"原始日志：{SelectedCase.SourcePath}\n"
            + $"解压目录：{SelectedCase.ExtractPath}\n\n"
            + "上述原始日志和解压目录会被删除，报告会保留。";
        if (Wpf.MessageBox.Show(message, "确认清理", Wpf.MessageBoxButton.YesNo, Wpf.MessageBoxImage.Warning) != Wpf.MessageBoxResult.Yes) return;
        await _storage.CleanCaseDataAsync(SelectedCase.Id);
        await LoadAsync();
    }
}
