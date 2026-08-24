using System.Text.Json;
using HephaestusWorkbench.Data;

namespace HephaestusWorkbench.Services;

public enum WorkspaceVersionStatus
{
    Empty,
    Ready,
    Legacy
}

public sealed record WorkspaceVersionResult(WorkspaceVersionStatus Status, string DataRoot);

/// <summary>
/// 在任何初始化写入发生前检查工作区版本。只有空目录或三个配置文件均明确声明
/// schemaVersion 2 的工作区可以继续；其余非空目录统一视为旧工作区并阻断。
/// </summary>
public sealed class WorkspaceVersionGate
{
    public async Task<WorkspaceVersionResult> InspectAsync(string dataRoot, CancellationToken cancellationToken = default)
    {
        var normalized = Path.GetFullPath(dataRoot);
        if (Directory.Exists(normalized) is false || Directory.EnumerateFileSystemEntries(normalized).Any() is false)
            return new(WorkspaceVersionStatus.Empty, normalized);

        if (Directory.Exists(Path.Combine(normalized, "Plugins"))
            || File.Exists(Path.Combine(normalized, "Config", "plugins.json")))
            return new(WorkspaceVersionStatus.Legacy, normalized);

        var paths = new DataPaths(normalized);
        var requiredConfigs = new[] { paths.WorkspaceConfigFile, paths.AppSettingsFile, paths.ExtensionsConfigFile };
        if (File.Exists(paths.DatabaseFile) is false || requiredConfigs.Any(path => File.Exists(path) is false))
            return new(WorkspaceVersionStatus.Legacy, normalized);

        foreach (var file in requiredConfigs)
        {
            if (await HasV2SchemaAsync(file, cancellationToken) is false)
                return new(WorkspaceVersionStatus.Legacy, normalized);
        }
        return new(WorkspaceVersionStatus.Ready, normalized);
    }

    private static async Task<bool> HasV2SchemaAsync(string file, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(file);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.TryGetProperty("schemaVersion", out var version)
                   && version.TryGetInt32(out var value)
                   && value == 2;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
