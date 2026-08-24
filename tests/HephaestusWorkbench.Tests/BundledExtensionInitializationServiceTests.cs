using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;
using NSec.Cryptography;

namespace HephaestusWorkbench.Tests;

public sealed class BundledExtensionInitializationServiceTests
{
    [Fact]
    public async Task InitializeAsync_ValidSignedBundle_InstallsHealthyVersion()
    {
        using var environment = new BundleTestEnvironment();
        environment.WriteBundle("2.0.0");

        await environment.Service.InitializeAsync();

        var current = ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(environment.CurrentPath));
        Assert.Equal(ExtensionActivationState.Healthy, current.State);
        Assert.Equal("2.0.0", current.Version);
        Assert.Equal("payload-2.0.0", await File.ReadAllTextAsync(Path.Combine(environment.VersionDirectory("2.0.0"), "bin", "log-analyzer.exe")));
        Assert.Contains("内置扩展 log-analyzer 2.0.0 已安装", File.ReadAllText(environment.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_RepeatedStartup_IsIdempotentAndRechecksInstalledPayload()
    {
        using var environment = new BundleTestEnvironment();
        environment.WriteBundle("2.0.0");
        await environment.Service.InitializeAsync();

        await environment.Service.InitializeAsync();
        File.WriteAllText(Path.Combine(environment.VersionDirectory("2.0.0"), "bin", "log-analyzer.exe"), "tampered");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => environment.Service.InitializeAsync());
        Assert.Contains("payload", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InitializeAsync_TamperedZip_IsRejected()
    {
        using var environment = new BundleTestEnvironment();
        environment.WriteBundle("2.0.0");
        var bytes = await File.ReadAllBytesAsync(environment.AssetPath);
        bytes[^1] ^= 0x01;
        await File.WriteAllBytesAsync(environment.AssetPath, bytes);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => environment.Service.InitializeAsync());

        Assert.Contains("SHA-256", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(environment.CurrentPath));
    }

    [Fact]
    public async Task InitializeAsync_BundleRequiresNewerHost_IsRejectedBeforeInstallation()
    {
        using var environment = new BundleTestEnvironment(hostVersion: "2.0.0");
        environment.WriteBundle("2.0.0", minHostVersion: "3.0.0");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => environment.Service.InitializeAsync());

        Assert.Contains("最低宿主版本", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(environment.CurrentPath));
    }

    [Fact]
    public async Task InitializeAsync_UnknownSigningKey_IsRejected()
    {
        using var environment = new BundleTestEnvironment();
        environment.WriteBundle("2.0.0", keyId: "unknown-key");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => environment.Service.InitializeAsync());

        Assert.Contains("不受信任", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(environment.CurrentPath));
    }

    [Fact]
    public async Task InitializeAsync_MissingAsset_ReportsAbsolutePath()
    {
        using var environment = new BundleTestEnvironment();
        environment.WriteBundle("2.0.0");
        File.Delete(environment.AssetPath);

        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => environment.Service.InitializeAsync());

        Assert.Contains(Path.GetFullPath(environment.AssetPath), error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InitializeAsync_DeclaredSizeMismatch_IsRejectedBeforeInstallation()
    {
        using var environment = new BundleTestEnvironment();
        environment.WriteBundle("2.0.0");
        environment.RewriteDeclaredSize(new FileInfo(environment.AssetPath).Length - 1);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => environment.Service.InitializeAsync());

        Assert.Contains("大小", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(environment.CurrentPath));
    }

    [Fact]
    public async Task InitializeAsync_WhenHealthyInstalledVersionIsNewer_DoesNotDowngrade()
    {
        using var environment = new BundleTestEnvironment();
        environment.WriteBundle("3.0.0");
        await environment.Service.InitializeAsync();
        environment.WriteBundle("2.0.0");

        await environment.Service.InitializeAsync();

        var current = ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(environment.CurrentPath));
        Assert.Equal("3.0.0", current.Version);
        Assert.Contains("高于安装包内置版本", File.ReadAllText(environment.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_WhenInstalledVersionIsNewer_StillRejectsTamperedBundle()
    {
        using var environment = new BundleTestEnvironment();
        environment.WriteBundle("3.0.0");
        await environment.Service.InitializeAsync();
        environment.WriteBundle("2.0.0");
        var bytes = await File.ReadAllBytesAsync(environment.AssetPath);
        bytes[^1] ^= 0x01;
        await File.WriteAllBytesAsync(environment.AssetPath, bytes);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => environment.Service.InitializeAsync());

        Assert.Contains("SHA-256", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("3.0.0", ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(environment.CurrentPath)).Version);
    }

    [Fact]
    public async Task InitializeAsync_WhenInstalledVersionIsNewerButEntryIsMissing_BlocksStartup()
    {
        using var environment = new BundleTestEnvironment();
        environment.WriteBundle("3.0.0");
        await environment.Service.InitializeAsync();
        File.Delete(Path.Combine(environment.VersionDirectory("3.0.0"), "bin", "log-analyzer.exe"));
        environment.WriteBundle("2.0.0");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => environment.Service.InitializeAsync());

        Assert.Contains("入口文件不存在", error.Message, StringComparison.Ordinal);
        Assert.Equal("3.0.0", ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(environment.CurrentPath)).Version);
    }

    [Fact]
    public async Task InitializeAsync_WhenInstalledVersionIsNewerButRequiresNewerHost_BlocksStartup()
    {
        using var environment = new BundleTestEnvironment(hostVersion: "2.0.0");
        environment.WriteInstalledVersion("3.0.0", "thelinyue", ExtensionKind.Analysis, minHostVersion: "3.0.0");
        environment.WriteBundle("2.0.0");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => environment.Service.InitializeAsync());

        Assert.Contains("最低宿主版本", error.Message, StringComparison.Ordinal);
        Assert.Equal("3.0.0", ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(environment.CurrentPath)).Version);
    }
    [Fact]
    public async Task InitializeAsync_InstalledIdentityConflict_BlocksStartup()
    {
        using var environment = new BundleTestEnvironment();
        environment.WriteInstalledVersion("3.0.0", "other-publisher", ExtensionKind.Analysis);
        environment.WriteBundle("2.0.0");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => environment.Service.InitializeAsync());

        Assert.Contains("身份", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_DoesNotModifyExtensionPreferences()
    {
        using var environment = new BundleTestEnvironment();
        var settingsPath = Path.Combine(environment.Root, "Config", "extensions.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        const string original = "{\"schemaVersion\":2,\"enabled\":{\"log-analyzer\":false}}";
        await File.WriteAllTextAsync(settingsPath, original);
        environment.WriteBundle("2.0.0");

        await environment.Service.InitializeAsync();

        Assert.Equal(original, await File.ReadAllTextAsync(settingsPath));
    }

    [Fact]
    public async Task InitializeAsync_WhenRequiredBundleRootIsMissing_BlocksStartup()
    {
        using var environment = new BundleTestEnvironment(createBundleRoot: false);

        var error = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => environment.Service.InitializeAsync());

        Assert.Contains(Path.GetFullPath(environment.BundleRoot), error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InitializeAsync_BundleRootReparsePoint_IsRejected()
    {
        using var environment = new BundleTestEnvironment();
        environment.WriteBundle("2.0.0");
        environment.ReplaceBundleRootWithDirectoryLink();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => environment.Service.InitializeAsync());

        Assert.Contains("重解析点", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bundled-extensions.json")]
    [InlineData("log-analyzer.zip")]
    public async Task InitializeAsync_ManifestOrAssetReparsePoint_IsRejected(string fileName)
    {
        using var environment = new BundleTestEnvironment();
        environment.WriteBundle("2.0.0");
        environment.ReplaceWithFilePathJunction(Path.Combine(environment.BundleRoot, fileName));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => environment.Service.InitializeAsync());

        Assert.Contains("重解析点", error.Message, StringComparison.Ordinal);
    }

    private sealed class BundleTestEnvironment : IDisposable
    {
        private readonly Key _key = Key.Create(SignatureAlgorithm.Ed25519);
        private readonly ExtensionRegistry _registry;
        private readonly List<string> _reparsePaths = [];
        private long _declaredSize;
        private string _version = "2.0.0";
        private string _minHostVersion = "2.0.0";
        private string _keyId = "test-key";

        public BundleTestEnvironment(bool createBundleRoot = true, string hostVersion = "2.0.0")
        {
            Root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
            BundleRoot = Path.Combine(Root, "App", "BundledExtensions");
            ExtensionsRoot = Path.Combine(Root, "Data", "Extensions");
            if (createBundleRoot)
                Directory.CreateDirectory(BundleRoot);
            Directory.CreateDirectory(ExtensionsRoot);

            var trustedKey = new TrustedPublisherKey
            {
                KeyId = "test-key",
                PublisherId = "thelinyue",
                PublicKeyBase64 = Convert.ToBase64String(_key.PublicKey.Export(KeyBlobFormat.RawPublicKey)),
                Scope = new ExtensionTrustScope
                {
                    AllowedKinds = [ExtensionKind.Analysis],
                    Permissions = []
                }
            };
            var otherTrustedKey = new TrustedPublisherKey
            {
                KeyId = "other-test-key",
                PublisherId = "other-publisher",
                PublicKeyBase64 = Convert.ToBase64String(new byte[32]),
                Scope = new ExtensionTrustScope
                {
                    AllowedKinds = [ExtensionKind.Analysis],
                    Permissions = []
                }
            };
            var trustStore = new ExtensionTrustStore([trustedKey, otherTrustedKey]);
            var healthChecker = new ExtensionHealthChecker();
            _registry = new ExtensionRegistry(ExtensionsRoot, healthChecker, trustStore);
            var verifier = new ExtensionPackageVerifier(trustStore);
            var installer = new ExtensionInstaller(ExtensionsRoot, verifier, _registry);
            Service = new BundledExtensionInitializationService(
                BundleRoot,
                installer,
                _registry,
                verifier,
                healthChecker,
                new ExtensionHostCompatibility(hostVersion),
                new WorkbenchLogger(Path.Combine(Root, "Data")));
        }

        public string Root { get; }
        public string BundleRoot { get; }
        public string ExtensionsRoot { get; }
        public string AssetPath => Path.Combine(BundleRoot, "log-analyzer.zip");
        public string CurrentPath => Path.Combine(ExtensionsRoot, "log-analyzer", "current.json");
        public string LogPath => Path.Combine(Root, "Data", "Logs", "workbench.log");
        public BundledExtensionInitializationService Service { get; }

        public string VersionDirectory(string version) => Path.Combine(ExtensionsRoot, "log-analyzer", version);

        public void WriteBundle(string version, string keyId = "test-key", string minHostVersion = "2.0.0")
        {
            Directory.CreateDirectory(BundleRoot);
            _version = version;
            _minHostVersion = minHostVersion;
            _keyId = keyId;
            var package = BuildPackage(version, minHostVersion);
            File.WriteAllBytes(AssetPath, package);
            _declaredSize = package.LongLength;
            WriteManifest(package);
        }

        public void RewriteDeclaredSize(long size)
        {
            _declaredSize = size;
            WriteManifest(File.ReadAllBytes(AssetPath));
        }

        public void WriteInstalledVersion(string version, string publisherId, ExtensionKind kind, string minHostVersion = "2.0.0")
        {
            var directory = VersionDirectory(version);
            Directory.CreateDirectory(Path.Combine(directory, "bin"));
            File.WriteAllText(Path.Combine(directory, "bin", "log-analyzer.exe"), "existing");
            File.WriteAllText(Path.Combine(directory, "manifest.json"), BuildManifest(version, publisherId, kind, minHostVersion));
            File.WriteAllText(
                Path.Combine(directory, "package.json"),
                JsonSerializer.Serialize(new { schemaVersion = 2, sha256 = new string('a', 64) }));
            Directory.CreateDirectory(Path.GetDirectoryName(CurrentPath)!);
            File.WriteAllText(CurrentPath, JsonSerializer.Serialize(new ExtensionCurrentDocument
            {
                SchemaVersion = 2,
                Id = "log-analyzer",
                Version = version,
                PackageSha256 = new string('a', 64),
                TrustedKeyId = string.Equals(publisherId, "other-publisher", StringComparison.Ordinal)
                    ? "other-test-key"
                    : "test-key",
                State = ExtensionActivationState.Healthy
            }));
        }

        private void WriteManifest(byte[] package)
        {
            var signature = Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(_key, package));
            var sha = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
            File.WriteAllText(Path.Combine(BundleRoot, "bundled-extensions.json"), $$"""
                {
                  "schemaVersion": 2,
                  "extensions": [
                    {
                      "id": "log-analyzer",
                      "name": "日志分析",
                      "description": "离线日志分析扩展",
                      "publisherId": "thelinyue",
                      "kind": "analysis",
                      "asset": "log-analyzer.zip",
                      "release": {
                        "version": "{{_version}}",
                        "minHostVersion": "{{_minHostVersion}}",
                        "url": "https://example.invalid/log-analyzer.zip",
                        "size": {{_declaredSize}},
                        "sha256": "{{sha}}",
                        "signature": {
                          "keyId": "{{_keyId}}",
                          "signature": "{{signature}}"
                        }
                      }
                    }
                  ]
                }
                """);
        }

        public void ReplaceBundleRootWithDirectoryLink()
        {
            var target = Path.Combine(Root, "BundleTarget");
            Directory.Move(BundleRoot, target);
            if (!TryCreateJunction(BundleRoot, target, out var error))
            {
                Directory.Move(target, BundleRoot);
                throw new InvalidOperationException($"测试无法创建目录 junction：{error}");
            }
        }

        public void ReplaceWithFilePathJunction(string path)
        {
            var original = Path.Combine(Root, $"original-{Guid.NewGuid():N}.bin");
            var targetDirectory = Path.Combine(Root, $"junction-target-{Guid.NewGuid():N}");
            File.Move(path, original);
            Directory.CreateDirectory(targetDirectory);
            if (!TryCreateJunction(path, targetDirectory, out var error))
            {
                Directory.Delete(targetDirectory);
                File.Move(original, path);
                throw new InvalidOperationException($"测试无法创建文件路径 junction：{error}");
            }
            _reparsePaths.Add(path);
        }

        private static bool TryCreateJunction(string linkDirectory, string targetDirectory, out string error)
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c mklink /J \"{linkDirectory}\" \"{targetDirectory}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            });
            if (process is null)
            {
                error = "无法启动 cmd.exe。";
                return false;
            }

            process.WaitForExit();
            error = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
            return process.ExitCode == 0 && Directory.Exists(linkDirectory) &&
                   File.GetAttributes(linkDirectory).HasFlag(FileAttributes.ReparsePoint);
        }

        private static byte[] BuildPackage(string version, string minHostVersion)
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteEntry(archive, "manifest.json", BuildManifest(version, "thelinyue", ExtensionKind.Analysis, minHostVersion));
                WriteEntry(archive, "bin/log-analyzer.exe", $"payload-{version}");
            }
            return stream.ToArray();
        }

        private static void WriteEntry(ZipArchive archive, string name, string content)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            entry.LastWriteTime = new DateTimeOffset(2026, 8, 23, 0, 0, 0, TimeSpan.Zero);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        private static string BuildManifest(string version, string publisherId, ExtensionKind kind, string minHostVersion = "2.0.0")
            => $$"""
                {
                  "schemaVersion": 2,
                  "id": "log-analyzer",
                  "name": "日志分析",
                  "version": "{{version}}",
                  "kind": "{{kind.ToString().ToLowerInvariant()}}",
                  "publisherId": "{{publisherId}}",
                  "hostApiVersion": "1.0",
                  "minHostVersion": "{{minHostVersion}}",
                  "runtime": { "kind": "process", "protocol": "analysis-process-v1", "entry": "bin/log-analyzer.exe" },
                  "capabilities": ["analysis.engine"],
                  "permissions": [],
                  "dependencies": []
                }
                """;

        public void Dispose()
        {
            _key.Dispose();
            foreach (var path in _reparsePaths.OrderByDescending(item => item.Length))
            {
                if (Directory.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                    Directory.Delete(path);
            }
            if (Directory.Exists(BundleRoot) &&
                File.GetAttributes(BundleRoot).HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(BundleRoot);
            }
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
