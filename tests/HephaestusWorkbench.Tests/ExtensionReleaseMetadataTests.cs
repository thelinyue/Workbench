using System.Text.Json;
using System.Text.Json.Nodes;
using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

/// <summary>锁定扩展正式发布 metadata v2 的跨仓交接契约；ZIP 内 manifest 仍是最终权威源。</summary>
public sealed class ExtensionReleaseMetadataTests
{
    [Fact]
    public void Parser_AcceptsCompleteManifestAndMapsExistingEditorialDescription()
    {
        var document = ExtensionReleaseMetadataParser.Parse(CreateMetadata().ToJsonString());
        var package = Assert.Single(document.Packages);

        Assert.Equal("test-tool", package.Manifest.Id);
        Assert.Equal(["analysis.engine"], package.Manifest.Capabilities);
        var bundled = ExtensionReleaseHandoffMapper.ToBundledExtension(package, "已审核的 Catalog 描述");
        Assert.Equal("已审核的 Catalog 描述", bundled.Description);
        Assert.Equal(package.File, bundled.Asset);
        Assert.Equal(package.KeyId, bundled.Release.Signature.KeyId);
    }

    [Fact]
    public void Parser_RejectsUnknownFieldsInsideEmbeddedManifest()
    {
        var metadata = CreateMetadata();
        GetManifest(metadata)["navigation"] = "analysis";

        var exception = Assert.Throws<ExtensionContractException>(() =>
            ExtensionReleaseMetadataParser.Parse(metadata.ToJsonString()));
        Assert.Contains("release metadata", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parser_RejectsFileThatDoesNotMatchExplicitReleaseUrl()
    {
        var metadata = CreateMetadata();
        GetPackage(metadata)["url"] = "https://example.invalid/releases/test-tool-other.zip";

        Assert.Throws<ExtensionContractException>(() => ExtensionReleaseMetadataParser.Parse(metadata.ToJsonString()));
    }


    [Theory]
    [InlineData(0L)]
    [InlineData(209715201L)]
    public void Parser_RejectsPackageSizeOutsideMetadataBoundary(long size)
    {
        var metadata = CreateMetadata();
        GetPackage(metadata)["size"] = size;

        Assert.Throws<ExtensionContractException>(() => ExtensionReleaseMetadataParser.Parse(metadata.ToJsonString()));
    }

    [Fact]
    public void Parser_AcceptsStrictPrereleaseVersionAndDependencyObjects()
    {
        var metadata = CreateMetadata();
        var manifest = GetManifest(metadata);
        manifest["version"] = "2.1.0-beta.1";
        manifest["dependencies"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "shared-rules",
                ["version"] = "1.2.0-rc.1"
            }
        };
        GetPackage(metadata)["file"] = "test-tool-v2.1.0-beta.1.zip";
        GetPackage(metadata)["url"] = "https://example.invalid/releases/test-tool-v2.1.0-beta.1.zip";

        var package = Assert.Single(ExtensionReleaseMetadataParser.Parse(metadata.ToJsonString()).Packages);

        Assert.Equal("2.1.0-beta.1", package.Manifest.Version);
        Assert.Equal("shared-rules", Assert.Single(package.Manifest.Dependencies).Id);
    }

    [Fact]
    public void Parser_RejectsSemanticVersionWithLeadingZero()
    {
        var metadata = CreateMetadata();
        GetManifest(metadata)["version"] = "02.0.0";

        Assert.Throws<ExtensionContractException>(() => ExtensionReleaseMetadataParser.Parse(metadata.ToJsonString()));
    }

    [Theory]
    [InlineData("workspace", "web", "workspace.page", "protocol", "workspace-bridge-v1")]
    [InlineData("analysis", "content", "analysis.rule-pack", "entry", "rules.json")]
    [InlineData("analysis", "content", "analysis.rule-pack", "protocol", "unexpected")]
    [InlineData("maintenance", "content", "maintenance.workflow-pack", "entry", "workflow.json")]
    [InlineData("maintenance", "content", "maintenance.workflow-pack", "protocol", "unexpected")]
    public void Parser_RejectsRuntimeFieldsOutsideExactKindShape(
        string kind,
        string runtimeKind,
        string capability,
        string extraField,
        string extraValue)
    {
        var metadata = CreateMetadata();
        SetManifestKind(metadata, kind, runtimeKind, capability);
        GetRuntime(metadata)[extraField] = extraValue;

        Assert.Throws<ExtensionContractException>(() => ExtensionReleaseMetadataParser.Parse(metadata.ToJsonString()));
    }

    [Theory]
    [InlineData("protocol")]
    [InlineData("entry")]
    public void Parser_RejectsAnalysisProcessRuntimeMissingRequiredField(string missingField)
    {
        var metadata = CreateMetadata();
        GetRuntime(metadata).Remove(missingField);

        Assert.Throws<ExtensionContractException>(() => ExtensionReleaseMetadataParser.Parse(metadata.ToJsonString()));
    }

    [Theory]
    [InlineData("https://user:pass@example.invalid/releases/test-tool-v2.0.0.zip")]
    [InlineData("https://example.invalid:8443/releases/test-tool-v2.0.0.zip")]
    [InlineData("https://[::1]/releases/test-tool-v2.0.0.zip")]
    [InlineData("https://example.invalid/releases/test-tool-v2.0.0.zip#fragment")]
    [InlineData("https://例子.invalid/releases/test-tool-v2.0.0.zip")]
    public void Parser_RejectsUnsafeCatalogUrls(string url)
    {
        var metadata = CreateMetadata();
        GetPackage(metadata)["url"] = url;

        Assert.Throws<ExtensionContractException>(() => ExtensionReleaseMetadataParser.Parse(metadata.ToJsonString()));
    }

    [Fact]
    public void Parser_AcceptsExplicitHttps443UrlWithQuery()
    {
        var metadata = CreateMetadata();
        GetPackage(metadata)["url"] = "https://example.invalid:443/releases/test-tool-v2.0.0.zip?download=1";

        var package = Assert.Single(ExtensionReleaseMetadataParser.Parse(metadata.ToJsonString()).Packages);

        Assert.Equal("test-tool-v2.0.0.zip", package.File);
    }

    [Fact]
    public void Mapper_RequiresExplicitReviewedDescriptionInsteadOfGuessing()
    {
        var package = Assert.Single(ExtensionReleaseMetadataParser.Parse(CreateMetadata().ToJsonString()).Packages);

        var exception = Assert.Throws<InvalidDataException>(() =>
            ExtensionReleaseHandoffMapper.ToBundledExtension(package, " "));
        Assert.Contains("description", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject CreateMetadata()
        => new()
        {
            ["schemaVersion"] = 2,
            ["generatedAtUtc"] = "2026-08-24T00:00:00Z",
            ["packages"] = new JsonArray
            {
                new JsonObject
                {
                    ["manifest"] = new JsonObject
                    {
                        ["schemaVersion"] = 2,
                        ["id"] = "test-tool",
                        ["name"] = "测试扩展",
                        ["version"] = "2.0.0",
                        ["kind"] = "analysis",
                        ["publisherId"] = "test-publisher",
                        ["hostApiVersion"] = "1.0",
                        ["minHostVersion"] = "2.0.0",
                        ["runtime"] = new JsonObject
                        {
                            ["kind"] = "process",
                            ["protocol"] = "analysis-process-v1",
                            ["entry"] = "tool.exe"
                        },
                        ["capabilities"] = new JsonArray("analysis.engine"),
                        ["permissions"] = new JsonArray(),
                        ["dependencies"] = new JsonArray()
                    },
                    ["file"] = "test-tool-v2.0.0.zip",
                    ["url"] = "https://example.invalid/releases/test-tool-v2.0.0.zip",
                    ["size"] = 123,
                    ["sha256"] = new string('a', 64),
                    ["keyId"] = "test-key",
                    ["signature"] = Convert.ToBase64String(new byte[64])
                }
            }
        };


    private static JsonObject GetRuntime(JsonObject metadata)
        => Assert.IsType<JsonObject>(GetManifest(metadata)["runtime"]);

    private static void SetManifestKind(JsonObject metadata, string kind, string runtimeKind, string capability)
    {
        var manifest = GetManifest(metadata);
        manifest["kind"] = kind;
        manifest["runtime"] = runtimeKind switch
        {
            "web" => new JsonObject { ["kind"] = runtimeKind, ["entry"] = "index.html" },
            "process" => new JsonObject
            {
                ["kind"] = runtimeKind,
                ["protocol"] = "analysis-process-v1",
                ["entry"] = "tool.exe"
            },
            _ => new JsonObject { ["kind"] = runtimeKind }
        };
        manifest["capabilities"] = new JsonArray(capability);
    }

    private static JsonObject GetPackage(JsonObject metadata)
        => Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(metadata["packages"])[0]);

    private static JsonObject GetManifest(JsonObject metadata)
        => Assert.IsType<JsonObject>(GetPackage(metadata)["manifest"]);
}
