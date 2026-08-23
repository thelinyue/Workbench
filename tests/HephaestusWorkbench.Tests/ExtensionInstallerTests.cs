using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class ExtensionInstallerTests
{
    [Fact]
    public async Task InstallAsync_ValidPackage_CreatesVersionMetadataAndHealthyCurrent()
    {
        using var environment = new InstallerTestEnvironment();
        var request = environment.CreateRequest(BuildPackage(
            ("manifest.json", BuildManifest("sample", "2.0.0")),
            ("bin/tool.exe", "payload")));

        var result = await environment.Installer.InstallAsync(request);

        Assert.False(result.AlreadyInstalled);
        Assert.Equal("sample", result.Manifest.Id);
        Assert.Equal("2.0.0", result.Manifest.Version);
        Assert.Equal(environment.VersionDirectory("sample", "2.0.0"), result.VersionDirectory);
        Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(result.VersionDirectory, "bin", "tool.exe")));

        using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result.VersionDirectory, "package.json")));
        Assert.Equal(2, metadata.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(Sha256(request.PackageBytes), metadata.RootElement.GetProperty("sha256").GetString());

        var current = ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(environment.CurrentPath("sample")));
        Assert.Equal(ExtensionActivationState.Healthy, current.State);
        Assert.Equal("2.0.0", current.Version);
        Assert.Equal(Sha256(request.PackageBytes), current.PackageSha256);
        Assert.Empty(environment.StagingDirectories());
    }

    [Theory]
    [InlineData("扩展包验签失败，内容已被篡改。")]
    [InlineData("扩展包签名密钥不受信任：unknown-key。")]
    public async Task InstallAsync_WhenVerifierRejects_DoesNotCreateInstallFiles(string message)
    {
        using var environment = new InstallerTestEnvironment(verifierError: message);
        var request = environment.CreateRequest(BuildPackage(("manifest.json", BuildManifest("sample", "2.0.0"))));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => environment.Installer.InstallAsync(request));

        Assert.Contains(message, exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(environment.ExtensionsRoot, "sample")));
        Assert.Empty(environment.StagingDirectories());
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("folder/../../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/absolute.txt")]
    public async Task InstallAsync_RejectsTraversalAndAbsoluteZipEntries(string entryName)
    {
        using var environment = new InstallerTestEnvironment();
        var request = environment.CreateRequest(BuildPackage(
            ("manifest.json", BuildManifest("sample", "2.0.0")),
            (entryName, "malicious")));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => environment.Installer.InstallAsync(request));

        Assert.Contains("路径", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(environment.Root, "escape.txt")));
        Assert.Empty(environment.StagingDirectories());
    }

    [Fact]
    public async Task InstallAsync_RejectsAlternateDataStreamEntry()
    {
        using var environment = new InstallerTestEnvironment();
        var request = environment.CreateRequest(BuildPackage(
            ("manifest.json", BuildManifest("sample", "2.0.0")),
            ("payload.txt:secret", "malicious")));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => environment.Installer.InstallAsync(request));

        Assert.Contains("数据流", exception.Message, StringComparison.Ordinal);
        Assert.Empty(environment.StagingDirectories());
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("folder/LPT1.log")]
    [InlineData("aux.txt")]
    [InlineData("CON .txt")]
    public async Task InstallAsync_RejectsWindowsReservedNames(string entryName)
    {
        using var environment = new InstallerTestEnvironment();
        var request = environment.CreateRequest(BuildPackage(
            ("manifest.json", BuildManifest("sample", "2.0.0")),
            (entryName, "malicious")));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => environment.Installer.InstallAsync(request));

        Assert.Contains("保留名称", exception.Message, StringComparison.Ordinal);
        Assert.Empty(environment.StagingDirectories());
    }

    [Fact]
    public async Task InstallAsync_RejectsDuplicateNormalizedPaths()
    {
        using var environment = new InstallerTestEnvironment();
        var request = environment.CreateRequest(BuildPackage(
            ("manifest.json", BuildManifest("sample", "2.0.0")),
            ("assets/report.js", "first"),
            ("assets\\REPORT.js", "second")));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => environment.Installer.InstallAsync(request));

        Assert.Contains("重复", exception.Message, StringComparison.Ordinal);
        Assert.Empty(environment.StagingDirectories());
    }

    [Fact]
    public async Task InstallAsync_RejectsZipEntryMarkedAsReparsePoint()
    {
        using var environment = new InstallerTestEnvironment();
        var package = BuildPackageWithReparseEntry(BuildManifest("sample", "2.0.0"));
        var request = environment.CreateRequest(package);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => environment.Installer.InstallAsync(request));

        Assert.Contains("重解析点", exception.Message, StringComparison.Ordinal);
        Assert.Empty(environment.StagingDirectories());
    }

    [Fact]
    public async Task InstallAsync_WhenRootManifestIsMissing_RejectsAndCleansStaging()
    {
        using var environment = new InstallerTestEnvironment();
        var request = environment.CreateRequest(BuildPackage(
            ("nested/manifest.json", BuildManifest("sample", "2.0.0")),
            ("bin/tool.exe", "payload")));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => environment.Installer.InstallAsync(request));

        Assert.Contains("根目录", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(environment.VersionDirectory("sample", "2.0.0")));
        Assert.Empty(environment.StagingDirectories());
    }

    [Fact]
    public async Task InstallAsync_SameVersionAndSameSha_IsIdempotent()
    {
        using var environment = new InstallerTestEnvironment();
        var request = environment.CreateRequest(BuildPackage(
            ("manifest.json", BuildManifest("sample", "2.0.0")),
            ("bin/tool.exe", "payload")));
        var first = await environment.Installer.InstallAsync(request);

        var second = await environment.Installer.InstallAsync(request);

        Assert.True(second.AlreadyInstalled);
        Assert.Equal(first.VersionDirectory, second.VersionDirectory);
        Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(second.VersionDirectory, "bin", "tool.exe")));
        Assert.Empty(environment.StagingDirectories());
    }

    [Fact]
    public async Task InstallAsync_SameVersionWithDifferentSha_IsRejectedWithoutOverwritingExistingVersion()
    {
        using var environment = new InstallerTestEnvironment();
        var firstRequest = environment.CreateRequest(BuildPackage(
            ("manifest.json", BuildManifest("sample", "2.0.0")),
            ("bin/tool.exe", "original")));
        await environment.Installer.InstallAsync(firstRequest);
        var secondRequest = environment.CreateRequest(BuildPackage(
            ("manifest.json", BuildManifest("sample", "2.0.0")),
            ("bin/tool.exe", "replacement")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => environment.Installer.InstallAsync(secondRequest));

        Assert.Contains("相同扩展版本", exception.Message, StringComparison.Ordinal);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(environment.VersionDirectory("sample", "2.0.0"), "bin", "tool.exe")));
        Assert.Empty(environment.StagingDirectories());
    }

    [Fact]
    public async Task InstallAsync_WhenActivationFails_PreservesOldActiveAndKeepsCandidateVersion()
    {
        using var environment = new InstallerTestEnvironment(failingHealthVersion: "2.0.0");
        environment.WriteInstalledVersion("sample", "1.0.0", new string('a', 64));
        environment.WriteHealthyCurrent("sample", "1.0.0", new string('a', 64));
        await environment.Registry.LoadAsync();
        var request = environment.CreateRequest(BuildPackage(
            ("manifest.json", BuildManifest("sample", "2.0.0")),
            ("bin/tool.exe", "candidate")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => environment.Installer.InstallAsync(request));

        Assert.Contains("激活失败", exception.Message, StringComparison.Ordinal);
        var current = ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(environment.CurrentPath("sample")));
        Assert.Equal("1.0.0", current.Version);
        Assert.Equal(ExtensionActivationState.Healthy, current.State);
        Assert.True(File.Exists(Path.Combine(environment.VersionDirectory("sample", "1.0.0"), "manifest.json")));
        Assert.True(File.Exists(Path.Combine(environment.VersionDirectory("sample", "2.0.0"), "package.json")));
        Assert.Empty(environment.StagingDirectories());
    }

    private static byte[] BuildPackage(params (string Name, string Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Name, CompressionLevel.NoCompression);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(item.Content);
            }
        }

        return output.ToArray();
    }

    private static byte[] BuildPackageWithReparseEntry(string manifest)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.NoCompression);
            using (var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false), leaveOpen: false))
                writer.Write(manifest);

            var linkedEntry = archive.CreateEntry("linked-file", CompressionLevel.NoCompression);
            linkedEntry.ExternalAttributes = (int)FileAttributes.ReparsePoint;
            using var linkedWriter = new StreamWriter(linkedEntry.Open(), new UTF8Encoding(false), leaveOpen: false);
            linkedWriter.Write("target");
        }

        return output.ToArray();
    }

    private static string BuildManifest(string id, string version) => $$"""
        {
          "schemaVersion": 2,
          "id": "{{id}}",
          "name": "测试分析扩展",
          "version": "{{version}}",
          "kind": "analysis",
          "publisherId": "test-publisher",
          "hostApiVersion": "1.0",
          "minHostVersion": "2.0.0",
          "runtime": {
            "kind": "process",
            "protocol": "analysis-process-v1",
            "entry": "bin/tool.exe"
          },
          "capabilities": ["analysis.engine"],
          "permissions": [],
          "dependencies": []
        }
        """;

    private static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class InstallerTestEnvironment : IDisposable
    {
        private readonly StubPackageVerifier _verifier;

        public InstallerTestEnvironment(string? verifierError = null, string? failingHealthVersion = null)
        {
            Root = Path.Combine(Path.GetTempPath(), $"hephaestus-installer-tests-{Guid.NewGuid():N}");
            ExtensionsRoot = Path.Combine(Root, "Extensions");
            Directory.CreateDirectory(ExtensionsRoot);
            _verifier = new StubPackageVerifier(verifierError);
            Registry = new ExtensionRegistry(ExtensionsRoot, new StubHealthChecker(failingHealthVersion));
            Installer = new ExtensionInstaller(ExtensionsRoot, _verifier, Registry);
        }

        public string Root { get; }
        public string ExtensionsRoot { get; }
        public ExtensionRegistry Registry { get; }
        public ExtensionInstaller Installer { get; }

        public ExtensionPackageVerificationRequest CreateRequest(byte[] packageBytes)
        {
            var manifest = ReadManifestForStub(packageBytes);
            var release = new ExtensionRelease
            {
                Version = manifest.Version,
                MinHostVersion = manifest.MinHostVersion,
                Url = "https://example.invalid/sample.zip",
                Size = packageBytes.Length,
                Sha256 = Sha256(packageBytes),
                Signature = new ExtensionPackageSignature
                {
                    KeyId = "test-key",
                    Signature = Convert.ToBase64String(new byte[64])
                }
            };
            var request = new ExtensionPackageVerificationRequest
            {
                PackageBytes = packageBytes,
                CatalogItem = new ExtensionCatalogItem
                {
                    Id = manifest.Id,
                    Name = manifest.Name,
                    Description = "测试扩展",
                    PublisherId = manifest.PublisherId,
                    Kind = manifest.Kind,
                    Releases = [release]
                },
                Release = release
            };
            _verifier.Result = new ExtensionPackageVerificationResult
            {
                Manifest = manifest,
                TrustedKeyId = "test-key",
                PackageSha256 = Sha256(packageBytes)
            };
            return request;
        }

        public string VersionDirectory(string id, string version)
            => Path.Combine(ExtensionsRoot, id, version);

        public string CurrentPath(string id)
            => Path.Combine(ExtensionsRoot, id, "current.json");

        public string[] StagingDirectories()
            => Directory.Exists(ExtensionsRoot)
                ? Directory.GetDirectories(ExtensionsRoot, ".install-*", SearchOption.TopDirectoryOnly)
                : [];

        public void WriteInstalledVersion(string id, string version, string sha256)
        {
            var directory = VersionDirectory(id, version);
            Directory.CreateDirectory(Path.Combine(directory, "bin"));
            File.WriteAllText(Path.Combine(directory, "manifest.json"), BuildManifest(id, version));
            File.WriteAllText(Path.Combine(directory, "bin", "tool.exe"), "old");
            File.WriteAllText(
                Path.Combine(directory, "package.json"),
                JsonSerializer.Serialize(new { schemaVersion = 2, sha256 }));
        }

        public void WriteHealthyCurrent(string id, string version, string sha256)
        {
            var directory = Path.Combine(ExtensionsRoot, id);
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                CurrentPath(id),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 2,
                    id,
                    version,
                    packageSha256 = sha256,
                    state = "healthy"
                }));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }

        private static ExtensionManifest ReadManifestForStub(byte[] packageBytes)
        {
            using var stream = new MemoryStream(packageBytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = archive.GetEntry("manifest.json") ?? archive.Entries.First(item => item.FullName.EndsWith("manifest.json", StringComparison.Ordinal));
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return ExtensionManifestParser.Parse(reader.ReadToEnd(), Path.Combine(Path.GetTempPath(), "HephaestusWorkbench", "InstallerTests"));
        }
    }

    private sealed class StubPackageVerifier(string? error) : IExtensionPackageVerifier
    {
        public ExtensionPackageVerificationResult? Result { get; set; }

        public Task<ExtensionPackageVerificationResult> VerifyAsync(
            ExtensionPackageVerificationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (error is not null) throw new InvalidDataException(error);
            return Task.FromResult(Result ?? throw new InvalidOperationException("测试未配置验签结果。"));
        }
    }

    private sealed class StubHealthChecker(string? failingVersion) : IExtensionHealthChecker
    {
        public Task CheckAsync(ExtensionManifest manifest, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(manifest.Version, failingVersion, StringComparison.Ordinal))
                throw new InvalidOperationException("测试健康检查失败。");
            return Task.CompletedTask;
        }
    }
}
