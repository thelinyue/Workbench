using System.Text.Json;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

/// <summary>
/// 为分析服务测试建立真实的 manifest v2/current.json 目录，并复制独立进程 fixture。
/// 测试由此覆盖 Registry、版本租约和 analysis-process-v1 的完整边界，而不是继续模拟已废弃的 v1 Runner。
/// </summary>
internal static class AnalysisExtensionTestSupport
{
    public const string ExtensionId = "log-analyzer";
    public const string Version = "2.0.0";
    public const string PackageHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public static async Task<ExtensionRegistry> CreateRegistryAsync(
        DataPaths paths,
        params AnalysisExtensionDefinition[] definitions)
    {
        paths.EnsureCreated();
        foreach (var definition in definitions)
            Install(paths.ExtensionsDirectory, definition);

        var registry = new ExtensionRegistry(paths.ExtensionsDirectory, new PassingHealthChecker());
        await registry.LoadAsync();
        return registry;
    }

    public static AnalysisExtensionDefinition Process(
        string id = ExtensionId,
        string version = Version,
        IReadOnlyList<string>? capabilities = null)
        => new(id, version, ExtensionKind.Analysis, ExtensionRuntimeKind.Process,
            capabilities ?? ["analysis.engine", "analysis.scope.comprehensive"]);

    public static AnalysisExtensionDefinition Workspace(string id = "workspace-tool")
        => new(id, Version, ExtensionKind.Workspace, ExtensionRuntimeKind.Web, ["workspace.page"]);

    public static void WriteCurrent(DataPaths paths, string id, string version, string packageHash = PackageHash)
    {
        var current = new ExtensionCurrentDocument
        {
            SchemaVersion = 2,
            Id = id,
            Version = version,
            PackageSha256 = packageHash,
            State = ExtensionActivationState.Healthy
        };
        File.WriteAllText(
            Path.Combine(paths.ExtensionsDirectory, id, "current.json"),
            JsonSerializer.Serialize(current));
    }

    private static void Install(string extensionsRoot, AnalysisExtensionDefinition definition)
    {
        var versionDirectory = Path.Combine(extensionsRoot, definition.Id, definition.Version);
        Directory.CreateDirectory(versionDirectory);

        string entry;
        string? protocol;
        if (definition.RuntimeKind == ExtensionRuntimeKind.Process)
        {
            entry = CopyFixture(versionDirectory);
            protocol = AnalysisProcessProtocol.Version;
        }
        else
        {
            entry = "index.html";
            protocol = "workspace-bridge-v1";
            File.WriteAllText(Path.Combine(versionDirectory, entry), "<html></html>");
        }

        var manifest = new
        {
            schemaVersion = 2,
            id = definition.Id,
            name = $"测试扩展 {definition.Id}",
            version = definition.Version,
            kind = ToJson(definition.Kind),
            publisherId = "thelinyue",
            hostApiVersion = "1.0",
            minHostVersion = "2.0.0",
            runtime = new { kind = ToJson(definition.RuntimeKind), protocol, entry },
            capabilities = definition.Capabilities,
            permissions = Array.Empty<string>(),
            dependencies = Array.Empty<object>()
        };
        File.WriteAllText(Path.Combine(versionDirectory, "manifest.json"), JsonSerializer.Serialize(manifest));

        var current = new ExtensionCurrentDocument
        {
            SchemaVersion = 2,
            Id = definition.Id,
            Version = definition.Version,
            PackageSha256 = PackageHash,
            State = ExtensionActivationState.Healthy
        };
        File.WriteAllText(
            Path.Combine(extensionsRoot, definition.Id, "current.json"),
            JsonSerializer.Serialize(current));
    }

    private static string CopyFixture(string versionDirectory)
    {
        const string fixtureName = "HephaestusWorkbench.AnalysisProcessFixture";
        var executableName = fixtureName + ".exe";
        var fixtureFiles = Directory.EnumerateFiles(AppContext.BaseDirectory, fixtureName + ".*").ToArray();
        if (fixtureFiles.Length == 0)
            throw new FileNotFoundException($"未找到分析进程测试程序：{Path.Combine(AppContext.BaseDirectory, executableName)}");

        foreach (var source in fixtureFiles)
            File.Copy(source, Path.Combine(versionDirectory, Path.GetFileName(source)), overwrite: true);

        var sdkAssembly = Path.Combine(AppContext.BaseDirectory, "HephaestusWorkbench.PluginSDK.dll");
        if (File.Exists(sdkAssembly))
            File.Copy(sdkAssembly, Path.Combine(versionDirectory, Path.GetFileName(sdkAssembly)), overwrite: true);
        return executableName;
    }

    private static string ToJson(ExtensionKind kind) => kind switch
    {
        ExtensionKind.Analysis => "analysis",
        ExtensionKind.Workspace => "workspace",
        ExtensionKind.Maintenance => "maintenance",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string ToJson(ExtensionRuntimeKind kind) => kind switch
    {
        ExtensionRuntimeKind.Process => "process",
        ExtensionRuntimeKind.Web => "web",
        ExtensionRuntimeKind.Content => "content",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private sealed class PassingHealthChecker : IExtensionHealthChecker
    {
        public Task CheckAsync(ExtensionManifest manifest, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}

internal sealed record AnalysisExtensionDefinition(
    string Id,
    string Version,
    ExtensionKind Kind,
    ExtensionRuntimeKind RuntimeKind,
    IReadOnlyList<string> Capabilities);
