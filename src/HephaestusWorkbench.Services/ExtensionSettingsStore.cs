using System.Text.Json;
using System.Text.Json.Serialization;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 读写 Config/extensions.json 的 v2 宿主偏好。
/// 扩展包版本、入口和健康状态由 ExtensionRegistry/current.json 管理，本存储不会复制这些事实。
/// </summary>
public sealed class ExtensionSettingsStore
{
    private const string StableChannel = "stable";
    private const string DefaultAnalysisCapability = "analysis.engine";

    private readonly DataPaths _paths;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public ExtensionSettingsStore(DataPaths paths) => _paths = paths;

    public async Task<ExtensionSettingsDocument> EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.ExtensionsConfigFile))
        {
            var created = new ExtensionSettingsDocument();
            await WriteAsync(created, cancellationToken);
            return created;
        }

        return await ReadAndValidateAsync(cancellationToken);
    }

    public async Task SetEnabledAsync(
        string extensionId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var normalizedId = extensionId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedId))
            throw new ArgumentException("扩展 ID 不能为空。", nameof(extensionId));

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var settings = File.Exists(_paths.ExtensionsConfigFile)
                ? await ReadAndValidateAsync(cancellationToken)
                : new ExtensionSettingsDocument();

            var matching = settings.Extensions
                .Where(entry => string.Equals(entry.Id, normalizedId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            settings.Extensions.RemoveAll(entry =>
                string.Equals(entry.Id, normalizedId, StringComparison.OrdinalIgnoreCase));
            settings.Extensions.Add(new ExtensionEnablementEntry
            {
                Id = matching.FirstOrDefault()?.Id ?? normalizedId,
                Enabled = enabled
            });

            await WriteAsync(settings, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<ExtensionSettingsDocument> ReadAndValidateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(_paths.ExtensionsConfigFile);
            var settings = await JsonSerializer.DeserializeAsync<ExtensionSettingsDocument>(
                stream,
                _options,
                cancellationToken);
            if (settings is null)
                throw new JsonException("配置内容为空。");

            Validate(settings);
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"扩展配置不是有效的 v2 配置：{_paths.ExtensionsConfigFile}", ex);
        }
    }

    private async Task WriteAsync(
        ExtensionSettingsDocument settings,
        CancellationToken cancellationToken)
    {
        Validate(settings);
        Directory.CreateDirectory(_paths.ConfigDirectory);
        var temporary = _paths.ExtensionsConfigFile + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, settings, _options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (File.Exists(_paths.ExtensionsConfigFile))
                File.Replace(temporary, _paths.ExtensionsConfigFile, null);
            else
                File.Move(temporary, _paths.ExtensionsConfigFile);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void Validate(ExtensionSettingsDocument settings)
    {
        if (settings.SchemaVersion != 2)
            throw new InvalidDataException("扩展配置 schemaVersion 必须为 2。");
        if (!string.Equals(settings.UpdateChannel, StableChannel, StringComparison.Ordinal))
            throw new InvalidDataException("扩展配置 updateChannel 当前只允许 stable。");
        if (!string.Equals(settings.DefaultAnalysisCapability, DefaultAnalysisCapability, StringComparison.Ordinal))
            throw new InvalidDataException("扩展配置 defaultAnalysisCapability 必须为 analysis.engine。");
        if (settings.Extensions is null)
            throw new InvalidDataException("扩展配置 extensions 不能为空。");
        if (settings.Extensions.Any(entry => string.IsNullOrWhiteSpace(entry.Id)))
            throw new InvalidDataException("扩展配置包含空的扩展 ID。");
        if (settings.Extensions
            .GroupBy(entry => entry.Id.Trim(), StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
            throw new InvalidDataException("扩展配置包含重复的扩展 ID。");

        foreach (var entry in settings.Extensions)
            entry.Id = entry.Id.Trim();
    }
}
