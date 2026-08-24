using System.Text.Json;
using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

/// <summary>
/// Extension Runtime Trust Binding M1 的红测试。
/// 这些测试只定义 Registry 在磁盘 current.json 与宿主 TrustStore 之间的最小闭环，
/// 不触发、也不放开 Workspace Bridge。
/// </summary>
public sealed class ExtensionRuntimeTrustBindingTests
{
    [Fact]
    public async Task LoadAsync_BindsCurrentTrustedKeyToLeaseAuthorizationAndDefensivelyCopiesScope()
    {
        using var layout = new RuntimeTrustTestLayout();
        layout.WriteVersion("sample", "1.0.0", "trusted-publisher", ExtensionKind.Analysis, []);
        layout.WriteCurrent("sample", "1.0.0", RuntimeTrustTestLayout.HashA, ExtensionActivationState.Healthy, "analysis-key");
        var trustStore = new MutableTrustStore(
            CreateTrustedKey("analysis-key", "trusted-publisher", [ExtensionKind.Analysis], ["workspace.readText"]));
        var registry = new ExtensionRegistry(layout.ExtensionsRoot, new StubHealthChecker(), trustStore);

        var active = await registry.LoadAsync();
        using var lease = registry.LeaseCurrentVersion("sample");
        ExtensionRuntimeAuthorization authorization = lease.Authorization;

        Assert.Equal("sample", Assert.Single(active).Id);
        Assert.Equal("analysis-key", authorization.KeyId);
        Assert.Equal("trusted-publisher", authorization.PublisherId);
        Assert.Equal([ExtensionKind.Analysis], authorization.AllowedKinds);
        Assert.Equal(["workspace.readText"], authorization.Permissions);
        Assert.True(trustStore.LookupCount >= 2, "LoadAsync 和每次 Lease 都必须重新向宿主 TrustStore 解析 keyId。");
        AssertCollectionCannotBeModified(authorization.AllowedKinds, ExtensionKind.Maintenance);
        AssertCollectionCannotBeModified(authorization.Permissions, "workspace.writeText");
    }

    [Fact]
    public async Task LoadAsync_RejectsSchemaV2CurrentWithoutTrustedKeyId()
    {
        using var layout = new RuntimeTrustTestLayout();
        layout.WriteVersion("sample", "1.0.0", "trusted-publisher", ExtensionKind.Analysis, []);
        layout.WriteCurrentWithoutTrustedKeyId("sample", "1.0.0", RuntimeTrustTestLayout.HashA, ExtensionActivationState.Healthy);
        var registry = new ExtensionRegistry(
            layout.ExtensionsRoot,
            new StubHealthChecker(),
            new MutableTrustStore(CreateTrustedKey("analysis-key", "trusted-publisher", [ExtensionKind.Analysis], [])));

        var active = await registry.LoadAsync();

        AssertRejected(active, registry, "缺少", "trustedKeyId");
    }

    [Fact]
    public async Task LoadAsync_UnknownTrustedKeyId_FailsClosedWithChineseIssue()
    {
        using var layout = new RuntimeTrustTestLayout();
        layout.WriteVersion("sample", "1.0.0", "trusted-publisher", ExtensionKind.Analysis, []);
        layout.WriteCurrent("sample", "1.0.0", RuntimeTrustTestLayout.HashA, ExtensionActivationState.Healthy, "unknown-key");
        var registry = new ExtensionRegistry(layout.ExtensionsRoot, new StubHealthChecker(), new MutableTrustStore());

        var active = await registry.LoadAsync();

        AssertRejected(active, registry, "未知", "key");
    }

    [Fact]
    public async Task LoadAsync_WhenManifestPublisherDoesNotMatchTrustedKey_FailsClosedWithChineseIssue()
    {
        using var layout = new RuntimeTrustTestLayout();
        layout.WriteVersion("sample", "1.0.0", "other-publisher", ExtensionKind.Analysis, []);
        layout.WriteCurrent("sample", "1.0.0", RuntimeTrustTestLayout.HashA, ExtensionActivationState.Healthy, "analysis-key");
        var registry = new ExtensionRegistry(
            layout.ExtensionsRoot,
            new StubHealthChecker(),
            new MutableTrustStore(CreateTrustedKey("analysis-key", "trusted-publisher", [ExtensionKind.Analysis], [])));

        var active = await registry.LoadAsync();

        AssertRejected(active, registry, "发布者");
    }

    [Fact]
    public async Task LoadAsync_WhenManifestKindExceedsTrustedScope_FailsClosedWithChineseIssue()
    {
        using var layout = new RuntimeTrustTestLayout();
        layout.WriteVersion("sample", "1.0.0", "trusted-publisher", ExtensionKind.Maintenance, []);
        layout.WriteCurrent("sample", "1.0.0", RuntimeTrustTestLayout.HashA, ExtensionActivationState.Healthy, "analysis-key");
        var registry = new ExtensionRegistry(
            layout.ExtensionsRoot,
            new StubHealthChecker(),
            new MutableTrustStore(CreateTrustedKey("analysis-key", "trusted-publisher", [ExtensionKind.Analysis], [])));

        var active = await registry.LoadAsync();

        AssertRejected(active, registry, "类别");
    }

    [Fact]
    public async Task LoadAsync_WhenManifestPermissionExceedsTrustedScope_FailsClosedWithChineseIssue()
    {
        using var layout = new RuntimeTrustTestLayout();
        // 仅构造 workspace manifest 的静态元数据以覆盖 permissions 授权校验；本测试不创建或调用 Workspace Bridge。
        layout.WriteVersion("sample", "1.0.0", "trusted-publisher", ExtensionKind.Workspace, ["workspace.writeText"]);
        layout.WriteCurrent("sample", "1.0.0", RuntimeTrustTestLayout.HashA, ExtensionActivationState.Healthy, "workspace-key");
        var registry = new ExtensionRegistry(
            layout.ExtensionsRoot,
            new StubHealthChecker(),
            new MutableTrustStore(CreateTrustedKey(
                "workspace-key",
                "trusted-publisher",
                [ExtensionKind.Workspace],
                ["workspace.readText"])));

        var active = await registry.LoadAsync();

        AssertRejected(active, registry, "权限");
    }

    [Fact]
    public async Task LoadAsync_WhenCurrentPackageHashDiffersFromHostPackageMetadata_FailsClosedWithChineseIssue()
    {
        using var layout = new RuntimeTrustTestLayout();
        layout.WriteVersion("sample", "1.0.0", "trusted-publisher", ExtensionKind.Analysis, [], RuntimeTrustTestLayout.HashB);
        layout.WriteCurrent("sample", "1.0.0", RuntimeTrustTestLayout.HashA, ExtensionActivationState.Healthy, "analysis-key");
        var registry = new ExtensionRegistry(
            layout.ExtensionsRoot,
            new StubHealthChecker(),
            new MutableTrustStore(CreateTrustedKey("analysis-key", "trusted-publisher", [ExtensionKind.Analysis], [])));

        var active = await registry.LoadAsync();

        AssertRejected(active, registry, "SHA-256");
    }

    [Fact]
    public async Task LoadAsync_WhenPendingCurrentIsRecovered_UsesRollbackCurrentOwnTrustedKey()
    {
        using var layout = new RuntimeTrustTestLayout();
        layout.WriteVersion("sample", "1.0.0", "rollback-publisher", ExtensionKind.Analysis, []);
        layout.WriteVersion("sample", "2.0.0", "pending-publisher", ExtensionKind.Analysis, []);
        layout.WriteCurrent("sample", "2.0.0", RuntimeTrustTestLayout.HashB, ExtensionActivationState.Pending, "pending-key");
        layout.WriteBackup("sample", "1.0.0", RuntimeTrustTestLayout.HashA, ExtensionActivationState.Healthy, "rollback-key");
        var registry = new ExtensionRegistry(
            layout.ExtensionsRoot,
            new StubHealthChecker(),
            new MutableTrustStore(CreateTrustedKey("rollback-key", "rollback-publisher", [ExtensionKind.Analysis], [])));

        var active = await registry.LoadAsync();
        using var lease = registry.LeaseCurrentVersion("sample");
        var restored = ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(layout.CurrentPath("sample")));

        Assert.Equal("1.0.0", Assert.Single(active).Version);
        Assert.Equal("rollback-key", restored.TrustedKeyId);
        Assert.Equal("rollback-key", lease.Authorization.KeyId);
        Assert.NotEqual("pending-key", lease.Authorization.KeyId);
    }

    [Fact]
    public async Task LoadAsync_WhenPendingCurrentLacksTrustedKeyId_StillRecoversAuthorizedBackup()
    {
        using var layout = new RuntimeTrustTestLayout();
        layout.WriteVersion("sample", "1.0.0", "rollback-publisher", ExtensionKind.Analysis, []);
        layout.WriteVersion("sample", "2.0.0", "pending-publisher", ExtensionKind.Analysis, [], RuntimeTrustTestLayout.HashB);
        layout.WriteCurrentWithoutTrustedKeyId(
            "sample",
            "2.0.0",
            RuntimeTrustTestLayout.HashB,
            ExtensionActivationState.Pending);
        layout.WriteBackup(
            "sample",
            "1.0.0",
            RuntimeTrustTestLayout.HashA,
            ExtensionActivationState.Healthy,
            "rollback-key");
        var registry = new ExtensionRegistry(
            layout.ExtensionsRoot,
            new StubHealthChecker(),
            new MutableTrustStore(CreateTrustedKey(
                "rollback-key",
                "rollback-publisher",
                [ExtensionKind.Analysis],
                [])));

        var active = await registry.LoadAsync();
        using var lease = registry.LeaseCurrentVersion("sample");

        Assert.Equal("1.0.0", Assert.Single(active).Version);
        Assert.Equal("rollback-key", lease.Authorization.KeyId);
        Assert.Empty(registry.Issues);
    }

    [Fact]
    public async Task ActivateAsync_PersistsTrustedKeyIdInPendingHealthyAndBackupCurrentDocuments()
    {
        using var layout = new RuntimeTrustTestLayout();
        layout.WriteVersion("sample", "1.0.0", "trusted-publisher", ExtensionKind.Analysis, []);
        layout.WriteVersion("sample", "2.0.0", "trusted-publisher", ExtensionKind.Analysis, [], RuntimeTrustTestLayout.HashB);
        layout.WriteCurrent("sample", "1.0.0", RuntimeTrustTestLayout.HashA, ExtensionActivationState.Healthy, "key-one");
        var sawPendingKey = false;
        var healthChecker = new StubHealthChecker(async (_, _) =>
        {
            var pending = ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(layout.CurrentPath("sample")));
            sawPendingKey = pending.State == ExtensionActivationState.Pending && pending.TrustedKeyId == "key-two";
        });
        var registry = new ExtensionRegistry(
            layout.ExtensionsRoot,
            healthChecker,
            new MutableTrustStore(
                CreateTrustedKey("key-one", "trusted-publisher", [ExtensionKind.Analysis], []),
                CreateTrustedKey("key-two", "trusted-publisher", [ExtensionKind.Analysis], [])));
        await registry.LoadAsync();

        await registry.ActivateAsync(layout.CreateVerification(
            "sample",
            "2.0.0",
            RuntimeTrustTestLayout.HashB,
            "key-two"));

        var current = ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(layout.CurrentPath("sample")));
        var backup = ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(layout.BackupPath("sample")));
        Assert.True(sawPendingKey);
        Assert.Equal(ExtensionActivationState.Healthy, current.State);
        Assert.Equal("key-two", current.TrustedKeyId);
        Assert.Equal(ExtensionActivationState.Healthy, backup.State);
        Assert.Equal("key-one", backup.TrustedKeyId);
    }

    [Fact]
    public async Task LeaseCurrentVersion_WhenTrustedKeyIsRevokedAfterLoad_RejectsNewLease()
    {
        using var layout = new RuntimeTrustTestLayout();
        layout.WriteVersion("sample", "1.0.0", "trusted-publisher", ExtensionKind.Analysis, []);
        layout.WriteCurrent("sample", "1.0.0", RuntimeTrustTestLayout.HashA, ExtensionActivationState.Healthy, "analysis-key");
        var trustStore = new MutableTrustStore(
            CreateTrustedKey("analysis-key", "trusted-publisher", [ExtensionKind.Analysis], []));
        var registry = new ExtensionRegistry(layout.ExtensionsRoot, new StubHealthChecker(), trustStore);
        await registry.LoadAsync();
        trustStore.Revoke("analysis-key");

        var error = Assert.Throws<InvalidOperationException>(() => registry.LeaseCurrentVersion("sample"));

        Assert.Contains("信任", error.Message, StringComparison.Ordinal);
    }

    private static TrustedPublisherKey CreateTrustedKey(
        string keyId,
        string publisherId,
        IReadOnlyList<ExtensionKind> allowedKinds,
        IReadOnlyList<string> permissions)
        => new()
        {
            KeyId = keyId,
            PublisherId = publisherId,
            PublicKeyBase64 = Convert.ToBase64String(new byte[32]),
            Scope = new ExtensionTrustScope
            {
                AllowedKinds = allowedKinds,
                Permissions = permissions
            }
        };

    private static void AssertRejected(
        IReadOnlyList<ExtensionManifest> active,
        ExtensionRegistry registry,
        params string[] expectedChineseReasonParts)
    {
        Assert.Empty(active);
        Assert.Contains(registry.Issues, issue => expectedChineseReasonParts.All(part =>
            issue.Contains(part, StringComparison.Ordinal)));
    }

    private static void AssertCollectionCannotBeModified<T>(IReadOnlyList<T> values, T replacement)
    {
        if (values is not IList<T> mutable || mutable.Count == 0)
            return;

        var before = values.ToArray();
        try
        {
            mutable[0] = replacement;
        }
        catch (NotSupportedException)
        {
        }

        Assert.Equal(before, values);
    }

    /// <summary>
    /// 可撤销的宿主信任表替身。它记录解析次数，以证明 Registry 不会只在 LoadAsync 时缓存授权结果。
    /// </summary>
    private sealed class MutableTrustStore(params TrustedPublisherKey[] trustedKeys) : IExtensionTrustStore
    {
        private readonly Dictionary<string, TrustedPublisherKey> _trustedKeys = trustedKeys.ToDictionary(key => key.KeyId, StringComparer.Ordinal);

        public int LookupCount { get; private set; }

        public bool TryGetTrustedKey(string keyId, out TrustedPublisherKey trustedKey)
        {
            LookupCount++;
            return _trustedKeys.TryGetValue(keyId, out trustedKey!);
        }

        public void Revoke(string keyId) => _trustedKeys.Remove(keyId);
    }

    /// <summary>
    /// Registry 测试专用的健康检查替身：默认通过，可在回调中观察 ActivateAsync 已写入的 pending current.json。
    /// </summary>
    private sealed class StubHealthChecker(Func<ExtensionManifest, CancellationToken, Task>? check = null) : IExtensionHealthChecker
    {
        public Task CheckAsync(ExtensionManifest manifest, CancellationToken cancellationToken = default)
            => check?.Invoke(manifest, cancellationToken) ?? Task.CompletedTask;
    }

    /// <summary>
    /// 每个测试独立创建 Extensions/&lt;id&gt;/&lt;version&gt;、宿主 package.json 与 current.json，避免依赖其他测试文件的布局。
    /// package.json 仅保留既有 SHA-256 元数据；信任绑定始终写入 current.json.trustedKeyId。
    /// </summary>
    private sealed class RuntimeTrustTestLayout : IDisposable
    {
        public const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        private readonly string _root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));

        public RuntimeTrustTestLayout()
        {
            ExtensionsRoot = Path.Combine(_root, "Extensions");
            Directory.CreateDirectory(ExtensionsRoot);
        }

        public string ExtensionsRoot { get; }

        public string CurrentPath(string id) => Path.Combine(ExtensionsRoot, id, "current.json");

        public string BackupPath(string id) => Path.Combine(ExtensionsRoot, id, "current.json.bak");

        public void WriteVersion(
            string id,
            string version,
            string publisherId,
            ExtensionKind kind,
            IReadOnlyList<string> permissions,
            string packageSha256 = HashA)
        {
            var versionDirectory = Path.Combine(ExtensionsRoot, id, version);
            Directory.CreateDirectory(versionDirectory);
            var (runtime, capabilities) = kind switch
            {
                ExtensionKind.Analysis => ("{ \"kind\": \"content\" }", "[\"analysis.rule-pack\"]"),
                ExtensionKind.Maintenance => ("{ \"kind\": \"content\" }", "[\"maintenance.workflow-pack\"]"),
                ExtensionKind.Workspace => ("{ \"kind\": \"web\", \"entry\": \"index.html\" }", "[\"workspace.page\"]"),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "测试布局不支持该扩展类别。")
            };
            var permissionsJson = JsonSerializer.Serialize(permissions);
            File.WriteAllText(Path.Combine(versionDirectory, "manifest.json"), $$"""
                {
                  "schemaVersion": 2,
                  "id": "{{id}}",
                  "name": "运行时信任测试扩展",
                  "version": "{{version}}",
                  "kind": "{{kind.ToString().ToLowerInvariant()}}",
                  "publisherId": "{{publisherId}}",
                  "hostApiVersion": "1.0",
                  "minHostVersion": "2.0.0",
                  "runtime": {{runtime}},
                  "capabilities": {{capabilities}},
                  "permissions": {{permissionsJson}},
                  "dependencies": []
                }
                """);
            File.WriteAllText(
                Path.Combine(versionDirectory, "package.json"),
                JsonSerializer.Serialize(new { schemaVersion = 2, sha256 = packageSha256 }));
            if (kind == ExtensionKind.Workspace)
                File.WriteAllText(Path.Combine(versionDirectory, "index.html"), "<!doctype html><title>not-opened</title>");
        }

        public ExtensionPackageVerificationResult CreateVerification(
            string id,
            string version,
            string packageSha256,
            string trustedKeyId)
        {
            var versionDirectory = Path.Combine(ExtensionsRoot, id, version);
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

        public void WriteCurrent(
            string id,
            string version,
            string packageSha256,
            ExtensionActivationState state,
            string trustedKeyId)
            => WriteCurrentDocument(CurrentPath(id), id, version, packageSha256, state, trustedKeyId);

        public void WriteBackup(
            string id,
            string version,
            string packageSha256,
            ExtensionActivationState state,
            string trustedKeyId)
            => WriteCurrentDocument(BackupPath(id), id, version, packageSha256, state, trustedKeyId);

        public void WriteCurrentWithoutTrustedKeyId(
            string id,
            string version,
            string packageSha256,
            ExtensionActivationState state)
        {
            var path = CurrentPath(id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $$"""
                {
                  "schemaVersion": 2,
                  "id": "{{id}}",
                  "version": "{{version}}",
                  "packageSha256": "{{packageSha256}}",
                  "state": "{{state.ToString().ToLowerInvariant()}}"
                }
                """);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private static void WriteCurrentDocument(
            string path,
            string id,
            string version,
            string packageSha256,
            ExtensionActivationState state,
            string trustedKeyId)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new ExtensionCurrentDocument
            {
                SchemaVersion = 2,
                Id = id,
                Version = version,
                PackageSha256 = packageSha256,
                State = state,
                TrustedKeyId = trustedKeyId
            }));
        }
    }
}

