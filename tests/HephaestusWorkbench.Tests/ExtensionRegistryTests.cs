using System.Diagnostics;
using System.Text.Json;
using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class ExtensionRegistryTests
{
    [Fact]
    public async Task LoadAsync_LoadsOnlyHealthyCurrentVersion()
    {
        using var layout = new ExtensionTestLayout();
        layout.WriteManifest("sample", "1.0.0");
        layout.WriteCurrent("sample", "1.0.0", ExtensionTestLayout.HashA, ExtensionActivationState.Healthy);
        var registry = new ExtensionRegistry(layout.ExtensionsRoot, new StubHealthChecker(), layout.TrustStore);

        var active = await registry.LoadAsync();

        var manifest = Assert.Single(active);
        Assert.Equal("sample", manifest.Id);
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Empty(registry.Issues);
    }

    [Fact]
    public async Task LoadAsync_AcceptsExtensionsRootWithTrailingSeparator()
    {
        using var layout = new ExtensionTestLayout();
        layout.WriteManifest("sample", "1.0.0");
        layout.WriteCurrent("sample", "1.0.0", ExtensionTestLayout.HashA, ExtensionActivationState.Healthy);
        var rootWithSeparator = layout.ExtensionsRoot + Path.DirectorySeparatorChar;
        var registry = new ExtensionRegistry(rootWithSeparator, new StubHealthChecker(), layout.TrustStore);

        var active = await registry.LoadAsync();

        Assert.Equal("sample", Assert.Single(active).Id);
        Assert.Empty(registry.Issues);
    }

    [Fact]
    public async Task LoadAsync_DoesNotRecursivelyLoadOrphanManifest()
    {
        using var layout = new ExtensionTestLayout();
        layout.WriteManifest("orphan", "1.0.0");
        var registry = new ExtensionRegistry(layout.ExtensionsRoot, new StubHealthChecker(), layout.TrustStore);

        var active = await registry.LoadAsync();

        Assert.Empty(active);
        Assert.Empty(registry.Issues);
    }

    [Fact]
    public async Task LoadAsync_WhenCurrentIsPending_RestoresValidBackup()
    {
        using var layout = new ExtensionTestLayout();
        layout.WriteManifest("sample", "1.0.0");
        layout.WriteManifest("sample", "2.0.0");
        layout.WriteCurrent("sample", "2.0.0", ExtensionTestLayout.HashB, ExtensionActivationState.Pending);
        layout.WriteBackup("sample", "1.0.0", ExtensionTestLayout.HashA, ExtensionActivationState.Healthy);
        var registry = new ExtensionRegistry(layout.ExtensionsRoot, new StubHealthChecker(), layout.TrustStore);

        var active = await registry.LoadAsync();

        Assert.Equal("1.0.0", Assert.Single(active).Version);
        var restored = ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(layout.CurrentPath("sample")));
        Assert.Equal("1.0.0", restored.Version);
        Assert.Equal(ExtensionActivationState.Healthy, restored.State);
        Assert.True(Directory.Exists(layout.VersionDirectory("sample", "2.0.0")));
    }

    [Fact]
    public async Task LoadAsync_WhenPendingHasNoValidBackup_SkipsExtensionAndReportsChineseIssue()
    {
        using var layout = new ExtensionTestLayout();
        layout.WriteManifest("sample", "2.0.0");
        layout.WriteCurrent("sample", "2.0.0", ExtensionTestLayout.HashB, ExtensionActivationState.Pending);
        var registry = new ExtensionRegistry(layout.ExtensionsRoot, new StubHealthChecker(), layout.TrustStore);

        var active = await registry.LoadAsync();

        Assert.Empty(active);
        Assert.Contains(registry.Issues, issue =>
            issue.Contains("待验证", StringComparison.Ordinal) &&
            issue.Contains("回滚", StringComparison.Ordinal));
        Assert.True(Directory.Exists(layout.VersionDirectory("sample", "2.0.0")));
    }

    [Fact]
    public async Task ActivateAsync_HealthCheckSucceeds_WritesPendingThenHealthy()
    {
        using var layout = new ExtensionTestLayout();
        layout.WriteManifest("sample", "1.0.0");
        layout.WriteManifest("sample", "2.0.0");
        layout.WriteCurrent("sample", "1.0.0", ExtensionTestLayout.HashA, ExtensionActivationState.Healthy);
        var observedPending = false;
        var checker = new StubHealthChecker(async (_, _) =>
        {
            var pending = ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(layout.CurrentPath("sample")));
            observedPending = pending.State == ExtensionActivationState.Pending && pending.Version == "2.0.0";
        });
        var registry = new ExtensionRegistry(layout.ExtensionsRoot, checker, layout.TrustStore);
        await registry.LoadAsync();

        var activated = await registry.ActivateAsync(layout.CreateVerification("sample", "2.0.0", ExtensionTestLayout.HashB, ExtensionTestTrust.DefaultKeyId));

        Assert.True(observedPending);
        Assert.Equal("2.0.0", activated.Version);
        var current = ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(layout.CurrentPath("sample")));
        var backup = ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(layout.BackupPath("sample")));
        Assert.Equal(ExtensionActivationState.Healthy, current.State);
        Assert.Equal("2.0.0", current.Version);
        Assert.Equal("1.0.0", backup.Version);
        Assert.Equal("2.0.0", registry.LeaseCurrentVersion("sample").Version);
    }

    [Theory]
    [InlineData("other-publisher", ExtensionKind.Analysis)]
    [InlineData("thelinyue", ExtensionKind.Workspace)]
    public async Task ActivateAsync_WhenCandidateIdentityDiffersFromActive_RejectsBeforeMutation(
        string candidatePublisherId,
        ExtensionKind candidateKind)
    {
        using var layout = new ExtensionTestLayout();
        layout.WriteManifest("sample", "1.0.0");
        layout.WriteManifest("sample", "2.0.0", candidatePublisherId, candidateKind);
        layout.WriteCurrent("sample", "1.0.0", ExtensionTestLayout.HashA, ExtensionActivationState.Healthy);
        var checker = new StubHealthChecker();
        var trustStore = ExtensionTestTrust.CreateStoreForPublishers(
            (ExtensionTestTrust.DefaultKeyId, "thelinyue"),
            ("other-key", "other-publisher"));
        var registry = new ExtensionRegistry(layout.ExtensionsRoot, checker, trustStore);
        await registry.LoadAsync();
        var candidateKeyId = string.Equals(candidatePublisherId, "other-publisher", StringComparison.Ordinal)
            ? "other-key"
            : ExtensionTestTrust.DefaultKeyId;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.ActivateAsync(layout.CreateVerification("sample", "2.0.0", ExtensionTestLayout.HashB, candidateKeyId)));
        var current = ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(layout.CurrentPath("sample")));

        Assert.Contains("身份冲突", exception.Message, StringComparison.Ordinal);
        Assert.Equal("1.0.0", current.Version);
        Assert.Equal(ExtensionActivationState.Healthy, current.State);
        Assert.Equal(0, checker.CallCount);
    }

    [Fact]
    public async Task ActivateAsync_WhenSameHealthyPackageIsAlreadyCurrent_PreservesExistingRollback()
    {
        using var layout = new ExtensionTestLayout();
        layout.WriteManifest("sample", "1.0.0");
        layout.WriteManifest("sample", "2.0.0");
        layout.WriteCurrent("sample", "2.0.0", ExtensionTestLayout.HashB, ExtensionActivationState.Healthy);
        layout.WriteBackup("sample", "1.0.0", ExtensionTestLayout.HashA, ExtensionActivationState.Healthy);
        var checker = new StubHealthChecker();
        var registry = new ExtensionRegistry(layout.ExtensionsRoot, checker, layout.TrustStore);
        await registry.LoadAsync();

        var activated = await registry.ActivateAsync(layout.CreateVerification("sample", "2.0.0", ExtensionTestLayout.HashB, ExtensionTestTrust.DefaultKeyId));

        Assert.Equal("2.0.0", activated.Version);
        Assert.Equal(1, checker.CallCount);
        var current = ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(layout.CurrentPath("sample")));
        var backup = ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(layout.BackupPath("sample")));
        Assert.Equal("2.0.0", current.Version);
        Assert.Equal(ExtensionActivationState.Healthy, current.State);
        Assert.Equal("1.0.0", backup.Version);
        Assert.Equal(ExtensionActivationState.Healthy, backup.State);
    }

    [Fact]
    public async Task ActivateAsync_WhenAnotherRegistryFinishesSamePackageFirst_PreservesRollbackAndRefreshesActiveState()
    {
        using var layout = new ExtensionTestLayout();
        layout.WriteManifest("sample", "1.0.0");
        layout.WriteManifest("sample", "2.0.0");
        layout.WriteCurrent("sample", "1.0.0", ExtensionTestLayout.HashA, ExtensionActivationState.Healthy);
        var firstRegistry = new ExtensionRegistry(layout.ExtensionsRoot, new StubHealthChecker(), layout.TrustStore);
        var laterChecker = new StubHealthChecker();
        var laterRegistry = new ExtensionRegistry(layout.ExtensionsRoot, laterChecker, layout.TrustStore);
        await firstRegistry.LoadAsync();
        await laterRegistry.LoadAsync();

        await firstRegistry.ActivateAsync(layout.CreateVerification("sample", "2.0.0", ExtensionTestLayout.HashB, ExtensionTestTrust.DefaultKeyId));
        var activated = await laterRegistry.ActivateAsync(layout.CreateVerification("sample", "2.0.0", ExtensionTestLayout.HashB, ExtensionTestTrust.DefaultKeyId));

        Assert.Equal("2.0.0", activated.Version);
        Assert.Equal(1, laterChecker.CallCount);
        var backup = ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(layout.BackupPath("sample")));
        Assert.Equal("1.0.0", backup.Version);
        Assert.Equal(ExtensionActivationState.Healthy, backup.State);
        using var lease = laterRegistry.LeaseCurrentVersion("sample");
        Assert.Equal("2.0.0", lease.Version);
    }

    [Fact]
    public async Task ActivateAsync_HealthCheckFails_RestoresBackup()
    {
        using var layout = new ExtensionTestLayout();
        layout.WriteManifest("sample", "1.0.0");
        layout.WriteManifest("sample", "2.0.0");
        layout.WriteCurrent("sample", "1.0.0", ExtensionTestLayout.HashA, ExtensionActivationState.Healthy);
        var registry = new ExtensionRegistry(
            layout.ExtensionsRoot,
            new StubHealthChecker((_, _) => throw new InvalidOperationException("正式加载失败")),
            layout.TrustStore);
        await registry.LoadAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.ActivateAsync(layout.CreateVerification("sample", "2.0.0", ExtensionTestLayout.HashB, ExtensionTestTrust.DefaultKeyId)));

        Assert.Contains("激活", error.Message, StringComparison.Ordinal);
        Assert.Contains("回滚", error.Message, StringComparison.Ordinal);
        var current = ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(layout.CurrentPath("sample")));
        Assert.Equal("1.0.0", current.Version);
        Assert.Equal(ExtensionActivationState.Healthy, current.State);
        using var lease = registry.LeaseCurrentVersion("sample");
        Assert.Equal("1.0.0", lease.Version);
    }

    [Fact]
    public async Task ActivateAsync_SameIdAndVersionWithDifferentHash_IsRejected()
    {
        using var layout = new ExtensionTestLayout();
        layout.WriteManifest("sample", "1.0.0");
        layout.WriteCurrent("sample", "1.0.0", ExtensionTestLayout.HashA, ExtensionActivationState.Healthy);
        var checker = new StubHealthChecker();
        var registry = new ExtensionRegistry(layout.ExtensionsRoot, checker, layout.TrustStore);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.ActivateAsync(layout.CreateVerification("sample", "1.0.0", ExtensionTestLayout.HashB, ExtensionTestTrust.DefaultKeyId)));

        Assert.Contains("相同扩展版本", error.Message, StringComparison.Ordinal);
        Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, checker.CallCount);
        var current = ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(layout.CurrentPath("sample")));
        Assert.Equal(ExtensionTestLayout.HashA, current.PackageSha256);
    }

    [Fact]
    public async Task LoadAsync_RejectsExtensionDirectoryJunctionOutsideRoot()
    {
        using var layout = new ExtensionTestLayout();
        var outsideRoot = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var outsideExtension = Path.Combine(outsideRoot, "sample");
        var linkDirectory = Path.Combine(layout.ExtensionsRoot, "sample");
        try
        {
            Directory.CreateDirectory(outsideExtension);
            WriteManifestAt(outsideExtension, "sample", "1.0.0");
            WriteCurrentAt(outsideExtension, "sample", "1.0.0", ExtensionTestLayout.HashA, ExtensionActivationState.Healthy);
            CreateJunction(linkDirectory, outsideExtension);
            var registry = new ExtensionRegistry(layout.ExtensionsRoot, new StubHealthChecker(), layout.TrustStore);

            var active = await registry.LoadAsync();

            Assert.Empty(active);
            Assert.Contains(registry.Issues, issue => issue.Contains("重解析点", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(outsideExtension, "current.json")));
        }
        finally
        {
            if (Directory.Exists(linkDirectory)) Directory.Delete(linkDirectory);
            if (Directory.Exists(outsideRoot)) Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ActivateAsync_CanRetryMatchingPendingWithoutBackup()
    {
        using var layout = new ExtensionTestLayout();
        layout.WriteManifest("sample", "2.0.0");
        layout.WriteCurrent("sample", "2.0.0", ExtensionTestLayout.HashB, ExtensionActivationState.Pending);
        var checker = new StubHealthChecker();
        var registry = new ExtensionRegistry(layout.ExtensionsRoot, checker, layout.TrustStore);
        await registry.LoadAsync();

        var activated = await registry.ActivateAsync(layout.CreateVerification("sample", "2.0.0", ExtensionTestLayout.HashB, ExtensionTestTrust.DefaultKeyId));

        Assert.Equal("2.0.0", activated.Version);
        Assert.Equal(1, checker.CallCount);
        var current = ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(layout.CurrentPath("sample")));
        Assert.Equal(ExtensionActivationState.Healthy, current.State);
        Assert.False(File.Exists(layout.BackupPath("sample")));
    }

    [Fact]
    public async Task ActivateAsync_WhenHealthCheckIsCancelled_RestoresCurrentAndPreservesCancellation()
    {
        using var layout = new ExtensionTestLayout();
        layout.WriteManifest("sample", "1.0.0");
        layout.WriteManifest("sample", "2.0.0");
        layout.WriteCurrent("sample", "1.0.0", ExtensionTestLayout.HashA, ExtensionActivationState.Healthy);
        var checker = new StubHealthChecker((_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        var registry = new ExtensionRegistry(layout.ExtensionsRoot, checker, layout.TrustStore);
        await registry.LoadAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            registry.ActivateAsync(layout.CreateVerification("sample", "2.0.0", ExtensionTestLayout.HashB, ExtensionTestTrust.DefaultKeyId), cancellation.Token));

        var current = ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(layout.CurrentPath("sample")));
        Assert.Equal("1.0.0", current.Version);
        Assert.Equal(ExtensionActivationState.Healthy, current.State);
    }

    private static void CreateJunction(string linkDirectory, string targetDirectory)
    {
        using var junction = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/d /c mklink /J \"{linkDirectory}\" \"{targetDirectory}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException("无法启动 junction 测试进程。");
        junction.WaitForExit();
        Assert.True(junction.ExitCode == 0, junction.StandardError.ReadToEnd() + junction.StandardOutput.ReadToEnd());
    }

    private static void WriteManifestAt(string extensionDirectory, string id, string version)
    {
        var directory = Path.Combine(extensionDirectory, version);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), $$"""
            {
              "schemaVersion": 2,
              "id": "{{id}}",
              "name": "测试扩展",
              "version": "{{version}}",
              "kind": "analysis",
              "publisherId": "thelinyue",
              "hostApiVersion": "1.0",
              "minHostVersion": "2.0.0",
              "runtime": { "kind": "content" },
              "capabilities": ["analysis.rule-pack"],
              "permissions": [],
              "dependencies": []
            }
            """);
    }

    private static void WriteCurrentAt(
        string extensionDirectory,
        string id,
        string version,
        string hash,
        ExtensionActivationState state)
    {
        File.WriteAllText(Path.Combine(extensionDirectory, "current.json"), JsonSerializer.Serialize(new ExtensionCurrentDocument
        {
            SchemaVersion = 2,
            Id = id,
            Version = version,
            PackageSha256 = hash,
            TrustedKeyId = ExtensionTestTrust.DefaultKeyId,
            State = state
        }));
    }

    private sealed class StubHealthChecker : IExtensionHealthChecker
    {
        private readonly Func<ExtensionManifest, CancellationToken, Task>? _check;

        public StubHealthChecker(Func<ExtensionManifest, CancellationToken, Task>? check = null)
        {
            _check = check;
        }

        public int CallCount { get; private set; }

        public async Task CheckAsync(ExtensionManifest manifest, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (_check is not null)
                await _check(manifest, cancellationToken);
        }
    }
}

internal sealed class ExtensionTestLayout : IDisposable
{
    public const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "HephaestusWorkbenchTests",
        Guid.NewGuid().ToString("N"));

    public ExtensionTestLayout()
    {
        ExtensionsRoot = Path.Combine(_root, "Extensions");
        Directory.CreateDirectory(ExtensionsRoot);
    }

    public string ExtensionsRoot { get; }

    public IExtensionTrustStore TrustStore => ExtensionTestTrust.CreateStore();

    public string VersionDirectory(string id, string version) => Path.Combine(ExtensionsRoot, id, version);

    public string CurrentPath(string id) => Path.Combine(ExtensionsRoot, id, "current.json");

    public string BackupPath(string id) => Path.Combine(ExtensionsRoot, id, "current.json.bak");

    public void WriteManifest(
        string id,
        string version,
        string publisherId = "thelinyue",
        ExtensionKind kind = ExtensionKind.Analysis)
    {
        var directory = VersionDirectory(id, version);
        Directory.CreateDirectory(directory);
        var kindText = kind.ToString().ToLowerInvariant();
        var runtime = kind == ExtensionKind.Workspace
            ? "{ \"kind\": \"web\", \"protocol\": \"workspace-bridge-v1\", \"entry\": \"index.html\" }"
            : "{ \"kind\": \"content\" }";
        var capability = kind == ExtensionKind.Workspace ? "workspace.page" : "analysis.rule-pack";
        File.WriteAllText(Path.Combine(directory, "manifest.json"), $$"""
            {
              "schemaVersion": 2,
              "id": "{{id}}",
              "name": "测试扩展",
              "version": "{{version}}",
              "kind": "{{kindText}}",
              "publisherId": "{{publisherId}}",
              "hostApiVersion": "1.0",
              "minHostVersion": "2.0.0",
              "runtime": {{runtime}},
              "capabilities": ["{{capability}}"],
              "permissions": [],
              "dependencies": []
            }
            """);
        var packageHash = string.Equals(version, "1.0.0", StringComparison.Ordinal) ? HashA : HashB;
        File.WriteAllText(
            Path.Combine(directory, "package.json"),
            JsonSerializer.Serialize(new { schemaVersion = 2, sha256 = packageHash }));
        if (kind == ExtensionKind.Workspace)
            File.WriteAllText(Path.Combine(directory, "index.html"), "fixture");
    }

    public ExtensionPackageVerificationResult CreateVerification(
        string id,
        string version,
        string packageSha256,
        string trustedKeyId)
    {
        var versionDirectory = VersionDirectory(id, version);
        var manifest = ExtensionManifestParser.Parse(
            File.ReadAllText(Path.Combine(versionDirectory, "manifest.json")),
            versionDirectory);
        return new ExtensionPackageVerificationResult
        {
            Manifest = manifest,
            PackageSha256 = packageSha256,
            TrustedKeyId = trustedKeyId
        };
    }

    public void WriteCurrent(string id, string version, string hash, ExtensionActivationState state)
        => WriteCurrentDocument(CurrentPath(id), id, version, hash, state);

    public void WriteBackup(string id, string version, string hash, ExtensionActivationState state)
        => WriteCurrentDocument(BackupPath(id), id, version, hash, state);

    private static void WriteCurrentDocument(
        string path,
        string id,
        string version,
        string hash,
        ExtensionActivationState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new ExtensionCurrentDocument
        {
            SchemaVersion = 2,
            Id = id,
            Version = version,
            PackageSha256 = hash,
            TrustedKeyId = ExtensionTestTrust.DefaultKeyId,
            State = state
        }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
