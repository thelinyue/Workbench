using HephaestusWorkbench.App.ViewModels;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

/// <summary>
/// 验证首次向导和设置页的目录编辑行为，确保界面优化没有改变目录配置约束。
/// </summary>
public sealed class DirectoryConfigurationViewModelTests
{
    [Fact]
    public void FirstRunDirectoryCommands_ValidateAddDuplicateAndKeepOneDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var viewModel = new FirstRunWizardViewModel(
            root,
            (_, _, _) => Task.CompletedTask,
            () => { },
            () => { },
            () => { });

        Assert.False(viewModel.RemoveMonitorCommand.CanExecute(null));
        var changedDataPath = Path.Combine(root, "Workspace");
        viewModel.DataPath = changedDataPath;
        Assert.Equal(Path.Combine(changedDataPath, "Inbox"), viewModel.MonitorPaths[0]);
        Assert.Equal(viewModel.MonitorPaths[0], viewModel.SelectedMonitorPath);

        viewModel.AddMonitorCommand.Execute(null);
        Assert.Equal("请输入要监控的目录路径。", viewModel.DirectoryFeedback);
        Assert.True(viewModel.DirectoryFeedbackIsError);

        viewModel.NewMonitorPath = "bad\0path";
        viewModel.AddMonitorCommand.Execute(null);
        Assert.Equal("目录路径无效，请检查路径后重试。", viewModel.DirectoryFeedback);

        var additionalPath = Path.Combine(root, "Logs");
        viewModel.NewMonitorPath = additionalPath;
        viewModel.AddMonitorCommand.Execute(null);
        Assert.Equal(2, viewModel.MonitorPaths.Count);
        Assert.Equal(Path.GetFullPath(additionalPath), viewModel.SelectedMonitorPath);
        Assert.True(viewModel.RemoveMonitorCommand.CanExecute(null));

        viewModel.NewMonitorPath = additionalPath;
        viewModel.AddMonitorCommand.Execute(null);
        Assert.Equal("该目录已经添加，无需重复添加。", viewModel.DirectoryFeedback);
        Assert.Equal(2, viewModel.MonitorPaths.Count);

        viewModel.RemoveMonitorCommand.Execute(null);
        Assert.Single(viewModel.MonitorPaths);
        Assert.False(viewModel.RemoveMonitorCommand.CanExecute(null));
        Assert.Equal("至少保留一个目录", viewModel.RemoveMonitorHint);
    }

    [Fact]
    public async Task SettingsDirectoryCommands_UseTheSameValidationAndSelectionBehavior()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new MemorySettingsStore();
        var logger = new WorkbenchLogger(root);
        using var inbox = new LogInboxService(new LogFileParser(), new ArchiveValidator(), store, logger, root);
        var settings = new SettingsService(store, root);
        var viewModel = new SettingsViewModel(settings, inbox, () => 0, _ => null);

        await WaitForAsync(() => viewModel.WatchDirectories.Count == 1);
        Assert.False(viewModel.RemoveWatchDirectoryCommand.CanExecute(null));

        viewModel.NewWatchDirectory = Path.Combine(root, "Downloads");
        viewModel.AddWatchDirectoryCommand.Execute(null);
        Assert.Equal(2, viewModel.WatchDirectories.Count);
        Assert.True(viewModel.RemoveWatchDirectoryCommand.CanExecute(null));

        viewModel.NewWatchDirectory = Path.Combine(root, "Downloads");
        viewModel.AddWatchDirectoryCommand.Execute(null);
        Assert.Equal("该目录已经添加，无需重复添加。", viewModel.DirectoryFeedback);

        viewModel.RemoveWatchDirectoryCommand.Execute(null);
        Assert.Single(viewModel.WatchDirectories);
        Assert.False(viewModel.RemoveWatchDirectoryCommand.CanExecute(null));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 50 && !condition(); attempt++) await Task.Delay(10);
        Assert.True(condition(), "设置页目录未在预期时间内加载完成。");
    }

    private sealed class MemorySettingsStore : HephaestusWorkbench.Core.Repositories.ISettingsStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(_values.GetValueOrDefault(key));

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }
}
