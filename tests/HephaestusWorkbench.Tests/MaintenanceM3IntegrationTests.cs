using HephaestusWorkbench.App.Ssh;
using HephaestusWorkbench.App.ViewModels;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Core.Services;

namespace HephaestusWorkbench.Tests;

public sealed class MaintenanceM3IntegrationTests
{
    [Fact]
    public async Task SshTerminalViewModel_OpensMaintenanceWorkspaceForTheSelectedSavedDevice()
    {
        var devices = new FakeDeviceRepository();
        var device = Device();
        await devices.UpsertAsync(device);
        var opened = new List<SshDevice>();
        var viewModel = new SshTerminalViewModel(
            new FakeTerminalService(),
            devices,
            new FakeHostKeyRepository(),
            new FakeHistoryRepository(),
            new FakeCredentialStore(),
            new FakeConfirmationService(),
            new AppSettingsConfig(),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            opened.Add);
        await viewModel.InitializeAsync();

        Assert.True(viewModel.OpenMaintenanceCommand.CanExecute(device));
        viewModel.OpenMaintenanceCommand.Execute(device);

        Assert.Same(device, Assert.Single(opened));
        viewModel.Dispose();
    }

    [Fact]
    public void AppComposition_RecoversMaintenanceOperationsAndWiresExecutor()
    {
        var source = File.ReadAllText(Path.Combine(FindAppDirectory(), "App.xaml.cs"));

        Assert.Contains("SqliteMaintenanceOperationRepository", source, StringComparison.Ordinal);
        Assert.Contains("MaintenanceOperations.RecoverInterruptedAsync", source, StringComparison.Ordinal);
        Assert.Contains("SshNetCommandExecutionService", source, StringComparison.Ordinal);
        Assert.Contains("LinuxMaintenanceDiscoveryService", source, StringComparison.Ordinal);
        Assert.Contains("new MaintenanceExecutor", source, StringComparison.Ordinal);
        Assert.Contains("OpenMaintenanceWorkspace", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SshPage_ProvidesContextualMaintenanceEntryWithoutChangingFixedNavigation()
    {
        var appDirectory = FindAppDirectory();
        var page = File.ReadAllText(Path.Combine(appDirectory, "Views", "SshTerminalPage.xaml"));
        var navigation = File.ReadAllText(Path.Combine(appDirectory, "ViewModels", "ViewModelInfrastructure.cs"));

        Assert.Contains("维护记录", page, StringComparison.Ordinal);
        Assert.Contains("OpenMaintenanceCommand", page, StringComparison.Ordinal);
        Assert.DoesNotContain("new NavigationItem(\"maintenance\"", navigation, StringComparison.Ordinal);
    }

    private static string FindAppDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "HephaestusWorkbench.App")))
            directory = directory.Parent;
        return Path.Combine(directory!.FullName, "src", "HephaestusWorkbench.App");
    }

    private static SshDevice Device() => new()
    {
        Id = "device-1",
        Name = "测试设备",
        Host = "server.example",
        Port = 22,
        Username = "root",
        AuthenticationMethod = SshAuthenticationMethod.Password,
        CredentialTarget = "HephaestusWorkbench/ssh/device-1/password",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private sealed class FakeTerminalService : ISshTerminalService
    {
        public Task<ITerminalSession> ConnectAsync(SshConnectionRequest request, SshCredentialSecret? credential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeDeviceRepository : ISshDeviceRepository
    {
        private readonly List<SshDevice> _devices = [];
        public Task<IReadOnlyList<SshDevice>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SshDevice>>(_devices.ToArray());
        public Task<SshDevice?> GetAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(_devices.FirstOrDefault(item => item.Id == id));
        public Task UpsertAsync(SshDevice device, CancellationToken cancellationToken = default) { _devices.Add(device); return Task.CompletedTask; }
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeHostKeyRepository : ISshHostKeyRepository
    {
        public Task<SshHostKey?> GetAsync(string host, int port, CancellationToken cancellationToken = default) => Task.FromResult<SshHostKey?>(null);
        public Task UpsertAsync(SshHostKey hostKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeHistoryRepository : ISshConnectionHistoryRepository
    {
        public Task InsertAsync(SshConnectionHistory history, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CompleteAsync(string id, DateTime disconnectedAt, SshConnectionOutcome outcome, string? errorMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SshConnectionHistory>> ListRecentAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SshConnectionHistory>>([]);
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public Task WriteAsync(string target, string userName, SshCredentialSecret secret, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<SshStoredCredential?> ReadAsync(string target, CancellationToken cancellationToken = default) => Task.FromResult<SshStoredCredential?>(null);
        public Task<bool> DeleteAsync(string target, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class FakeConfirmationService : IHostKeyConfirmationService
    {
        public Task<bool> ConfirmAsync(SshHostKeyObservation observation, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
