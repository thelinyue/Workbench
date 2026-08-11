using System.Text.Json;
using HephaestusWorkbench.Setup;

namespace HephaestusWorkbench.Tests;

public sealed class InstallerTests
{
    [Fact]
    public void InstallMetadata_UsesHephaestusProductIdentity()
    {
        var metadata = new InstallMetadata();

        Assert.Equal("1.1.0", metadata.Version);
        Assert.Equal("赫菲斯托斯工程工作台", metadata.Product);
    }

    [Theory]
    [InlineData("D:\\Apps\\HephaestusWorkbench")]
    [InlineData("E:\\Tools\\HephaestusWorkbench")]
    public void NormalizeInstallDirectory_AllowsEditableCustomPaths(string input)
    {
        Assert.Equal(input, InstallPathForm.NormalizeInstallDirectory(input));
    }

    [Fact]
    public void NormalizeInstallDirectory_RejectsDriveRoot()
    {
        var driveRoot = Path.GetPathRoot(Environment.CurrentDirectory)!;
        Assert.Throws<ArgumentException>(() => InstallPathForm.NormalizeInstallDirectory(driveRoot));
    }

    [Fact]
    public void ReadInstalledVersion_ReadsVersionMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "install.json"), JsonSerializer.Serialize(new InstallMetadata { Version = "1.2.3" }));
            Assert.Equal(new Version(1, 2, 3), InstallOperations.ReadInstalledVersion(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RemoveDataDirectory_RejectsDriveRoot()
    {
        var driveRoot = Path.GetPathRoot(Environment.CurrentDirectory)!;
        Assert.Throws<InvalidOperationException>(() => InstallOperations.RemoveDataDirectory(driveRoot, new SetupLogger()));
    }

    [Fact]
    public void ReplaceProgramDirectory_UsesStagedFilesAndRemovesPreviousVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "Install");
        var staged = Path.Combine(root, "Staged");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(install, "old.txt"), "old");
        File.WriteAllText(Path.Combine(staged, "new.txt"), "new");
        try
        {
            InstallOperations.ReplaceProgramDirectory(staged, install, new SetupLogger());
            Assert.False(File.Exists(Path.Combine(install, "old.txt")));
            Assert.Equal("new", File.ReadAllText(Path.Combine(install, "new.txt")));
            Assert.False(Directory.Exists(install + ".previous"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
