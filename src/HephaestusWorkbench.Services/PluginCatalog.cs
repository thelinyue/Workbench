using System.Text.Json;
using System.Text.Json.Serialization;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 本地插件目录，负责发现并校验用户数据目录中的 manifest.json。
/// 插件中心和分析服务共用此目录，避免 UI 自己拼接插件路径或重复实现扫描逻辑。
/// </summary>
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

    public string PluginsDirectory => _paths.PluginsDirectory;
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
                    Capabilities = manifest.Capabilities.ToList(),
                    DirectoryPath = Path.GetDirectoryName(manifestPath)!
                };
                if (!IsWithinPluginDirectory(withPath.EntryPath, withPath.DirectoryPath))
                {
                    AddIssue($"插件入口不能指向插件目录之外：{withPath.EntryPath}");
                    continue;
                }
                if (!File.Exists(withPath.EntryPath))
                {
                    AddIssue($"插件入口不存在：{withPath.EntryPath}");
                    continue;
                }
                if (withPath.Type == PluginType.Web
                    && !withPath.EntryPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                    && !withPath.EntryPath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                {
                    AddIssue($"Web 插件入口必须是 HTML 文件：{withPath.EntryPath}");
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

    private static bool IsWithinPluginDirectory(string entryPath, string pluginDirectory)
    {
        var directory = Path.GetFullPath(pluginDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullEntryPath = Path.GetFullPath(entryPath);
        return fullEntryPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase);
    }
}
