using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

/// <summary>
/// 锁定 Release Trust Anchor M1 的宿主信任锚边界：解析器只接受固定 schema，
/// 且开发构建与正式构建对缺少嵌入资源采取不同的、可验证的失败策略。
/// </summary>
public sealed class ExtensionTrustStoreTests
{
    [Fact]
    public void CustomStore_ResolvesTrustByKeyIdAndPreservesPublisherScope()
    {
        var trustedKey = CreateTrustedKey();
        IExtensionTrustStore store = new ExtensionTrustStore([trustedKey]);

        Assert.True(store.TryGetTrustedKey("test-key", out var resolved));
        Assert.Equal("test-publisher", resolved.PublisherId);
        Assert.Equal([ExtensionKind.Analysis], resolved.Scope.AllowedKinds);
        Assert.Equal(["workspace.readText"], resolved.Scope.Permissions);
        Assert.False(store.TryGetTrustedKey("missing", out _));
    }

    [Fact]
    public void DefaultStore_HasNoTrustAnchorWhenFormalKeyIsUnavailable()
    {
        IExtensionTrustStore store = new ExtensionTrustStore();

        Assert.False(store.TryGetTrustedKey("official-2026", out _));
        Assert.False(store.TryGetTrustedKey("test-key", out _));
    }

    [Fact]
    public void TrustAnchorLoader_ExposesFrozenStaticApi()
    {
        var loader = GetRequiredLoaderType();
        var parse = GetRequiredStaticMethod(loader, "Parse", [typeof(string)]);
        var loadEmbedded = GetRequiredStaticMethod(loader, "LoadEmbedded", [typeof(Assembly), typeof(string), typeof(bool)]);

        Assert.Equal(typeof(ExtensionTrustStore), parse.ReturnType);
        Assert.Equal(typeof(ExtensionTrustStore), loadEmbedded.ReturnType);
    }

    [Fact]
    public void TrustAnchorLoader_ParseAcceptsOnlySchemaVersion2AndPreservesPublisherScope()
    {
        var store = ParseTrustAnchor(CreateValidTrustAnchor().ToJsonString());

        Assert.True(store.TryGetTrustedKey("test-key", out var trustedKey));
        Assert.Equal("test-publisher", trustedKey.PublisherId);
        Assert.Equal([ExtensionKind.Analysis], trustedKey.Scope.AllowedKinds);
        Assert.Equal(["workspace.readText"], trustedKey.Scope.Permissions);
    }

    [Fact]
    public void TrustAnchorLoader_ParseRejectsUnknownFieldsAtEveryFixedSchemaLevelInChinese()
    {
        var mutations = new Action<JsonObject>[]
        {
            anchor => anchor["unexpected"] = true,
            anchor => GetFirstTrustedPublisher(anchor)["unexpected"] = true,
            anchor => GetTrustedScope(anchor)["unexpected"] = true
        };

        foreach (var mutate in mutations)
        {
            var anchor = CreateValidTrustAnchor();
            mutate(anchor);
            AssertChineseParseFailure(anchor);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void TrustAnchorLoader_ParseRejectsSchemaVersionsOtherThan2InChinese(int schemaVersion)
    {
        var anchor = CreateValidTrustAnchor();
        anchor["schemaVersion"] = schemaVersion;

        AssertChineseParseFailure(anchor);
    }

    [Fact]
    public void TrustAnchorLoader_ParseRejectsEmptyTrustedPublishersInChinese()
    {
        var anchor = CreateValidTrustAnchor();
        anchor["trustedPublishers"] = new JsonArray();

        AssertChineseParseFailure(anchor);
    }

    [Fact]
    public void TrustAnchorLoader_ParseRejectsDuplicateKeyIdInChinese()
    {
        var anchor = CreateValidTrustAnchor();
        var publishers = Assert.IsType<JsonArray>(anchor["trustedPublishers"]);
        publishers.Add(GetFirstTrustedPublisher(anchor).DeepClone());

        AssertChineseParseFailure(anchor);
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("AA==")]
    public void TrustAnchorLoader_ParseRejectsInvalidOrNon32BytePublicKeyInChinese(string publicKey)
    {
        var anchor = CreateValidTrustAnchor();
        GetFirstTrustedPublisher(anchor)["publicKey"] = publicKey;

        AssertChineseParseFailure(anchor);
    }

    [Fact]
    public void TrustAnchorLoader_ParseRejectsEmptyAllowedKindsInChinese()
    {
        var anchor = CreateValidTrustAnchor();
        GetTrustedScope(anchor)["allowedKinds"] = new JsonArray();

        AssertChineseParseFailure(anchor);
    }

    [Fact]
    public void TrustAnchorLoader_LoadEmbeddedReturnsEmptyStoreWhenOptionalDevelopmentResourceIsMissing()
    {
        var store = LoadEmbedded(typeof(ExtensionTrustStoreTests).Assembly, "HephaestusWorkbench.Tests.MissingTrustAnchor.json", required: false);

        Assert.False(store.TryGetTrustedKey("test-key", out _));
    }

    [Fact]
    public void TrustAnchorLoader_LoadEmbeddedRejectsMissingRequiredFormalResourceInChinese()
    {
        // 先锁定 API 存在；否则缺少 API 的中文异常不能被误判为缺少正式资源的正确行为。
        _ = GetRequiredLoaderType();
        var exception = Record.Exception(() =>
            LoadEmbedded(typeof(ExtensionTrustStoreTests).Assembly, "HephaestusWorkbench.Tests.MissingTrustAnchor.json", required: true));

        AssertChineseFailure(exception);
    }

    [Fact]
    public void Store_CopiesInputAndDoesNotExposeMutableScopeArrays()
    {
        var allowedKinds = new[] { ExtensionKind.Analysis };
        var permissions = new[] { "workspace.readText" };
        var store = new ExtensionTrustStore([CreateTrustedKey(allowedKinds, permissions)]);
        allowedKinds[0] = ExtensionKind.Workspace;
        permissions[0] = "mutated.input";

        Assert.True(store.TryGetTrustedKey("test-key", out var first));
        TryMutate(first.Scope.AllowedKinds, ExtensionKind.Maintenance);
        TryMutate(first.Scope.Permissions, "mutated.output");

        Assert.True(store.TryGetTrustedKey("test-key", out var second));
        Assert.Equal([ExtensionKind.Analysis], second.Scope.AllowedKinds);
        Assert.Equal(["workspace.readText"], second.Scope.Permissions);
    }

    [Fact]
    public void TrustModels_AreJsonSerializable()
    {
        var trustedKey = CreateTrustedKey();

        var json = JsonSerializer.Serialize(trustedKey);
        var restored = JsonSerializer.Deserialize<TrustedPublisherKey>(json);

        Assert.NotNull(restored);
        Assert.Equal(trustedKey.KeyId, restored.KeyId);
        Assert.Equal(trustedKey.Scope.Permissions, restored.Scope.Permissions);
    }

    /// <summary>构造只含测试字节的 schema v2 信任锚，绝不包含仓库正式密钥或签名材料。</summary>
    private static JsonObject CreateValidTrustAnchor()
        => new()
        {
            ["schemaVersion"] = 2,
            ["trustedPublishers"] = new JsonArray
            {
                new JsonObject
                {
                    ["keyId"] = "test-key",
                    ["publisherId"] = "test-publisher",
                    ["publicKey"] = Convert.ToBase64String(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray()),
                    ["scope"] = new JsonObject
                    {
                        ["allowedKinds"] = new JsonArray("analysis"),
                        ["permissions"] = new JsonArray("workspace.readText")
                    }
                }
            }
        };

    private static JsonObject GetFirstTrustedPublisher(JsonObject anchor)
        => Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(anchor["trustedPublishers"])[0]);

    private static JsonObject GetTrustedScope(JsonObject anchor)
        => Assert.IsType<JsonObject>(GetFirstTrustedPublisher(anchor)["scope"]);

    private static ExtensionTrustStore ParseTrustAnchor(string json)
        => Assert.IsType<ExtensionTrustStore>(InvokeLoader("Parse", [typeof(string)], [json]));

    private static ExtensionTrustStore LoadEmbedded(Assembly assembly, string resourceName, bool required)
        => Assert.IsType<ExtensionTrustStore>(InvokeLoader(
            "LoadEmbedded",
            [typeof(Assembly), typeof(string), typeof(bool)],
            [assembly, resourceName, required]));

    private static object? InvokeLoader(string methodName, Type[] parameterTypes, object?[] arguments)
        => GetRequiredStaticMethod(GetRequiredLoaderType(), methodName, parameterTypes).Invoke(null, arguments);

    private static Type GetRequiredLoaderType()
        => typeof(ExtensionTrustStore).Assembly.GetType("HephaestusWorkbench.Services.ExtensionTrustAnchorLoader")
           ?? throw new InvalidOperationException("缺少 Release Trust Anchor M1 的 ExtensionTrustAnchorLoader 公共 API。");

    private static MethodInfo GetRequiredStaticMethod(Type loader, string methodName, Type[] parameterTypes)
        => loader.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static, parameterTypes)
           ?? throw new InvalidOperationException($"ExtensionTrustAnchorLoader 缺少公共静态方法：{methodName}。");

    private static void AssertChineseParseFailure(JsonObject anchor)
    {
        // 先确认解析 API 已存在，避免“缺少加载器”意外满足负例的中文错误断言。
        _ = GetRequiredLoaderType();
        AssertChineseFailure(Record.Exception(() => ParseTrustAnchor(anchor.ToJsonString())));
    }

    /// <summary>加载器可能通过反射调用抛出包装异常；这里统一检查实际失败信息必须可供中文用户理解。</summary>
    private static void AssertChineseFailure(Exception? exception)
    {
        Assert.NotNull(exception);
        var actual = exception is TargetInvocationException { InnerException: not null } invocation
            ? invocation.InnerException
            : exception;
        Assert.True(Regex.IsMatch(actual!.Message, "[一-龥]"), $"失败信息必须包含中文：{actual.Message}");
    }

    private static TrustedPublisherKey CreateTrustedKey(
        IReadOnlyList<ExtensionKind>? allowedKinds = null,
        IReadOnlyList<string>? permissions = null)
        => new()
        {
            KeyId = "test-key",
            PublisherId = "test-publisher",
            PublicKeyBase64 = Convert.ToBase64String(new byte[32]),
            Scope = new ExtensionTrustScope
            {
                AllowedKinds = allowedKinds ?? [ExtensionKind.Analysis],
                Permissions = permissions ?? ["workspace.readText"]
            }
        };

    private static void TryMutate<T>(IReadOnlyList<T> values, T replacement)
    {
        if (values is not IList<T> mutable || mutable.Count == 0) return;
        try
        {
            mutable[0] = replacement;
        }
        catch (NotSupportedException)
        {
        }
    }
}
