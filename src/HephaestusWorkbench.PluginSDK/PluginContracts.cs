using System.Text.Json.Serialization;

namespace HephaestusWorkbench.PluginSDK;

public enum PluginType
{
    Exe,
    Dll,
    /// <summary>由工作台 WebView2 承载的本地静态工具页面。</summary>
    Web
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

/// <summary>
/// 插件生成的单个报告产物。File 始终是相对于本次输出目录的 HTML 入口，
/// 工作台通过稳定的 Id 区分同一分析批次中的多份报告，而不是依赖文件名猜测用途。
/// </summary>
public sealed record PluginReportArtifact(
    string Id,
    string Title,
    string Kind,
    string File,
    bool IsDefault);

/// <summary>
/// 插件执行结果。ReportPath 保留为报告输出目录以兼容旧插件；
/// Reports 仅在插件提供 reports.json 时赋值，用于承载同一分析批次的多个正式报告。
/// </summary>
public sealed record PluginExecutionResult(
    int ExitCode,
    string? ReportPath,
    string? ErrorMessage,
    bool Cancelled = false,
    IReadOnlyList<PluginReportArtifact>? Reports = null)
{
    /// <summary>
    /// 保留 v1 多报告协议之前的四参数构造签名，确保已编译插件无需重新编译即可加载。
    /// 新代码需要返回多份报告时使用包含 Reports 的五参数主构造函数。
    /// </summary>
    public PluginExecutionResult(int exitCode, string? reportPath, string? errorMessage, bool cancelled)
        : this(exitCode, reportPath, errorMessage, cancelled, null)
    {
    }

    /// <summary>保留旧版四元素解构签名；Reports 通过属性读取，避免破坏既有调用方。</summary>
    public void Deconstruct(out int exitCode, out string? reportPath, out string? errorMessage, out bool cancelled)
    {
        exitCode = ExitCode;
        reportPath = ReportPath;
        errorMessage = ErrorMessage;
        cancelled = Cancelled;
    }
}

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
