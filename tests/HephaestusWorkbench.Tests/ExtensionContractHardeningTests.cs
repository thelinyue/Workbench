using System.Text.Json.Nodes;
using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Tests;

public sealed class ExtensionContractHardeningTests
{
    private const string VersionDirectory = @"C:\WorkbenchData\Extensions\log-analyzer\2.0.0";
    private const string ValidSignature = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==";

    [Theory]
    [InlineData("id", "../escape")]
    [InlineData("publisherId", "UPPER/../x")]
    [InlineData("name", "")]
    [InlineData("description", "")]
    [InlineData("version", "../current")]
    [InlineData("minHostVersion", "v2")]
    public void ParseCatalog_RejectsInvalidIdentityAndVersionMetadata(string field, string value)
    {
        var root = JsonNode.Parse(BuildCatalog())!.AsObject();
        var extension = root["extensions"]![0]!.AsObject();
        var release = extension["releases"]![0]!.AsObject();
        if (field is "version" or "minHostVersion") release[field] = value;
        else extension[field] = value;

        Assert.Throws<ExtensionContractException>(() => ExtensionCatalogParser.Parse(root.ToJsonString()));
    }

    [Fact]
    public void ParseManifest_RejectsInvalidDependency()
    {
        var json = BuildManifest("""[{ "id": "../other", "version": "anything" }]""");

        Assert.Throws<ExtensionContractException>(() => ExtensionManifestParser.Parse(json, VersionDirectory));
    }

    [Fact]
    public void ParseManifest_RejectsNullDuplicateAndSelfDependencies()
    {
        var invalidDependencies = new[]
        {
            "[null]",
            """[{ "id": "log-analyzer", "version": "2.0.0" }]""",
            """[{ "id": "rules", "version": "2.0.0" }, { "id": "rules", "version": "2.0.0" }]"""
        };

        foreach (var dependencies in invalidDependencies)
        {
            Assert.Throws<ExtensionContractException>(() => ExtensionManifestParser.Parse(
                BuildManifest(dependencies), VersionDirectory));
        }
    }

    [Fact]
    public void ParseManifest_WrapsInvalidPathAsContractError()
    {
        var json = BuildManifest("[]").Replace("analyzer.exe", "bad\\u0000name.exe", StringComparison.Ordinal);

        var error = Assert.Throws<ExtensionContractException>(() => ExtensionManifestParser.Parse(json, VersionDirectory));

        Assert.Contains("入口路径无效", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseManifest_RejectsNumericPrereleaseWithLeadingZero()
    {
        var json = BuildManifest("[]").Replace("\"version\": \"2.0.0\"", "\"version\": \"2.0.0-01\"", StringComparison.Ordinal);

        Assert.Throws<ExtensionContractException>(() => ExtensionManifestParser.Parse(json, VersionDirectory));
    }

    [Theory]
    [InlineData(1, "log-analyzer", "2.0.0", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData(2, "../escape", "2.0.0", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData(2, "log-analyzer", "v2", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData(2, "log-analyzer", "2.0.0", "x")]
    public void ParseCurrent_RejectsInvalidSecurityMetadata(int schemaVersion, string id, string version, string sha256)
    {
        var json = $$"""
            {
              "schemaVersion": {{schemaVersion}},
              "id": "{{id}}",
              "version": "{{version}}",
              "packageSha256": "{{sha256}}",
              "state": "healthy"
            }
            """;

        Assert.Throws<ExtensionContractException>(() => ExtensionCurrentParser.Parse(json));
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("catalog")]
    [InlineData("current")]
    [InlineData("analysis")]
    [InlineData("workspace")]
    public void ExternalJsonParsers_RejectWhitespaceAsContractError(string parser)
    {
        Action action = parser switch
        {
            "manifest" => () => ExtensionManifestParser.Parse(" ", VersionDirectory),
            "catalog" => () => ExtensionCatalogParser.Parse(" "),
            "current" => () => ExtensionCurrentParser.Parse(" "),
            "analysis" => () => AnalysisProcessProtocol.ParseRequest(" "),
            _ => () => WorkspaceBridgeProtocol.ParseRequest(" ")
        };

        Assert.Throws<ExtensionContractException>(action);
    }

    [Fact]
    public void ParseCurrent_RejectsUnknownField()
    {
        var json = """
            {
              "schemaVersion": 2,
              "id": "log-analyzer",
              "version": "2.0.0",
              "packageSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "state": "healthy",
              "extra": true
            }
            """;

        Assert.Throws<ExtensionContractException>(() => ExtensionCurrentParser.Parse(json));
    }
    [Fact]
    public void ParseManifest_WrapsInvalidDirectoryPathAsContractError()
    {
        var invalidDirectory = "bad\0directory";

        Assert.Throws<ExtensionContractException>(() => ExtensionManifestParser.Parse(BuildManifest("[]"), invalidDirectory));
    }
    private static string BuildManifest(string dependencies) => $$"""
        {
          "schemaVersion": 2,
          "id": "log-analyzer",
          "name": "日志分析",
          "version": "2.0.0",
          "kind": "analysis",
          "publisherId": "thelinyue",
          "hostApiVersion": "1.0",
          "minHostVersion": "2.0.0",
          "runtime": { "kind": "process", "protocol": "analysis-process-v1", "entry": "analyzer.exe" },
          "capabilities": ["analysis.engine"],
          "permissions": [],
          "dependencies": {{dependencies}}
        }
        """;

    private static string BuildCatalog() => $$"""
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
                  "url": "https://example.invalid/log-analyzer.zip",
                  "size": 1024,
                  "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "signature": { "keyId": "official-2026", "signature": "{{ValidSignature}}" }
                }
              ]
            }
          ]
        }
        """;
}
