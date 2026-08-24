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
        Assert.True(entry.IsCatalogListed);
        Assert.True(entry.HasCompatibleRelease);
        Assert.True(entry.IsInstalledVersionCompatible);
        Assert.False(entry.HasIdentityConflict);
        Assert.False(snapshot.IsCatalogFromCache);
    }

    [Fact]
    public async Task LoadAsync_WhenStartupAutoCheckUpdatesIsDisabled_UsesValidatedCacheWithoutNetworkRequest()
    {
        using var environment = new TestEnvironment(CatalogJson());
        Directory.CreateDirectory(Path.GetDirectoryName(environment.Paths.ExtensionCatalogCacheFile)!);
        await File.WriteAllTextAsync(environment.Paths.ExtensionCatalogCacheFile, CatalogJson());

        var snapshot = await LoadForStartupAsync(environment.Service, autoCheckUpdates: false);

        Assert.Equal(0, environment.Handler.CatalogRequestCount);
        Assert.True(snapshot.IsCatalogFromCache);
        Assert.Single(snapshot.Extensions);
    }

    [Fact]
    public async Task LoadAsync_WhenStartupAutoCheckUpdatesIsDisabledAndCacheIsMissing_StillShowsInstalledExtensionWithChineseExplanation()
    {
        using var environment = new TestEnvironment(CatalogJson());
        await environment.AddInstalledAsync("log-analyzer", "2.0.0", ExtensionKind.Analysis);

        var snapshot = await LoadForStartupAsync(environment.Service, autoCheckUpdates: false);

        Assert.Equal(0, environment.Handler.CatalogRequestCount);
        Assert.Equal("log-analyzer", Assert.Single(snapshot.Extensions).Id);
        Assert.Contains("自动检查扩展更新已关闭", snapshot.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_WhenStartupAutoCheckUpdatesIsEnabled_RefreshesOnlineCatalog()
    {
        using var environment = new TestEnvironment(CatalogJson());

        var snapshot = await LoadForStartupAsync(environment.Service, autoCheckUpdates: true);

        Assert.Equal(1, environment.Handler.CatalogRequestCount);
        Assert.False(snapshot.IsCatalogFromCache);
        Assert.Single(snapshot.Extensions);
    }

    [Fact]
    public async Task RefreshAsync_AlwaysRefreshesOnlineCatalogRegardlessOfStartupPolicy()
    {
        using var environment = new TestEnvironment(CatalogJson());

        var snapshot = await RefreshManuallyAsync(environment.Service);

        Assert.Equal(1, environment.Handler.CatalogRequestCount);
        Assert.False(snapshot.IsCatalogFromCache);
        Assert.Single(snapshot.Extensions);
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
        Assert.False(entry.IsCatalogListed);
        Assert.False(entry.HasCompatibleRelease);
        Assert.True(entry.IsInstalledVersionCompatible);
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
        Assert.True(entry.IsCatalogListed);
        Assert.True(entry.HasCompatibleRelease);
        Assert.True(entry.IsInstalledVersionCompatible);
        Assert.True(entry.HasUpdate);
    }

    [Fact]
    public async Task Compatibility_WhenMinHostUsesLargeNumericIdentifier_RuntimeMatchesExtensionCenter()
    {
        const string minHostVersion = "1.999999999999999999999.0";
        using var environment = new TestEnvironment(null);
        await environment.AddInstalledAsync(
            "log-analyzer",
            "2.0.0",
            ExtensionKind.Analysis,
            minHostVersion: minHostVersion);

        var entry = Assert.Single((await environment.Service.LoadAsync()).Extensions);

        Assert.True(entry.IsInstalledVersionCompatible);
        Assert.True(new ExtensionHostCompatibility("2.0.0").IsCompatible(minHostVersion));
    }

    [Theory]
    [InlineData("other-publisher", ExtensionKind.Analysis)]
    [InlineData("thelinyue", ExtensionKind.Workspace)]
    public async Task LoadAsync_WhenInstalledIdentityConflictsWithCatalog_PreservesLocalAndBlocksUpdate(
        string installedPublisherId,
        ExtensionKind installedKind)
    {
        using var environment = new TestEnvironment(CatalogJson());
        await environment.AddInstalledAsync(
            "log-analyzer",
            "2.0.0",
            installedKind,
            installedPublisherId);

        var snapshot = await environment.Service.LoadAsync();

        var entry = Assert.Single(snapshot.Extensions);
        Assert.Equal(installedPublisherId, entry.PublisherId);
        Assert.Equal(installedKind, entry.Kind);
        Assert.Equal("测试扩展", entry.Name);
        Assert.NotNull(entry.InstalledManifest);
        Assert.True(entry.IsCatalogListed);
        Assert.True(entry.HasIdentityConflict);
        Assert.True(entry.HasCompatibleRelease);
        Assert.Null(entry.AvailableRelease);
        Assert.False(entry.HasUpdate);
        Assert.Contains("身份冲突", snapshot.Warning, StringComparison.Ordinal);
        Assert.Contains(environment.LogMessages, message =>
            message.Contains("身份冲突", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_CatalogListedWithoutCompatibleRelease_ReportsSeparateCompatibilityStates()
    {
        using var environment = new TestEnvironment(IncompatibleOnlyCatalogJson());

        var catalogOnly = Assert.Single((await environment.Service.LoadAsync()).Extensions);

        Assert.True(catalogOnly.IsCatalogListed);
        Assert.False(catalogOnly.HasCompatibleRelease);
        Assert.Null(catalogOnly.IsInstalledVersionCompatible);
        Assert.False(catalogOnly.HasIdentityConflict);
        Assert.Null(catalogOnly.AvailableRelease);
    }

    [Fact]
    public async Task LoadAsync_InstalledCompatibleVersionIsNotMisreportedWhenCatalogHasNoCompatibleRelease()
    {
        using var environment = new TestEnvironment(IncompatibleOnlyCatalogJson());
        await environment.AddInstalledAsync("log-analyzer", "2.0.0", ExtensionKind.Analysis);

        var entry = Assert.Single((await environment.Service.LoadAsync()).Extensions);

        Assert.True(entry.IsCatalogListed);
        Assert.False(entry.HasCompatibleRelease);
        Assert.True(entry.IsInstalledVersionCompatible);
        Assert.Null(entry.AvailableRelease);
        Assert.False(entry.HasUpdate);
    }

    [Theory]
    [InlineData("other-publisher", ExtensionKind.Analysis)]
    [InlineData("thelinyue", ExtensionKind.Workspace)]
    public async Task InstallAsync_WhenInstalledIdentityConflictsWithCatalog_RejectsBeforeDownload(
        string installedPublisherId,
        ExtensionKind installedKind)
    {
        using var environment = new TestEnvironment(CatalogJson());
        await environment.AddInstalledAsync(
            "log-analyzer",
            "2.0.0",
            installedKind,
            installedPublisherId);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            environment.Service.InstallAsync(new ExtensionCenterInstallRequest
            {
                ExtensionId = "log-analyzer",
                Version = "2.1.0"
            }));

        Assert.Contains("身份冲突", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, environment.Handler.PackageRequestCount);
        Assert.Equal(0, environment.Verifier.VerificationCount);
        Assert.Contains(environment.LogMessages, message =>
            message.Contains("身份冲突", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAsync_WhenActiveIdentityChangesDuringVerification_RejectsInsideActivationTransaction()
    {
        var package = CreateAnalysisPackage("log-analyzer", "2.1.0");
        var sha256 = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
        using var environment = new TestEnvironment(InstallCatalogJson(package.Length, sha256), package);
        await environment.AddInstalledAsync("log-analyzer", "2.0.0", ExtensionKind.Analysis);
        environment.Verifier.PauseVerification = true;

        var installation = environment.Service.InstallAsync(new ExtensionCenterInstallRequest
        {
            ExtensionId = "log-analyzer",
            Version = "2.1.0"
        });
        await environment.Verifier.VerificationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await environment.AddInstalledAsync(
            "log-analyzer",
            "2.0.5",
            ExtensionKind.Analysis,
            publisherId: "other-publisher");
        environment.Verifier.ContinueVerification.TrySetResult();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => installation);
        var current = ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(
            Path.Combine(environment.Paths.ExtensionsDirectory, "log-analyzer", "current.json")));

        Assert.Contains("身份冲突", exception.Message, StringComparison.Ordinal);
        Assert.Equal("2.0.5", current.Version);
        Assert.Equal(ExtensionActivationState.Healthy, current.State);
    }

    [Fact]
    public async Task LoadAsync_InstalledVersionAboveHost_ReportsInstalledIncompatibleOnly()
    {
        using var environment = new TestEnvironment(null);
        await environment.AddInstalledAsync(
            "rule-editor",
            "3.0.0",
            ExtensionKind.Workspace,
            minHostVersion: "3.0.0");

        var entry = Assert.Single((await environment.Service.LoadAsync()).Extensions);

        Assert.False(entry.IsCatalogListed);
        Assert.False(entry.HasCompatibleRelease);
        Assert.False(entry.IsInstalledVersionCompatible);
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

    private static string IncompatibleOnlyCatalogJson()
        => CatalogJson().Replace("\"minHostVersion\": \"2.0.0\"", "\"minHostVersion\": \"3.0.0\"", StringComparison.Ordinal);

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

    private static async Task<ExtensionCenterSnapshot> LoadForStartupAsync(
        ExtensionCenterService service,
        bool autoCheckUpdates)
    {
        var method = typeof(ExtensionCenterService).GetMethod(
            nameof(ExtensionCenterService.LoadAsync),
            [typeof(bool), typeof(CancellationToken)]);
        Assert.True(method is not null,
            "ExtensionCenterService 必须提供 LoadAsync(bool autoCheckUpdates, CancellationToken)，以区分启动检查和仅本地缓存加载。");
        var result = method!.Invoke(service, [autoCheckUpdates, CancellationToken.None]);
        return await Assert.IsAssignableFrom<Task<ExtensionCenterSnapshot>>(result);
    }

    private static async Task<ExtensionCenterSnapshot> RefreshManuallyAsync(ExtensionCenterService service)
    {
        var method = typeof(ExtensionCenterService).GetMethod("RefreshAsync", [typeof(CancellationToken)]);
        Assert.True(method is not null,
            "ExtensionCenterService 必须提供 RefreshAsync(CancellationToken)，供用户手动刷新时强制联网。");
        var result = method!.Invoke(service, [CancellationToken.None]);
        return await Assert.IsAssignableFrom<Task<ExtensionCenterSnapshot>>(result);
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
            Logger.MessageWritten += (_, message) => LogMessages.Add(message);
            var trustStore = ExtensionTestTrust.CreateStoreForPublishers(
                (ExtensionTestTrust.DefaultKeyId, "thelinyue"),
                ("other-key", "other-publisher"));
            Registry = new ExtensionRegistry(Paths.ExtensionsDirectory, new ExtensionHealthChecker(), trustStore);
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
        public List<string> LogMessages { get; } = [];
        public ExtensionRegistry Registry { get; }
        public ExtensionSettingsStore Settings { get; }
        public StubHandler Handler { get; }
        public RecordingVerifier Verifier { get; }
        public ExtensionCenterService Service { get; }

        public async Task AddInstalledAsync(
            string id,
            string version,
            ExtensionKind kind,
            string publisherId = "thelinyue",
            string minHostVersion = "2.0.0")
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
                  "publisherId": "{{publisherId}}",
                  "hostApiVersion": "1.0",
                  "minHostVersion": "{{minHostVersion}}",
                  "runtime": { "kind": "{{runtimeKind}}", "protocol": "{{protocol}}", "entry": "{{entry}}" },
                  "capabilities": ["{{capability}}"],
                  "permissions": [],
                  "dependencies": []
                }
                """);
            var entryPath = Path.Combine(versionDirectory, entry.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
            await File.WriteAllTextAsync(entryPath, "fixture");
            await File.WriteAllTextAsync(
                Path.Combine(versionDirectory, "package.json"),
                JsonSerializer.Serialize(new { schemaVersion = 2, sha256 = new string('c', 64) }));
            var current = new ExtensionCurrentDocument
            {
                SchemaVersion = 2,
                Id = id,
                Version = version,
                PackageSha256 = new string('c', 64),
                TrustedKeyId = string.Equals(publisherId, "other-publisher", StringComparison.Ordinal)
                    ? "other-key"
                    : ExtensionTestTrust.DefaultKeyId,
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
        public int CatalogRequestCount { get; private set; }
        public int PackageRequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("catalog.json", StringComparison.Ordinal) == true)
            {
                CatalogRequestCount++;
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
        public bool PauseVerification { get; set; }
        public TaskCompletionSource VerificationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueVerification { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ExtensionPackageVerificationResult> VerifyAsync(
            ExtensionPackageVerificationRequest request,
            CancellationToken cancellationToken = default)
        {
            VerificationCount++;
            if (PauseVerification)
            {
                VerificationStarted.TrySetResult();
                await ContinueVerification.Task.WaitAsync(cancellationToken);
            }
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
            return new ExtensionPackageVerificationResult
            {
                Manifest = manifest,
                TrustedKeyId = request.Release.Signature.KeyId,
                PackageSha256 = Convert.ToHexString(SHA256.HashData(request.PackageBytes)).ToLowerInvariant()
            };
        }
    }
}
