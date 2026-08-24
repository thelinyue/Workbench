using System.Diagnostics;
using System.Text.Json.Nodes;
using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Tests;

public sealed class ExtensionContractTests
{
    [Fact]
    public void ParseManifest_AcceptsV2AnalysisProcessManifest()
    {
        var manifest = ExtensionManifestParser.Parse(BuildManifest(
            kind: "analysis",
            runtime: """{ "kind": "process", "protocol": "analysis-process-v1", "entry": "bin/log-analyzer.exe" }""",
            capabilities: """["analysis.engine", "analysis.scope.comprehensive"]"""), VersionDirectory);

        Assert.Equal(2, manifest.SchemaVersion);
        Assert.Equal(ExtensionKind.Analysis, manifest.Kind);
        Assert.Equal(ExtensionRuntimeKind.Process, manifest.Runtime.Kind);
        Assert.Equal("analysis-process-v1", manifest.Runtime.Protocol);
        Assert.True(manifest.SupportsCapability("analysis.engine"));
        Assert.Equal(
            Path.GetFullPath(@"C:\WorkbenchData\Extensions\log-analyzer\2.0.0\bin\log-analyzer.exe"),
            manifest.EntryPath);
    }

    [Fact]
    public void ParseManifest_RejectsSchemaOtherThanV2()
    {
        var json = BuildManifest(
            kind: "analysis",
            runtime: """{ "kind": "process", "protocol": "analysis-process-v1", "entry": "bin/log-analyzer.exe" }""",
            capabilities: """["analysis.engine"]""").Replace("\"schemaVersion\": 2", "\"schemaVersion\": 1", StringComparison.Ordinal);

        var error = Assert.Throws<ExtensionContractException>(() => ExtensionManifestParser.Parse(json, VersionDirectory));

        Assert.Contains("schemaVersion 必须为 2", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("workspace", "content", "workspace.page")]
    [InlineData("analysis", "web", "analysis.engine")]
    [InlineData("maintenance", "process", "maintenance.workflow-pack")]
    public void ParseManifest_RejectsUnsupportedKindRuntimeCombination(string kind, string runtimeKind, string capability)
    {
        var runtime = runtimeKind == "content"
            ? """{ "kind": "content" }"""
            : $$"""{ "kind": "{{runtimeKind}}", "entry": "index.html" }""";

        var error = Assert.Throws<ExtensionContractException>(() => ExtensionManifestParser.Parse(
            BuildManifest(kind, runtime, $$"""["{{capability}}"]"""), VersionDirectory));

        Assert.Contains("kind/runtime 组合不受支持", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("workspace", """{ "kind": "web", "entry": "index.html" }""", "analysis.engine")]
    [InlineData("analysis", """{ "kind": "process", "protocol": "analysis-process-v1", "entry": "analyzer.exe" }""", "workspace.page")]
    [InlineData("maintenance", """{ "kind": "content" }""", "analysis.rule-pack")]
    public void ParseManifest_RejectsCapabilityOutsideKindAllowList(string kind, string runtime, string capability)
    {
        var error = Assert.Throws<ExtensionContractException>(() => ExtensionManifestParser.Parse(
            BuildManifest(kind, runtime, $$"""["{{capability}}"]"""), VersionDirectory));

        Assert.Contains("不允许声明能力", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("legacy-process-v1")]
    [InlineData("")]
    public void ParseManifest_RejectsAnalysisProcessWithoutRequiredProtocol(string protocol)
    {
        var runtime = $$"""{ "kind": "process", "protocol": "{{protocol}}", "entry": "analyzer.exe" }""";

        var error = Assert.Throws<ExtensionContractException>(() => ExtensionManifestParser.Parse(
            BuildManifest("analysis", runtime, """["analysis.engine"]"""), VersionDirectory));

        Assert.Contains("analysis-process-v1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseManifest_RejectsExecutableEntryForContentExtension()
    {
        var error = Assert.Throws<ExtensionContractException>(() => ExtensionManifestParser.Parse(
            BuildManifest("analysis", """{ "kind": "content", "entry": "rules.json" }""", """["analysis.rule-pack"]"""),
            VersionDirectory));

        Assert.Contains("content 运行时不能声明 entry", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../outside.exe")]
    [InlineData("C:/Windows/System32/cmd.exe")]
    public void ParseManifest_RejectsEntryOutsideVersionDirectory(string entry)
    {
        var error = Assert.Throws<ExtensionContractException>(() => ExtensionManifestParser.Parse(
            BuildManifest(
                "analysis",
                $$"""{ "kind": "process", "protocol": "analysis-process-v1", "entry": "{{entry}}" }""",
                """["analysis.engine"]"""),
            VersionDirectory));

        Assert.Contains("入口必须位于扩展版本目录内", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("navigation")]
    [InlineData("order")]
    [InlineData("group")]
    [InlineData("pinned")]
    [InlineData("reportPath")]
    [InlineData("runner")]
    [InlineData("type")]
    public void ParseManifest_RejectsFieldsOutsideV2Contract(string field)
    {
        var json = BuildManifest(
            "analysis",
            """{ "kind": "process", "protocol": "analysis-process-v1", "entry": "analyzer.exe" }""",
            """["analysis.engine"]""");
        json = json.Replace("\"dependencies\": []", $"\"dependencies\": [], \"{field}\": \"not-allowed\"", StringComparison.Ordinal);

        var error = Assert.Throws<ExtensionContractException>(() => ExtensionManifestParser.Parse(json, VersionDirectory));

        Assert.Contains("不符合 v2 结构", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("analysis", """{ "kind": "content" }""", "analysis.engine")]
    [InlineData("analysis", """{ "kind": "process", "protocol": "analysis-process-v1", "entry": "analyzer.exe" }""", "analysis.rule-pack")]
    public void ParseManifest_RejectsCapabilityOutsideRuntimeAllowList(string kind, string runtime, string capability)
    {
        var error = Assert.Throws<ExtensionContractException>(() => ExtensionManifestParser.Parse(
            BuildManifest(kind, runtime, $$"""["{{capability}}"]"""), VersionDirectory));

        Assert.Contains("运行时不允许声明能力", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "thelinyue", "2.0.0", "1.0", "2.0.0")]
    [InlineData("Log Analyzer", "thelinyue", "2.0.0", "1.0", "2.0.0")]
    [InlineData("log-analyzer", "", "2.0.0", "1.0", "2.0.0")]
    [InlineData("log-analyzer", "thelinyue", "2.0", "1.0", "2.0.0")]
    [InlineData("log-analyzer", "thelinyue", "2.0.0", "2.0", "2.0.0")]
    [InlineData("log-analyzer", "thelinyue", "2.0.0", "1.0", "v2")]
    public void ParseManifest_RejectsInvalidIdentityOrVersionFields(
        string id,
        string publisherId,
        string version,
        string hostApiVersion,
        string minHostVersion)
    {
        var json = BuildManifest(
            "analysis",
            """{ "kind": "process", "protocol": "analysis-process-v1", "entry": "analyzer.exe" }""",
            """["analysis.engine"]""");
        json = json.Replace("\"id\": \"log-analyzer\"", $"\"id\": \"{id}\"", StringComparison.Ordinal)
            .Replace("\"publisherId\": \"thelinyue\"", $"\"publisherId\": \"{publisherId}\"", StringComparison.Ordinal)
            .Replace("\"version\": \"2.0.0\"", $"\"version\": \"{version}\"", StringComparison.Ordinal)
            .Replace("\"hostApiVersion\": \"1.0\"", $"\"hostApiVersion\": \"{hostApiVersion}\"", StringComparison.Ordinal)
            .Replace("\"minHostVersion\": \"2.0.0\"", $"\"minHostVersion\": \"{minHostVersion}\"", StringComparison.Ordinal);

        Assert.Throws<ExtensionContractException>(() => ExtensionManifestParser.Parse(json, VersionDirectory));
    }

    [Fact]
    public void ParseManifest_RejectsPermissionsForNonWorkspaceExtension()
    {
        var json = BuildManifest(
            "analysis",
            """{ "kind": "process", "protocol": "analysis-process-v1", "entry": "analyzer.exe" }""",
            """["analysis.engine"]""")
            .Replace("\"permissions\": []", "\"permissions\": [\"workspace.readText\"]", StringComparison.Ordinal);

        var error = Assert.Throws<ExtensionContractException>(() => ExtensionManifestParser.Parse(json, VersionDirectory));

        Assert.Contains("只有 workspace 扩展可以声明 permissions", error.Message, StringComparison.Ordinal);
    }
    [Fact]
    public void Manifest_DefaultSerializationUsesExactCamelCaseEnumValues()
    {
        var manifest = ExtensionManifestParser.Parse(
            BuildManifest(
                "analysis",
                """{ "kind": "process", "protocol": "analysis-process-v1", "entry": "analyzer.exe" }""",
                """["analysis.engine"]"""),
            VersionDirectory);

        var json = System.Text.Json.JsonSerializer.Serialize(manifest);

        Assert.Contains("\"kind\":\"analysis\"", json, StringComparison.Ordinal);
        Assert.Contains("\"runtime\":{\"kind\":\"process\"", json, StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("runtime", "null")]
    [InlineData("capabilities", "null")]
    [InlineData("permissions", "null")]
    [InlineData("dependencies", "null")]
    public void ParseManifest_RejectsExplicitNullRequiredContractMembers(string property, string replacement)
    {
        var json = BuildManifest(
            "analysis",
            """{ "kind": "process", "protocol": "analysis-process-v1", "entry": "analyzer.exe" }""",
            """["analysis.engine"]""");
        json = property switch
        {
            "runtime" => json.Replace("\"runtime\": { \"kind\": \"process\", \"protocol\": \"analysis-process-v1\", \"entry\": \"analyzer.exe\" }", $"\"runtime\": {replacement}", StringComparison.Ordinal),
            "capabilities" => json.Replace("\"capabilities\": [\"analysis.engine\"]", $"\"capabilities\": {replacement}", StringComparison.Ordinal),
            "permissions" => json.Replace("\"permissions\": []", $"\"permissions\": {replacement}", StringComparison.Ordinal),
            _ => json.Replace("\"dependencies\": []", $"\"dependencies\": {replacement}", StringComparison.Ordinal)
        };

        Assert.Throws<ExtensionContractException>(() => ExtensionManifestParser.Parse(json, VersionDirectory));
    }
    [Theory]
    [InlineData("permissions")]
    [InlineData("dependencies")]
    public void ParseManifest_RejectsMissingRequiredArrayMember(string property)
    {
        var document = JsonNode.Parse(BuildManifest(
            "analysis",
            """{ "kind": "process", "protocol": "analysis-process-v1", "entry": "analyzer.exe" }""",
            """["analysis.engine"]"""))!.AsObject();
        Assert.True(document.Remove(property));

        Assert.Throws<ExtensionContractException>(() => ExtensionManifestParser.Parse(document.ToJsonString(), VersionDirectory));
    }
    [Fact]
    public void ParseManifest_RejectsEntryThroughDirectoryLinkOutsideVersionDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var versionDirectory = Path.Combine(root, "version");
        var outsideDirectory = Path.Combine(root, "outside");
        var linkDirectory = Path.Combine(versionDirectory, "linked");
        try
        {
            Directory.CreateDirectory(versionDirectory);
            Directory.CreateDirectory(outsideDirectory);
            File.WriteAllText(Path.Combine(outsideDirectory, "analyzer.exe"), "test");
            using (var junction = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c mklink /J \"{linkDirectory}\" \"{outsideDirectory}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }) ?? throw new InvalidOperationException("无法启动 junction 测试进程。"))
            {
                junction.WaitForExit();
                Assert.True(junction.ExitCode == 0, junction.StandardError.ReadToEnd() + junction.StandardOutput.ReadToEnd());
            }

            var json = BuildManifest(
                "analysis",
                """{ "kind": "process", "protocol": "analysis-process-v1", "entry": "linked/analyzer.exe" }""",
                """["analysis.engine"]""");

            var error = Assert.Throws<ExtensionContractException>(() => ExtensionManifestParser.Parse(json, versionDirectory));

            Assert.Contains("重解析点", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(linkDirectory)) Directory.Delete(linkDirectory);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
    [Fact]
    public void ParseManifest_RejectsDllRuntimeKind()
    {
        var error = Assert.Throws<ExtensionContractException>(() => ExtensionManifestParser.Parse(
            BuildManifest("analysis", """{ "kind": "dll", "entry": "plugin.dll" }""", """["analysis.engine"]"""),
            VersionDirectory));

        Assert.Contains("不符合 v2 结构", error.Message, StringComparison.Ordinal);
    }
    [Fact]
    public void ParseManifest_RejectsNullName()
    {
        var json = BuildManifest(
            "analysis",
            """{ "kind": "process", "protocol": "analysis-process-v1", "entry": "analyzer.exe" }""",
            """["analysis.engine"]""")
            .Replace("\"name\": \"日志分析\"", "\"name\": null", StringComparison.Ordinal);

        Assert.Throws<ExtensionContractException>(() => ExtensionManifestParser.Parse(json, VersionDirectory));
    }
    private const string VersionDirectory = @"C:\WorkbenchData\Extensions\log-analyzer\2.0.0";

    private static string BuildManifest(string kind, string runtime, string capabilities) => $$"""
        {
          "schemaVersion": 2,
          "id": "log-analyzer",
          "name": "日志分析",
          "version": "2.0.0",
          "kind": "{{kind}}",
          "publisherId": "thelinyue",
          "hostApiVersion": "1.0",
          "minHostVersion": "2.0.0",
          "runtime": {{runtime}},
          "capabilities": {{capabilities}},
          "permissions": [],
          "dependencies": []
        }
        """;
}
