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
    public void Verify_AcceptsValidOfficialPackageSignedOverRawZipBytes()
    {
        using var fixture = new VerificationFixture();
        var request = fixture.CreateRequest();
        var verifier = fixture.CreateVerifier();

        var result = verifier.Verify(request);

        Assert.Equal("official-tool", result.Manifest.Id);
        Assert.Equal("official-2026", result.TrustedKeyId);
        Assert.Equal(request.Release.Sha256, result.PackageSha256);
        Assert.NotNull(JsonSerializer.Deserialize<ExtensionPackageVerificationRequest>(JsonSerializer.Serialize(request)));
        Assert.NotNull(JsonSerializer.Deserialize<ExtensionPackageVerificationResult>(JsonSerializer.Serialize(result)));
    }

    [Fact]
    public void Verify_RejectsUnknownKey()
    {
        using var fixture = new VerificationFixture();
        var request = fixture.CreateRequest(keyId: "unknown-key");

        var error = Assert.Throws<InvalidDataException>(() => fixture.CreateVerifier().Verify(request));

        Assert.Contains("密钥", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RejectsPublisherMismatch()
    {
        using var fixture = new VerificationFixture();
        var request = fixture.CreateRequest(publisherId: "other-publisher");

        var error = Assert.Throws<InvalidDataException>(() => fixture.CreateVerifier().Verify(request));

        Assert.Contains("发布者", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RejectsManifestPublisherDifferentFromCatalogAndTrustedKey()
    {
        using var fixture = new VerificationFixture();
        var manifest = BuildAnalysisManifest().Replace("\"publisherId\": \"thelinyue\"", "\"publisherId\": \"other-publisher\"", StringComparison.Ordinal);
        var request = fixture.CreateRequest(manifestJson: manifest);

        var error = Assert.Throws<InvalidDataException>(() => fixture.CreateVerifier().Verify(request));

        Assert.Contains("发布者", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RejectsManifestKindDifferentFromCatalog()
    {
        using var fixture = new VerificationFixture();
        var request = fixture.CreateRequest(manifestJson: BuildWorkspaceManifest([]));

        var error = Assert.Throws<InvalidDataException>(() => fixture.CreateVerifier().Verify(request));

        Assert.Contains("类别", error.Message, StringComparison.Ordinal);
    }
    [Fact]
    public void Verify_ChecksManifestPublisherBeforeKindScope()
    {
        using var fixture = new VerificationFixture(allowedKinds: [ExtensionKind.Workspace]);
        var manifest = BuildAnalysisManifest().Replace("\"publisherId\": \"thelinyue\"", "\"publisherId\": \"other-publisher\"", StringComparison.Ordinal);
        var request = fixture.CreateRequest(manifestJson: manifest);

        var error = Assert.Throws<InvalidDataException>(() => fixture.CreateVerifier().Verify(request));

        Assert.Contains("发布者", error.Message, StringComparison.Ordinal);
    }
    [Fact]
    public void Verify_RejectsKindOutsideTrustedScope()
    {
        using var fixture = new VerificationFixture(allowedKinds: [ExtensionKind.Workspace]);
        var request = fixture.CreateRequest();

        var error = Assert.Throws<InvalidDataException>(() => fixture.CreateVerifier().Verify(request));

        Assert.Contains("类别", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RejectsManifestPermissionOutsideTrustedScope()
    {
        using var fixture = new VerificationFixture(
            allowedKinds: [ExtensionKind.Workspace],
            allowedPermissions: []);
        var request = fixture.CreateRequest(
            kind: ExtensionKind.Workspace,
            manifestJson: BuildWorkspaceManifest(["workspace.readText"]));

        var error = Assert.Throws<InvalidDataException>(() => fixture.CreateVerifier().Verify(request));

        Assert.Contains("权限", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RejectsWrongPackageSizeBeforeOtherChecks()
    {
        using var fixture = new VerificationFixture();
        var valid = fixture.CreateRequest();
        var request = ReplaceRelease(valid, size: valid.Release.Size + 1, sha256: new string('0', 64), keyId: "unknown-key");

        var error = Assert.Throws<InvalidDataException>(() => fixture.CreateVerifier().Verify(request));

        Assert.Contains("大小", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RejectsWrongShaBeforeTrustChecks()
    {
        using var fixture = new VerificationFixture();
        var valid = fixture.CreateRequest();
        var request = ReplaceRelease(valid, sha256: new string('0', 64), keyId: "unknown-key");

        var error = Assert.Throws<InvalidDataException>(() => fixture.CreateVerifier().Verify(request));

        Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RejectsTamperedZipEvenWhenCatalogSizeAndShaAreUpdated()
    {
        using var fixture = new VerificationFixture();
        var original = fixture.CreateRequest(extraFileContent: "original");
        var tamperedBytes = BuildPackage(BuildAnalysisManifest(), "tampered");
        var request = ReplaceRelease(
            original,
            packageBytes: tamperedBytes,
            size: tamperedBytes.Length,
            sha256: Sha256(tamperedBytes));

        var error = Assert.Throws<InvalidDataException>(() => fixture.CreateVerifier().Verify(request));

        Assert.Contains("验签", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RejectsInvalidSignature()
    {
        using var fixture = new VerificationFixture();
        var valid = fixture.CreateRequest();
        var request = ReplaceRelease(valid, signature: Convert.ToBase64String(new byte[64]));

        var error = Assert.Throws<InvalidDataException>(() => fixture.CreateVerifier().Verify(request));

        Assert.Contains("验签", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RejectsUnsignedPackageWithoutDeveloperModeBypass()
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

        var error = Assert.Throws<InvalidDataException>(() => fixture.CreateVerifier().Verify(request));

        Assert.Contains("签名", error.Message, StringComparison.Ordinal);
    }

    private static ExtensionPackageVerificationRequest ReplaceRelease(
        ExtensionPackageVerificationRequest request,
        byte[]? packageBytes = null,
        long? size = null,
        string? sha256 = null,
        string? keyId = null,
        string? signature = null)
        => new()
        {
            PackageBytes = packageBytes ?? request.PackageBytes,
            CatalogItem = request.CatalogItem,
            Release = new ExtensionRelease
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
            }
        };

    private static byte[] BuildPackage(string manifestJson, string extraFileContent = "payload")
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "manifest.json", manifestJson);
            WriteEntry(archive, "payload.txt", extraFileContent);
        }
        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(2026, 8, 23, 0, 0, 0, TimeSpan.Zero);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string BuildAnalysisManifest() => """
        {
          "schemaVersion": 2,
          "id": "official-tool",
          "name": "官方分析扩展",
          "version": "2.0.0",
          "kind": "analysis",
          "publisherId": "thelinyue",
          "hostApiVersion": "1.0",
          "minHostVersion": "2.0.0",
          "runtime": { "kind": "process", "protocol": "analysis-process-v1", "entry": "tool.exe" },
          "capabilities": ["analysis.engine"],
          "permissions": [],
          "dependencies": []
        }
        """;

    private static string BuildWorkspaceManifest(IReadOnlyList<string> permissions)
        => $$"""
        {
          "schemaVersion": 2,
          "id": "official-tool",
          "name": "官方工作区扩展",
          "version": "2.0.0",
          "kind": "workspace",
          "publisherId": "thelinyue",
          "hostApiVersion": "1.0",
          "minHostVersion": "2.0.0",
          "runtime": { "kind": "web", "entry": "index.html" },
          "capabilities": ["workspace.page"],
          "permissions": {{JsonSerializer.Serialize(permissions)}},
          "dependencies": []
        }
        """;

    private sealed class VerificationFixture : IDisposable
    {
        private readonly Key _key = Key.Create(SignatureAlgorithm.Ed25519);
        private readonly TrustedPublisherKey _trustedKey;

        public VerificationFixture(
            IReadOnlyList<ExtensionKind>? allowedKinds = null,
            IReadOnlyList<string>? allowedPermissions = null)
        {
            _trustedKey = new TrustedPublisherKey
            {
                KeyId = "official-2026",
                PublisherId = "thelinyue",
                PublicKeyBase64 = Convert.ToBase64String(_key.PublicKey.Export(KeyBlobFormat.RawPublicKey)),
                Scope = new ExtensionTrustScope
                {
                    AllowedKinds = allowedKinds ?? [ExtensionKind.Analysis],
                    Permissions = allowedPermissions ?? []
                }
            };
        }

        public IExtensionPackageVerifier CreateVerifier()
            => new ExtensionPackageVerifier(new ExtensionTrustStore([_trustedKey]));

        public ExtensionPackageVerificationRequest CreateRequest(
            string keyId = "official-2026",
            string publisherId = "thelinyue",
            ExtensionKind kind = ExtensionKind.Analysis,
            string? manifestJson = null,
            string extraFileContent = "payload")
        {
            var packageBytes = BuildPackage(manifestJson ?? BuildAnalysisManifest(), extraFileContent);
            return new ExtensionPackageVerificationRequest
            {
                PackageBytes = packageBytes,
                CatalogItem = new ExtensionCatalogItem
                {
                    Id = "official-tool",
                    Name = "官方扩展",
                    Description = "测试包",
                    PublisherId = publisherId,
                    Kind = kind,
                    Releases = []
                },
                Release = new ExtensionRelease
                {
                    Version = "2.0.0",
                    MinHostVersion = "2.0.0",
                    Url = "https://example.invalid/official-tool.zip",
                    Size = packageBytes.Length,
                    Sha256 = Sha256(packageBytes),
                    Signature = new ExtensionPackageSignature
                    {
                        KeyId = keyId,
                        Signature = Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(_key, packageBytes))
                    }
                }
            };
        }

        public void Dispose() => _key.Dispose();
    }
}
