using HephaestusWorkbench.App.ViewModels;
using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class ExtensionCenterViewModelTests
{
    [Fact]
    public async Task InitializeAsync_LoadsDiscoveryAndFiltersInstalledAndUpdates()
    {
        var service = new FakeExtensionCenterService(CreateSnapshot());
        var viewModel = new ExtensionCenterViewModel(service, _ => { }, new WorkbenchLogger(CreateRoot()));

        await viewModel.InitializeAsync();

        Assert.True(viewModel.IsDiscoveryTabSelected);
        Assert.Equal(2, viewModel.VisibleItems.Count);
        viewModel.SelectInstalledTabCommand.Execute(null);
        Assert.True(viewModel.IsInstalledTabSelected);
        Assert.False(viewModel.IsDiscoveryTabSelected);
        Assert.Single(viewModel.VisibleItems);
        Assert.Equal("log-analyzer", viewModel.VisibleItems[0].Id);
        viewModel.SelectUpdatesTabCommand.Execute(null);
        Assert.True(viewModel.IsUpdatesTabSelected);
        Assert.Single(viewModel.VisibleItems);
        viewModel.SelectedTypeFilter = "Workspace";
        Assert.Empty(viewModel.VisibleItems);
    }

    [Fact]
    public async Task Discovery_ShowsCatalogListedExtensionWithoutCompatibleRelease()
    {
        var snapshot = new ExtensionCenterSnapshot
        {
            IsCatalogFromCache = false,
            Warning = null,
            Extensions =
            [
                Entry(
                    "future-tool",
                    "未来工具",
                    ExtensionKind.Workspace,
                    installedManifest: null,
                    availableRelease: null,
                    isCatalogListed: true,
                    hasCompatibleRelease: false,
                    isInstalledVersionCompatible: null)
            ]
        };
        var viewModel = new ExtensionCenterViewModel(
            new FakeExtensionCenterService(snapshot),
            _ => { },
            new WorkbenchLogger(CreateRoot()));

        await viewModel.InitializeAsync();

        var item = Assert.Single(viewModel.VisibleItems);
        Assert.Equal("当前宿主暂无兼容版本", item.StatusText);
        Assert.False(item.CanInstall);
        Assert.Equal(System.Windows.Visibility.Visible, item.InstallVisibility);
    }

    [Fact]
    public async Task HasEnabledAnalysisEngine_WhenInstalledAnalysisRequiresNewerHost_ReturnsFalse()
    {
        var manifest = Manifest(
            "future-analyzer",
            "未来日志分析",
            "3.0.0",
            ExtensionKind.Analysis,
            minHostVersion: "3.0.0");
        var snapshot = new ExtensionCenterSnapshot
        {
            IsCatalogFromCache = false,
            Warning = null,
            Extensions =
            [
                Entry(
                    manifest.Id,
                    manifest.Name,
                    manifest.Kind,
                    manifest,
                    availableRelease: null,
                    isCatalogListed: false,
                    hasCompatibleRelease: false,
                    isInstalledVersionCompatible: false,
                    enabled: true)
            ]
        };
        var viewModel = new ExtensionCenterViewModel(
            new FakeExtensionCenterService(snapshot),
            _ => { },
            new WorkbenchLogger(CreateRoot()));

        await viewModel.InitializeAsync();
        viewModel.SelectInstalledTabCommand.Execute(null);

        Assert.Equal("已安装版本不兼容", Assert.Single(viewModel.VisibleItems).StatusText);
        Assert.False(viewModel.HasEnabledAnalysisEngine);
    }

    [Fact]
    public void IdentityConflict_BlocksInstallAndUsesExplicitStatus()
    {
        var item = new ExtensionCenterItemViewModel(Entry(
            "log-analyzer",
            "日志分析",
            ExtensionKind.Analysis,
            Manifest("log-analyzer", "日志分析", "2.0.0", ExtensionKind.Analysis),
            availableRelease: null,
            isCatalogListed: true,
            hasCompatibleRelease: true,
            isInstalledVersionCompatible: true,
            hasUpdate: false,
            hasIdentityConflict: true));

        Assert.Equal("扩展身份冲突", item.StatusText);
        Assert.False(item.CanInstall);
        Assert.Equal(System.Windows.Visibility.Collapsed, item.InstallVisibility);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task OpenCommand_WhenInstalledWorkspaceCompatibilityIsNotConfirmed_DoesNotOpen(
        bool? isInstalledVersionCompatible)
    {
        var opened = new List<ExtensionManifest>();
        var viewModel = new ExtensionCenterViewModel(
            new FakeExtensionCenterService(CreateInstalledWorkspaceSnapshot(
                enabled: true,
                isInstalledVersionCompatible)),
            opened.Add,
            new WorkbenchLogger(CreateRoot()));
        await viewModel.InitializeAsync();
        var workspace = Assert.Single(viewModel.VisibleItems);

        Assert.False(workspace.CanOpen);
        Assert.False(viewModel.OpenCommand.CanExecute(workspace));

        viewModel.OpenCommand.Execute(workspace);

        Assert.Empty(opened);
    }

    [Fact]
    public async Task OpenCommand_OnlyOpensEnabledInstalledWorkspaceExtension()
    {
        var opened = new List<ExtensionManifest>();
        var service = new FakeExtensionCenterService(CreateSnapshot());
        var viewModel = new ExtensionCenterViewModel(service, opened.Add, new WorkbenchLogger(CreateRoot()));
        await viewModel.InitializeAsync();
        viewModel.SelectInstalledTabCommand.Execute(null);

        var analysis = Assert.Single(viewModel.VisibleItems);
        Assert.False(viewModel.OpenCommand.CanExecute(analysis));

        viewModel.SelectDiscoveryTabCommand.Execute(null);
        var workspace = viewModel.VisibleItems.Single(item => item.Id == "rule-editor");
        Assert.False(viewModel.OpenCommand.CanExecute(workspace));
    }

    [Fact]
    public async Task OpenAndToggleCommands_UseInstalledWorkspaceManifestAndV2Enablement()
    {
        var snapshot = CreateInstalledWorkspaceSnapshot(enabled: true);
        var workspaceManifest = snapshot.Extensions.Single().InstalledManifest!;
        var opened = new List<ExtensionManifest>();
        var service = new FakeExtensionCenterService(snapshot);
        var viewModel = new ExtensionCenterViewModel(service, opened.Add, new WorkbenchLogger(CreateRoot()));
        await viewModel.InitializeAsync();
        var workspaceItem = Assert.Single(viewModel.VisibleItems);

        Assert.True(viewModel.OpenCommand.CanExecute(workspaceItem));
        viewModel.OpenCommand.Execute(workspaceItem);
        viewModel.ToggleEnabledCommand.Execute(workspaceItem);
        await service.ToggleCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(workspaceManifest, Assert.Single(opened));
        Assert.Equal(("rule-editor", false), service.LastToggle);
    }

    [Fact]
    public async Task DisableSucceeded_WhenReloadFails_KeepsLocalItemDisabledAndCannotOpen()
    {
        var service = new FakeExtensionCenterService(CreateInstalledWorkspaceSnapshot(enabled: true))
        {
            FailLoadsAfterFirst = true
        };
        var viewModel = new ExtensionCenterViewModel(service, _ => { }, new WorkbenchLogger(CreateRoot()));
        await viewModel.InitializeAsync();
        var workspace = Assert.Single(viewModel.VisibleItems);

        viewModel.ToggleEnabledCommand.Execute(workspace);
        await service.ReloadAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !viewModel.IsBusy);

        Assert.False(workspace.Enabled);
        Assert.False(workspace.CanOpen);
        Assert.False(viewModel.OpenCommand.CanExecute(workspace));
        Assert.Contains("加载扩展中心失败", viewModel.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenCommand_WhenWorkspaceHostFails_ShowsChineseErrorAndWritesLog()
    {
        var root = CreateRoot();
        var logger = new WorkbenchLogger(root);
        var logs = new List<string>();
        logger.MessageWritten += (_, message) => logs.Add(message);
        var viewModel = new ExtensionCenterViewModel(
            new FakeExtensionCenterService(CreateInstalledWorkspaceSnapshot(enabled: true)),
            _ => throw new InvalidOperationException("入口文件缺失"),
            logger);
        await viewModel.InitializeAsync();
        var workspace = Assert.Single(viewModel.VisibleItems);

        var exception = Record.Exception(() => viewModel.OpenCommand.Execute(workspace));

        Assert.Null(exception);
        Assert.Contains("打开扩展“规则编辑器”失败", viewModel.Message, StringComparison.Ordinal);
        Assert.Contains(logs, message => message.Contains("打开扩展“规则编辑器”失败", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ItemAutomationNames_ContainExtensionNameAndActualAction()
    {
        var viewModel = new ExtensionCenterViewModel(
            new FakeExtensionCenterService(CreateInstalledWorkspaceSnapshot(enabled: true)),
            _ => { },
            new WorkbenchLogger(CreateRoot()));
        await viewModel.InitializeAsync();

        var item = Assert.Single(viewModel.VisibleItems);

        Assert.Equal("禁用扩展：规则编辑器", item.ToggleAutomationName);
        Assert.Equal("打开扩展：规则编辑器", item.OpenAutomationName);
        Assert.Equal("更新扩展：规则编辑器", item.InstallAutomationName);
    }

    [Fact]
    public async Task InstallCommand_InstallsSelectedCatalogReleaseAndReloads()
    {
        var service = new FakeExtensionCenterService(CreateSnapshot());
        var viewModel = new ExtensionCenterViewModel(service, _ => { }, new WorkbenchLogger(CreateRoot()));
        await viewModel.InitializeAsync();
        var workspace = viewModel.VisibleItems.Single(item => item.Id == "rule-editor");

        viewModel.InstallCommand.Execute(workspace);
        await service.InstallCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.ReloadAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("rule-editor", service.LastInstallRequest?.ExtensionId);
        Assert.Equal("2.0.0", service.LastInstallRequest?.Version);
        Assert.Equal(2, service.LoadCount);
    }

    [Fact]
    public async Task SetAllowPrerelease_UpdatesSubsequentRefreshAndInstallPolicy()
    {
        var service = new FakeExtensionCenterService(CreateSnapshot());
        var viewModel = new ExtensionCenterViewModel(service, _ => { }, new WorkbenchLogger(CreateRoot()));
        await viewModel.InitializeAsync(autoCheckUpdates: true, allowPrerelease: false);
        var workspace = viewModel.VisibleItems.Single(item => item.Id == "rule-editor");

        viewModel.SetAllowPrerelease(true);
        viewModel.RefreshCommand.Execute(null);
        await service.ReloadAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !viewModel.IsBusy);
        viewModel.InstallCommand.Execute(workspace);
        await service.InstallCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(service.LastRefreshAllowPrerelease);
        Assert.True(service.LastInstallAllowPrerelease);
    }

    private static ExtensionCenterSnapshot CreateSnapshot()
    {
        var analysisManifest = Manifest("log-analyzer", "日志分析", "2.0.0", ExtensionKind.Analysis);
        return new ExtensionCenterSnapshot
        {
            IsCatalogFromCache = false,
            Warning = null,
            Extensions =
            [
                Entry(
                    "log-analyzer",
                    "日志分析",
                    ExtensionKind.Analysis,
                    analysisManifest,
                    Release("2.1.0"),
                    isCatalogListed: true,
                    hasCompatibleRelease: true,
                    isInstalledVersionCompatible: true,
                    hasUpdate: true),
                Entry(
                    "rule-editor",
                    "规则编辑器",
                    ExtensionKind.Workspace,
                    installedManifest: null,
                    availableRelease: Release("2.0.0"),
                    isCatalogListed: true,
                    hasCompatibleRelease: true,
                    isInstalledVersionCompatible: null)
            ]
        };
    }

    private static ExtensionCenterSnapshot CreateInstalledWorkspaceSnapshot(
        bool enabled,
        bool? isInstalledVersionCompatible = true)
    {
        var manifest = Manifest("rule-editor", "规则编辑器", "2.0.0", ExtensionKind.Workspace);
        return new ExtensionCenterSnapshot
        {
            IsCatalogFromCache = false,
            Warning = null,
            Extensions =
            [
                Entry(
                    "rule-editor",
                    "规则编辑器",
                    ExtensionKind.Workspace,
                    manifest,
                    Release("2.1.0"),
                    isCatalogListed: true,
                    hasCompatibleRelease: true,
                    isInstalledVersionCompatible: isInstalledVersionCompatible,
                    enabled: enabled,
                    hasUpdate: true)
            ]
        };
    }

    private static ExtensionCenterEntry Entry(
        string id,
        string name,
        ExtensionKind kind,
        ExtensionManifest? installedManifest,
        ExtensionRelease? availableRelease,
        bool isCatalogListed,
        bool hasCompatibleRelease,
        bool? isInstalledVersionCompatible,
        bool enabled = true,
        bool hasUpdate = false,
        bool hasIdentityConflict = false)
        => new()
        {
            Id = id,
            Name = name,
            Description = kind == ExtensionKind.Analysis ? "综合日志分析" : "编辑分析规则",
            PublisherId = "thelinyue",
            Kind = kind,
            InstalledManifest = installedManifest,
            AvailableRelease = availableRelease,
            Enabled = enabled,
            IsCatalogListed = isCatalogListed,
            HasCompatibleRelease = hasCompatibleRelease,
            IsInstalledVersionCompatible = isInstalledVersionCompatible,
            HasIdentityConflict = hasIdentityConflict,
            HasUpdate = hasUpdate
        };

    private static ExtensionManifest Manifest(
        string id,
        string name,
        string version,
        ExtensionKind kind,
        string minHostVersion = "2.0.0")
        => new()
        {
            SchemaVersion = 2,
            Id = id,
            Name = name,
            Version = version,
            Kind = kind,
            PublisherId = "thelinyue",
            HostApiVersion = "1.0",
            MinHostVersion = minHostVersion,
            Runtime = new ExtensionRuntime
            {
                Kind = kind == ExtensionKind.Workspace ? ExtensionRuntimeKind.Web : ExtensionRuntimeKind.Process,
                Protocol = kind == ExtensionKind.Workspace ? "workspace-bridge-v1" : "analysis-process-v1",
                Entry = kind == ExtensionKind.Workspace ? "index.html" : "bin/analyzer.exe"
            },
            Capabilities = kind == ExtensionKind.Workspace ? ["workspace.page"] : ["analysis.engine"],
            Permissions = [],
            Dependencies = [],
            DirectoryPath = CreateRoot()
        };

    private static ExtensionRelease Release(string version)
        => new()
        {
            Version = version,
            MinHostVersion = "2.0.0",
            Url = "https://example.invalid/extension.zip",
            Size = 1,
            Sha256 = new string('a', 64),
            Signature = new ExtensionPackageSignature
            {
                KeyId = "test-key",
                Signature = Convert.ToBase64String(new byte[64])
            }
        };

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", "ExtensionCenterViewModel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("等待扩展中心状态更新超时。");
            await Task.Delay(10);
        }
    }

    private sealed class FakeExtensionCenterService(ExtensionCenterSnapshot snapshot) : IExtensionCenterService
    {
        public TaskCompletionSource InstallCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ToggleCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReloadAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ExtensionCenterInstallRequest? LastInstallRequest { get; private set; }
        public bool? LastRefreshAllowPrerelease { get; private set; }
        public bool? LastInstallAllowPrerelease { get; private set; }
        public (string Id, bool Enabled)? LastToggle { get; private set; }
        public bool FailLoadsAfterFirst { get; init; }
        public int LoadCount { get; private set; }

        public Task<ExtensionCenterSnapshot> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            if (LoadCount > 1)
            {
                ReloadAttempted.TrySetResult();
                if (FailLoadsAfterFirst)
                    throw new InvalidOperationException("模拟刷新失败");
            }

            return Task.FromResult(snapshot);
        }

        public Task<ExtensionCenterSnapshot> LoadAsync(
            bool autoCheckUpdates,
            CancellationToken cancellationToken = default)
            => LoadAsync(cancellationToken);

        public Task<ExtensionCenterSnapshot> LoadAsync(
            bool autoCheckUpdates,
            bool allowPrerelease,
            CancellationToken cancellationToken = default)
            => LoadAsync(cancellationToken);

        public Task<ExtensionCenterSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
            => LoadAsync(cancellationToken);

        public Task<ExtensionCenterSnapshot> RefreshAsync(
            bool allowPrerelease,
            CancellationToken cancellationToken = default)
        {
            LastRefreshAllowPrerelease = allowPrerelease;
            return LoadAsync(cancellationToken);
        }

        public Task<ExtensionInstallResult> InstallAsync(
            ExtensionCenterInstallRequest request,
            CancellationToken cancellationToken = default)
        {
            LastInstallRequest = request;
            InstallCompleted.TrySetResult();
            return Task.FromResult(new ExtensionInstallResult
            {
                Manifest = snapshot.Extensions.Single(item => item.Id == request.ExtensionId).InstalledManifest
                           ?? Manifest(request.ExtensionId, request.ExtensionId, request.Version ?? "2.0.0", ExtensionKind.Workspace),
                VersionDirectory = CreateRoot(),
                AlreadyInstalled = false
            });
        }

        public Task<ExtensionInstallResult> InstallAsync(
            ExtensionCenterInstallRequest request,
            bool allowPrerelease,
            CancellationToken cancellationToken = default)
        {
            LastInstallAllowPrerelease = allowPrerelease;
            return InstallAsync(request, cancellationToken);
        }

        public Task SetEnabledAsync(string extensionId, bool enabled, CancellationToken cancellationToken = default)
        {
            LastToggle = (extensionId, enabled);
            ToggleCompleted.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
