using System.Text.Json;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using HephaestusWorkbench.Setup;

namespace HephaestusWorkbench.Tests;

public sealed class InstallerTests
{
    [Fact]
    public async Task PayloadPackage_DownloadsAndVerifiesExpectedHash()
    {
        var file = Path.Combine(Path.GetTempPath(), $"payload-{Guid.NewGuid():N}.zip");
        var content = "payload"u8.ToArray();
        try
        {
            using var client = new HttpClient(new PayloadHandler(content));
            await PayloadPackage.DownloadAsync(file, Convert.ToHexString(SHA256.HashData(content)), content.Length, new SetupLogger(), client);
            Assert.Equal(content, await File.ReadAllBytesAsync(file));

            await Assert.ThrowsAsync<InvalidDataException>(() => PayloadPackage.DownloadAsync(file, new string('0', 64), content.Length, new SetupLogger(), client));
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public void PayloadPackage_RejectsArchiveTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "payload.zip");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(root);
        try
        {
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("../outside.txt").Open())) writer.Write("bad");
            Assert.Throws<InvalidDataException>(() => PayloadPackage.Extract(package, target));
            Assert.False(File.Exists(Path.Combine(root, "outside.txt")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

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

    private sealed class PayloadHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content), RequestMessage = request };
            return Task.FromResult(response);
        }
    }
}
