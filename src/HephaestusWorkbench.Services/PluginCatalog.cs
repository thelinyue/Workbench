using System.Text.Json;
using System.Text.Json.Serialization;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Services;

public sealed class PluginCatalog : IPluginCatalog
{
    private readonly DataPaths _paths;
    private readonly WorkbenchLogger _logger;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly Dictionary<string, PluginManifest> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _issues = new();

    public PluginCatalog(DataPaths paths, WorkbenchLogger logger)
    {
        _paths = paths;
        _logger = logger;
        _jsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public IReadOnlyList<string> Issues => _issues;

    public async Task<IReadOnlyList<PluginManifest>> ScanAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_paths.PluginsDirectory);
        _issues.Clear();
        _cache.Clear();
        var found = new List<PluginManifest>();
        foreach (var manifestPath in Directory.EnumerateFiles(_paths.PluginsDirectory, "manifest.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(manifestPath);
                var manifest = await JsonSerializer.DeserializeAsync<PluginManifest>(stream, _jsonOptions, cancellationToken);
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
                {
                    AddIssue($"插件清单无效：{manifestPath}");
                    continue;
                }

                var withPath = new PluginManifest
                {
                    Id = manifest.Id,
                    Name = manifest.Name,
                    Version = manifest.Version,
                    Type = manifest.Type,
                    Entry = manifest.Entry,
                    Runner = manifest.Runner,
                    ReportPath = manifest.ReportPath,
                    DirectoryPath = Path.GetDirectoryName(manifestPath)!
                };
                if (!File.Exists(withPath.EntryPath))
                {
                    AddIssue($"插件入口不存在：{withPath.EntryPath}");
                    continue;
                }

                _cache[withPath.Id] = withPath;
                found.Add(withPath);
            }
            catch (Exception ex)
            {
                AddIssue($"读取插件清单失败：{manifestPath}：{ex.Message}");
            }
        }
        return found;
    }

    private void AddIssue(string message)
    {
        _issues.Add(message);
        _logger.Error(message);
    }

    public async Task<PluginManifest?> GetAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(pluginId, out var cached)) return cached;
        await ScanAsync(cancellationToken);
        return _cache.GetValueOrDefault(pluginId);
    }
}
