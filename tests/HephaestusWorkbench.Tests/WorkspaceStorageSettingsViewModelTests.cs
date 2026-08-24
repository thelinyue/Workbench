using System.Reflection;
using System.Windows.Input;
using HephaestusWorkbench.App;
using HephaestusWorkbench.App.ViewModels;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

/// <summary>
/// 锁定设置页中工作空间和存储切换的可观察行为。
/// 数据根切换只登记下一次启动使用的 bootstrap 指针；当前运行中的工作区绝不移动或迁移。
/// </summary>
public sealed class WorkspaceStorageSettingsViewModelTests
{
    [Fact]
    public async Task CurrentDataRoot_IsExposedAndWorkspaceDirectoryCanBeOpened()
    {
        using var fixture = await WorkspaceStorageSettingsFixture.CreateAsync();
        var openedPath = string.Empty;
        var directoryOpen = new DirectoryOpenService(
            fixture.Logger,
            startInfo => openedPath = startInfo.FileName);
        var viewModel = fixture.CreateViewModel(directoryOpen);

        await viewModel.Initialization;

        Assert.Equal(fixture.Paths.Root, GetRequiredString(viewModel, "CurrentDataRoot"));
        GetRequiredCommand(viewModel, "OpenWorkspaceDirectoryCommand").Execute(null);
        Assert.Equal(fixture.Paths.Root, openedPath);
    }

    [Fact]
    public async Task OpeningWorkspaceDirectoryFailure_ShowsChineseError()
    {
        using var fixture = await WorkspaceStorageSettingsFixture.CreateAsync();
        var directoryOpen = new DirectoryOpenService(
            fixture.Logger,
            _ => throw new InvalidOperationException("模拟资源管理器启动失败"));
        var viewModel = fixture.CreateViewModel(directoryOpen);

        await viewModel.Initialization;

        GetRequiredCommand(viewModel, "OpenWorkspaceDirectoryCommand").Execute(null);

        Assert.True(GetRequiredBoolean(viewModel, "StorageFeedbackIsError"));
        Assert.Contains("无法打开工作空间目录", GetRequiredString(viewModel, "StorageFeedback"));
    }

    [Fact]
    public async Task RegisterDataRootChange_RejectsNonEmptyDirectoryWithoutChangingBootstrapOrData()
    {
        using var fixture = await WorkspaceStorageSettingsFixture.CreateAsync();
        var candidate = Path.Combine(fixture.Root, "NonEmptyTarget");
        Directory.CreateDirectory(candidate);
        var retainedFile = Path.Combine(candidate, "user-file.txt");
        await File.WriteAllTextAsync(retainedFile, "不得修改");
        var currentDataFile = Path.Combine(fixture.Paths.Root, "keep-current-data.txt");
        await File.WriteAllTextAsync(currentDataFile, "当前数据保持不变");
        var viewModel = fixture.CreateViewModel();

        await viewModel.Initialization;
        SetRequiredString(viewModel, "CandidateDataRoot", candidate);
        GetRequiredCommand(viewModel, "RegisterDataRootChangeCommand").Execute(null);
        await WaitUntilAsync(() => !string.IsNullOrWhiteSpace(GetRequiredString(viewModel, "StorageFeedback")));

        Assert.True(GetRequiredBoolean(viewModel, "StorageFeedbackIsError"));
        Assert.Contains("目录必须为空", GetRequiredString(viewModel, "StorageFeedback"));
        var bootstrap = await fixture.BootstrapStore.ReadAsync();
        Assert.Equal(BootstrapReadStatus.Ready, bootstrap.Status);
        Assert.Equal(fixture.Paths.Root, bootstrap.DataRoot);
        Assert.Equal("不得修改", await File.ReadAllTextAsync(retainedFile));
        Assert.Equal("当前数据保持不变", await File.ReadAllTextAsync(currentDataFile));
        Assert.False(GetRequiredCommand(viewModel, "RestartApplicationCommand").CanExecute(null));
    }

    [Fact]
    public async Task RegisterDataRootChange_OnlyWritesNextStartupBootstrapAndRequiresRestart()
    {
        using var fixture = await WorkspaceStorageSettingsFixture.CreateAsync();
        var candidate = Path.Combine(fixture.Root, "EmptyTarget");
        Directory.CreateDirectory(candidate);
        var viewModel = fixture.CreateViewModel();

        await viewModel.Initialization;
        SetRequiredString(viewModel, "CandidateDataRoot", candidate);
        GetRequiredCommand(viewModel, "RegisterDataRootChangeCommand").Execute(null);
        await WaitUntilAsync(() => GetRequiredString(viewModel, "StorageFeedback").Contains("重启后生效", StringComparison.Ordinal));

        var bootstrap = await fixture.BootstrapStore.ReadAsync();
        Assert.Equal(BootstrapReadStatus.Ready, bootstrap.Status);
        Assert.Equal(Path.GetFullPath(candidate), bootstrap.DataRoot);
        Assert.Equal(fixture.Paths.Root, GetRequiredString(viewModel, "CurrentDataRoot"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(candidate));
        Assert.True(GetRequiredCommand(viewModel, "RestartApplicationCommand").CanExecute(null));
    }

    [Fact]
    public async Task RestartApplication_IsUnavailableBeforeRegistration_AndDoesNotCloseCurrentProcessWhenReplacementFails()
    {
        using var fixture = await WorkspaceStorageSettingsFixture.CreateAsync(
            startReplacementProcess: () => "无法启动新的工作台进程。",
            shutdownCurrentProcess: () => throw new InvalidOperationException("启动失败时不应关闭当前进程"));
        var candidate = Path.Combine(fixture.Root, "EmptyTarget");
        Directory.CreateDirectory(candidate);
        var viewModel = fixture.CreateViewModel();

        await viewModel.Initialization;
        var restartCommand = GetRequiredCommand(viewModel, "RestartApplicationCommand");
        Assert.False(restartCommand.CanExecute(null));

        SetRequiredString(viewModel, "CandidateDataRoot", candidate);
        GetRequiredCommand(viewModel, "RegisterDataRootChangeCommand").Execute(null);
        await WaitUntilAsync(() => restartCommand.CanExecute(null));

        restartCommand.Execute(null);

        Assert.True(GetRequiredBoolean(viewModel, "StorageFeedbackIsError"));
        Assert.Contains("重新启动失败", GetRequiredString(viewModel, "StorageFeedback"));
    }

    private static SettingsViewModel CreateSettingsViewModel(
        SettingsService settings,
        LogInboxService inbox,
        Func<string, string?> applyTheme,
        DataPaths paths,
        BootstrapConfigurationStore bootstrapStore,
        DirectoryOpenService directoryOpen,
        Func<string?> startReplacementProcess,
        Action shutdownCurrentProcess)
    {
        var constructor = typeof(SettingsViewModel).GetConstructor(
        [
            typeof(SettingsService),
            typeof(LogInboxService),
            typeof(Func<string, string?>),
            typeof(Action<SshTerminalPreferences>),
            typeof(DataPaths),
            typeof(BootstrapConfigurationStore),
            typeof(DirectoryOpenService),
            typeof(Func<string?>),
            typeof(Action)
        ]);
        Assert.True(constructor is not null,
            "SettingsViewModel 应提供可注入工作空间目录、bootstrap、目录打开和重启依赖的构造函数。");

        return (SettingsViewModel)constructor!.Invoke(
        [
            settings,
            inbox,
            applyTheme,
            null,
            paths,
            bootstrapStore,
            directoryOpen,
            startReplacementProcess,
            shutdownCurrentProcess
        ]);
    }

    private static ICommand GetRequiredCommand(SettingsViewModel viewModel, string name)
    {
        var property = typeof(SettingsViewModel).GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.True(property is not null, $"SettingsViewModel 应公开 {name}。 ");
        return Assert.IsAssignableFrom<ICommand>(property!.GetValue(viewModel));
    }

    private static string GetRequiredString(SettingsViewModel viewModel, string name)
    {
        var property = typeof(SettingsViewModel).GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.True(property is not null, $"SettingsViewModel 应公开 {name}。 ");
        return Assert.IsType<string>(property!.GetValue(viewModel));
    }

    private static bool GetRequiredBoolean(SettingsViewModel viewModel, string name)
    {
        var property = typeof(SettingsViewModel).GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.True(property is not null, $"SettingsViewModel 应公开 {name}。 ");
        return Assert.IsType<bool>(property!.GetValue(viewModel));
    }

    private static void SetRequiredString(SettingsViewModel viewModel, string name, string value)
    {
        var property = typeof(SettingsViewModel).GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.True(property is not null, $"SettingsViewModel 应公开 {name}。 ");
        property!.SetValue(viewModel, value);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(10);
        Assert.True(condition(), "设置页存储切换操作未在预期时间内完成。");
    }

    private sealed class WorkspaceStorageSettingsFixture : IDisposable
    {
        private WorkspaceStorageSettingsFixture(
            string root,
            DataPaths paths,
            WorkbenchLogger logger,
            BootstrapConfigurationStore bootstrapStore,
            SettingsService settings,
            LogInboxService inbox,
            Func<string?> startReplacementProcess,
            Action shutdownCurrentProcess)
        {
            Root = root;
            Paths = paths;
            Logger = logger;
            BootstrapStore = bootstrapStore;
            Settings = settings;
            Inbox = inbox;
            StartReplacementProcess = startReplacementProcess;
            ShutdownCurrentProcess = shutdownCurrentProcess;
        }

        public string Root { get; }
        public DataPaths Paths { get; }
        public WorkbenchLogger Logger { get; }
        public BootstrapConfigurationStore BootstrapStore { get; }
        public SettingsService Settings { get; }
        public LogInboxService Inbox { get; }
        public Func<string?> StartReplacementProcess { get; }
        public Action ShutdownCurrentProcess { get; }

        public static async Task<WorkspaceStorageSettingsFixture> CreateAsync(
            Func<string?>? startReplacementProcess = null,
            Action? shutdownCurrentProcess = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", "WorkspaceStorageSettings", Guid.NewGuid().ToString("N"));
            var paths = new DataPaths(Path.Combine(root, "CurrentWorkspace"));
            paths.EnsureCreated();
            var logger = new WorkbenchLogger(paths.Root);
            var configuration = new WorkbenchConfigurationService(paths);
            var inbox = new LogInboxService(new LogFileParser(), new ArchiveValidator(), configuration, logger, paths.InboxDirectory);
            var settings = new SettingsService(configuration, paths.InboxDirectory);
            var bootstrapStore = new BootstrapConfigurationStore(Path.Combine(root, "bootstrap", "bootstrap.json"));
            await bootstrapStore.WriteAsync(paths.Root);
            return new WorkspaceStorageSettingsFixture(
                root,
                paths,
                logger,
                bootstrapStore,
                settings,
                inbox,
                startReplacementProcess ?? (() => null),
                shutdownCurrentProcess ?? (() => { }));
        }

        public SettingsViewModel CreateViewModel(DirectoryOpenService? directoryOpen = null)
            => CreateSettingsViewModel(
                Settings,
                Inbox,
                _ => null,
                Paths,
                BootstrapStore,
                directoryOpen ?? new DirectoryOpenService(Logger, _ => { }),
                StartReplacementProcess,
                ShutdownCurrentProcess);

        public void Dispose()
        {
            Inbox.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}


