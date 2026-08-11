using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class DefaultPluginSelectionTests
{
    [Fact]
    public async Task StartAsync_UsesEnabledDefaultPlugin()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new DataPaths(root);
            paths.EnsureCreated();
            var factory = new SqliteConnectionFactory(paths);
            await new DatabaseInitializer(factory).InitializeAsync();
            var cases = new SqliteCaseRepository(factory);
            var tasks = new SqliteTaskRepository(factory);
            var reports = new SqliteReportRepository(factory);
            var configuration = new WorkbenchConfigurationService(paths);
            await configuration.SavePluginConfigAsync(new PluginConfig
            {
                DefaultPluginId = "second",
                Plugins =
                {
                    new PluginConfigEntry { Id = "first", Version = "1.0", Enabled = true },
                    new PluginConfigEntry { Id = "second", Version = "1.0", Enabled = true }
                }
            });
            var runner = new FailedRunner();
            var taskCenter = new TaskCenter(tasks);
            var service = new CaseAnalysisService(paths, cases, tasks, reports, new TwoPluginCatalog(), runner, runner, taskCenter, new WorkbenchLogger(root), configuration);
            var log = Path.Combine(root, "test.tgz");
            await File.WriteAllTextAsync(log, "test");

            var task = await service.StartAsync(new LogInboxItem
            {
                FilePath = log,
                FileName = "test.tgz",
                DeviceId = "device",
                LogTime = DateTime.Now,
                FileSize = 4,
                IsValidArchive = true
            });

            Assert.NotNull(task);
            Assert.Equal("second", task.PluginId);
            for (var attempt = 0; attempt < 50 && taskCenter.IsPluginActive("second"); attempt++) await Task.Delay(10);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private sealed class TwoPluginCatalog : IPluginCatalog
    {
        private static readonly PluginManifest[] Plugins =
        {
            new() { Id = "first", Name = "第一个", Version = "1.0", Type = PluginType.Exe, Entry = "first.exe" },
            new() { Id = "second", Name = "第二个", Version = "1.0", Type = PluginType.Exe, Entry = "second.exe" }
        };
        public Task<IReadOnlyList<PluginManifest>> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PluginManifest>>(Plugins);
        public Task<PluginManifest?> GetAsync(string pluginId, CancellationToken cancellationToken = default) => Task.FromResult<PluginManifest?>(Plugins.FirstOrDefault(x => x.Id == pluginId));
    }

    private sealed class FailedRunner : IPluginRunner
    {
        public Task<PluginExecutionResult> RunAsync(PluginManifest manifest, PluginExecutionContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new PluginExecutionResult(1, null, "测试结束"));
    }
}
