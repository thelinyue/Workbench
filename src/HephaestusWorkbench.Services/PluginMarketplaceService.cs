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
}

public sealed record MarketplacePlugin
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public required string Version { get; init; }
    public required PluginType Type { get; init; }
    public required string PackageUrl { get; init; }
    public required string Sha256 { get; init; }
    public long PackageSize { get; init; }
    public string MinimumAppVersion { get; init; } = "1.1.0";
    public string? ReleaseNotesUrl { get; init; }
}

public sealed record MarketplaceCatalogResult(
    IReadOnlyList<MarketplacePlugin> Plugins,
    bool IsFromCache,
    string? Warning);

/// <summary>
/// 官方插件市场服务。网络数据在进入插件目录前必须通过目录字段、哈希、压缩包边界和本地清单四层校验。
/// 安装采用同盘暂存与目录切换，避免下载中断或解压失败破坏当前可用插件。
/// </summary>
public sealed partial class PluginMarketplaceService
{
    public const string CatalogUrl = "https://raw.githubusercontent.com/thelinyue/Hephaestus-Workbench-Plugins/main/catalog.json";
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
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<MarketplaceCatalogResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var catalog = await DownloadCatalogAsync(cancellationToken);
            await WriteAtomicAsync(_paths.MarketplaceCatalogCacheFile, catalog, cancellationToken);
            return new MarketplaceCatalogResult(catalog.Plugins, false, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error("刷新在线插件目录失败，正在尝试使用本地缓存。", ex);
            var cached = await ReadCatalogAsync(_paths.MarketplaceCatalogCacheFile, cancellationToken);
            if (cached is null) throw new InvalidOperationException($"无法获取在线插件目录，且没有可用缓存：{ex.Message}", ex);
            return new MarketplaceCatalogResult(cached.Plugins, true, $"网络不可用，正在显示上次缓存：{ex.Message}");
        }
    }

    public Task<PluginConfig> GetConfigurationAsync(CancellationToken cancellationToken = default)
        => _configuration.EnsurePluginConfigAsync(cancellationToken);

    public async Task InstallOrUpdateAsync(MarketplacePlugin item, CancellationToken cancellationToken = default)
    {
        ValidateItem(item);
        if (_tasks.IsPluginActive(item.Id)) throw new InvalidOperationException("插件正在执行分析任务，暂时不能安装或更新。");

        var config = await _configuration.EnsurePluginConfigAsync(cancellationToken);
        var existingConfig = config.Plugins.FirstOrDefault(x => string.Equals(x.Id, item.Id, StringComparison.OrdinalIgnoreCase));
        if (existingConfig?.Source == PluginInstallSource.Manual)
            throw new InvalidOperationException("同名插件由用户手工管理，在线市场不会覆盖其文件。");

        var packagePath = Path.Combine(_paths.TempDirectory, $"plugin-{Guid.NewGuid():N}.zip");
        var staging = Path.Combine(_paths.PluginsDirectory, $".install-{Guid.NewGuid():N}");
        var target = Path.Combine(_paths.PluginsDirectory, item.Id);
        var backup = Path.Combine(_paths.PluginsDirectory, $".backup-{item.Id}-{Guid.NewGuid():N}");
        var swapped = false;
        try
        {
            await DownloadPackageAsync(item, packagePath, cancellationToken);
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
            _logger.Info($"插件已安装或更新：{item.Name} {item.Version}");
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
        config.DefaultPluginId ??= config.Plugins.FirstOrDefault(x => x.Enabled)?.Id;
        await _configuration.SavePluginConfigAsync(config, cancellationToken);
    }

    private async Task<MarketplaceCatalog> DownloadCatalogAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(CatalogUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        EnsureHttps(response.RequestMessage?.RequestUri);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var catalog = await JsonSerializer.DeserializeAsync<MarketplaceCatalog>(stream, _jsonOptions, cancellationToken)
            ?? throw new InvalidDataException("在线插件目录为空。");
        ValidateCatalog(catalog);
        return catalog;
    }

    private async Task<MarketplaceCatalog?> ReadCatalogAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        var catalog = await JsonSerializer.DeserializeAsync<MarketplaceCatalog>(stream, _jsonOptions, cancellationToken);
        if (catalog is not null) ValidateCatalog(catalog);
        return catalog;
    }

    private async Task DownloadPackageAsync(MarketplacePlugin item, string destination, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(item.PackageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
            DirectoryPath = staging
        };
        if (!string.Equals(resolved.Id, item.Id, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(resolved.Version, item.Version, StringComparison.OrdinalIgnoreCase)
            || resolved.Type != item.Type)
            throw new InvalidDataException("插件包清单与在线目录不一致。");
        if (!resolved.EntryPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(resolved.EntryPath))
            throw new InvalidDataException("插件入口不存在或指向插件目录之外。");
    }

    private static void ValidateCatalog(MarketplaceCatalog catalog)
    {
        if (catalog.SchemaVersion != 1) throw new InvalidDataException($"不支持的插件目录版本：{catalog.SchemaVersion}");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in catalog.Plugins)
        {
            ValidateItem(item);
            if (!ids.Add(item.Id)) throw new InvalidDataException($"在线插件目录包含重复 ID：{item.Id}");
        }
    }

    private static void ValidateItem(MarketplacePlugin item)
    {
        if (!PluginIdPattern().IsMatch(item.Id)) throw new InvalidDataException($"插件 ID 无效：{item.Id}");
        if (string.IsNullOrWhiteSpace(item.Name)) throw new InvalidDataException($"插件名称为空：{item.Id}");
        if (!Version.TryParse(item.Version, out _)) throw new InvalidDataException($"插件版本无效：{item.Version}");
        if (!Version.TryParse(item.MinimumAppVersion, out var minimum)) throw new InvalidDataException($"最低应用版本无效：{item.MinimumAppVersion}");
        if (item.Type != PluginType.Exe) throw new InvalidDataException("当前版本仅支持 EXE 插件。");
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
