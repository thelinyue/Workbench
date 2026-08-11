using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Services;
using Wpf = System.Windows;

namespace HephaestusWorkbench.App.ViewModels;

public sealed record PluginFilterOption(string? Id, string Name);

/// <summary>报告库的搜索、筛选和操作模型。</summary>
public sealed class ReportsViewModel : ViewModelBase, IDisposable
{
    private readonly ReportService _reports;
    private readonly Func<ReportSummary, Task> _openReport;
    private readonly Action<string> _openCase;
    private readonly Func<ReportSummary, Task> _deleteReport;
    private CancellationTokenSource? _searchCancellation;
    private string _keyword = string.Empty;
    private string _deviceId = string.Empty;
    private PluginFilterOption? _selectedPlugin;
    private DateTime? _startDate;
    private DateTime? _endDate;
    private string _message = string.Empty;

    public ReportsViewModel(ReportService reports, Func<ReportSummary, Task> openReport, Action<string> openCase, Func<ReportSummary, Task> deleteReport)
    {
        _reports = reports;
        _openReport = openReport;
        _openCase = openCase;
        _deleteReport = deleteReport;
        RefreshCommand = new DelegateCommand(() => _ = LoadAsync());
        OpenCommand = new DelegateCommand(parameter => { if (parameter is ReportSummary item) _ = _openReport(item); });
        OpenCaseCommand = new DelegateCommand(parameter => { if (parameter is ReportSummary item) _openCase(item.CaseId); });
        OpenFolderCommand = new DelegateCommand(parameter => { if (parameter is ReportSummary item) OpenFolder(item); });
        DeleteCommand = new DelegateCommand(parameter => { if (parameter is ReportSummary item) _ = DeleteAsync(item); });
    }

    public ObservableCollection<ReportSummary> Items { get; } = new();
    public ObservableCollection<PluginFilterOption> PluginOptions { get; } = new() { new(null, "全部插件") };
    public ICommand RefreshCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand OpenCaseCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand DeleteCommand { get; }
    public bool ShowEmptyState => Items.Count == 0;
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public string Keyword { get => _keyword; set { if (SetProperty(ref _keyword, value)) ScheduleLoad(); } }
    public string DeviceId { get => _deviceId; set { if (SetProperty(ref _deviceId, value)) ScheduleLoad(); } }
    public PluginFilterOption? SelectedPlugin { get => _selectedPlugin; set { if (SetProperty(ref _selectedPlugin, value)) ScheduleLoad(); } }
    public DateTime? StartDate { get => _startDate; set { if (SetProperty(ref _startDate, value)) ScheduleLoad(); } }
    public DateTime? EndDate { get => _endDate; set { if (SetProperty(ref _endDate, value)) ScheduleLoad(); } }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (StartDate is not null && EndDate is not null && StartDate > EndDate)
            {
                Message = "开始日期不能晚于结束日期。";
                return;
            }
            var items = await _reports.ListAsync(new ReportQuery(Keyword, DeviceId, SelectedPlugin?.Id, StartDate, EndDate), cancellationToken);
            Items.Clear();
            foreach (var item in items) Items.Add(item);
            if (PluginOptions.Count == 1 && SelectedPlugin is null)
            {
                var all = await _reports.ListAsync(new ReportQuery(), cancellationToken);
                foreach (var plugin in all.Where(x => !string.IsNullOrWhiteSpace(x.PluginId)).GroupBy(x => x.PluginId).Select(x => x.First()))
                    PluginOptions.Add(new PluginFilterOption(plugin.PluginId, plugin.PluginName));
                SelectedPlugin = PluginOptions[0];
            }
            Message = string.Empty;
            OnPropertyChanged(nameof(ShowEmptyState));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Message = $"加载报告失败：{ex.Message}"; }
    }

    private void ScheduleLoad()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        _ = DelayLoadAsync(_searchCancellation.Token);
    }

    private async Task DelayLoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(250, cancellationToken);
            await LoadAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
    }

    private static void OpenFolder(ReportSummary report)
    {
        try
        {
            if (!Directory.Exists(report.Path))
            {
                Wpf.MessageBox.Show("报告目录不存在。", "无法打开位置", Wpf.MessageBoxButton.OK, Wpf.MessageBoxImage.Information);
                return;
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{report.ReportFile}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Wpf.MessageBox.Show($"打开报告位置失败：{ex.Message}", "无法打开位置", Wpf.MessageBoxButton.OK, Wpf.MessageBoxImage.Error);
        }
    }

    private async Task DeleteAsync(ReportSummary report)
    {
        await _deleteReport(report);
        await LoadAsync();
    }

    public void Dispose()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
    }
}
