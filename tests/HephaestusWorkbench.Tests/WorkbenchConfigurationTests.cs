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
    public async Task SettingsService_PersistsNormalizedSshTerminalPreferences()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var paths = new DataPaths(root);
        var settings = new SettingsService(new WorkbenchConfigurationService(paths), paths.InboxDirectory);
        try
        {
            await settings.SetSshTerminalPreferencesAsync(2222, "  Consolas  ", 18, SshReconnectBehavior.Disabled);
            var saved = await settings.GetSshTerminalPreferencesAsync();

            Assert.Equal(2222, saved.DefaultPort);
            Assert.Equal("Consolas", saved.FontFamily);
            Assert.Equal(18, saved.FontSize);
            Assert.Equal(SshReconnectBehavior.Disabled, saved.ReconnectBehavior);
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
    public async Task InitializationService_DatabaseStageFailureLeavesTargetEmptyAndPreservesStagingForManualCleanup()
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
            var retainedStaging = AssertSingleRetainedStaging(root, data);
            Assert.True(File.Exists(Path.Combine(retainedStaging, ".hephaestus-workbench-initialization")));
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
    public async Task InitializationService_FailureDoesNotDeleteStagingReplacedByUnmarkedDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "Data");
        Directory.CreateDirectory(data);
        string? replacement = null;
        var progress = new InlineProgress(message =>
        {
            if (message != "正在写入工作区配置…") return;

            replacement = FindStagingDirectory(root, data);
            Directory.Delete(replacement, recursive: true);
            Directory.CreateDirectory(replacement);
            File.WriteAllText(Path.Combine(replacement, "keep.txt"), "user");
            throw new IOException("受控的 staging 替换故障");
        });

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => new WorkbenchInitializationService().InitializeAsync(data, progress: progress));

            Assert.NotNull(replacement);
            Assert.Equal("user", await File.ReadAllTextAsync(Path.Combine(replacement!, "keep.txt")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(data));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializationService_FailureDoesNotDeleteStagingReplacedByDirectoryWithCopiedMarker()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "Data");
        Directory.CreateDirectory(data);
        string? replacement = null;
        var progress = new InlineProgress(message =>
        {
            if (message != "正在写入工作区配置…") return;

            replacement = FindStagingDirectory(root, data);
            var markerPath = Path.Combine(replacement, ".hephaestus-workbench-initialization");
            var copiedMarker = File.ReadAllText(markerPath);
            Directory.Delete(replacement, recursive: true);
            Directory.CreateDirectory(replacement);
            File.WriteAllText(Path.Combine(replacement, ".hephaestus-workbench-initialization"), copiedMarker);
            File.WriteAllText(Path.Combine(replacement, "keep.txt"), "user");
            throw new IOException("受控的复制 marker staging 替换故障");
        });

        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new WorkbenchInitializationService().InitializeAsync(data, progress: progress));

            Assert.Equal("受控的复制 marker staging 替换故障", error.InnerException?.Message);
            Assert.NotNull(replacement);
            Assert.True(Directory.Exists(replacement));
            Assert.Equal("user", await File.ReadAllTextAsync(Path.Combine(replacement!, "keep.txt")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(data));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializationService_FailureDoesNotDeleteStagingReplacedByReparsePointWhenSupported()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "Data");
        var replacementTarget = Path.Combine(root, "ReplacementTarget");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(replacementTarget);
        await File.WriteAllTextAsync(
            Path.Combine(replacementTarget, ".hephaestus-workbench-initialization"),
            "HephaestusWorkbench.WorkspaceInitialization.v2");
        await File.WriteAllTextAsync(Path.Combine(replacementTarget, "keep.txt"), "user");
        string? replacement = null;
        var reparseSupported = false;
        var progress = new InlineProgress(message =>
        {
            if (message != "正在写入工作区配置…") return;

            replacement = FindStagingDirectory(root, data);
            Directory.Delete(replacement, recursive: true);
            try
            {
                Directory.CreateSymbolicLink(replacement, replacementTarget);
                reparseSupported = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
            {
                Directory.CreateDirectory(replacement);
            }
            throw new IOException("受控的 reparse staging 替换故障");
        });

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => new WorkbenchInitializationService().InitializeAsync(data, progress: progress));

            if (!reparseSupported) return;
            Assert.NotNull(replacement);
            Assert.True(Directory.Exists(replacement));
            Assert.True((File.GetAttributes(replacement!) & FileAttributes.ReparsePoint) != 0);
            Assert.Equal("user", await File.ReadAllTextAsync(Path.Combine(replacementTarget, "keep.txt")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializationService_AllWhitespaceMonitorPathsUseTargetInbox()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "Data");

        try
        {
            await new WorkbenchInitializationService().InitializeAsync(data, new[] { " ", "\t", "\r\n" });

            var paths = new DataPaths(data);
            using var workspace = JsonDocument.Parse(await File.ReadAllTextAsync(paths.WorkspaceConfigFile));
            var monitorPath = Assert.Single(workspace.RootElement.GetProperty("monitorPaths").EnumerateArray()).GetString();
            Assert.Equal(Path.GetFullPath(Path.Combine(data, "Inbox")), monitorPath);
            Assert.DoesNotContain(".hephaestus-init-", monitorPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializationService_TargetWrittenDuringInitializationIsRejectedAndPreserved()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "Data");
        Directory.CreateDirectory(data);
        var marker = Path.Combine(data, "external.marker");
        var progress = new InlineProgress(message =>
        {
            if (message == "正在写入工作区配置…") File.WriteAllText(marker, "external");
        });

        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new WorkbenchInitializationService().InitializeAsync(data, progress: progress));

            Assert.Contains("目标目录出现了新文件", error.Message, StringComparison.Ordinal);
            Assert.Equal("external", await File.ReadAllTextAsync(marker));
            Assert.Equal(new[] { marker }, Directory.EnumerateFiles(data, "*", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateDirectories(data));
            AssertSingleRetainedStaging(root, data);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializationService_CancellationAfterDatabaseLeavesTargetEmptyAndPreservesStaging()
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
            AssertSingleRetainedStaging(root, data);
            await new WorkbenchInitializationService().InitializeAsync(data);
            Assert.Equal(WorkspaceVersionStatus.Ready, (await new WorkspaceVersionGate().InspectAsync(data)).Status);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializationService_PreservesStaleStagingWithoutCurrentOwnershipProof()
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

            Assert.Equal("host", await File.ReadAllTextAsync(Path.Combine(stale, "partial.tmp")));
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

    private static string FindStagingDirectory(string root, string dataRoot)
    {
        var prefix = $".{Path.GetFileName(Path.TrimEndingDirectorySeparator(dataRoot))}.hephaestus-init-";
        return Directory.EnumerateDirectories(root)
            .Single(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal));
    }

    private static string AssertSingleRetainedStaging(string root, string dataRoot)
    {
        var staging = Assert.Single(
            Directory.EnumerateDirectories(root),
            path => !string.Equals(path, dataRoot, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(".hephaestus-init-", Path.GetFileName(staging), StringComparison.OrdinalIgnoreCase);
        return staging;
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
