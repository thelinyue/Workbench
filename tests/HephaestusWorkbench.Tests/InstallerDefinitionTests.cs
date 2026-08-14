namespace HephaestusWorkbench.Tests;

public sealed class InstallerDefinitionTests
{
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
    public void MainApplication_BundlesOnlyLogAnalyzer()
    {
        var project = ReadRepositoryFile("src", "HephaestusWorkbench.App", "HephaestusWorkbench.App.csproj");
        var workflow = ReadRepositoryFile(".github", "workflows", "release.yml");

        Assert.Contains("PluginSeed\\manifest.json", project);
        Assert.DoesNotContain("PluginSeed\\RuleEditor", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RuleEditorBinaryPath", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RuleEditorBinaryPath", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rule_editor.exe", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(
            FindRepositoryRoot(), "src", "HephaestusWorkbench.App", "PluginSeed", "RuleEditor", "manifest.json")));
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
