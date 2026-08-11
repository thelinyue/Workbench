namespace HephaestusWorkbench.Tests;

public sealed class InstallerDefinitionTests
{
    [Fact]
    public void InnoSetup_UsesStandardOfflineWizard()
    {
        var script = ReadRepositoryFile("installer", "HephaestusWorkbench.iss");

        Assert.Contains("WizardStyle=modern", script);
        Assert.Contains("LicenseFile=", script);
        Assert.Contains("OutputBaseFilename=HephaestusWorkbench_Setup_v{#MyAppVersion}", script);
        Assert.Contains("VersionInfoProductVersion={#MyAppVersion}", script);
        Assert.Contains("Source: \"{#AppSource}\\*\"", script);
        Assert.DoesNotContain("PayloadPackage", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildScript_GeneratesOnlyOneApplicationInstaller()
    {
        var script = ReadRepositoryFile("installer", "build-installer.ps1");

        Assert.Contains("HephaestusWorkbench_Setup_v$Version.exe", script);
        Assert.DoesNotContain("HephaestusWorkbench_Update.exe", script);
        Assert.DoesNotContain("HephaestusWorkbench_Uninstall.exe", script);
        Assert.DoesNotContain("HephaestusWorkbench-v$Version-win-x64.zip", script);
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
}
