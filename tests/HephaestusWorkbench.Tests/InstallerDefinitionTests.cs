namespace HephaestusWorkbench.Tests;

public sealed class InstallerDefinitionTests
{
    [Fact]
    public void ReleaseDefaults_TargetFormalV2Version()
    {
        var props = ReadRepositoryFile("Directory.Build.props");
        var buildScript = ReadRepositoryFile("installer", "build-installer.ps1");
        var innoSetup = ReadRepositoryFile("installer", "HephaestusWorkbench.iss");
        var workflow = ReadRepositoryFile(".github", "workflows", "release.yml");

        Assert.Contains("<Version>2.0.0</Version>", props);
        Assert.Contains("[string]$Version = '2.0.0'", buildScript);
        Assert.Contains("#define MyAppVersion \"2.0.0\"", innoSetup);
        Assert.Contains("default: \"2.0.0\"", workflow);
    }

    [Fact]
    public void InnoSetup_UsesStandardOfflineWizard()
    {
        var script = ReadRepositoryFile("installer", "HephaestusWorkbench.iss");

        Assert.Contains("WizardStyle=modern", script);
        Assert.Contains("LicenseFile=", script);
        Assert.Contains("AppName=Hephaestus工作台", script);
        Assert.Contains("AppVerName=Hephaestus工作台", script);
        Assert.Contains("VersionInfoProductName=Hephaestus工作台", script);
        Assert.Contains("OutputBaseFilename=HephaestusWorkbench_v{#MyAppVersion}", script);
        Assert.DoesNotContain("赫菲斯托斯工程工作台", script);
        Assert.Contains("VersionInfoProductVersion={#MyAppVersion}", script);
        Assert.Contains("Source: \"{#AppSource}\\*\"", script);
        Assert.DoesNotContain("PayloadPackage", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildScript_GeneratesOnlyOneApplicationInstaller()
    {
        var script = ReadRepositoryFile("installer", "build-installer.ps1");

        Assert.Contains("HephaestusWorkbench_v$Version.exe", script);
        Assert.DoesNotContain("HephaestusWorkbench_Update.exe", script);
        Assert.DoesNotContain("HephaestusWorkbench_Uninstall.exe", script);
        Assert.DoesNotContain("HephaestusWorkbench-v$Version-win-x64.zip", script);
    }

    [Fact]
    public void MainApplication_DoesNotPublishLegacyPluginSeed()
    {
        var project = ReadRepositoryFile("src", "HephaestusWorkbench.App", "HephaestusWorkbench.App.csproj");
        var pluginSeedDirectory = Path.Combine(
            FindRepositoryRoot(), "src", "HephaestusWorkbench.App", "PluginSeed");

        // 正式 v2 后续只能通过统一扩展安装事务携带离线扩展，本阶段禁止旧 PluginSeed 混入发布目录。
        Assert.DoesNotContain("PluginSeed", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PluginBinaryPath", project, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(pluginSeedDirectory));
    }

    [Fact]
    public void InnoSetup_UninstallDoesNotDeleteUserData()
    {
        var script = ReadRepositoryFile("installer", "HephaestusWorkbench.iss");

        Assert.DoesNotContain("[UninstallDelete]", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HephaestusWorkbenchData", script, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HephaestusWorkbench.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(new[] { directory!.FullName }.Concat(segments).ToArray()));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HephaestusWorkbench.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
