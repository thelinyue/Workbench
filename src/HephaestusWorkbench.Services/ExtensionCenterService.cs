using System.Text.Json.Serialization;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Services;

/// <summary>扩展中心安装请求；未指定版本时安装当前预发布策略允许的最新兼容版本。</summary>
public sealed class ExtensionCenterInstallRequest
{
    [JsonPropertyName("extensionId")]
    public required string ExtensionId { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

/// <summary>扩展中心用于列表呈现的纯数据项，不包含 WPF 控件或宿主实现对象。</summary>
public sealed class ExtensionCenterEntry
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("publisherId")]
    public required string PublisherId { get; init; }

    [JsonPropertyName("kind")]
    public required ExtensionKind Kind { get; init; }

    [JsonPropertyName("installedManifest")]
    public ExtensionManifest? InstalledManifest { get; init; }

    [JsonPropertyName("availableRelease")]
    public ExtensionRelease? AvailableRelease { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    /// <summary>Catalog v2 当前是否仍列出该扩展；离线或仅本机安装时为 false。</summary>
    [JsonPropertyName("isCatalogListed")]
    public bool IsCatalogListed { get; init; }

    /// <summary>当前预发布策略下是否存在适用于本宿主版本的发布包。</summary>
    [JsonPropertyName("hasCompatibleRelease")]
    public bool HasCompatibleRelease { get; init; }

    /// <summary>已安装版本是否适用于当前宿主；未安装时为 null。</summary>
    [JsonPropertyName("isInstalledVersionCompatible")]
    public bool? IsInstalledVersionCompatible { get; init; }

    /// <summary>同 ID 的本机 manifest 与 Catalog 在发布者或类别上是否冲突。</summary>
    [JsonPropertyName("hasIdentityConflict")]
    public bool HasIdentityConflict { get; init; }

    [JsonPropertyName("hasUpdate")]
    public bool HasUpdate { get; init; }
}

/// <summary>一次扩展中心加载的可序列化快照。</summary>
public sealed class ExtensionCenterSnapshot
{
    [JsonPropertyName("extensions")]
    public required IReadOnlyList<ExtensionCenterEntry> Extensions { get; init; }

    [JsonPropertyName("isCatalogFromCache")]
    public bool IsCatalogFromCache { get; init; }

    [JsonPropertyName("warning")]
    public string? Warning { get; init; }
}

/// <summary>
/// App 层使用的最小扩展中心边界。接口只交换可序列化 DTO，不暴露 WPF、文件系统或验签实现。
/// </summary>
public interface IExtensionCenterService
{
    Task<ExtensionCenterSnapshot> LoadAsync(CancellationToken cancellationToken = default);

    Task<ExtensionCenterSnapshot> LoadAsync(bool autoCheckUpdates, CancellationToken cancellationToken = default);

    Task<ExtensionCenterSnapshot> LoadAsync(
        bool autoCheckUpdates,
        bool allowPrerelease,
        CancellationToken cancellationToken = default);

    Task<ExtensionCenterSnapshot> RefreshAsync(CancellationToken cancellationToken = default);

    Task<ExtensionCenterSnapshot> RefreshAsync(
        bool allowPrerelease,
        CancellationToken cancellationToken = default);

    Task<ExtensionInstallResult> InstallAsync(
        ExtensionCenterInstallRequest request,
        CancellationToken cancellationToken = default);

    Task<ExtensionInstallResult> InstallAsync(
        ExtensionCenterInstallRequest request,
        bool allowPrerelease,
        CancellationToken cancellationToken = default);

    Task SetEnabledAsync(string extensionId, bool enabled, CancellationToken cancellationToken = default);
}

/// <summary>
/// 汇总 Registry 的活动版本、extensions.json 的启用偏好和 Catalog v2 的可用发布。
/// Catalog 不可用不会掩盖本机已安装扩展；安装始终重新从 CatalogClient 下载并交给正式 Installer 验签、落盘和激活。
/// </summary>
public sealed class ExtensionCenterService : IExtensionCenterService
{
    private readonly ExtensionCatalogClient _catalogClient;
    private readonly ExtensionInstaller _installer;
    private readonly ExtensionRegistry _registry;
    private readonly ExtensionSettingsStore _settings;
    private readonly WorkbenchLogger _logger;
    private readonly ExtensionHostCompatibility _hostCompatibility;

    public ExtensionCenterService(
        ExtensionCatalogClient catalogClient,
        ExtensionInstaller installer,
        ExtensionRegistry registry,
        ExtensionSettingsStore settings,
        WorkbenchLogger logger,
        string hostVersion)
    {
        _catalogClient = catalogClient ?? throw new ArgumentNullException(nameof(catalogClient));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hostCompatibility = new ExtensionHostCompatibility(hostVersion);
    }

    public Task<ExtensionCenterSnapshot> LoadAsync(CancellationToken cancellationToken = default)
        => RefreshAsync(allowPrerelease: false, cancellationToken);

    public Task<ExtensionCenterSnapshot> LoadAsync(
        bool autoCheckUpdates,
        CancellationToken cancellationToken = default)
        => LoadAsync(autoCheckUpdates, allowPrerelease: false, cancellationToken);

    public Task<ExtensionCenterSnapshot> LoadAsync(
        bool autoCheckUpdates,
        bool allowPrerelease,
        CancellationToken cancellationToken = default)
        => autoCheckUpdates
            ? RefreshAsync(allowPrerelease, cancellationToken)
            : LoadCoreAsync(refreshCatalog: false, allowPrerelease, cancellationToken);

    public Task<ExtensionCenterSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
        => RefreshAsync(allowPrerelease: false, cancellationToken);

    public Task<ExtensionCenterSnapshot> RefreshAsync(
        bool allowPrerelease,
        CancellationToken cancellationToken = default)
        => LoadCoreAsync(refreshCatalog: true, allowPrerelease, cancellationToken);

    private async Task<ExtensionCenterSnapshot> LoadCoreAsync(
        bool refreshCatalog,
        bool allowPrerelease,
        CancellationToken cancellationToken)
    {
        var installed = await _registry.LoadAsync(cancellationToken);
        var settings = await _settings.EnsureAsync(cancellationToken);
        ExtensionCatalogLoadResult? catalogResult = null;
        string? warning = null;

        try
        {
            catalogResult = refreshCatalog
                ? await _catalogClient.RefreshAsync(cancellationToken)
                : await _catalogClient.LoadCachedAsync(cancellationToken);
            warning = catalogResult.Warning;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            warning = refreshCatalog
                ? $"在线扩展目录不可用，仍显示本机已安装扩展：{exception.Message}"
                : $"自动检查扩展更新已关闭，且没有可用的本地 v2 缓存；仍显示本机已安装扩展：{exception.Message}";
            _logger.Error(
                refreshCatalog
                    ? "在线扩展目录不可用，扩展中心将仅显示本机已安装扩展。"
                    : "自动检查扩展更新已关闭，本地 v2 缓存不可用，扩展中心将仅显示本机已安装扩展。",
                exception);
        }

        var catalogItems = catalogResult?.Catalog.Extensions
            .ToDictionary(item => item.Id, StringComparer.Ordinal)
            ?? new Dictionary<string, ExtensionCatalogItem>(StringComparer.Ordinal);
        var installedItems = installed.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var enabledPreferences = settings.Extensions.ToDictionary(
            item => item.Id,
            item => item.Enabled,
            StringComparer.OrdinalIgnoreCase);
        var ids = installedItems.Keys
            .Concat(catalogItems.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal);
        var entries = new List<ExtensionCenterEntry>();

        foreach (var id in ids)
        {
            installedItems.TryGetValue(id, out var manifest);
            catalogItems.TryGetValue(id, out var catalogItem);
            var hasIdentityConflict = manifest is not null && catalogItem is not null &&
                                      !HasSameIdentity(manifest, catalogItem);
            if (hasIdentityConflict)
            {
                var conflictMessage = CreateIdentityConflictMessage(manifest!, catalogItem!);
                warning = AppendWarning(warning, conflictMessage);
                _logger.Error(conflictMessage);
            }

            var compatibleRelease = catalogItem is null
                ? null
                : SelectLatestRelease(catalogItem, allowPrerelease);
            var availableRelease = hasIdentityConflict ? null : compatibleRelease;
            var enabled = !enabledPreferences.TryGetValue(id, out var configuredEnabled) || configuredEnabled;
            var installedVersion = manifest is null ? null : SemanticVersion.Parse(manifest.Version);
            var hasUpdate = !hasIdentityConflict && installedVersion is not null && availableRelease is not null &&
                            (allowPrerelease || !installedVersion.IsPrerelease) &&
                            SemanticVersion.Parse(availableRelease.Version).CompareTo(installedVersion) > 0;
            var useInstalledIdentity = manifest is not null && hasIdentityConflict;

            entries.Add(new ExtensionCenterEntry
            {
                Id = id,
                Name = useInstalledIdentity ? manifest!.Name : catalogItem?.Name ?? manifest!.Name,
                Description = useInstalledIdentity ? string.Empty : catalogItem?.Description ?? string.Empty,
                PublisherId = useInstalledIdentity ? manifest!.PublisherId : catalogItem?.PublisherId ?? manifest!.PublisherId,
                Kind = useInstalledIdentity ? manifest!.Kind : catalogItem?.Kind ?? manifest!.Kind,
                InstalledManifest = manifest,
                AvailableRelease = availableRelease,
                Enabled = enabled,
                IsCatalogListed = catalogItem is not null,
                HasCompatibleRelease = compatibleRelease is not null,
                IsInstalledVersionCompatible = manifest is null ? null : IsHostCompatible(manifest.MinHostVersion),
                HasIdentityConflict = hasIdentityConflict,
                HasUpdate = hasUpdate
            });
        }

        return new ExtensionCenterSnapshot
        {
            Extensions = entries,
            IsCatalogFromCache = catalogResult?.IsFromCache == true,
            Warning = warning
        };
    }

    public Task<ExtensionInstallResult> InstallAsync(
        ExtensionCenterInstallRequest request,
        CancellationToken cancellationToken = default)
        => InstallAsync(request, allowPrerelease: false, cancellationToken);

    public async Task<ExtensionInstallResult> InstallAsync(
        ExtensionCenterInstallRequest request,
        bool allowPrerelease,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
            throw new InvalidDataException("扩展安装请求不能为空。");
        var extensionId = request.ExtensionId?.Trim();
        if (string.IsNullOrWhiteSpace(extensionId))
            throw new ArgumentException("扩展 ID 不能为空。", nameof(request));

        var catalogResult = await _catalogClient.RefreshAsync(cancellationToken);
        var item = catalogResult.Catalog.Extensions.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, extensionId, StringComparison.Ordinal));
        if (item is null)
            throw new InvalidDataException($"扩展目录中不存在扩展 {extensionId}。");

        var installedManifest = (await _registry.LoadAsync(cancellationToken)).SingleOrDefault(candidate =>
            string.Equals(candidate.Id, extensionId, StringComparison.Ordinal));
        if (installedManifest is not null && !HasSameIdentity(installedManifest, item))
        {
            var conflictMessage = CreateIdentityConflictMessage(installedManifest, item);
            _logger.Error(conflictMessage);
            throw new InvalidDataException(conflictMessage);
        }

        ExtensionRelease? release;
        if (string.IsNullOrWhiteSpace(request.Version))
        {
            release = SelectLatestRelease(item, allowPrerelease);
        }
        else
        {
            release = item.Releases.SingleOrDefault(candidate =>
                string.Equals(candidate.Version, request.Version, StringComparison.Ordinal));
            if (release is not null)
            {
                var requestedVersion = SemanticVersion.Parse(release.Version);
                if (requestedVersion.IsPrerelease && !allowPrerelease)
                    throw new InvalidDataException($"扩展 {extensionId} 的版本 {release.Version} 是预发布版本，当前策略不允许安装预发布扩展。");
                if (!IsAllowedRelease(release, allowPrerelease))
                    release = null;
            }
        }

        if (release is null)
            throw new InvalidDataException($"扩展 {extensionId} 没有符合当前预发布策略且适用于本宿主的发布版本。");

        var packageBytes = await _catalogClient.DownloadPackageAsync(
            item,
            release,
            progress: null,
            cancellationToken);
        return await _installer.InstallAsync(new ExtensionPackageVerificationRequest
        {
            PackageBytes = packageBytes,
            CatalogItem = item,
            Release = release
        }, cancellationToken);
    }

    public Task SetEnabledAsync(
        string extensionId,
        bool enabled,
        CancellationToken cancellationToken = default)
        => _settings.SetEnabledAsync(extensionId, enabled, cancellationToken);

    private bool IsHostCompatible(string minHostVersion)
        => _hostCompatibility.IsCompatible(minHostVersion);

    private static bool HasSameIdentity(ExtensionManifest manifest, ExtensionCatalogItem catalogItem)
        => string.Equals(manifest.PublisherId, catalogItem.PublisherId, StringComparison.Ordinal) &&
           manifest.Kind == catalogItem.Kind;

    private static string CreateIdentityConflictMessage(
        ExtensionManifest manifest,
        ExtensionCatalogItem catalogItem)
        => $"扩展 {manifest.Id} 存在身份冲突：本机发布者/类别为 {manifest.PublisherId}/{manifest.Kind}，" +
           $"Catalog 为 {catalogItem.PublisherId}/{catalogItem.Kind}。已保留本机版本并禁止更新或安装。";

    private static string AppendWarning(string? current, string warning)
        => string.IsNullOrWhiteSpace(current) ? warning : $"{current}；{warning}";

    private ExtensionRelease? SelectLatestRelease(ExtensionCatalogItem item, bool allowPrerelease)
        => item.Releases
            .Where(release => IsAllowedRelease(release, allowPrerelease))
            .Select(release => (Release: release, Version: SemanticVersion.Parse(release.Version)))
            .OrderByDescending(candidate => candidate.Version)
            .Select(candidate => candidate.Release)
            .FirstOrDefault();

    private bool IsAllowedRelease(ExtensionRelease release, bool allowPrerelease)
    {
        var version = SemanticVersion.Parse(release.Version);
        if (version.IsPrerelease && !allowPrerelease)
            return false;

        return IsHostCompatible(release.MinHostVersion);
    }

}
