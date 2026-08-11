using System.Collections.ObjectModel;
using Wpf = System.Windows;
using System.Windows.Input;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.App.ViewModels;

public sealed class InboxViewModel : ViewModelBase
{
    private readonly LogInboxService _inbox;
    private readonly CaseAnalysisService _analysis;
    private LogInboxItem? _selectedItem;

    public InboxViewModel(LogInboxService inbox, CaseAnalysisService analysis)
    {
        _inbox = inbox;
        _analysis = analysis;
        RefreshCommand = new DelegateCommand(() => _ = LoadAsync(), () => IsConfigured);
        StartCommand = new DelegateCommand(() => _ = StartAsync(), () => SelectedItem is { IsValidArchive: true });
        DeleteCommand = new DelegateCommand(() => _ = DeleteAsync(), () => SelectedItem is not null);
        _inbox.ItemsChanged += OnItemsChanged;
        _inbox.ConfigurationChanged += OnConfigurationChanged;
        _ = LoadAsync();
    }

    public ObservableCollection<LogInboxItem> Items { get; } = new();
    public bool IsConfigured => _inbox.IsConfigured;
    public bool ShowEmptyState => Items.Count == 0;
    public string EmptyStateMessage => IsConfigured
        ? (_inbox.IsUsingDefaultDirectory
            ? "默认收件目录当前没有可识别的日志 .tgz 文件；可在“设置”中改用其他目录。"
            : "当前目录没有可识别的日志 .tgz 文件。")
        : "尚未设置日志收件目录，请先到“设置”保存目录。";

    public LogInboxItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value)) return;
            ((DelegateCommand)StartCommand).RaiseCanExecuteChanged();
            ((DelegateCommand)DeleteCommand).RaiseCanExecuteChanged();
        }
    }
    public ICommand RefreshCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand DeleteCommand { get; }

    public async Task LoadAsync()
    {
        if (!IsConfigured)
        {
            ApplyItems(Array.Empty<LogInboxItem>());
            return;
        }

        var items = await _inbox.RefreshAsync();
        ApplyItems(items);
    }

    private void OnItemsChanged(object? sender, EventArgs e) => RunOnUi(ApplyCurrentItems);

    private void OnConfigurationChanged(object? sender, EventArgs e)
        => RunOnUi(() =>
        {
            OnPropertyChanged(nameof(IsConfigured));
            OnPropertyChanged(nameof(EmptyStateMessage));
            RaiseCommands();
            ApplyCurrentItems();
        });

    private void ApplyCurrentItems() => ApplyItems(_inbox.Items);

    private void ApplyItems(IReadOnlyList<LogInboxItem> items)
    {
        SelectedItem = null;
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(EmptyStateMessage));
        RaiseCommands();
    }

    private void RunOnUi(Action action)
    {
        var dispatcher = Wpf.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else _ = dispatcher.InvokeAsync(action);
    }

    private void RaiseCommands()
    {
        ((DelegateCommand)RefreshCommand).RaiseCanExecuteChanged();
        ((DelegateCommand)StartCommand).RaiseCanExecuteChanged();
        ((DelegateCommand)DeleteCommand).RaiseCanExecuteChanged();
    }

    private async Task StartAsync()
    {
        if (SelectedItem is null) return;
        await _analysis.StartAsync(SelectedItem);
        await LoadAsync();
    }

    private async Task DeleteAsync()
    {
        if (SelectedItem is null) return;
        if (Wpf.MessageBox.Show($"确认删除日志文件“{SelectedItem.FileName}”吗？此操作不可恢复。", "确认删除", Wpf.MessageBoxButton.YesNo, Wpf.MessageBoxImage.Warning) != Wpf.MessageBoxResult.Yes) return;
        await _inbox.DeleteAsync(SelectedItem);
    }
}
