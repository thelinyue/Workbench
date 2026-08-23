using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;
using NSec.Cryptography;

namespace HephaestusWorkbench.Tests;

public sealed class ExtensionPackageVerifierTests
{
    [Fact]
    public async Task VerifyAsync_AcceptsValidTrustedPackageSignedOverRawZipBytes()
    {
        using var fixture = new VerificationFixture();
        var request = fixture.CreateRequest();

        var result = await fixture.CreateVerifier().VerifyAsync(request);

        Assert.Equal("test-tool", result.Manifest.Id);
        Assert.Equal("test-key", result.TrustedKeyId);
        Assert.Equal(request.Release.Sha256, result.PackageSha256);
        Assert.NotNull(JsonSerializer.Deserialize<ExtensionPackageVerificationRequest>(JsonSerializer.Serialize(request)));
        Assert.NotNull(JsonSerializer.Deserialize<ExtensionPackageVerificationResult>(JsonSerializer.Serialize(result)));
    }

    [Fact]
    public async Task VerifyAsync_SnapshotsPackageBytesBeforeTrustLookup()
    {
        using var fixture = new VerificationFixture();
        var request = fixture.CreateRequest();
        var store = new CallbackTrustStore(fixture.TrustedKey, () => Array.Clear(request.PackageBytes));
        var verifier = new ExtensionPackageVerifier(store);

        var result = await verifier.VerifyAsync(request);

        Assert.Equal("test-tool", result.Manifest.Id);
    }

    [Fact]
    public async Task VerifyAsync_RejectsUnknownKey()
    {
        using var fixture = new VerificationFixture();
        var request = fixture.CreateRequest(keyId: "unknown-key");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateVerifier().VerifyAsync(request));

        Assert.Contains("密钥", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_RejectsCatalogPublisherMismatchBeforeReadingZip()
    {
        using var fixture = new VerificationFixture();
        var request = fixture.CreateRequest(packageBytes: [1, 2, 3], publisherId: "other-publisher");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateVerifier().VerifyAsync(request));

        Assert.Contains("发布者", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_RejectsKindOutsideTrustedScopeBeforeReadingZip()
    {
        using var fixture = new VerificationFixture(allowedKinds: [ExtensionKind.Workspace]);
        var request = fixture.CreateRequest(packageBytes: [1, 2, 3]);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateVerifier().VerifyAsync(request));

        Assert.Contains("类别", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_VerifiesSignatureBeforeReadingZip()
    {
        using var fixture = new VerificationFixture();
        var request = fixture.CreateRequest(
            packageBytes: [1, 2, 3],
            signature: Convert.ToBase64String(new byte[64]));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateVerifier().VerifyAsync(request));

        Assert.Contains("验签", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("损坏", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_ReportsSignedCorruptZipWithChineseError()
    {
        using var fixture = new VerificationFixture();
        var request = fixture.CreateRequest(packageBytes: [1, 2, 3]);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateVerifier().VerifyAsync(request));

        Assert.Contains("扩展 ZIP", error.Message, StringComparison.Ordinal);
        Assert.Contains("损坏", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_RejectsManifestPublisherDifferentFromCatalogAndTrustedKey()
    {
        using var fixture = new VerificationFixture();
        var manifest = BuildAnalysisManifest().Replace("\"publisherId\": \"test-publisher\"", "\"publisherId\": \"other-publisher\"", StringComparison.Ordinal);
        var request = fixture.CreateRequest(manifestJson: manifest);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateVerifier().VerifyAsync(request));

        Assert.Contains("发布者", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_RejectsManifestKindDifferentFromCatalog()
    {
        using var fixture = new VerificationFixture();
        var request = fixture.CreateRequest(manifestJson: BuildWorkspaceManifest("[]"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateVerifier().VerifyAsync(request));

        Assert.Contains("类别", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("version")]
    [InlineData("minHostVersion")]
    public async Task VerifyAsync_BindsManifestIdentityAndReleaseMetadata(string field)
    {
        using var fixture = new VerificationFixture();
        var manifest = field switch
        {
            "id" => BuildAnalysisManifest().Replace("\"id\": \"test-tool\"", "\"id\": \"other-tool\"", StringComparison.Ordinal),
            "version" => BuildAnalysisManifest().Replace("\"version\": \"2.0.0\"", "\"version\": \"2.1.0\"", StringComparison.Ordinal),
            _ => BuildAnalysisManifest().Replace("\"minHostVersion\": \"2.0.0\"", "\"minHostVersion\": \"2.1.0\"", StringComparison.Ordinal)
        };
        var request = fixture.CreateRequest(manifestJson: manifest);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateVerifier().VerifyAsync(request));

        Assert.Contains(field, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_RejectsManifestPermissionOutsideTrustedScope()
    {
        using var fixture = new VerificationFixture(
            allowedKinds: [ExtensionKind.Workspace],
            allowedPermissions: []);
        var request = fixture.CreateRequest(
            kind: ExtensionKind.Workspace,
            manifestJson: BuildWorkspaceManifest("[\"workspace.readText\"]"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateVerifier().VerifyAsync(request));

        Assert.Contains("权限", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_RejectsNullManifestPermission()
    {
        using var fixture = new VerificationFixture(
            allowedKinds: [ExtensionKind.Workspace],
            allowedPermissions: []);
        var request = fixture.CreateRequest(
            kind: ExtensionKind.Workspace,
            manifestJson: BuildWorkspaceManifest("[null]"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateVerifier().VerifyAsync(request));

        Assert.Contains("权限", error.Message, StringComparison.Ordinal);
        Assert.Contains("不能为空", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_RejectsEmptyManifestPermissionEvenWhenTrustScopeContainsIt()
    {
        using var fixture = new VerificationFixture(
            allowedKinds: [ExtensionKind.Workspace],
            allowedPermissions: [""]);
        var request = fixture.CreateRequest(
            kind: ExtensionKind.Workspace,
            manifestJson: BuildWorkspaceManifest("[\"\"]"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateVerifier().VerifyAsync(request));

        Assert.Contains("权限", error.Message, StringComparison.Ordinal);
        Assert.Contains("不能为空", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_RejectsWrongPackageSizeBeforeOtherChecks()
    {
        using var fixture = new VerificationFixture();
        var valid = fixture.CreateRequest();
        var request = ReplaceRelease(valid, size: valid.Release.Size + 1, sha256: new string('0', 64), keyId: "unknown-key");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateVerifier().VerifyAsync(request));

        Assert.Contains("大小", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_RejectsWrongShaBeforeTrustChecks()
    {
        using var fixture = new VerificationFixture();
        var valid = fixture.CreateRequest();
        var request = ReplaceRelease(valid, sha256: new string('0', 64), keyId: "unknown-key");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateVerifier().VerifyAsync(request));

        Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_RejectsTamperedZipEvenWhenCatalogSizeAndShaAreUpdated()
    {
        using var fixture = new VerificationFixture();
        var original = fixture.CreateRequest(extraFileContent: "original");
        var tamperedBytes = BuildPackage(BuildAnalysisManifest(), "tampered");
        var request = ReplaceRelease(
            original,
            packageBytes: tamperedBytes,
            size: tamperedBytes.Length,
            sha256: Sha256(tamperedBytes));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateVerifier().VerifyAsync(request));

        Assert.Contains("验签", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_RejectsInvalidSignature()
    {
        using var fixture = new VerificationFixture();
        var valid = fixture.CreateRequest();
        var request = ReplaceRelease(valid, signature: Convert.ToBase64String(new byte[64]));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateVerifier().VerifyAsync(request));

        Assert.Contains("验签", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_RejectsUnsignedPackageWithoutDeveloperModeBypass()
    {
        using var fixture = new VerificationFixture();
        var valid = fixture.CreateRequest();
        var request = new ExtensionPackageVerificationRequest
        {
            PackageBytes = valid.PackageBytes,
            CatalogItem = valid.CatalogItem,
            Release = new ExtensionRelease
            {
                Version = valid.Release.Version,
                MinHostVersion = valid.Release.MinHostVersion,
                Url = valid.Release.Url,
                Size = valid.Release.Size,
                Sha256 = valid.Release.Sha256,
                Signature = null!
            }
        };

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateVerifier().VerifyAsync(request));

        Assert.Contains("签名", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_BoundsManifestReadEvenWhenZipLengthMetadataUnderreports()
    {
        using var fixture = new VerificationFixture();
        var oversizedManifest = BuildAnalysisManifest() + new string(' ', 1024 * 1024 + 1);
        var packageBytes = BuildPackage(oversizedManifest, compressionLevel: CompressionLevel.NoCompression);
        UnderreportFirstCentralDirectoryEntryLength(packageBytes);
        var request = fixture.CreateRequest(packageBytes: packageBytes);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateVerifier().VerifyAsync(request));

        Assert.Contains("1 MB", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_RejectsReleaseThatIsNotListedByCatalogItem()
    {
        using var fixture = new VerificationFixture();
        var valid = fixture.CreateRequest();
        var detached = new ExtensionPackageVerificationRequest
        {
            PackageBytes = valid.PackageBytes,
            CatalogItem = new ExtensionCatalogItem
            {
                Id = valid.CatalogItem.Id,
                Name = valid.CatalogItem.Name,
                Description = valid.CatalogItem.Description,
                PublisherId = valid.CatalogItem.PublisherId,
                Kind = valid.CatalogItem.Kind,
                Releases = []
            },
            Release = valid.Release
        };

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateVerifier().VerifyAsync(detached));

        Assert.Contains("Catalog", error.Message, StringComparison.Ordinal);
        Assert.Contains("release", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ExtensionPackageVerificationRequest ReplaceRelease(
        ExtensionPackageVerificationRequest request,
        byte[]? packageBytes = null,
        long? size = null,
        string? sha256 = null,
        string? keyId = null,
        string? signature = null)
    {
        var release = new ExtensionRelease
        {
            Version = request.Release.Version,
            MinHostVersion = request.Release.MinHostVersion,
            Url = request.Release.Url,
            Size = size ?? request.Release.Size,
            Sha256 = sha256 ?? request.Release.Sha256,
            Signature = new ExtensionPackageSignature
            {
                KeyId = keyId ?? request.Release.Signature.KeyId,
                Signature = signature ?? request.Release.Signature.Signature
            }
        };
        return new ExtensionPackageVerificationRequest
        {
            PackageBytes = packageBytes ?? request.PackageBytes,
            CatalogItem = new ExtensionCatalogItem
            {
                Id = request.CatalogItem.Id,
                Name = request.CatalogItem.Name,
                Description = request.CatalogItem.Description,
                PublisherId = request.CatalogItem.PublisherId,
                Kind = request.CatalogItem.Kind,
                Releases = [release]
            },
            Release = release
        };
    }

    private static byte[] BuildPackage(
        string manifestJson,
        string extraFileContent = "payload",
        CompressionLevel compressionLevel = CompressionLevel.NoCompression)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "manifest.json", manifestJson, compressionLevel);
            WriteEntry(archive, "payload.txt", extraFileContent, compressionLevel);
        }
        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content, CompressionLevel compressionLevel)
    {
        var entry = archive.CreateEntry(name, compressionLevel);
        entry.LastWriteTime = new DateTimeOffset(2026, 8, 23, 0, 0, 0, TimeSpan.Zero);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void UnderreportFirstCentralDirectoryEntryLength(byte[] packageBytes)
    {
        ReadOnlySpan<byte> signature = [0x50, 0x4b, 0x01, 0x02];
        var offset = packageBytes.AsSpan().IndexOf(signature);
        Assert.True(offset >= 0, "测试 ZIP 缺少中央目录记录。");
        BinaryPrimitives.WriteUInt32LittleEndian(packageBytes.AsSpan(offset + 24, 4), 1);
    }

    private static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string BuildAnalysisManifest() => """
        {
          "schemaVersion": 2,
          "id": "test-tool",
          "name": "测试分析扩展",
          "version": "2.0.0",
          "kind": "analysis",
          "publisherId": "test-publisher",
          "hostApiVersion": "1.0",
          "minHostVersion": "2.0.0",
          "runtime": { "kind": "process", "protocol": "analysis-process-v1", "entry": "tool.exe" },
          "capabilities": ["analysis.engine"],
          "permissions": [],
          "dependencies": []
        }
        """;

    private static string BuildWorkspaceManifest(string permissionsJson)
        => $$"""
        {
          "schemaVersion": 2,
          "id": "test-tool",
          "name": "测试工作区扩展",
          "version": "2.0.0",
          "kind": "workspace",
          "publisherId": "test-publisher",
          "hostApiVersion": "1.0",
          "minHostVersion": "2.0.0",
          "runtime": { "kind": "web", "entry": "index.html" },
          "capabilities": ["workspace.page"],
          "permissions": {{permissionsJson}},
          "dependencies": []
        }
        """;

    private sealed class VerificationFixture : IDisposable
    {
        private readonly Key _key = Key.Create(SignatureAlgorithm.Ed25519);

        public VerificationFixture(
            IReadOnlyList<ExtensionKind>? allowedKinds = null,
            IReadOnlyList<string>? allowedPermissions = null)
        {
            TrustedKey = new TrustedPublisherKey
            {
                KeyId = "test-key",
                PublisherId = "test-publisher",
                PublicKeyBase64 = Convert.ToBase64String(_key.PublicKey.Export(KeyBlobFormat.RawPublicKey)),
                Scope = new ExtensionTrustScope
                {
                    AllowedKinds = allowedKinds ?? [ExtensionKind.Analysis],
                    Permissions = allowedPermissions ?? []
                }
            };
        }

        public TrustedPublisherKey TrustedKey { get; }

        public IExtensionPackageVerifier CreateVerifier()
            => new ExtensionPackageVerifier(new ExtensionTrustStore([TrustedKey]));

        public ExtensionPackageVerificationRequest CreateRequest(
            string keyId = "test-key",
            string publisherId = "test-publisher",
            ExtensionKind kind = ExtensionKind.Analysis,
            string? manifestJson = null,
            string extraFileContent = "payload",
            byte[]? packageBytes = null,
            string? signature = null)
        {
            packageBytes ??= BuildPackage(manifestJson ?? BuildAnalysisManifest(), extraFileContent);
            var release = new ExtensionRelease
            {
                Version = "2.0.0",
                MinHostVersion = "2.0.0",
                Url = "https://example.invalid/test-tool.zip",
                Size = packageBytes.Length,
                Sha256 = Sha256(packageBytes),
                Signature = new ExtensionPackageSignature
                {
                    KeyId = keyId,
                    Signature = signature ?? Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(_key, packageBytes))
                }
            };
            return new ExtensionPackageVerificationRequest
            {
                PackageBytes = packageBytes,
                CatalogItem = new ExtensionCatalogItem
                {
                    Id = "test-tool",
                    Name = "测试扩展",
                    Description = "测试包",
                    PublisherId = publisherId,
                    Kind = kind,
                    Releases = [release]
                },
                Release = release
            };
        }

        public void Dispose() => _key.Dispose();
    }

    private sealed class CallbackTrustStore(TrustedPublisherKey trustedKey, Action onLookup) : IExtensionTrustStore
    {
        public bool TryGetTrustedKey(string keyId, out TrustedPublisherKey resolved)
        {
            onLookup();
            resolved = trustedKey;
            return string.Equals(keyId, trustedKey.KeyId, StringComparison.Ordinal);
        }
    }
}
