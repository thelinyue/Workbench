using System.Collections.ObjectModel;
using System.Windows.Input;
using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.App.ViewModels;

public sealed class PluginsViewModel : ViewModelBase
{
    private readonly PluginCatalog _catalog;

    public PluginsViewModel(PluginCatalog catalog)
    {
        _catalog = catalog;
        RefreshCommand = new DelegateCommand(() => _ = LoadAsync());
        _ = LoadAsync();
    }

    public ObservableCollection<PluginManifest> Items { get; } = new();
    public ObservableCollection<string> Issues { get; } = new();
    public ICommand RefreshCommand { get; }
    public bool ShowEmptyState => Items.Count == 0;

    private async Task LoadAsync()
    {
        var plugins = await _catalog.ScanAsync();
        Items.Clear();
        foreach (var plugin in plugins) Items.Add(plugin);
        Issues.Clear();
        foreach (var issue in _catalog.Issues) Issues.Add(issue);
        OnPropertyChanged(nameof(ShowEmptyState));
    }
}
