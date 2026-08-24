using System.Xml.Linq;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

/// <summary>验证 v2 启动只接受空目录或显式 schemaVersion 2 工作区。</summary>
public sealed class V2WorkspaceGateTests
{
    [Fact]
    public async Task BootstrapStore_RejectsLegacyBootstrapAndPreservesAbsoluteDataPath()
    {
        var root = CreateRoot();
        try
        {
            var bootstrap = Path.Combine(root, "bootstrap.json");
            var dataRoot = Path.Combine(root, "LegacyData");
            await File.WriteAllTextAsync(bootstrap, $$"""{"DataRoot":"{{dataRoot.Replace("\\", "\\\\")}}"}""");

            var result = await new BootstrapConfigurationStore(bootstrap).ReadAsync();

            Assert.Equal(BootstrapReadStatus.Legacy, result.Status);
            Assert.Equal(Path.GetFullPath(dataRoot), result.DataRoot);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BootstrapStore_WritesSchemaVersion2()
    {
        var root = CreateRoot();
        try
        {
            var bootstrap = Path.Combine(root, "bootstrap.json");
            var dataRoot = Path.Combine(root, "Data");
            var store = new BootstrapConfigurationStore(bootstrap);

            await store.WriteAsync(dataRoot);
            var result = await store.ReadAsync();

            Assert.Equal(BootstrapReadStatus.Ready, result.Status);
            Assert.Equal(Path.GetFullPath(dataRoot), result.DataRoot);
            Assert.Contains("\"schemaVersion\": 2", await File.ReadAllTextAsync(bootstrap));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceGate_BlocksLegacyMarkersWithoutChangingFiles()
    {
        var root = CreateRoot();
        try
        {
            var database = Path.Combine(root, "Database", "workbench.db");
            Directory.CreateDirectory(Path.GetDirectoryName(database)!);
            await File.WriteAllTextAsync(database, "legacy");
            Directory.CreateDirectory(Path.Combine(root, "Plugins"));

            var result = await new WorkspaceVersionGate().InspectAsync(root);

            Assert.Equal(WorkspaceVersionStatus.Legacy, result.Status);
            Assert.Equal(Path.GetFullPath(root), result.DataRoot);
            Assert.Equal("legacy", await File.ReadAllTextAsync(database));
            Assert.True(Directory.Exists(Path.Combine(root, "Plugins")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceGate_AcceptsEmptyAndInitializedV2Workspace()
    {
        var root = CreateRoot();
        try
        {
            var gate = new WorkspaceVersionGate();
            Assert.Equal(WorkspaceVersionStatus.Empty, (await gate.InspectAsync(root)).Status);

            var paths = new DataPaths(root);
            var configuration = new WorkbenchConfigurationService(paths);
            await new DatabaseInitializer(new SqliteConnectionFactory(paths)).InitializeAsync();
            await configuration.EnsureWorkspaceAsync();
            await configuration.EnsureAppSettingsAsync();
            await new ExtensionSettingsStore(paths).EnsureAsync();

            Assert.Equal(WorkspaceVersionStatus.Ready, (await gate.InspectAsync(root)).Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LegacyWorkspaceWindow_OffersOnlyOpenDirectoryAndExit()
    {
        var text = LoadAppFile("LegacyWorkspaceWindow.xaml").ToString();

        Assert.Contains("打开目录", text);
        Assert.Contains("退出", text);
        Assert.DoesNotContain("迁移", text);
        Assert.DoesNotContain("删除", text);
        Assert.DoesNotContain("备份", text);
    }

    [Fact]
    public void FirstRunWizard_DoesNotCreateOrOpenSelectedWorkspaceBeforeInitializationGate()
    {
        var xaml = LoadAppFile("FirstRunWizard.xaml").ToString();
        var codeBehind = LoadAppSource("FirstRunWizard.xaml.cs");

        Assert.DoesNotContain("打开扩展目录", xaml);
        Assert.DoesNotContain("Directory.CreateDirectory", codeBehind);
        Assert.DoesNotContain("OpenExtensionDirectory", codeBehind);
        Assert.DoesNotContain("插件目录", xaml);
        Assert.DoesNotContain("内置分析插件", xaml);
        Assert.DoesNotContain("插件和日志", xaml);
        Assert.DoesNotContain("ExtensionDirectory", File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "HephaestusWorkbench.App", "ViewModels", "FirstRunWizardViewModel.cs")));
    }

    [Fact]
    public void AppStartup_ChecksWorkspaceBeforeWizardAndDoesNotRewriteOldDatabase()
    {
        var source = LoadAppSource("App.xaml.cs");

        Assert.Contains("BootstrapConfigurationStore", source);
        Assert.Contains("WorkspaceVersionGate", source);
        Assert.True(source.IndexOf("InspectAsync", StringComparison.Ordinal) < source.IndexOf("FirstRunWizard", StringComparison.Ordinal));
        Assert.DoesNotContain(".corrupt-", source);
        Assert.DoesNotContain("File.Delete(Paths.DatabaseFile)", source);
    }
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HephaestusWorkbench.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string LoadAppSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HephaestusWorkbench.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, "src", "HephaestusWorkbench.App", relativePath));
    }
    private static XDocument LoadAppFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HephaestusWorkbench.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return XDocument.Load(Path.Combine(directory!.FullName, "src", "HephaestusWorkbench.App", relativePath));
    }
}

