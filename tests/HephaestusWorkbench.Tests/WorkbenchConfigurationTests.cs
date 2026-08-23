using System.Text.Json;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class WorkbenchConfigurationTests
{
    [Fact]
    public async Task AppSettings_DefaultsToLightAndNormalizesThemeNames()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var paths = new DataPaths(root);
        var configuration = new WorkbenchConfigurationService(paths);

        try
        {
            var created = await configuration.EnsureAppSettingsAsync();
            Assert.Equal("Light", created.Theme);

            await configuration.SaveAppSettingsAsync(new AppSettingsConfig { Theme = "dArK" });
            var dark = await configuration.EnsureAppSettingsAsync();
            Assert.Equal("Dark", dark.Theme);

            await configuration.SaveAppSettingsAsync(new AppSettingsConfig { GitHubDownloadMirrorTemplate = " https://mirror.example/{url} " });
            var withMirror = await configuration.EnsureAppSettingsAsync();
            Assert.Equal("https://mirror.example/{url}", withMirror.GitHubDownloadMirrorTemplate);

            await File.WriteAllTextAsync(paths.AppSettingsFile, "{\"schemaVersion\":2,\"theme\":\"unknown\",\"cleanupRetentionDays\":30}");
            var normalized = await configuration.EnsureAppSettingsAsync();
            Assert.Equal("Light", normalized.Theme);
            Assert.Equal(7, normalized.CleanupRetentionDays);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureConfigFiles_CreatesSchemaV2FilesAndIsIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var paths = new DataPaths(root);
        var external = Path.Combine(root, "CustomerLogs");
        var configuration = new WorkbenchConfigurationService(paths);

        try
        {
            var workspace = await configuration.EnsureWorkspaceAsync(new[] { external });
            var appSettings = await configuration.EnsureAppSettingsAsync();
            var extensions = await new ExtensionSettingsStore(paths).EnsureAsync();
            var workspaceJson = await File.ReadAllTextAsync(paths.WorkspaceConfigFile);
            var appSettingsJson = await File.ReadAllTextAsync(paths.AppSettingsFile);
            var extensionsJson = await File.ReadAllTextAsync(paths.ExtensionsConfigFile);

            Assert.Equal(2, workspace.SchemaVersion);
            Assert.Equal(2, appSettings.SchemaVersion);
            Assert.Equal(2, extensions.SchemaVersion);
            Assert.Equal(Path.GetFullPath(external), Assert.Single(workspace.MonitorPaths));
            Assert.Empty(extensions.Extensions);
            Assert.Equal("stable", extensions.UpdateChannel);
            Assert.Equal("analysis.engine", extensions.DefaultAnalysisCapability);

            var secondWorkspace = await configuration.EnsureWorkspaceAsync();
            var secondAppSettings = await configuration.EnsureAppSettingsAsync();
            var secondExtensions = await new ExtensionSettingsStore(paths).EnsureAsync();

            Assert.Equal(workspace.MonitorPaths, secondWorkspace.MonitorPaths);
            Assert.Equal(appSettings.Theme, secondAppSettings.Theme);
            Assert.Equal(extensions.Extensions.Count, secondExtensions.Extensions.Count);
            Assert.Equal(workspaceJson, await File.ReadAllTextAsync(paths.WorkspaceConfigFile));
            Assert.Equal(appSettingsJson, await File.ReadAllTextAsync(paths.AppSettingsFile));
            Assert.Equal(extensionsJson, await File.ReadAllTextAsync(paths.ExtensionsConfigFile));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializationService_RejectsNonEmptyLegacyDirectoryWithoutWriting()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "LegacyData");
        Directory.CreateDirectory(data);
        var marker = Path.Combine(data, "legacy.marker");
        await File.WriteAllTextAsync(marker, "legacy");

        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new WorkbenchInitializationService().InitializeAsync(data));

            Assert.Contains(Path.GetFullPath(data), error.Message);
            Assert.Equal("legacy", await File.ReadAllTextAsync(marker));
            Assert.Equal(new[] { marker }, Directory.EnumerateFiles(data, "*", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateDirectories(data));
            Assert.Equal(new[] { data }, Directory.EnumerateDirectories(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializationService_DatabaseStageFailureLeavesPrecreatedTargetEmptyAndRetrySucceeds()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "Data");
        Directory.CreateDirectory(data);
        var databaseWasCreated = false;
        var progress = new InlineProgress(message =>
        {
            if (message != "正在写入工作区配置…") return;

            databaseWasCreated = File.Exists(new DataPaths(data).DatabaseFile)
                || Directory.EnumerateDirectories(root)
                    .Where(path => !string.Equals(path, data, StringComparison.OrdinalIgnoreCase))
                    .Any(path => File.Exists(Path.Combine(path, "Database", "workbench.db")));
            throw new IOException("受控的数据库后故障");
        });

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => new WorkbenchInitializationService().InitializeAsync(data, progress: progress));

            Assert.True(databaseWasCreated);
            Assert.True(Directory.Exists(data));
            Assert.Empty(Directory.EnumerateFileSystemEntries(data));
            Assert.Equal(new[] { data }, Directory.EnumerateDirectories(root));
            Assert.Equal(WorkspaceVersionStatus.Empty, (await new WorkspaceVersionGate().InspectAsync(data)).Status);

            await new WorkbenchInitializationService().InitializeAsync(data);

            Assert.Equal(WorkspaceVersionStatus.Ready, (await new WorkspaceVersionGate().InspectAsync(data)).Status);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializationService_CancellationAfterDatabaseLeavesTargetEmptyAndCanRetry()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "Data");
        Directory.CreateDirectory(data);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(message =>
        {
            if (message == "正在写入工作区配置…") cancellation.Cancel();
        });

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => new WorkbenchInitializationService().InitializeAsync(
                    data,
                    progress: progress,
                    cancellationToken: cancellation.Token));

            Assert.Empty(Directory.EnumerateFileSystemEntries(data));
            Assert.Equal(new[] { data }, Directory.EnumerateDirectories(root));
            await new WorkbenchInitializationService().InitializeAsync(data);
            Assert.Equal(WorkspaceVersionStatus.Ready, (await new WorkspaceVersionGate().InspectAsync(data)).Status);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializationService_CleansOnlyConfirmedStaleHostStaging()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "Data");
        var stale = Path.Combine(root, ".Data.hephaestus-init-stale");
        var unowned = Path.Combine(root, ".Data.hephaestus-init-unowned");
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(unowned);
        await File.WriteAllTextAsync(
            Path.Combine(stale, ".hephaestus-workbench-initialization"),
            "HephaestusWorkbench.WorkspaceInitialization.v2");
        await File.WriteAllTextAsync(Path.Combine(stale, "partial.tmp"), "host");
        await File.WriteAllTextAsync(Path.Combine(unowned, "keep.txt"), "user");

        try
        {
            await new WorkbenchInitializationService().InitializeAsync(data);

            Assert.False(Directory.Exists(stale));
            Assert.True(File.Exists(Path.Combine(unowned, "keep.txt")));
            Assert.Equal(WorkspaceVersionStatus.Ready, (await new WorkspaceVersionGate().InspectAsync(data)).Status);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializationService_CreatesOnlyV2WorkspaceWithoutLegacyPluginState()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "Data");

        try
        {
            var service = new WorkbenchInitializationService();
            await service.InitializeAsync(data, new[] { Path.Combine(data, "Inbox") });
            await service.InitializeAsync(data, new[] { Path.Combine(data, "Inbox") });

            var paths = new DataPaths(data);
            Assert.True(File.Exists(paths.DatabaseFile));
            Assert.True(File.Exists(paths.AppSettingsFile));
            Assert.True(File.Exists(paths.WorkspaceConfigFile));
            Assert.True(File.Exists(paths.ExtensionsConfigFile));
            using var workspaceJson = JsonDocument.Parse(await File.ReadAllTextAsync(paths.WorkspaceConfigFile));
            Assert.Equal(Path.GetFullPath(data), workspaceJson.RootElement.GetProperty("dataPath").GetString());
            Assert.Equal(
                Path.GetFullPath(Path.Combine(data, "Inbox")),
                workspaceJson.RootElement.GetProperty("monitorPaths")[0].GetString());
            Assert.Empty(Directory.EnumerateDirectories(paths.ExtensionsDirectory));
            var extensionsJson = await File.ReadAllTextAsync(paths.ExtensionsConfigFile);
            Assert.Contains("\"defaultAnalysisCapability\"", extensionsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("defaultPluginId", extensionsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"plugins\"", extensionsJson, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
