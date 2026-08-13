using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Services;

/// <summary>下载、校验并安装维护者主规则；规则更新与插件二进制更新完全解耦。</summary>
public interface IRuleDistributionService
{
    Task<RuleCatalogEntry?> CheckForUpdateAsync(CancellationToken cancellationToken = default);
    Task<RuleSyncResult> UpdateAsync(CancellationToken cancellationToken = default);
}

public sealed class RuleDistributionService : IRuleDistributionService
{
    public const string CatalogUrl = "https://raw.githubusercontent.com/thelinyue/Hephaestus-Workbench-Plugins/main/rules/log-analyzer/catalog.json";
    private const long MaximumRulePackageBytes = 20L * 1024 * 1024;
    private readonly RuleSetService _rules;
    private readonly IRulePackageVerifier _verifier;
    private readonly WorkbenchLogger _logger;
    private readonly HttpClient _http;
    private readonly IPluginCatalog? _plugins;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public RuleDistributionService(RuleSetService rules, IRulePackageVerifier verifier, WorkbenchLogger logger, HttpClient? httpClient = null, IPluginCatalog? plugins = null)
    {
        _rules = rules;
        _verifier = verifier;
        _logger = logger;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _plugins = plugins;
    }

    public async Task<RuleCatalogEntry?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        var catalog = await DownloadCatalogAsync(cancellationToken);
        await ValidatePluginVersionAsync(catalog, cancellationToken);
        var local = await _rules.ReadOfficialAsync(cancellationToken);
        return local is not null && CompareVersions(catalog.Version, local.Version) <= 0 ? null : catalog;
    }

    public async Task<RuleSyncResult> UpdateAsync(CancellationToken cancellationToken = default)
    {
        var catalog = await DownloadCatalogAsync(cancellationToken);
        await ValidatePluginVersionAsync(catalog, cancellationToken);
        var local = await _rules.ReadOfficialAsync(cancellationToken);
        if (local is not null && CompareVersions(catalog.Version, local.Version) <= 0)
            return new RuleSyncResult(false, local.Version, $"当前主规则已是最新版本：{local.Version}");

        ValidateCatalog(catalog);
        var payload = await DownloadBytesAsync(catalog.PackageUrl, cancellationToken);
        if (catalog.PackageSize > 0 && payload.LongLength != catalog.PackageSize)
            throw new InvalidDataException($"规则包大小不符，期望 {catalog.PackageSize} 字节，实际 {payload.LongLength} 字节。");
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        if (!string.Equals(hash, catalog.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("规则包 SHA-256 校验失败。");

        var official = _verifier.VerifyAndRead(payload, catalog);
        await _rules.ApplyOfficialAsync(official, cancellationToken);
        _logger.Info($"规则更新成功：{catalog.Version}");
        return new RuleSyncResult(true, catalog.Version, $"主规则已更新：{catalog.Version}");
    }

    private async Task<RuleCatalogEntry> DownloadCatalogAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(CatalogUrl, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"获取主规则清单失败：HTTP {(int)response.StatusCode}。");
        var catalog = await JsonSerializer.DeserializeAsync<RuleCatalogEntry>(await response.Content.ReadAsStreamAsync(cancellationToken), _json, cancellationToken)
            ?? throw new InvalidDataException("主规则清单为空。");
        ValidateCatalog(catalog);
        return catalog;
    }

    private async Task<byte[]> DownloadBytesAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("规则下载地址必须使用 HTTPS。");
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"下载主规则失败：HTTP {(int)response.StatusCode}。");
        if (response.Content.Headers.ContentLength is > MaximumRulePackageBytes)
            throw new InvalidDataException("规则包超过 20 MB 限制。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            memory.Write(buffer, 0, read);
            if (memory.Length > MaximumRulePackageBytes) throw new InvalidDataException("规则包超过 20 MB 限制。");
        }
        return memory.ToArray();
    }

    private static void ValidateCatalog(RuleCatalogEntry catalog)
    {
        if (catalog.SchemaVersion != 1) throw new InvalidDataException("不支持的主规则清单版本。");
        if (catalog.RuleSetId != "log-analyzer" || catalog.PluginId != "log-analyzer") throw new InvalidDataException("主规则清单插件标识不匹配。");
        if (!string.Equals(catalog.SignatureAlgorithm, Ed25519RulePackageVerifier.Algorithm, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("规则清单签名算法不受支持。");
        if (!string.Equals(catalog.KeyId, Ed25519RulePackageVerifier.DefaultKeyId, StringComparison.Ordinal))
            throw new InvalidDataException("规则清单签名密钥标识不受信任。");
        if (!Version.TryParse(catalog.Version, out _)) throw new InvalidDataException("主规则版本无效。");
        if (!Version.TryParse(catalog.MinimumPluginVersion, out _)) throw new InvalidDataException("主规则要求的最低插件版本无效。");
        if (!Uri.TryCreate(catalog.PackageUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("规则包地址必须使用 HTTPS。");
        if (!Regex.IsMatch(catalog.Sha256 ?? string.Empty, "^[0-9a-fA-F]{64}$")) throw new InvalidDataException("主规则 SHA-256 无效。");
        if (catalog.PackageSize <= 0 || catalog.PackageSize > MaximumRulePackageBytes) throw new InvalidDataException("主规则包大小无效。");
        if (!Regex.IsMatch(catalog.Signature ?? string.Empty, "^[A-Za-z0-9+/]+={0,2}$")) throw new InvalidDataException("规则包签名格式无效。");
    }

    private static int CompareVersions(string left, string? right)
    {
        if (string.IsNullOrWhiteSpace(right)) return 1;
        if (Version.TryParse(left, out var a) && Version.TryParse(right, out var b)) return a.CompareTo(b);
        return string.CompareOrdinal(left, right);
    }

    private async Task ValidatePluginVersionAsync(RuleCatalogEntry catalog, CancellationToken cancellationToken)
    {
        if (_plugins is null) return;
        var plugin = await _plugins.GetAsync(catalog.PluginId, cancellationToken);
        if (plugin is null)
            throw new InvalidDataException($"规则清单要求的插件不存在：{catalog.PluginId}。");
        if (CompareVersions(plugin.Version, catalog.MinimumPluginVersion) < 0)
            throw new InvalidDataException($"当前插件版本 {plugin.Version} 低于规则要求的最低版本 {catalog.MinimumPluginVersion}。");
    }
}
