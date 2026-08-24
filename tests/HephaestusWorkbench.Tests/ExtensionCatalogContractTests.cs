using System.Text.Json.Nodes;
using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Tests;

public sealed class ExtensionCatalogContractTests
{
    private const string ValidSignature = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==";

    [Fact]
    public void ParseCatalog_AcceptsSignedV2Release()
    {
        var catalog = ExtensionCatalogParser.Parse(BuildCatalog(
            "https://example.invalid/log-analyzer-2.0.0.zip",
            1024,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "official-2026",
            ValidSignature));

        var extension = Assert.Single(catalog.Extensions);
        var release = Assert.Single(extension.Releases);
        Assert.Equal(ExtensionKind.Analysis, extension.Kind);
        Assert.Equal("official-2026", release.Signature.KeyId);
        Assert.Equal(1024, release.Size);
    }

    [Theory]
    [InlineData("http://example.invalid/log-analyzer.zip", 1024, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "official-2026", ValidSignature)]
    [InlineData("https://example.invalid/log-analyzer.zip", 0, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "official-2026", ValidSignature)]
    [InlineData("https://example.invalid/log-analyzer.zip", 1024, "not-a-sha", "official-2026", ValidSignature)]
    [InlineData("https://example.invalid/log-analyzer.zip", 1024, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "", ValidSignature)]
    [InlineData("https://example.invalid/log-analyzer.zip", 1024, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "official-2026", "")]
    [InlineData("https://example.invalid/log-analyzer.zip", 1024, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "official-2026", "ZmFrZQ==")]
    public void ParseCatalog_RejectsInvalidReleaseIntegrityMetadata(
        string url,
        long size,
        string sha256,
        string keyId,
        string signature)
    {
        var json = BuildCatalog(url, size, sha256, keyId, signature);

        Assert.Throws<ExtensionContractException>(() => ExtensionCatalogParser.Parse(json));
    }

    [Fact]
    public void ParseCatalog_RejectsDuplicateExtensionVersion()
    {
        var release = $$"""
            {
              "version": "2.0.0",
              "minHostVersion": "2.0.0",
              "url": "https://example.invalid/log-analyzer.zip",
              "size": 1024,
              "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "signature": { "keyId": "official-2026", "signature": "{{ValidSignature}}" }
            }
            """;
        var json = $$"""
            {
              "schemaVersion": 2,
              "extensions": [
                {
                  "id": "log-analyzer",
                  "name": "日志分析",
                  "description": "诊断报告",
                  "publisherId": "thelinyue",
                  "kind": "analysis",
                  "releases": [{{release}}, {{release}}]
                }
              ]
            }
            """;

        var error = Assert.Throws<ExtensionContractException>(() => ExtensionCatalogParser.Parse(json));

        Assert.Contains("重复版本", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("extensions")]
    [InlineData("releases")]
    [InlineData("sha256")]
    [InlineData("signature")]
    public void ParseCatalog_RejectsExplicitNullRequiredMembersWithContractError(string property)
    {
        var root = JsonNode.Parse(BuildCatalog(
            "https://example.invalid/log-analyzer.zip",
            1024,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "official-2026",
            ValidSignature))!.AsObject();
        var extension = root["extensions"]![0]!.AsObject();
        var release = extension["releases"]![0]!.AsObject();
        switch (property)
        {
            case "extensions": root["extensions"] = null; break;
            case "releases": extension["releases"] = null; break;
            default: release[property] = null; break;
        }

        var error = Assert.Throws<ExtensionContractException>(() => ExtensionCatalogParser.Parse(root.ToJsonString()));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Theory]
    [InlineData("extension")]
    [InlineData("release")]
    public void ParseCatalog_RejectsNullArrayElementWithContractError(string element)
    {
        var root = JsonNode.Parse(BuildCatalog(
            "https://example.invalid/log-analyzer.zip",
            1024,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "official-2026",
            ValidSignature))!.AsObject();
        if (element == "extension")
        {
            root["extensions"]![0] = null;
        }
        else
        {
            root["extensions"]![0]!["releases"]![0] = null;
        }

        Assert.Throws<ExtensionContractException>(() => ExtensionCatalogParser.Parse(root.ToJsonString()));
    }
    private static string BuildCatalog(string url, long size, string sha256, string keyId, string signature) => $$"""
        {
          "schemaVersion": 2,
          "extensions": [
            {
              "id": "log-analyzer",
              "name": "日志分析",
              "description": "诊断报告",
              "publisherId": "thelinyue",
              "kind": "analysis",
              "releases": [
                {
                  "version": "2.0.0",
                  "minHostVersion": "2.0.0",
                  "url": "{{url}}",
                  "size": {{size}},
                  "sha256": "{{sha256}}",
                  "signature": { "keyId": "{{keyId}}", "signature": "{{signature}}" }
                }
              ]
            }
          ]
        }
        """;
}
