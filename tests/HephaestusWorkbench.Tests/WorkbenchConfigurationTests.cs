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
            var extensions = await configuration.EnsurePluginConfigAsync();
            var workspaceJson = await File.ReadAllTextAsync(paths.WorkspaceConfigFile);
            var appSettingsJson = await File.ReadAllTextAsync(paths.AppSettingsFile);
            var extensionsJson = await File.ReadAllTextAsync(paths.ExtensionsConfigFile);

            Assert.Equal(2, workspace.SchemaVersion);
            Assert.Equal(2, appSettings.SchemaVersion);
            Assert.Equal(2, extensions.SchemaVersion);
            Assert.Equal(Path.GetFullPath(external), Assert.Single(workspace.MonitorPaths));
            Assert.Empty(extensions.Plugins);

            var secondWorkspace = await configuration.EnsureWorkspaceAsync();
            var secondAppSettings = await configuration.EnsureAppSettingsAsync();
            var secondExtensions = await configuration.EnsurePluginConfigAsync();

            Assert.Equal(workspace.MonitorPaths, secondWorkspace.MonitorPaths);
            Assert.Equal(appSettings.Theme, secondAppSettings.Theme);
            Assert.Equal(extensions.Plugins.Count, secondExtensions.Plugins.Count);
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
        var seed = Path.Combine(root, "Seed");
        var data = Path.Combine(root, "LegacyData");
        Directory.CreateDirectory(seed);
        Directory.CreateDirectory(data);
        var marker = Path.Combine(data, "legacy.marker");
        await File.WriteAllTextAsync(marker, "legacy");

        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new WorkbenchInitializationService(seed).InitializeAsync(data));

            Assert.Contains(Path.GetFullPath(data), error.Message);
            Assert.Equal("legacy", await File.ReadAllTextAsync(marker));
            Assert.Equal(new[] { marker }, Directory.EnumerateFiles(data, "*", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateDirectories(data));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializationService_CreatesDirectoriesDatabaseConfigAndBuiltinPlugin()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var seed = Path.Combine(root, "Seed");
        var data = Path.Combine(root, "Data");
        Directory.CreateDirectory(seed);
        await File.WriteAllTextAsync(Path.Combine(seed, "log_analyzer.exe"), "plugin");
        await File.WriteAllTextAsync(Path.Combine(seed, "manifest.json"), """
            { "id":"log-analyzer", "name":"日志分析", "version":"1.49", "type":"Exe", "entry":"log_analyzer.exe" }
            """);

        try
        {
            var service = new WorkbenchInitializationService(seed);
            await service.InitializeAsync(data, new[] { Path.Combine(data, "Inbox") });
            await service.InitializeAsync(data, new[] { Path.Combine(data, "Inbox") });

            var paths = new DataPaths(data);
            Assert.True(File.Exists(paths.DatabaseFile));
            Assert.True(File.Exists(paths.AppSettingsFile));
            Assert.True(File.Exists(paths.WorkspaceConfigFile));
            Assert.True(File.Exists(paths.ExtensionsConfigFile));
            Assert.True(File.Exists(Path.Combine(paths.ExtensionsDirectory, "log-analyzer", "log_analyzer.exe")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

}
