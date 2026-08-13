using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Services;

public sealed class MarketplaceCatalog
{
    public int SchemaVersion { get; init; }
    public List<MarketplacePlugin> Plugins { get; init; } = new();
    [JsonIgnore]
    public List<string> Issues { get; } = new();
}

public sealed record MarketplacePlugin
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    /// <summary>应用商店中展示的开发者名称，兼容目录中的 author 字段。</summary>
    public string Author { get; init; } = string.Empty;
    public string Category { get; init; } = "其他";
    public string? IconUrl { get; init; }
    public List<string> Screenshots { get; init; } = new();
    public string License { get; init; } = string.Empty;
    public string Repository { get; init; } = string.Empty;
    public MarketplaceManifestInfo? Manifest { get; init; }
    [JsonIgnore]
    public IReadOnlyList<string> Capabilities => Manifest?.Capabilities ?? (IReadOnlyList<string>)Array.Empty<string>();
    public required string Version { get; init; }
    public required PluginType Type { get; init; }
    public required string PackageUrl { get; init; }
    public required string Sha256 { get; init; }
    public long PackageSize { get; init; }
    public string MinimumAppVersion { get; init; } = "1.1.0";
    public string? ReleaseNotesUrl { get; init; }
}

/// <summary>目录条目中的插件清单摘要，用于搜索能力标签而不改变本地 manifest 契约。</summary>
public sealed class MarketplaceManifestInfo
{
    public List<string> Capabilities { get; init; } = new();
}

public sealed record MarketplaceCatalogResult(
    IReadOnlyList<MarketplacePlugin> Plugins,
    bool IsFromCache,
    string? Warning,
    IReadOnlyList<string>? Issues = null);

/// <summary>
/// 在线插件安装进度。下载阶段提供字节数，解压和校验阶段只提供阶段文字，避免展示没有依据的百分比。
/// </summary>
public sealed record PluginInstallProgress(string Stage, long BytesReceived, long? TotalBytes);

/// <summary>
/// 官方插件市场服务。网络数据在进入插件目录前必须通过目录字段、哈希、压缩包边界和本地清单四层校验。
/// 安装采用同盘暂存与目录切换，避免下载中断或解压失败破坏当前可用插件。
/// </summary>
public sealed partial class PluginMarketplaceService
{
    public const string CatalogUrl = "https://raw.githubusercontent.com/thelinyue/Hephaestus-Workbench-Plugins/main/catalog.json";
    // GitHub Release 下载会先经过重定向，弱网环境下 15 秒容易在包尚未下载完时误判失败。
    // 这里保留有限超时，避免网络异常导致插件中心永久等待，同时给正常的慢速下载留出余量。
    public static readonly TimeSpan DefaultHttpTimeout = TimeSpan.FromMinutes(2);
    public const long MaximumPackageBytes = 200L * 1024 * 1024;
    public const long MaximumExtractedBytes = 1024L * 1024 * 1024;

    private readonly DataPaths _paths;
    private readonly PluginCatalog _catalog;
    private readonly WorkbenchConfigurationService _configuration;
    private readonly TaskCenter _tasks;
    private readonly WorkbenchLogger _logger;
    private readonly IPluginInfoRepository? _pluginInfo;
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public PluginMarketplaceService(
        DataPaths paths,
        PluginCatalog catalog,
        WorkbenchConfigurationService configuration,
        TaskCenter tasks,
        WorkbenchLogger logger,
        IPluginInfoRepository? pluginInfo = null,
        HttpClient? httpClient = null)
    {
        _paths = paths;
        _catalog = catalog;
        _configuration = configuration;
        _tasks = tasks;
        _logger = logger;
        _pluginInfo = pluginInfo;
        _http = httpClient ?? new HttpClient { Timeout = DefaultHttpTimeout };
    }

    public async Task<MarketplaceCatalogResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var catalog = await DownloadCatalogAsync(cancellationToken);
            await WriteAtomicAsync(_paths.MarketplaceCatalogCacheFile, catalog, cancellationToken);
            return new MarketplaceCatalogResult(catalog.Plugins, false, null, catalog.Issues);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error("刷新在线插件目录失败，正在尝试使用本地缓存。", ex);
            var cached = await ReadCatalogAsync(_paths.MarketplaceCatalogCacheFile, cancellationToken);
            if (cached is null) throw new InvalidOperationException($"无法获取在线插件目录，且没有可用缓存：{ex.Message}", ex);
            return new MarketplaceCatalogResult(cached.Plugins, true, $"网络不可用，正在显示上次缓存：{ex.Message}", cached.Issues);
        }
    }

    public Task<PluginConfig> GetConfigurationAsync(CancellationToken cancellationToken = default)
        => _configuration.EnsurePluginConfigAsync(cancellationToken);

    public async Task InstallOrUpdateAsync(MarketplacePlugin item, CancellationToken cancellationToken = default, IProgress<PluginInstallProgress>? progress = null)
    {
        ValidateItem(item);
        if (_tasks.IsPluginActive(item.Id)) throw new InvalidOperationException("插件正在执行分析任务，暂时不能安装或更新。");

        var config = await _configuration.EnsurePluginConfigAsync(cancellationToken);
        var appSettings = await _configuration.EnsureAppSettingsAsync(cancellationToken: cancellationToken);
        var existingConfig = config.Plugins.FirstOrDefault(x => string.Equals(x.Id, item.Id, StringComparison.OrdinalIgnoreCase));
        var wasManual = existingConfig?.Source == PluginInstallSource.Manual;

        var packagePath = Path.Combine(_paths.TempDirectory, $"plugin-{Guid.NewGuid():N}.zip");
        var staging = Path.Combine(_paths.PluginsDirectory, $".install-{Guid.NewGuid():N}");
        var target = Path.Combine(_paths.PluginsDirectory, item.Id);
        var backup = Path.Combine(_paths.PluginsDirectory, $".backup-{item.Id}-{Guid.NewGuid():N}");
        var swapped = false;
        try
        {
            progress?.Report(new PluginInstallProgress("正在下载插件…", 0, item.PackageSize > 0 ? item.PackageSize : null));
            await DownloadPackageAsync(item, packagePath, appSettings.GitHubDownloadMirrorTemplate, cancellationToken, progress);
            progress?.Report(new PluginInstallProgress("正在解压并校验插件…", 0, null));
            ExtractAndValidate(packagePath, staging, item);

            if (Directory.Exists(target)) Directory.Move(target, backup);
            try
            {
                Directory.Move(staging, target);
                swapped = true;
            }
            catch
            {
                if (Directory.Exists(backup)) Directory.Move(backup, target);
                throw;
            }

            if (existingConfig is null)
            {
                existingConfig = new PluginConfigEntry
                {
                    Id = item.Id,
                    Version = item.Version,
                    Enabled = true,
                    Source = PluginInstallSource.Marketplace
                };
                config.Plugins.Add(existingConfig);
            }
            else
            {
                existingConfig.Version = item.Version;
                if (existingConfig.Source != PluginInstallSource.Bundled)
                    existingConfig.Source = PluginInstallSource.Marketplace;
            }
            config.DefaultPluginId ??= item.Id;
            await _configuration.SavePluginConfigAsync(config, cancellationToken);
            await SynchronizePluginInfoAsync(cancellationToken);
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            _logger.Info(wasManual
                ? $"手工安装插件已通过在线市场更新并接管来源：{item.Name} {item.Version}"
                : $"插件已安装或更新：{item.Name} {item.Version}");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (swapped)
            {
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                if (Directory.Exists(backup)) Directory.Move(backup, target);
            }
            _logger.Error($"安装或更新插件失败：{item.Name}", ex);
            throw;
        }
        finally
        {
            if (File.Exists(packagePath)) File.Delete(packagePath);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            if (Directory.Exists(backup) && !Directory.Exists(target)) Directory.Move(backup, target);
        }
    }

    public async Task UninstallAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        if (_tasks.IsPluginActive(pluginId)) throw new InvalidOperationException("插件正在执行分析任务，暂时不能卸载。");
        var config = await _configuration.EnsurePluginConfigAsync(cancellationToken);
        var entry = config.Plugins.FirstOrDefault(x => string.Equals(x.Id, pluginId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("插件没有登记，无法卸载。");
        if (entry.Source != PluginInstallSource.Marketplace)
            throw new InvalidOperationException(entry.Source == PluginInstallSource.Bundled ? "内置插件不能卸载。" : "手工安装的插件请在插件目录中管理。");
        if (string.Equals(config.DefaultPluginId, pluginId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("默认插件不能卸载，请先选择其他默认插件。");

        var directory = Path.Combine(_paths.PluginsDirectory, pluginId);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        config.Plugins.Remove(entry);
        await _configuration.SavePluginConfigAsync(config, cancellationToken);
        await SynchronizePluginInfoAsync(cancellationToken);
        _logger.Info($"插件已卸载：{pluginId}");
    }

    public async Task SetDefaultAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        var config = await _configuration.EnsurePluginConfigAsync(cancellationToken);
        var manifest = await _catalog.GetAsync(pluginId, cancellationToken);
        if (manifest?.Supports("standalone-tool") == true)
            throw new InvalidOperationException("独立工具插件不能设置为默认分析插件。");
        var entry = config.Plugins.FirstOrDefault(x => string.Equals(x.Id, pluginId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("插件没有登记，无法设为默认。");
        entry.Enabled = true;
        config.DefaultPluginId = entry.Id;
        await _configuration.SavePluginConfigAsync(config, cancellationToken);
    }

    public async Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken = default)
    {
        if (!enabled && _tasks.IsPluginActive(pluginId)) throw new InvalidOperationException("插件正在执行分析任务，暂时不能禁用。");
        var config = await _configuration.EnsurePluginConfigAsync(cancellationToken);
        var entry = config.Plugins.FirstOrDefault(x => string.Equals(x.Id, pluginId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("插件没有登记。");
        if (!enabled && string.Equals(config.DefaultPluginId, pluginId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("默认插件不能禁用，请先选择其他默认插件。");
        entry.Enabled = enabled;
        await _configuration.SavePluginConfigAsync(config, cancellationToken);
        await SynchronizePluginInfoAsync(cancellationToken);
    }

    public async Task SynchronizePluginInfoAsync(CancellationToken cancellationToken = default)
    {
        var installed = await _catalog.ScanAsync(cancellationToken);
        var config = await _configuration.EnsurePluginConfigAsync(cancellationToken);
        foreach (var plugin in installed)
        {
            var entry = config.Plugins.FirstOrDefault(x => string.Equals(x.Id, plugin.Id, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                entry = new PluginConfigEntry { Id = plugin.Id, Version = plugin.Version, Enabled = true };
                config.Plugins.Add(entry);
            }
            else entry.Version = plugin.Version;
            if (_pluginInfo is not null)
            {
                await _pluginInfo.UpsertAsync(new PluginInfo
                {
                    Id = plugin.Id,
                    Name = plugin.Name,
                    Version = plugin.Version,
                    Type = plugin.Type.ToString(),
                    Path = plugin.DirectoryPath,
                    Entry = plugin.Entry,
                    Enabled = entry.Enabled
                }, cancellationToken);
            }
        }
        var defaultManifest = installed.FirstOrDefault(x => string.Equals(x.Id, config.DefaultPluginId, StringComparison.OrdinalIgnoreCase));
        if (defaultManifest?.Supports("standalone-tool") == true || string.IsNullOrWhiteSpace(config.DefaultPluginId))
        {
            var analysisIds = installed
                .Where(x => !x.Supports("standalone-tool") && x.Type == PluginType.Exe)
                .Select(x => x.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            config.DefaultPluginId = config.Plugins.FirstOrDefault(x => x.Enabled && analysisIds.Contains(x.Id))?.Id;
        }
        await _configuration.SavePluginConfigAsync(config, cancellationToken);
    }

    private async Task<MarketplaceCatalog> DownloadCatalogAsync(CancellationToken cancellationToken)
    {
        // GitHub Raw 前面存在 CDN；刷新目录时必须主动绕过旧响应，否则商店可能长期显示已发布前的版本。
        var requestUri = new Uri($"{CatalogUrl}?refresh={Guid.NewGuid():N}");
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache, no-store");
        request.Headers.TryAddWithoutValidation("Pragma", "no-cache");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        EnsureHttps(response.RequestMessage?.RequestUri);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await ParseCatalogAsync(stream, cancellationToken);
    }

    private async Task<MarketplaceCatalog?> ReadCatalogAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await ParseCatalogAsync(stream, cancellationToken);
    }

    private async Task<MarketplaceCatalog> ParseCatalogAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || (!root.TryGetProperty("schemaVersion", out var schemaElement) && !root.TryGetProperty("SchemaVersion", out schemaElement))
            || !schemaElement.TryGetInt32(out var schemaVersion))
            throw new InvalidDataException("在线插件目录缺少有效的 schemaVersion。");
        if (schemaVersion != 1) throw new InvalidDataException($"不支持的插件目录版本：{schemaVersion}");
        if ((!root.TryGetProperty("plugins", out var pluginsElement) && !root.TryGetProperty("Plugins", out pluginsElement)) || pluginsElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("在线插件目录缺少 plugins 数组。");

        var catalog = new MarketplaceCatalog { SchemaVersion = schemaVersion };
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in pluginsElement.EnumerateArray())
        {
            try
            {
                var item = element.Deserialize<MarketplacePlugin>(_jsonOptions) ?? throw new InvalidDataException("条目为空。");
                ValidateItem(item);
                if (!ids.Add(item.Id)) throw new InvalidDataException($"目录中包含重复 ID：{item.Id}");
                catalog.Plugins.Add(item);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
            {
                var id = element.TryGetProperty("id", out var idElement) ? idElement.GetString() : "未知 ID";
                var issue = $"已跳过在线插件条目 {id}：{ex.Message}";
                catalog.Issues.Add(issue);
                _logger.Error(issue, ex);
            }
        }
        return catalog;
    }

    private async Task DownloadPackageAsync(MarketplacePlugin item, string destination, string? mirrorTemplate, CancellationToken cancellationToken, IProgress<PluginInstallProgress>? progress)
    {
        var original = new Uri(item.PackageUrl, UriKind.Absolute);
        var candidates = new List<(Uri Uri, string Label)> { (original, "官方地址") };
        Uri? mirror = null;
        try { mirror = GitHubDownloadMirrorTemplate.BuildUri(mirrorTemplate, original); }
        catch (ArgumentException ex)
        {
            _logger.Error("GitHub 下载加速配置无效，将继续使用官方地址。", ex);
        }
        if (mirror is not null && !string.Equals(mirror.AbsoluteUri, original.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            candidates.Add((mirror, "加速地址"));

        var errors = new List<(string Label, Exception Error)>();
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var temporary = destination + $".{Guid.NewGuid():N}.download";
            try
            {
                if (index > 0)
                {
                    progress?.Report(new PluginInstallProgress("官方地址下载失败，正在尝试 GitHub 加速地址…", 0, item.PackageSize > 0 ? item.PackageSize : null));
                    _logger.Info("插件官方地址下载失败，正在尝试配置的 GitHub 加速地址。" );
                }

                await DownloadCandidateAsync(item, candidate.Uri, temporary, candidate.Label, cancellationToken, progress);
                File.Move(temporary, destination, overwrite: true);
                if (index > 0) _logger.Info($"插件已通过 GitHub 加速地址下载并校验成功：{item.Name}");
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add((candidate.Label, ex));
                _logger.Error($"插件{candidate.Label}下载失败：{item.Name}", ex);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        if (errors.Count > 0 && errors.All(error => error.Error is InvalidDataException))
            throw errors[0].Error;
        throw new InvalidOperationException($"插件下载失败：{string.Join("；", errors.Select(error => $"{error.Label}：{error.Error.Message}"))}");
    }

    private async Task DownloadCandidateAsync(MarketplacePlugin item, Uri uri, string destination, string label, CancellationToken cancellationToken, IProgress<PluginInstallProgress>? progress)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        EnsureHttps(response.RequestMessage?.RequestUri);
        if (response.Content.Headers.ContentLength is > MaximumPackageBytes)
            throw new InvalidDataException("插件安装包超过 200 MB 限制。");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(destination);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > MaximumPackageBytes) throw new InvalidDataException("插件安装包超过 200 MB 限制。");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            progress?.Report(new PluginInstallProgress($"正在通过{label}下载插件…", total, item.PackageSize > 0 ? item.PackageSize : response.Content.Headers.ContentLength));
        }
        if (item.PackageSize > 0 && total != item.PackageSize)
            throw new InvalidDataException($"插件安装包大小不符，期望 {item.PackageSize} 字节，实际 {total} 字节。");
        var actualHash = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.Equals(actualHash, item.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("插件安装包 SHA-256 校验失败。");
    }

    private void ExtractAndValidate(string packagePath, string staging, MarketplacePlugin item)
    {
        Directory.CreateDirectory(staging);
        var root = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(packagePath);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            expanded += entry.Length;
            if (expanded > MaximumExtractedBytes) throw new InvalidDataException("插件解压内容超过 1 GB 限制。");
            var destination = Path.GetFullPath(Path.Combine(staging, entry.FullName));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("插件压缩包包含越界路径。");
            if (string.IsNullOrEmpty(entry.Name)) Directory.CreateDirectory(destination);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: false);
            }
        }

        var manifestPath = Path.Combine(staging, "manifest.json");
        if (!File.Exists(manifestPath)) throw new InvalidDataException("插件包根目录缺少 manifest.json。");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), _jsonOptions)
            ?? throw new InvalidDataException("插件清单无效。");
        var resolved = new PluginManifest
        {
            Id = manifest.Id,
            Name = manifest.Name,
            Version = manifest.Version,
            Type = manifest.Type,
            Entry = manifest.Entry,
            Runner = manifest.Runner,
            ReportPath = manifest.ReportPath,
            Capabilities = manifest.Capabilities.ToList(),
            DirectoryPath = staging
        };
        if (!string.Equals(resolved.Id, item.Id, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(resolved.Version, item.Version, StringComparison.OrdinalIgnoreCase)
            || resolved.Type != item.Type)
            throw new InvalidDataException("插件包清单与在线目录不一致。");
        if (!resolved.EntryPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(resolved.EntryPath))
            throw new InvalidDataException("插件入口不存在或指向插件目录之外。");
    }

    private static void ValidateItem(MarketplacePlugin item)
    {
        if (!PluginIdPattern().IsMatch(item.Id)) throw new InvalidDataException($"插件 ID 无效：{item.Id}");
        if (string.IsNullOrWhiteSpace(item.Name)) throw new InvalidDataException($"插件名称为空：{item.Id}");
        if (!Version.TryParse(item.Version, out _)) throw new InvalidDataException($"插件版本无效：{item.Version}");
        if (!Version.TryParse(item.MinimumAppVersion, out var minimum)) throw new InvalidDataException($"最低应用版本无效：{item.MinimumAppVersion}");
        if (item.Type is not (PluginType.Exe or PluginType.Web)) throw new InvalidDataException("当前版本仅支持 EXE 或 Web 插件。");
        if (!string.IsNullOrWhiteSpace(item.Category) && item.Category is not ("日志分析" or "规则工具" or "运维工具" or "其他"))
            throw new InvalidDataException($"插件分类无效：{item.Category}");
        EnsureHttps(new Uri(item.PackageUrl, UriKind.Absolute));
        if (!Sha256Pattern().IsMatch(item.Sha256)) throw new InvalidDataException($"插件 SHA-256 无效：{item.Id}");
        if (item.PackageSize <= 0 || item.PackageSize > MaximumPackageBytes) throw new InvalidDataException($"插件包大小无效：{item.Id}");
    }

    private async Task WriteAtomicAsync(string path, MarketplaceCatalog catalog, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, catalog, _jsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void EnsureHttps(Uri? uri)
    {
        if (uri is null || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("插件市场只允许 HTTPS 地址。");
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex PluginIdPattern();

    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
