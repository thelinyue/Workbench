using System.Net.Http;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Services;

/// <summary>扩展目录刷新结果；网络失败时可明确标记为使用最近一次通过 v2 校验的本地缓存。</summary>
public sealed record ExtensionCatalogLoadResult(
    ExtensionCatalogDocument Catalog,
    bool IsFromCache,
    string? Warning);

/// <summary>扩展包下载进度。总字节数来自已签名 release 元数据，不使用无依据的估算值。</summary>
public sealed record ExtensionDownloadProgress(long BytesReceived, long TotalBytes);

/// <summary>
/// 只处理 Extension Catalog v2 的网络读取与包字节下载。
/// 本类不决定信任、不激活扩展；下载结果仍必须交给 ExtensionPackageVerifier 和 ExtensionInstaller。
/// </summary>
public sealed class ExtensionCatalogClient
{
    public const string CatalogUrl = "https://raw.githubusercontent.com/thelinyue/Hephaestus-Workbench-Plugins/main/catalog.json";
    public const long MaximumPackageBytes = 200L * 1024 * 1024;
    private const int MaximumCatalogBytes = 2 * 1024 * 1024;

    private readonly DataPaths _paths;
    private readonly WorkbenchLogger _logger;
    private readonly HttpClient _http;

    public ExtensionCatalogClient(DataPaths paths, WorkbenchLogger logger, HttpClient? httpClient = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    }

    /// <summary>强制刷新在线目录；失败时只回退到仍能通过严格 v2 解析的本地缓存。</summary>
    public async Task<ExtensionCatalogLoadResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var catalogJson = await DownloadCatalogJsonAsync(cancellationToken);
            var catalog = ExtensionCatalogParser.Parse(catalogJson);
            await WriteCacheAtomicAsync(catalogJson, cancellationToken);
            return new ExtensionCatalogLoadResult(catalog, false, null);
        }
        catch (Exception onlineException) when (onlineException is not OperationCanceledException)
        {
            _logger.Error("刷新在线扩展目录失败，正在尝试本地 v2 缓存。", onlineException);
            try
            {
                if (!File.Exists(_paths.ExtensionCatalogCacheFile))
                    throw new FileNotFoundException("本地扩展目录缓存不存在。", _paths.ExtensionCatalogCacheFile);

                var cachedJson = await File.ReadAllTextAsync(_paths.ExtensionCatalogCacheFile, cancellationToken);
                var cached = ExtensionCatalogParser.Parse(cachedJson);
                return new ExtensionCatalogLoadResult(cached, true, $"在线目录不可用，当前使用本地缓存：{onlineException.Message}");
            }
            catch (Exception cacheException) when (cacheException is not OperationCanceledException)
            {
                throw new InvalidDataException(
                    $"刷新扩展目录失败，且没有可用的 v2 缓存：{onlineException.Message}",
                    new AggregateException(onlineException, cacheException));
            }
        }
    }

    /// <summary>
    /// 下载目录中声明的一个 release，并严格按 release.size 限制读取。
    /// 返回值仍是未受信的 ZIP 字节，调用方必须继续执行 SHA-256 和 Ed25519 验签。
    /// </summary>
    public async Task<byte[]> DownloadPackageAsync(
        ExtensionCatalogItem item,
        ExtensionRelease release,
        IProgress<ExtensionDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(release);
        if (!item.Releases.Any(candidate => SameRelease(candidate, release)))
            throw new InvalidDataException($"扩展 {item.Id} 的下载版本不属于当前 Catalog 条目。");
        if (release.Size <= 0 || release.Size > MaximumPackageBytes)
            throw new InvalidDataException($"扩展 {item.Id} 的发布包大小必须在 1 到 {MaximumPackageBytes} 字节之间。");

        using var request = new HttpRequestMessage(HttpMethod.Get, release.Url);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        EnsureHttps(response.RequestMessage?.RequestUri, "扩展包下载地址");

        if (response.Content.Headers.ContentLength is { } contentLength && contentLength != release.Size)
            throw new InvalidDataException($"扩展 {item.Id} 的下载响应大小与 Catalog 声明不一致。");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var target = new MemoryStream((int)release.Size);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > release.Size)
                throw new InvalidDataException($"扩展 {item.Id} 的下载内容超过 Catalog 声明大小。");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            progress?.Report(new ExtensionDownloadProgress(total, release.Size));
        }

        if (total != release.Size)
            throw new InvalidDataException($"扩展 {item.Id} 的下载内容大小与 Catalog 声明不一致。");
        return target.ToArray();
    }

    private async Task<string> DownloadCatalogJsonAsync(CancellationToken cancellationToken)
    {
        var separator = CatalogUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{CatalogUrl}{separator}refresh={Guid.NewGuid():N}");
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache, no-store");
        request.Headers.TryAddWithoutValidation("Pragma", "no-cache");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        EnsureHttps(response.RequestMessage?.RequestUri, "扩展目录地址");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var target = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (target.Length + read > MaximumCatalogBytes)
                throw new InvalidDataException("在线扩展目录超过 2 MB 安全限制。");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return System.Text.Encoding.UTF8.GetString(target.ToArray());
    }

    private async Task WriteCacheAtomicAsync(string json, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.ExtensionCatalogCacheFile)!);
        var temporary = _paths.ExtensionCatalogCacheFile + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, json, cancellationToken);
            File.Move(temporary, _paths.ExtensionCatalogCacheFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void EnsureHttps(Uri? uri, string description)
    {
        if (uri is null || !uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{description}必须使用 HTTPS，且重定向后仍须保持 HTTPS。");
    }

    private static bool SameRelease(ExtensionRelease left, ExtensionRelease right)
        => string.Equals(left.Version, right.Version, StringComparison.Ordinal)
           && string.Equals(left.MinHostVersion, right.MinHostVersion, StringComparison.Ordinal)
           && string.Equals(left.Url, right.Url, StringComparison.Ordinal)
           && left.Size == right.Size
           && string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase)
           && string.Equals(left.Signature.KeyId, right.Signature.KeyId, StringComparison.Ordinal)
           && string.Equals(left.Signature.Signature, right.Signature.Signature, StringComparison.Ordinal);
}
