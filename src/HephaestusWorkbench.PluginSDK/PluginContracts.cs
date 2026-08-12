using System.Text.Json.Serialization;

namespace HephaestusWorkbench.PluginSDK;

public enum PluginType
{
    Exe,
    Dll
}

/// <summary>
/// 插件清单。runner/reportPath 是现有 log_analyzer.exe 的兼容扩展字段，标准插件可不填写。
/// </summary>
public sealed class PluginManifest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required PluginType Type { get; init; }
    public required string Entry { get; init; }
    public string? Runner { get; init; }
    public string? ReportPath { get; init; }
    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; init; } = new();

    public bool Supports(string capability) => Capabilities.Any(x => string.Equals(x, capability, StringComparison.OrdinalIgnoreCase));

    [JsonIgnore]
    public string DirectoryPath { get; init; } = string.Empty;

    [JsonIgnore]
    public string EntryPath => Path.GetFullPath(Path.Combine(DirectoryPath, Entry));
}

public sealed record PluginExecutionContext(
    string CaseId,
    string SourcePath,
    string OutputPath,
    string ExtractPath,
    string WorkingDirectory,
    string? RulesPath = null);

public sealed record PluginExecutionResult(
    int ExitCode,
    string? ReportPath,
    string? ErrorMessage,
    bool Cancelled = false);

/// <summary>供未来 DLL 插件实现的统一入口。</summary>
public interface IAnalysisPlugin
{
    Task<PluginExecutionResult> ExecuteAsync(PluginExecutionContext context, CancellationToken cancellationToken = default);
}

public interface IPluginCatalog
{
    Task<IReadOnlyList<PluginManifest>> ScanAsync(CancellationToken cancellationToken = default);
    Task<PluginManifest?> GetAsync(string pluginId, CancellationToken cancellationToken = default);
}

public interface IPluginRunner
{
    Task<PluginExecutionResult> RunAsync(PluginManifest manifest, PluginExecutionContext context, CancellationToken cancellationToken = default);
}
