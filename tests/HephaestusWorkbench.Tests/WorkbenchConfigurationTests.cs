using HephaestusWorkbench.Core.Repositories;
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

            await File.WriteAllTextAsync(paths.AppSettingsFile, "{\"Theme\":\"unknown\",\"MaxReportTabs\":10}");
            var normalized = await configuration.EnsureAppSettingsAsync();
            Assert.Equal("Light", normalized.Theme);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureConfigFiles_MigratesLegacyWatchDirectoryAndIsIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var paths = new DataPaths(root);
        paths.EnsureCreated();
        var legacy = new MemorySettingsStore();
        var external = Path.Combine(root, "CustomerLogs");
        await legacy.SetAsync("watch_directory", external);
        await legacy.SetAsync("report_max_tabs", "7");

        try
        {
            var factory = new SqliteConnectionFactory(paths);
            await new DatabaseInitializer(factory).InitializeAsync();
            var configuration = new WorkbenchConfigurationService(paths);

            var workspace = await configuration.EnsureWorkspaceAsync(legacyStore: legacy);
            var appSettings = await configuration.EnsureAppSettingsAsync(legacy);
            var plugins = await configuration.EnsurePluginConfigAsync();

            Assert.Equal(Path.GetFullPath(external), Assert.Single(workspace.MonitorPaths));
            Assert.Equal(7, appSettings.MaxReportTabs);
            Assert.Empty(plugins.Plugins);
            Assert.True(File.Exists(paths.WorkspaceConfigFile));
            Assert.True(File.Exists(paths.AppSettingsFile));
            Assert.True(File.Exists(paths.PluginsConfigFile));

            var second = await configuration.EnsureWorkspaceAsync(legacyStore: legacy);
            Assert.Equal(workspace.MonitorPaths, second.MonitorPaths);
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
            Assert.True(File.Exists(paths.PluginsConfigFile));
            Assert.True(File.Exists(Path.Combine(paths.PluginsDirectory, "log-analyzer", "log_analyzer.exe")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class MemorySettingsStore : ISettingsStore
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
