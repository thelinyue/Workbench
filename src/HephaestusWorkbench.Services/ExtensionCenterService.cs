using System.Text.Json.Serialization;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Services;

/// <summary>扩展中心安装请求；未指定版本时安装当前更新通道下最新且兼容的版本。</summary>
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

    /// <summary>当前更新通道是否存在适用于本宿主版本的发布包。</summary>
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

    Task<ExtensionInstallResult> InstallAsync(
        ExtensionCenterInstallRequest request,
        CancellationToken cancellationToken = default);

    Task SetEnabledAsync(string extensionId, bool enabled, CancellationToken cancellationToken = default);
}

/// <summary>
/// 汇总 Registry 的活动版本、extensions.json 的启用偏好和 Catalog v2 的可用发布。
/// Catalog 不可用不会掩盖本机已安装扩展；安装始终重新从 CatalogClient 下载并交给正式 Installer 验签、落盘和激活。
/// </summary>
public sealed class ExtensionCenterService : IExtensionCenterService
{
    private const string StableChannel = "stable";

    private readonly ExtensionCatalogClient _catalogClient;
    private readonly ExtensionInstaller _installer;
    private readonly ExtensionRegistry _registry;
    private readonly ExtensionSettingsStore _settings;
    private readonly WorkbenchLogger _logger;
    private readonly SemanticVersion _hostVersion;

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
        if (!SemanticVersion.TryParse(hostVersion, out _hostVersion))
            throw new ArgumentException("宿主版本必须是有效的语义化版本。", nameof(hostVersion));
    }

    public async Task<ExtensionCenterSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        var installed = await _registry.LoadAsync(cancellationToken);
        var settings = await _settings.EnsureAsync(cancellationToken);
        ExtensionCatalogLoadResult? catalogResult = null;
        string? warning = null;

        try
        {
            catalogResult = await _catalogClient.RefreshAsync(cancellationToken);
            warning = catalogResult.Warning;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            warning = $"在线扩展目录不可用，仍显示本机已安装扩展：{exception.Message}";
            _logger.Error("在线扩展目录不可用，扩展中心将仅显示本机已安装扩展。", exception);
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
                : SelectLatestRelease(catalogItem, settings.UpdateChannel);
            var availableRelease = hasIdentityConflict ? null : compatibleRelease;
            var enabled = !enabledPreferences.TryGetValue(id, out var configuredEnabled) || configuredEnabled;
            var hasUpdate = !hasIdentityConflict && manifest is not null && availableRelease is not null &&
                            SemanticVersion.Parse(availableRelease.Version).CompareTo(SemanticVersion.Parse(manifest.Version)) > 0;
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

    public async Task<ExtensionInstallResult> InstallAsync(
        ExtensionCenterInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
            throw new InvalidDataException("扩展安装请求不能为空。");
        var extensionId = request.ExtensionId?.Trim();
        if (string.IsNullOrWhiteSpace(extensionId))
            throw new ArgumentException("扩展 ID 不能为空。", nameof(request));

        var settings = await _settings.EnsureAsync(cancellationToken);
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
            release = SelectLatestRelease(item, settings.UpdateChannel);
        }
        else
        {
            release = item.Releases.SingleOrDefault(candidate =>
                string.Equals(candidate.Version, request.Version, StringComparison.Ordinal));
            if (release is not null && !IsAllowedRelease(release, settings.UpdateChannel))
                release = null;
        }

        if (release is null)
            throw new InvalidDataException($"扩展 {extensionId} 没有适用于当前宿主和更新通道的发布版本。");

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
        => SemanticVersion.Parse(minHostVersion).CompareTo(_hostVersion) <= 0;

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

    private ExtensionRelease? SelectLatestRelease(ExtensionCatalogItem item, string updateChannel)
        => item.Releases
            .Where(release => IsAllowedRelease(release, updateChannel))
            .Select(release => (Release: release, Version: SemanticVersion.Parse(release.Version)))
            .OrderByDescending(candidate => candidate.Version)
            .Select(candidate => candidate.Release)
            .FirstOrDefault();

    private bool IsAllowedRelease(ExtensionRelease release, string updateChannel)
    {
        var version = SemanticVersion.Parse(release.Version);
        if (string.Equals(updateChannel, StableChannel, StringComparison.Ordinal) && version.IsPrerelease)
            return false;

        return IsHostCompatible(release.MinHostVersion);
    }

    /// <summary>
    /// 仅实现扩展中心需要的 SemVer 2.0.0 顺序：构建元数据不参与排序，正式版高于预发布版，
    /// 数字预发布标识低于非数字标识。使用字符串长度比较数字，避免版本段溢出整数范围。
    /// </summary>
    private sealed class SemanticVersion : IComparable<SemanticVersion>
    {
        private readonly string[] _core;
        private readonly string[] _prerelease;

        private SemanticVersion(string[] core, string[] prerelease)
        {
            _core = core;
            _prerelease = prerelease;
        }

        public bool IsPrerelease => _prerelease.Length > 0;

        public static SemanticVersion Parse(string value)
            => TryParse(value, out var version)
                ? version
                : throw new InvalidDataException($"扩展版本不是有效的语义化版本：{value}");

        public static bool TryParse(string? value, out SemanticVersion version)
        {
            version = null!;
            if (string.IsNullOrWhiteSpace(value)) return false;

            var buildParts = value.Split('+');
            if (buildParts.Length > 2 || buildParts.Any(string.IsNullOrEmpty)) return false;
            var versionParts = buildParts[0].Split('-', 2);
            var core = versionParts[0].Split('.');
            if (core.Length != 3 || core.Any(part => !IsNumeric(part, rejectLeadingZero: true))) return false;

            var prerelease = versionParts.Length == 1 ? [] : versionParts[1].Split('.');
            if (prerelease.Any(identifier => !IsIdentifier(identifier))) return false;
            if (buildParts.Length == 2 && buildParts[1].Split('.').Any(identifier => !IsIdentifier(identifier, false)))
                return false;

            version = new SemanticVersion(core, prerelease);
            return true;
        }

        public int CompareTo(SemanticVersion? other)
        {
            if (other is null) return 1;
            for (var index = 0; index < _core.Length; index++)
            {
                var comparison = CompareNumeric(_core[index], other._core[index]);
                if (comparison != 0) return comparison;
            }

            if (_prerelease.Length == 0 || other._prerelease.Length == 0)
                return _prerelease.Length == other._prerelease.Length ? 0 : _prerelease.Length == 0 ? 1 : -1;

            for (var index = 0; index < Math.Min(_prerelease.Length, other._prerelease.Length); index++)
            {
                var left = _prerelease[index];
                var right = other._prerelease[index];
                var leftNumeric = left.All(char.IsAsciiDigit);
                var rightNumeric = right.All(char.IsAsciiDigit);
                int comparison;
                if (leftNumeric && rightNumeric)
                    comparison = CompareNumeric(left, right);
                else if (leftNumeric != rightNumeric)
                    comparison = leftNumeric ? -1 : 1;
                else
                    comparison = string.CompareOrdinal(left, right);
                if (comparison != 0) return comparison;
            }

            return _prerelease.Length.CompareTo(other._prerelease.Length);
        }

        private static int CompareNumeric(string left, string right)
        {
            var lengthComparison = left.Length.CompareTo(right.Length);
            return lengthComparison != 0 ? lengthComparison : string.CompareOrdinal(left, right);
        }

        private static bool IsIdentifier(string value, bool rejectNumericLeadingZero = true)
            => value.Length > 0 &&
               value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-') &&
               (!rejectNumericLeadingZero || !value.All(char.IsAsciiDigit) || value.Length == 1 || value[0] != '0');

        private static bool IsNumeric(string value, bool rejectLeadingZero)
            => value.Length > 0 && value.All(char.IsAsciiDigit) &&
               (!rejectLeadingZero || value.Length == 1 || value[0] != '0');
    }
}
