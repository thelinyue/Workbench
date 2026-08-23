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

        using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(environment.MetadataPath("sample", "2.0.0")));
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

    [Fact]
    public async Task InstallAsync_WhenVerifierMutatesItsInput_ExtractsPrivateSnapshot()
    {
        using var environment = new InstallerTestEnvironment(onVerify: request => request.PackageBytes.AsSpan().Fill(0x5a));
        var package = BuildPackage(
            ("manifest.json", BuildManifest("sample", "2.0.0")),
            ("bin/tool.exe", "trusted-payload"));
        var request = environment.CreateRequest(package);

        var result = await environment.Installer.InstallAsync(request);

        Assert.Equal("trusted-payload", await File.ReadAllTextAsync(Path.Combine(result.VersionDirectory, "bin", "tool.exe")));
        Assert.Equal(Sha256(package), ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(environment.CurrentPath("sample"))).PackageSha256);
    }

    [Fact]
    public async Task InstallAsync_WhenVerifierReturnsDifferentSha_RejectsBeforeCreatingStaging()
    {
        using var environment = new InstallerTestEnvironment();
        var request = environment.CreateRequest(BuildPackage(("manifest.json", BuildManifest("sample", "2.0.0"))));
        environment.OverrideVerifierSha(new string('b', 64));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => environment.Installer.InstallAsync(request));

        Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(environment.ExtensionsRoot, "sample")));
        Assert.Empty(environment.StagingDirectories());
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("folder/../../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/absolute.txt")]
    [InlineData("C:drive-relative.txt")]
    [InlineData("\\\\server\\share\\payload.txt")]
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
    [InlineData("COM¹.txt")]
    [InlineData("COM²")]
    [InlineData("COM³.log")]
    [InlineData("LPT¹.txt")]
    [InlineData("LPT²")]
    [InlineData("LPT³.log")]
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

    [Theory]
    [InlineData("folder./payload.txt")]
    [InlineData("folder /payload.txt")]
    [InlineData("payload.txt.")]
    [InlineData("payload.txt ")]
    public async Task InstallAsync_RejectsTrailingDotOrSpacePaths(string entryName)
    {
        using var environment = new InstallerTestEnvironment();
        var request = environment.CreateRequest(BuildPackage(
            ("manifest.json", BuildManifest("sample", "2.0.0")),
            (entryName, "malicious")));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => environment.Installer.InstallAsync(request));

        Assert.Contains("自动规范化", exception.Message, StringComparison.Ordinal);
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
    public async Task InstallAsync_RejectsFileDirectoryConflict()
    {
        using var environment = new InstallerTestEnvironment();
        var request = environment.CreateRequest(BuildPackage(
            ("manifest.json", BuildManifest("sample", "2.0.0")),
            ("node", "file"),
            ("node/child.txt", "child")));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => environment.Installer.InstallAsync(request));

        Assert.Contains("冲突", exception.Message, StringComparison.Ordinal);
        Assert.Empty(environment.StagingDirectories());
    }

    [Fact]
    public async Task InstallAsync_RejectsZipEntryMarkedAsReparsePoint()
    {
        using var environment = new InstallerTestEnvironment();
        var request = environment.CreateRequest(BuildPackageWithLinkedEntry(
            BuildManifest("sample", "2.0.0"),
            (int)FileAttributes.ReparsePoint));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => environment.Installer.InstallAsync(request));

        Assert.Contains("重解析点", exception.Message, StringComparison.Ordinal);
        Assert.Empty(environment.StagingDirectories());
    }

    [Fact]
    public async Task InstallAsync_RejectsUnixSymbolicLinkEntry()
    {
        using var environment = new InstallerTestEnvironment();
        var unixSymbolicLinkAttributes = unchecked((int)0xA1FF0000);
        var request = environment.CreateRequest(BuildPackageWithLinkedEntry(
            BuildManifest("sample", "2.0.0"),
            unixSymbolicLinkAttributes));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => environment.Installer.InstallAsync(request));

        Assert.Contains("符号链接", exception.Message, StringComparison.Ordinal);
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
    public async Task InstallAsync_WhenExtractedManifestDiffersFromVerifier_RejectsPackage()
    {
        using var environment = new InstallerTestEnvironment();
        var request = environment.CreateRequest(BuildPackage(
            ("manifest.json", BuildManifest("sample", "2.0.0")),
            ("bin/tool.exe", "payload")));
        environment.OverrideVerifierManifest(ParseManifest(BuildManifest("other", "2.0.0")));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => environment.Installer.InstallAsync(request));

        Assert.Contains("验签结果不一致", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(environment.VersionDirectory("other", "2.0.0")));
        Assert.Empty(environment.StagingDirectories());
    }

    [Fact]
    public async Task InstallAsync_WhenVerifierManifestRuntimeDiffers_RejectsPackage()
    {
        using var environment = new InstallerTestEnvironment();
        var request = environment.CreateRequest(BuildPackage(
            ("manifest.json", BuildManifest("sample", "2.0.0")),
            ("bin/tool.exe", "payload")));
        environment.OverrideVerifierManifest(ParseManifest(BuildManifest("sample", "2.0.0", "bin/other.exe")));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => environment.Installer.InstallAsync(request));

        Assert.Contains("验签结果不一致", exception.Message, StringComparison.Ordinal);
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
    public async Task InstallAsync_TwoInstallerInstancesInstallSamePackageConcurrently_IsIdempotent()
    {
        using var ready = new CountdownEvent(2);
        using var environment = new InstallerTestEnvironment(onVerify: _ =>
        {
            ready.Signal();
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)), "两个安装器没有同时到达验签边界。");
        });
        var request = environment.CreateRequest(BuildPackage(
            ("manifest.json", BuildManifest("sample", "2.0.0")),
            ("bin/tool.exe", new string('x', 8 * 1024 * 1024))));
        var otherRegistry = new ExtensionRegistry(environment.ExtensionsRoot, new StubHealthChecker(null));
        var otherInstaller = new ExtensionInstaller(environment.ExtensionsRoot, environment.Verifier, otherRegistry);

        var firstTask = Task.Run(() => environment.Installer.InstallAsync(request));
        var secondTask = Task.Run(() => otherInstaller.InstallAsync(request));
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Single(results, result => !result.AlreadyInstalled);
        Assert.Single(results, result => result.AlreadyInstalled);
        Assert.True(File.Exists(Path.Combine(environment.VersionDirectory("sample", "2.0.0"), "package.json")));
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
    public async Task InstallAsync_ExistingVersionWithoutPackageMetadata_IsRejected()
    {
        using var environment = new InstallerTestEnvironment();
        var package = BuildPackage(("manifest.json", BuildManifest("sample", "2.0.0")), ("bin/tool.exe", "payload"));
        var request = environment.CreateRequest(package);
        environment.WriteInstalledVersion("sample", "2.0.0", Sha256(package));
        File.Delete(environment.MetadataPath("sample", "2.0.0"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => environment.Installer.InstallAsync(request));

        Assert.Contains("缺少不可变 package.json", exception.Message, StringComparison.Ordinal);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(environment.VersionDirectory("sample", "2.0.0"), "bin", "tool.exe")));
    }

    [Fact]
    public async Task InstallAsync_ExistingVersionWithInvalidPackageMetadata_IsRejected()
    {
        using var environment = new InstallerTestEnvironment();
        var package = BuildPackage(("manifest.json", BuildManifest("sample", "2.0.0")), ("bin/tool.exe", "payload"));
        var request = environment.CreateRequest(package);
        environment.WriteInstalledVersion("sample", "2.0.0", Sha256(package));
        File.WriteAllText(environment.MetadataPath("sample", "2.0.0"), "{\"schemaVersion\":2,\"sha256\":\"bad\"}");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => environment.Installer.InstallAsync(request));

        Assert.Contains("不符合 schema v2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAsync_ExistingVersionMetadataWithUnknownField_IsRejected()
    {
        using var environment = new InstallerTestEnvironment();
        var package = BuildPackage(("manifest.json", BuildManifest("sample", "2.0.0")), ("bin/tool.exe", "payload"));
        var request = environment.CreateRequest(package);
        environment.WriteInstalledVersion("sample", "2.0.0", Sha256(package));
        File.WriteAllText(
            environment.MetadataPath("sample", "2.0.0"),
            JsonSerializer.Serialize(new { schemaVersion = 2, sha256 = Sha256(package), unexpected = true }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => environment.Installer.InstallAsync(request));

        Assert.Contains("package.json 无效", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAsync_WhenSuccessfulIdempotentCleanupFails_ReturnsChineseFailure()
    {
        using var environment = new InstallerTestEnvironment();
        var request = environment.CreateRequest(BuildPackage(
            ("manifest.json", BuildManifest("sample", "2.0.0")),
            ("bin/tool.exe", "payload")));
        await environment.Installer.InstallAsync(request);
        var cleaner = new ThrowingStagingCleaner(new IOException("测试暂存目录被占用。"));
        var installer = environment.CreateAdditionalInstaller(cleaner);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(request));

        Assert.Contains("安装已完成", exception.Message, StringComparison.Ordinal);
        Assert.Contains("清理暂存目录失败", exception.Message, StringComparison.Ordinal);
        Assert.IsType<IOException>(exception.InnerException);
        Assert.NotNull(cleaner.LastPath);
        Assert.True(Directory.Exists(cleaner.LastPath));
    }

    [Fact]
    public async Task InstallAsync_WhenInstallAndCleanupBothFail_PreservesBothExceptions()
    {
        var cleaner = new ThrowingStagingCleaner(new IOException("测试暂存目录被占用。"));
        using var environment = new InstallerTestEnvironment(stagingCleaner: cleaner);
        var request = environment.CreateRequest(BuildPackage(
            ("manifest.json", BuildManifest("sample", "2.0.0")),
            ("../escape.txt", "malicious")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => environment.Installer.InstallAsync(request));

        Assert.Contains("安装失败", exception.Message, StringComparison.Ordinal);
        Assert.Contains("清理暂存目录也失败", exception.Message, StringComparison.Ordinal);
        var aggregate = Assert.IsType<AggregateException>(exception.InnerException);
        Assert.Contains(aggregate.InnerExceptions, item => item is InvalidDataException && item.Message.Contains("路径穿越", StringComparison.Ordinal));
        Assert.Contains(aggregate.InnerExceptions, item => item is IOException && item.Message.Contains("被占用", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAsync_WhenActivationFails_PreservesActiveAndRollbackVersionDirectories()
    {
        using var environment = new InstallerTestEnvironment(failingHealthVersion: "2.0.0");
        environment.WriteInstalledVersion("sample", "0.9.0", new string('0', 64));
        environment.WriteInstalledVersion("sample", "1.0.0", new string('a', 64));
        environment.WriteHealthyCurrent("sample", "1.0.0", new string('a', 64));
        environment.WriteHealthyBackup("sample", "0.9.0", new string('0', 64));
        await environment.Registry.LoadAsync();
        var request = environment.CreateRequest(BuildPackage(
            ("manifest.json", BuildManifest("sample", "2.0.0")),
            ("bin/tool.exe", "candidate")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => environment.Installer.InstallAsync(request));

        Assert.Contains("激活失败", exception.Message, StringComparison.Ordinal);
        var current = ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(environment.CurrentPath("sample")));
        var backup = ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(environment.BackupPath("sample")));
        Assert.Equal("1.0.0", current.Version);
        Assert.Equal(ExtensionActivationState.Healthy, current.State);
        Assert.Equal("1.0.0", backup.Version);
        Assert.True(File.Exists(environment.MetadataPath("sample", "0.9.0")));
        Assert.True(File.Exists(environment.MetadataPath("sample", "1.0.0")));
        Assert.True(File.Exists(environment.MetadataPath("sample", "2.0.0")));
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

    private static byte[] BuildPackageWithLinkedEntry(string manifest, int externalAttributes)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.NoCompression);
            using (var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false), leaveOpen: false))
                writer.Write(manifest);

            var linkedEntry = archive.CreateEntry("linked-file", CompressionLevel.NoCompression);
            linkedEntry.ExternalAttributes = externalAttributes;
            using var linkedWriter = new StreamWriter(linkedEntry.Open(), new UTF8Encoding(false), leaveOpen: false);
            linkedWriter.Write("target");
        }

        return output.ToArray();
    }

    private static string BuildManifest(string id, string version, string entry = "bin/tool.exe") => $$"""
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
            "entry": "{{entry}}"
          },
          "capabilities": ["analysis.engine"],
          "permissions": [],
          "dependencies": []
        }
        """;

    private static ExtensionManifest ParseManifest(string json)
        => ExtensionManifestParser.Parse(
            json,
            Path.Combine(Path.GetTempPath(), "HephaestusWorkbench", "InstallerTests"));

    private static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class InstallerTestEnvironment : IDisposable
    {
        public InstallerTestEnvironment(
            string? verifierError = null,
            string? failingHealthVersion = null,
            Action<ExtensionPackageVerificationRequest>? onVerify = null,
            IExtensionStagingCleaner? stagingCleaner = null)
        {
            Root = Path.Combine(Path.GetTempPath(), $"hephaestus-installer-tests-{Guid.NewGuid():N}");
            ExtensionsRoot = Path.Combine(Root, "Extensions");
            Directory.CreateDirectory(ExtensionsRoot);
            Verifier = new StubPackageVerifier(verifierError, onVerify);
            Registry = new ExtensionRegistry(ExtensionsRoot, new StubHealthChecker(failingHealthVersion));
            Installer = new ExtensionInstaller(ExtensionsRoot, Verifier, Registry, stagingCleaner);
        }

        public string Root { get; }
        public string ExtensionsRoot { get; }
        public StubPackageVerifier Verifier { get; }
        public ExtensionRegistry Registry { get; }
        public ExtensionInstaller Installer { get; }

        public ExtensionInstaller CreateAdditionalInstaller(IExtensionStagingCleaner? stagingCleaner = null)
            => new(ExtensionsRoot, Verifier, Registry, stagingCleaner);

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
            Verifier.Result = new ExtensionPackageVerificationResult
            {
                Manifest = manifest,
                TrustedKeyId = "test-key",
                PackageSha256 = Sha256(packageBytes)
            };
            return request;
        }

        public void OverrideVerifierSha(string sha256)
        {
            var result = Verifier.Result ?? throw new InvalidOperationException("测试未配置验签结果。");
            Verifier.Result = new ExtensionPackageVerificationResult
            {
                Manifest = result.Manifest,
                TrustedKeyId = result.TrustedKeyId,
                PackageSha256 = sha256
            };
        }

        public void OverrideVerifierManifest(ExtensionManifest manifest)
        {
            var result = Verifier.Result ?? throw new InvalidOperationException("测试未配置验签结果。");
            Verifier.Result = new ExtensionPackageVerificationResult
            {
                Manifest = manifest,
                TrustedKeyId = result.TrustedKeyId,
                PackageSha256 = result.PackageSha256
            };
        }

        public string VersionDirectory(string id, string version)
            => Path.Combine(ExtensionsRoot, id, version);

        public string MetadataPath(string id, string version)
            => Path.Combine(VersionDirectory(id, version), "package.json");

        public string CurrentPath(string id)
            => Path.Combine(ExtensionsRoot, id, "current.json");

        public string BackupPath(string id)
            => Path.Combine(ExtensionsRoot, id, "current.json.bak");

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
            => WriteCurrentDocument(CurrentPath(id), id, version, sha256);

        public void WriteHealthyBackup(string id, string version, string sha256)
            => WriteCurrentDocument(BackupPath(id), id, version, sha256);

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }

        private static void WriteCurrentDocument(string path, string id, string version, string sha256)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 2,
                    id,
                    version,
                    packageSha256 = sha256,
                    state = "healthy"
                }));
        }

        private static ExtensionManifest ReadManifestForStub(byte[] packageBytes)
        {
            using var stream = new MemoryStream(packageBytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = archive.GetEntry("manifest.json") ?? archive.Entries.First(item => item.FullName.EndsWith("manifest.json", StringComparison.Ordinal));
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return ParseManifest(reader.ReadToEnd());
        }
    }

    private sealed class StubPackageVerifier(
        string? error,
        Action<ExtensionPackageVerificationRequest>? onVerify) : IExtensionPackageVerifier
    {
        public ExtensionPackageVerificationResult? Result { get; set; }

        public Task<ExtensionPackageVerificationResult> VerifyAsync(
            ExtensionPackageVerificationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onVerify?.Invoke(request);
            if (error is not null) throw new InvalidDataException(error);
            return Task.FromResult(Result ?? throw new InvalidOperationException("测试未配置验签结果。"));
        }
    }

    private sealed class ThrowingStagingCleaner(Exception exception) : IExtensionStagingCleaner
    {
        public string? LastPath { get; private set; }

        public void Delete(string stagingDirectory)
        {
            LastPath = stagingDirectory;
            throw exception;
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
