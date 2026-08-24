using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class BundledExtensionManifestTests
{
    [Fact]
    public void Parse_ValidV2Lock_ProducesCatalogVerificationRequestMetadata()
    {
        var document = BundledExtensionManifestParser.Parse(ValidJson());
        var item = Assert.Single(document.Extensions);
        var catalogItem = item.ToCatalogItem();

        Assert.Equal(2, document.SchemaVersion);
        Assert.Equal("log-analyzer-v2.0.0.zip", item.Asset);
        Assert.Equal("log-analyzer", catalogItem.Id);
        Assert.Equal(ExtensionKind.Analysis, catalogItem.Kind);
        Assert.Same(item.Release, Assert.Single(catalogItem.Releases));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"extensions\":[]}", "schemaVersion")]
    [InlineData("{\"schemaVersion\":2,\"extensions\":[]}", "不能为空")]
    [InlineData("{\"schemaVersion\":2,\"extensions\":[],\"legacy\":true}", "JSON")]
    public void Parse_InvalidRoot_IsRejected(string json, string expected)
    {
        var error = Assert.Throws<InvalidDataException>(() => BundledExtensionManifestParser.Parse(json));
        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../tool.zip")]
    [InlineData("folder/tool.zip")]
    [InlineData("folder\\tool.zip")]
    [InlineData("C:\\tool.zip")]
    [InlineData("CON.zip")]
    [InlineData("COM¹.zip")]
    [InlineData("LPT².zip")]
    public void Parse_UnsafeAssetName_IsRejected(string asset)
    {
        var error = Assert.Throws<InvalidDataException>(() => BundledExtensionManifestParser.Parse(ValidJson(asset)));
        Assert.Contains("asset", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_MissingKind_IsRejectedInsteadOfDefaultingToWorkspace()
    {
        var json = ValidJson().Replace("\"kind\":\"analysis\",", string.Empty, StringComparison.Ordinal);

        var error = Assert.Throws<InvalidDataException>(() => BundledExtensionManifestParser.Parse(json));

        Assert.Contains("kind", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(67_108_865L)]
    [InlineData(209_715_200L)]
    public void Parse_PackageAtFrozenSharedResourceLimit_IsAccepted(long size)
    {
        var json = ValidJson().Replace("\"size\":1024", $"\"size\":{size}", StringComparison.Ordinal);

        var document = BundledExtensionManifestParser.Parse(json);

        Assert.Equal(209_715_200L, ExtensionPackageLimits.MaximumPackageBytes);
        Assert.Equal(size, Assert.Single(document.Extensions).Release.Size);
    }

    [Fact]
    public void Parse_PackageAboveFrozenSharedResourceLimit_IsRejected()
    {
        const long oversized = 209_715_201L;
        var json = ValidJson().Replace("\"size\":1024", $"\"size\":{oversized}", StringComparison.Ordinal);

        var error = Assert.Throws<InvalidDataException>(() => BundledExtensionManifestParser.Parse(json));

        Assert.Contains("size", error.Message, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void Parse_NullRelease_IsReportedAsChineseContractError()
    {
        var item = ValidItem("log-analyzer", "a.zip");
        var releaseStart = item.IndexOf("\"release\":", StringComparison.Ordinal);
        var prefix = item[..releaseStart];
        var json = $$"""{"schemaVersion":2,"extensions":[{{prefix}}"release":null}]}""";

        var error = Assert.Throws<InvalidDataException>(() => BundledExtensionManifestParser.Parse(json));
        Assert.Contains("release", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_DuplicateIdOrAsset_IsRejected()
    {
        var first = ValidItem("log-analyzer", "a.zip");
        var second = ValidItem("log-analyzer", "b.zip");
        var duplicateId = $$"""{"schemaVersion":2,"extensions":[{{first}},{{second}}]}""";
        var duplicateAsset = $$"""{"schemaVersion":2,"extensions":[{{first}},{{ValidItem("other", "a.zip")}}]}""";

        Assert.Contains("重复", Assert.Throws<InvalidDataException>(() => BundledExtensionManifestParser.Parse(duplicateId)).Message);
        Assert.Contains("重复", Assert.Throws<InvalidDataException>(() => BundledExtensionManifestParser.Parse(duplicateAsset)).Message);
    }

    private static string ValidJson(string asset = "log-analyzer-v2.0.0.zip")
        => $$"""{"schemaVersion":2,"extensions":[{{ValidItem("log-analyzer", asset)}}]}""";

    private static string ValidItem(string id, string asset)
        => $$"""
        {
          "id":"{{id}}",
          "name":"日志分析",
          "description":"离线日志分析扩展",
          "publisherId":"thelinyue",
          "kind":"analysis",
          "asset":"{{asset.Replace("\\", "\\\\")}}",
          "release":{
            "version":"2.0.0",
            "minHostVersion":"2.0.0",
            "url":"https://example.test/{{asset.Replace("\\", "%5C")}}",
            "size":1024,
            "sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "signature":{"keyId":"official-2026","signature":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=="}
          }
        }
        """;
}
