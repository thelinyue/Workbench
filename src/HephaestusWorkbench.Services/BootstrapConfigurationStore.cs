using System.Text.Json;
using System.Text.Json.Serialization;

namespace HephaestusWorkbench.Services;

public enum BootstrapReadStatus
{
    Missing,
    Ready,
    Legacy
}

public sealed record BootstrapReadResult(BootstrapReadStatus Status, string? DataRoot);

/// <summary>
/// 读写位于 LocalAppData 的 v2 启动指针。旧 bootstrap 只用于定位并阻断旧工作区，
/// 不会被静默补写版本号或迁移为新格式。
/// </summary>
public sealed class BootstrapConfigurationStore
{
    private readonly string _file;
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public BootstrapConfigurationStore(string file) => _file = Path.GetFullPath(file);

    public async Task<BootstrapReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_file)) return new(BootstrapReadStatus.Missing, null);

        try
        {
            await using var stream = File.OpenRead(_file);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var dataRoot = ReadString(root, "dataRoot") ?? ReadString(root, "DataRoot");
            var normalized = string.IsNullOrWhiteSpace(dataRoot) ? null : Path.GetFullPath(dataRoot);
            var schemaVersion = TryReadInt32(root, "schemaVersion") ?? TryReadInt32(root, "SchemaVersion");
            return schemaVersion == 2 && normalized is not null
                ? new(BootstrapReadStatus.Ready, normalized)
                : new(BootstrapReadStatus.Legacy, normalized);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new InvalidDataException($"启动配置无法读取：{_file}", ex);
        }
    }

    public async Task WriteAsync(string dataRoot, CancellationToken cancellationToken = default)
    {
        var normalized = Path.GetFullPath(dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temporary = _file + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, new BootstrapConfig(normalized), _options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            if (File.Exists(_file)) File.Replace(temporary, _file, null);
            else File.Move(temporary, _file);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? TryReadInt32(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;

    private sealed record BootstrapConfig([property: JsonPropertyName("dataRoot")] string DataRoot)
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = 2;
    }
}
