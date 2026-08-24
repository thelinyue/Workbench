using System.Reflection;
using System.Text.Json;
using HephaestusWorkbench.App.ViewModels;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

/// <summary>
/// 锁定扩展更新策略的用户级配置边界。
/// AutoCheckUpdates 只属于 appsettings.json，绝不借用 extensions.json 的扩展启用状态或更新通道。
/// </summary>
public sealed class ExtensionPolicySettingsTests
{
    [Fact]
    public async Task EnsureAppSettingsAsync_CreatesAndRepeatedlyReadsExtensionAutoCheckUpdatesDefault()
    {
        using var fixture = new SettingsFixture();

        var created = await fixture.Configuration.EnsureAppSettingsAsync();
        Assert.Equal(2, created.SchemaVersion);
        Assert.True(GetExtensionAutoCheckUpdates(created));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.Paths.AppSettingsFile));
        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(document.RootElement.GetProperty("extension").GetProperty("autoCheckUpdates").GetBoolean());

        var reloaded = await new WorkbenchConfigurationService(fixture.Paths).EnsureAppSettingsAsync();
        Assert.True(GetExtensionAutoCheckUpdates(reloaded));
    }

    [Fact]
    public async Task SettingsService_PersistsExtensionAutoCheckUpdatesWithoutChangingExtensionsConfiguration()
    {
        using var fixture = new SettingsFixture();
        var extensionSettings = new ExtensionSettingsStore(fixture.Paths);
        await extensionSettings.EnsureAsync();
        var extensionsBefore = await File.ReadAllTextAsync(fixture.Paths.ExtensionsConfigFile);

        await InvokeRequiredTask(fixture.Settings, "SetExtensionAutoCheckUpdatesAsync", false, CancellationToken.None);
        var autoCheckUpdates = await InvokeRequiredBoolTask(fixture.Settings, "GetExtensionAutoCheckUpdatesAsync", CancellationToken.None);

        Assert.False(autoCheckUpdates);
        Assert.False(GetExtensionAutoCheckUpdates(await fixture.Configuration.EnsureAppSettingsAsync()));
        Assert.Equal(extensionsBefore, await File.ReadAllTextAsync(fixture.Paths.ExtensionsConfigFile));
    }

    [Fact]
    public async Task SettingsViewModel_SavesExtensionAutoCheckUpdatesWithoutChangingExtensionsConfiguration()
    {
        using var fixture = new SettingsFixture();
        var extensionSettings = new ExtensionSettingsStore(fixture.Paths);
        await extensionSettings.EnsureAsync();
        var extensionsBefore = await File.ReadAllTextAsync(fixture.Paths.ExtensionsConfigFile);
        var viewModel = fixture.CreateViewModel();

        await viewModel.Initialization;
        SetRequiredBoolean(viewModel, "AutoCheckExtensionUpdates", false);
        viewModel.SaveCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.HasUnsavedChanges && viewModel.Message == "设置已保存。");

        Assert.False(GetRequiredBoolean(viewModel, "AutoCheckExtensionUpdates"));
        Assert.False(GetExtensionAutoCheckUpdates(await fixture.Configuration.EnsureAppSettingsAsync()));
        Assert.Equal(extensionsBefore, await File.ReadAllTextAsync(fixture.Paths.ExtensionsConfigFile));
    }

    [Fact]
    public void ExtensionCenterStartupAndManualRefresh_ExposeSeparatePolicyAwareContracts()
    {
        var startupLoad = typeof(IExtensionCenterService).GetMethod(
            "LoadAsync",
            [typeof(bool), typeof(CancellationToken)]);
        Assert.True(startupLoad is not null,
            "IExtensionCenterService 必须通过 LoadAsync(bool autoCheckUpdates, CancellationToken) 区分启动刷新策略。");

        var manualRefresh = typeof(IExtensionCenterService).GetMethod("RefreshAsync", [typeof(CancellationToken)]);
        Assert.True(manualRefresh is not null,
            "IExtensionCenterService 必须提供 RefreshAsync(CancellationToken)，使用户手动刷新始终走联网路径。");

        var viewModelStartup = typeof(ExtensionCenterViewModel).GetMethod(
            "InitializeAsync",
            [typeof(bool), typeof(CancellationToken)]);
        Assert.True(viewModelStartup is not null,
            "ExtensionCenterViewModel 必须接收启动时的 AutoCheckUpdates 策略，而不是自行无条件联网。");

        var mainViewModelSource = File.ReadAllText(SourceFile("src", "HephaestusWorkbench.App", "ViewModels", "MainViewModel.cs"));
        Assert.Contains("AutoCheckExtensionUpdates", mainViewModelSource, StringComparison.Ordinal);
        Assert.Contains("Extensions.InitializeAsync", mainViewModelSource, StringComparison.Ordinal);
    }

    private static bool GetExtensionAutoCheckUpdates(AppSettingsConfig settings)
    {
        var extensionProperty = typeof(AppSettingsConfig).GetProperty("Extension", BindingFlags.Instance | BindingFlags.Public);
        Assert.True(extensionProperty is not null, "AppSettingsConfig 必须公开 Extension 更新策略配置。");
        var extension = extensionProperty!.GetValue(settings);
        Assert.NotNull(extension);
        var autoCheckProperty = extension!.GetType().GetProperty("AutoCheckUpdates", BindingFlags.Instance | BindingFlags.Public);
        Assert.True(autoCheckProperty is not null, "AppSettingsConfig.Extension 必须公开 AutoCheckUpdates。");
        return Assert.IsType<bool>(autoCheckProperty!.GetValue(extension));
    }

    private static async Task InvokeRequiredTask(object target, string methodName, params object[] arguments)
    {
        var result = InvokeRequiredMethod(target, methodName, arguments);
        await Assert.IsAssignableFrom<Task>(result);
    }

    private static async Task<bool> InvokeRequiredBoolTask(object target, string methodName, params object[] arguments)
    {
        var result = InvokeRequiredMethod(target, methodName, arguments);
        var task = Assert.IsAssignableFrom<Task<bool>>(result);
        return await task;
    }

    private static object? InvokeRequiredMethod(object target, string methodName, object[] arguments)
    {
        var argumentTypes = arguments.Select(argument => argument.GetType()).ToArray();
        var method = target.GetType().GetMethod(methodName, argumentTypes);
        Assert.True(method is not null, $"{target.GetType().Name} 必须提供 {methodName}。 ");
        return method!.Invoke(target, arguments);
    }

    private static bool GetRequiredBoolean(SettingsViewModel viewModel, string propertyName)
    {
        var property = typeof(SettingsViewModel).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        Assert.True(property is not null, $"SettingsViewModel 必须公开 {propertyName}。 ");
        return Assert.IsType<bool>(property!.GetValue(viewModel));
    }

    private static void SetRequiredBoolean(SettingsViewModel viewModel, string propertyName, bool value)
    {
        var property = typeof(SettingsViewModel).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        Assert.True(property is not null, $"SettingsViewModel 必须公开 {propertyName}。 ");
        property!.SetValue(viewModel, value);
    }

    private static string SourceFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HephaestusWorkbench.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. segments]);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(10);
        Assert.True(condition(), "设置未在预期时间内保存完成。");
    }

    private sealed class SettingsFixture : IDisposable
    {
        public SettingsFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
            Paths = new DataPaths(Root);
            Configuration = new WorkbenchConfigurationService(Paths);
            Logger = new WorkbenchLogger(Root);
            Inbox = new LogInboxService(new LogFileParser(), new ArchiveValidator(), Configuration, Logger, Paths.InboxDirectory);
            Settings = new SettingsService(Configuration, Paths.InboxDirectory);
        }

        public string Root { get; }
        public DataPaths Paths { get; }
        public WorkbenchConfigurationService Configuration { get; }
        public WorkbenchLogger Logger { get; }
        public LogInboxService Inbox { get; }
        public SettingsService Settings { get; }

        public SettingsViewModel CreateViewModel()
            => new(Settings, Inbox, _ => null);

        public void Dispose()
        {
            Inbox.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}