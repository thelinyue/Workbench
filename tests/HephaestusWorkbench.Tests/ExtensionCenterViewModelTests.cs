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

        Assert.Equal(2, viewModel.VisibleItems.Count);
        viewModel.SelectInstalledTabCommand.Execute(null);
        Assert.Single(viewModel.VisibleItems);
        Assert.Equal("log-analyzer", viewModel.VisibleItems[0].Id);
        viewModel.SelectUpdatesTabCommand.Execute(null);
        Assert.Single(viewModel.VisibleItems);
        viewModel.SelectedTypeFilter = "Workspace";
        Assert.Empty(viewModel.VisibleItems);
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
        var source = CreateSnapshot();
        var workspaceManifest = Manifest("rule-editor", "规则编辑器", "2.0.0", ExtensionKind.Workspace);
        var workspace = source.Extensions.Single(item => item.Id == "rule-editor");
        var snapshot = new ExtensionCenterSnapshot
        {
            IsCatalogFromCache = source.IsCatalogFromCache,
            Warning = source.Warning,
            Extensions = source.Extensions.Select(item => item.Id == workspace.Id
                ? new ExtensionCenterEntry
                {
                    Id = item.Id,
                    Name = item.Name,
                    Description = item.Description,
                    PublisherId = item.PublisherId,
                    Kind = item.Kind,
                    InstalledManifest = workspaceManifest,
                    AvailableRelease = item.AvailableRelease,
                    Enabled = true,
                    IsCompatible = true,
                    HasUpdate = false
                }
                : item).ToArray()
        };
        var opened = new List<ExtensionManifest>();
        var service = new FakeExtensionCenterService(snapshot);
        var viewModel = new ExtensionCenterViewModel(service, opened.Add, new WorkbenchLogger(CreateRoot()));
        await viewModel.InitializeAsync();
        viewModel.SelectInstalledTabCommand.Execute(null);
        var workspaceItem = viewModel.VisibleItems.Single(item => item.Id == "rule-editor");

        Assert.True(viewModel.OpenCommand.CanExecute(workspaceItem));
        viewModel.OpenCommand.Execute(workspaceItem);
        viewModel.ToggleEnabledCommand.Execute(workspaceItem);
        await service.ToggleCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(workspaceManifest, Assert.Single(opened));
        Assert.Equal(("rule-editor", false), service.LastToggle);
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

        Assert.Equal("rule-editor", service.LastInstallRequest?.ExtensionId);
        Assert.Equal("2.0.0", service.LastInstallRequest?.Version);
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
                new ExtensionCenterEntry
                {
                    Id = "log-analyzer",
                    Name = "日志分析",
                    Description = "综合日志分析",
                    PublisherId = "thelinyue",
                    Kind = ExtensionKind.Analysis,
                    InstalledManifest = analysisManifest,
                    AvailableRelease = Release("2.1.0"),
                    Enabled = true,
                    IsCompatible = true,
                    HasUpdate = true
                },
                new ExtensionCenterEntry
                {
                    Id = "rule-editor",
                    Name = "规则编辑器",
                    Description = "编辑分析规则",
                    PublisherId = "thelinyue",
                    Kind = ExtensionKind.Workspace,
                    InstalledManifest = null,
                    AvailableRelease = Release("2.0.0"),
                    Enabled = true,
                    IsCompatible = true,
                    HasUpdate = false
                }
            ]
        };
    }

    private static ExtensionManifest Manifest(string id, string name, string version, ExtensionKind kind)
        => new()
        {
            SchemaVersion = 2,
            Id = id,
            Name = name,
            Version = version,
            Kind = kind,
            PublisherId = "thelinyue",
            HostApiVersion = "1.0",
            MinHostVersion = "2.0.0",
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
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", "ExtensionCenterViewModel");
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class FakeExtensionCenterService(ExtensionCenterSnapshot snapshot) : IExtensionCenterService
    {
        public TaskCompletionSource InstallCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ExtensionCenterInstallRequest? LastInstallRequest { get; private set; }
        public TaskCompletionSource ToggleCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public (string Id, bool Enabled)? LastToggle { get; private set; }

        public Task<ExtensionCenterSnapshot> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);

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

        public Task SetEnabledAsync(string extensionId, bool enabled, CancellationToken cancellationToken = default)
        {
            LastToggle = (extensionId, enabled);
            ToggleCompleted.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
