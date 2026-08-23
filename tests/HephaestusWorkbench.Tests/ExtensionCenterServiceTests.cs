using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class ExtensionCenterServiceTests
{
    [Fact]
    public async Task LoadAsync_MergesRegistrySettingsAndLatestCompatibleStableRelease()
    {
        using var environment = new TestEnvironment(CatalogJson());
        await environment.AddInstalledAsync("log-analyzer", "2.0.0", ExtensionKind.Analysis);
        await environment.Settings.SetEnabledAsync("log-analyzer", false);

        var snapshot = await environment.Service.LoadAsync();

        var entry = Assert.Single(snapshot.Extensions);
        Assert.Equal("log-analyzer", entry.Id);
        Assert.Equal("2.0.0", entry.InstalledManifest?.Version);
        Assert.Equal("2.1.0", entry.AvailableRelease?.Version);
        Assert.True(entry.HasUpdate);
        Assert.False(entry.Enabled);
        Assert.True(entry.IsCompatible);
        Assert.False(snapshot.IsCatalogFromCache);
    }

    [Fact]
    public async Task LoadAsync_WhenCatalogUnavailable_StillReturnsInstalledExtensionsWithChineseWarning()
    {
        using var environment = new TestEnvironment(null);
        await environment.AddInstalledAsync("rule-editor", "2.0.0", ExtensionKind.Workspace);

        var snapshot = await environment.Service.LoadAsync();

        var entry = Assert.Single(snapshot.Extensions);
        Assert.Equal("rule-editor", entry.Id);
        Assert.NotNull(entry.InstalledManifest);
        Assert.Null(entry.AvailableRelease);
        Assert.Contains("在线扩展目录", snapshot.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_UsesSemVerAndSkipsPrereleaseAndIncompatibleRelease()
    {
        using var environment = new TestEnvironment(SemVerCatalogJson());
        await environment.AddInstalledAsync("log-analyzer", "2.0.0", ExtensionKind.Analysis);

        var snapshot = await environment.Service.LoadAsync();

        var entry = Assert.Single(snapshot.Extensions);
        Assert.Equal("2.10.0", entry.AvailableRelease?.Version);
        Assert.True(entry.IsCompatible);
        Assert.True(entry.HasUpdate);
    }

    [Fact]
    public async Task InstallAsync_DownloadsCatalogPackageAndRunsVerifiedInstaller()
    {
        var package = CreateAnalysisPackage("log-analyzer", "2.1.0");
        var sha256 = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
        using var environment = new TestEnvironment(InstallCatalogJson(package.Length, sha256), package);

        var result = await environment.Service.InstallAsync(new ExtensionCenterInstallRequest
        {
            ExtensionId = "log-analyzer",
            Version = "2.1.0"
        });

        Assert.Equal("2.1.0", result.Manifest.Version);
        Assert.Equal(1, environment.Handler.PackageRequestCount);
        Assert.Equal(1, environment.Verifier.VerificationCount);
        Assert.Equal("2.1.0", Assert.Single(await environment.Registry.LoadAsync()).Version);
    }

    [Fact]
    public async Task SetEnabledAsync_UpdatesV2PreferenceWithoutChangingInstalledVersion()
    {
        using var environment = new TestEnvironment(CatalogJson());
        await environment.AddInstalledAsync("log-analyzer", "2.0.0", ExtensionKind.Analysis);

        await environment.Service.SetEnabledAsync("log-analyzer", false);
        var snapshot = await environment.Service.LoadAsync();
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(environment.Paths.ExtensionsConfigFile));

        Assert.False(Assert.Single(snapshot.Extensions).Enabled);
        Assert.False(json.RootElement.GetProperty("extensions")[0].TryGetProperty("version", out _));
    }

    private static string SemVerCatalogJson() => """
        {
          "schemaVersion": 2,
          "extensions": [
            {
              "id": "log-analyzer",
              "name": "日志分析",
              "description": "综合日志分析",
              "publisherId": "thelinyue",
              "kind": "analysis",
              "releases": [
                {
                  "version": "9.0.0",
                  "minHostVersion": "3.0.0",
                  "url": "https://example.invalid/incompatible.zip",
                  "size": 10,
                  "sha256": "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
                  "signature": { "keyId": "test-key", "signature": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==" }
                },
                {
                  "version": "3.0.0-beta.1",
                  "minHostVersion": "2.0.0",
                  "url": "https://example.invalid/prerelease.zip",
                  "size": 10,
                  "sha256": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                  "signature": { "keyId": "test-key", "signature": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==" }
                },
                {
                  "version": "2.9.0",
                  "minHostVersion": "2.0.0",
                  "url": "https://example.invalid/2.9.0.zip",
                  "size": 10,
                  "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                  "signature": { "keyId": "test-key", "signature": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==" }
                },
                {
                  "version": "2.10.0",
                  "minHostVersion": "2.0.0",
                  "url": "https://example.invalid/2.10.0.zip",
                  "size": 10,
                  "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "signature": { "keyId": "test-key", "signature": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==" }
                }
              ]
            }
          ]
        }
        """;

    private static string InstallCatalogJson(long size, string sha256) => $$"""
        {
          "schemaVersion": 2,
          "extensions": [
            {
              "id": "log-analyzer",
              "name": "日志分析",
              "description": "综合日志分析",
              "publisherId": "thelinyue",
              "kind": "analysis",
              "releases": [
                {
                  "version": "2.1.0",
                  "minHostVersion": "2.0.0",
                  "url": "https://example.invalid/log-analyzer.zip",
                  "size": {{size}},
                  "sha256": "{{sha256}}",
                  "signature": { "keyId": "test-key", "signature": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==" }
                }
              ]
            }
          ]
        }
        """;

    private static byte[] CreateAnalysisPackage(string id, string version)
    {
        using var target = new MemoryStream();
        using (var archive = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("manifest.json");
            using (var writer = new StreamWriter(manifest.Open(), Encoding.UTF8))
            {
                writer.Write($$"""
                    {
                      "schemaVersion": 2,
                      "id": "{{id}}",
                      "name": "日志分析",
                      "version": "{{version}}",
                      "kind": "analysis",
                      "publisherId": "thelinyue",
                      "hostApiVersion": "1.0",
                      "minHostVersion": "2.0.0",
                      "runtime": { "kind": "process", "protocol": "analysis-process-v1", "entry": "bin/analyzer.exe" },
                      "capabilities": ["analysis.engine"],
                      "permissions": [],
                      "dependencies": []
                    }
                    """);
            }

            var entry = archive.CreateEntry("bin/analyzer.exe");
            using var entryWriter = new StreamWriter(entry.Open(), Encoding.UTF8);
            entryWriter.Write("fixture");
        }

        return target.ToArray();
    }

    private static string CatalogJson() => """
        {
          "schemaVersion": 2,
          "extensions": [
            {
              "id": "log-analyzer",
              "name": "日志分析",
              "description": "综合日志分析",
              "publisherId": "thelinyue",
              "kind": "analysis",
              "releases": [
                {
                  "version": "3.0.0-beta.1",
                  "minHostVersion": "2.0.0",
                  "url": "https://example.invalid/log-analyzer-beta.zip",
                  "size": 10,
                  "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                  "signature": { "keyId": "test-key", "signature": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==" }
                },
                {
                  "version": "2.1.0",
                  "minHostVersion": "2.0.0",
                  "url": "https://example.invalid/log-analyzer.zip",
                  "size": 10,
                  "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "signature": { "keyId": "test-key", "signature": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==" }
                }
              ]
            }
          ]
        }
        """;

    private sealed class TestEnvironment : IDisposable
    {
        public TestEnvironment(string? catalogJson, byte[]? packageBytes = null)
        {
            Root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
            Paths = new DataPaths(Root);
            Logger = new WorkbenchLogger(Root);
            Registry = new ExtensionRegistry(Paths.ExtensionsDirectory, new ExtensionHealthChecker());
            Settings = new ExtensionSettingsStore(Paths);
            Handler = new StubHandler(catalogJson, packageBytes);
            Verifier = new RecordingVerifier();
            var client = new ExtensionCatalogClient(Paths, Logger, new HttpClient(Handler));
            var installer = new ExtensionInstaller(Paths.ExtensionsDirectory, Verifier, Registry);
            Service = new ExtensionCenterService(client, installer, Registry, Settings, Logger, "2.0.0");
        }

        public string Root { get; }
        public DataPaths Paths { get; }
        public WorkbenchLogger Logger { get; }
        public ExtensionRegistry Registry { get; }
        public ExtensionSettingsStore Settings { get; }
        public StubHandler Handler { get; }
        public RecordingVerifier Verifier { get; }
        public ExtensionCenterService Service { get; }

        public async Task AddInstalledAsync(string id, string version, ExtensionKind kind)
        {
            var versionDirectory = Path.Combine(Paths.ExtensionsDirectory, id, version);
            Directory.CreateDirectory(versionDirectory);
            var runtimeKind = kind == ExtensionKind.Workspace ? "web" : "process";
            var protocol = kind == ExtensionKind.Workspace ? "workspace-bridge-v1" : "analysis-process-v1";
            var entry = kind == ExtensionKind.Workspace ? "index.html" : "bin/analyzer.exe";
            var capability = kind == ExtensionKind.Workspace ? "workspace.page" : "analysis.engine";
            await File.WriteAllTextAsync(Path.Combine(versionDirectory, "manifest.json"), $$"""
                {
                  "schemaVersion": 2,
                  "id": "{{id}}",
                  "name": "测试扩展",
                  "version": "{{version}}",
                  "kind": "{{kind.ToString().ToLowerInvariant()}}",
                  "publisherId": "thelinyue",
                  "hostApiVersion": "1.0",
                  "minHostVersion": "2.0.0",
                  "runtime": { "kind": "{{runtimeKind}}", "protocol": "{{protocol}}", "entry": "{{entry}}" },
                  "capabilities": ["{{capability}}"],
                  "permissions": [],
                  "dependencies": []
                }
                """);
            var entryPath = Path.Combine(versionDirectory, entry.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
            await File.WriteAllTextAsync(entryPath, "fixture");
            var current = new ExtensionCurrentDocument
            {
                SchemaVersion = 2,
                Id = id,
                Version = version,
                PackageSha256 = new string('c', 64),
                State = ExtensionActivationState.Healthy
            };
            await File.WriteAllTextAsync(
                Path.Combine(Paths.ExtensionsDirectory, id, "current.json"),
                JsonSerializer.Serialize(current));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class StubHandler(string? catalogJson, byte[]? packageBytes) : HttpMessageHandler
    {
        public int PackageRequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("catalog.json", StringComparison.Ordinal) == true)
            {
                if (catalogJson is null) throw new HttpRequestException("offline");
                return Task.FromResult(CreateResponse(
                    new StringContent(catalogJson, Encoding.UTF8, "application/json"),
                    request.RequestUri));
            }

            PackageRequestCount++;
            if (packageBytes is null) throw new HttpRequestException("package unavailable");
            return Task.FromResult(CreateResponse(new ByteArrayContent(packageBytes), request.RequestUri));
        }

        private static HttpResponseMessage CreateResponse(HttpContent content, Uri? requestUri)
            => new(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, requestUri)
            };
    }

    private sealed class RecordingVerifier : IExtensionPackageVerifier
    {
        public int VerificationCount { get; private set; }

        public Task<ExtensionPackageVerificationResult> VerifyAsync(
            ExtensionPackageVerificationRequest request,
            CancellationToken cancellationToken = default)
        {
            VerificationCount++;
            var manifest = new ExtensionManifest
            {
                SchemaVersion = 2,
                Id = request.CatalogItem.Id,
                Name = request.CatalogItem.Name,
                Version = request.Release.Version,
                Kind = request.CatalogItem.Kind,
                PublisherId = request.CatalogItem.PublisherId,
                HostApiVersion = "1.0",
                MinHostVersion = request.Release.MinHostVersion,
                Runtime = new ExtensionRuntime
                {
                    Kind = ExtensionRuntimeKind.Process,
                    Protocol = "analysis-process-v1",
                    Entry = "bin/analyzer.exe"
                },
                Capabilities = ["analysis.engine"],
                Permissions = [],
                Dependencies = []
            };
            return Task.FromResult(new ExtensionPackageVerificationResult
            {
                Manifest = manifest,
                TrustedKeyId = request.Release.Signature.KeyId,
                PackageSha256 = Convert.ToHexString(SHA256.HashData(request.PackageBytes)).ToLowerInvariant()
            });
        }
    }
}
