using System.Text.Json;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

/// <summary>
/// 锁定正式版 v2 工作区的配置与数据库边界，防止初始化过程重新创建旧版兼容结构。
/// </summary>
public sealed class V2WorkspaceContractTests
{
    [Fact]
    public async Task ConfigurationFiles_WriteSchemaVersion2AndUseExtensionsFile()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var paths = new DataPaths(root);
            var configuration = new WorkbenchConfigurationService(paths);

            await configuration.EnsureWorkspaceAsync();
            await configuration.EnsureAppSettingsAsync();
            await configuration.EnsurePluginConfigAsync();

            Assert.Equal("extensions.json", Path.GetFileName(paths.ExtensionsConfigFile));
            Assert.False(File.Exists(Path.Combine(paths.ConfigDirectory, "plugins.json")));
            Assert.Equal(2, ReadSchemaVersion(paths.WorkspaceConfigFile));
            Assert.Equal(2, ReadSchemaVersion(paths.AppSettingsFile));
            Assert.Equal(2, ReadSchemaVersion(paths.ExtensionsConfigFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConfigurationFiles_RejectUnsupportedSchemaInsteadOfMigrating()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var paths = new DataPaths(root);
            paths.EnsureCreated();
            await File.WriteAllTextAsync(paths.WorkspaceConfigFile, """{"schemaVersion":1,"dataPath":"legacy","monitorPaths":[]}""");

            var configuration = new WorkbenchConfigurationService(paths);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => configuration.EnsureWorkspaceAsync());

            Assert.Contains("schemaVersion", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("2", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DatabaseInitializer_CreatesOnlyV2Tables()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var paths = new DataPaths(root);
            var factory = new SqliteConnectionFactory(paths);
            await new DatabaseInitializer(factory).InitializeAsync();

            await using var connection = await factory.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            await using var reader = await command.ExecuteReaderAsync();
            var names = new List<string>();
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));

            Assert.Equal(new[]
            {
                "analysis_cases",
                "analysis_tasks",
                "maintenance_operation_steps",
                "maintenance_operations",
                "reports",
                "ssh_connection_history",
                "ssh_devices",
                "ssh_host_keys"
            }, names);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AppSettings_DoesNotExposeEmbeddedReportTabOptions()
    {
        Assert.Null(typeof(AppSettingsConfig).GetProperty("MaxReportTabs"));
        Assert.Null(typeof(AppSettingsConfig).GetProperty("ManualCleanupEnabled"));
    }

    [Fact]
    public void RuntimeSurface_DoesNotExposeRemovedV1SettingsOrPluginRepositories()
    {
        var repositoryAssembly = typeof(HephaestusWorkbench.Core.Repositories.IAnalysisCaseRepository).Assembly;
        var dataAssembly = typeof(SqliteCaseRepository).Assembly;

        Assert.Null(repositoryAssembly.GetType("HephaestusWorkbench.Core.Repositories.ISettingsStore"));
        Assert.Null(repositoryAssembly.GetType("HephaestusWorkbench.Core.Repositories.IPluginInfoRepository"));
        Assert.Null(dataAssembly.GetType("HephaestusWorkbench.Data.SqliteSettingsStore"));
        Assert.Null(dataAssembly.GetType("HephaestusWorkbench.Data.SqlitePluginInfoRepository"));
    }

    [Fact]
    public async Task AnalysisTask_PersistsExplicitComprehensiveScope()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var paths = new DataPaths(root);
            var factory = new SqliteConnectionFactory(paths);
            await new DatabaseInitializer(factory).InitializeAsync();
            var cases = new SqliteCaseRepository(factory);
            var tasks = new SqliteTaskRepository(factory);
            var now = DateTime.Now;
            await cases.InsertAsync(new AnalysisCase
            {
                Id = "case-1",
                DisplayName = "测试案例",
                OriginalName = "diag.tgz",
                DeviceId = "NODE-01",
                LogTime = now,
                Status = CaseStatus.Ready,
                SourcePath = Path.Combine(root, "diag.tgz"),
                ExtractPath = Path.Combine(root, "Extract"),
                CreateTime = now,
                UpdateTime = now
            });
            await tasks.InsertAsync(new AnalysisTask
            {
                Id = "task-1",
                CaseId = "case-1",
                PluginId = "log-analyzer",
                AnalysisScope = AnalysisScope.Comprehensive,
                Status = HephaestusWorkbench.Core.Models.TaskStatus.Waiting
            });

            var saved = await tasks.GetAsync("task-1");

            Assert.Equal(AnalysisScope.Comprehensive, saved?.AnalysisScope);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
    private static int ReadSchemaVersion(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("schemaVersion").GetInt32();
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}

