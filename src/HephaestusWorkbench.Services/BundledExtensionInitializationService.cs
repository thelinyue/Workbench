using System.Text;
using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 在主界面显示前部署安装介质锁定的离线扩展。
/// 本服务只负责安全读取 Bundle、恢复 Registry 状态并调用统一安装事务；验签、解压、
/// current.json 切换和健康检查仍由 ExtensionPackageVerifier、ExtensionInstaller 与 ExtensionRegistry 负责。
/// </summary>
public sealed class BundledExtensionInitializationService
{
    private const long MaximumManifestBytes = 1024 * 1024;

    private readonly string _bundleRoot;
    private readonly ExtensionInstaller _installer;
    private readonly ExtensionRegistry _registry;
    private readonly IExtensionPackageVerifier _packageVerifier;
    private readonly IExtensionHealthChecker _healthChecker;
    private readonly ExtensionHostCompatibility _hostCompatibility;
    private readonly WorkbenchLogger _logger;

    public BundledExtensionInitializationService(
        string bundleRoot,
        ExtensionInstaller installer,
        ExtensionRegistry registry,
        IExtensionPackageVerifier packageVerifier,
        IExtensionHealthChecker healthChecker,
        ExtensionHostCompatibility hostCompatibility,
        WorkbenchLogger logger)
    {
        if (string.IsNullOrWhiteSpace(bundleRoot))
            throw new ArgumentException("BundledExtensions 根目录不能为空。", nameof(bundleRoot));

        _bundleRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundleRoot));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _packageVerifier = packageVerifier ?? throw new ArgumentNullException(nameof(packageVerifier));
        _healthChecker = healthChecker ?? throw new ArgumentNullException(nameof(healthChecker));
        _hostCompatibility = hostCompatibility ?? throw new ArgumentNullException(nameof(hostCompatibility));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 按清单顺序部署内置扩展。已安装的较新版本必须通过身份、宿主兼容性和健康检查，且内置 ZIP 仍需验签，但不会降级；
    /// 同版仍进入安装器完成签名与落盘内容复核，避免静态文件被篡改后继续启动。
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureBundleRoot();
            var manifestPath = Path.Combine(_bundleRoot, "bundled-extensions.json");
            EnsureDirectFile(manifestPath, "Bundled Extension 清单");
            var document = BundledExtensionManifestParser.Parse(
                await ReadBoundedTextAsync(manifestPath, MaximumManifestBytes, cancellationToken));

            var active = (await _registry.LoadAsync(cancellationToken))
                .ToDictionary(manifest => manifest.Id, StringComparer.Ordinal);

            foreach (var item in document.Extensions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_hostCompatibility.IsCompatible(item.Release.MinHostVersion))
                {
                    throw new InvalidOperationException(
                        $"内置扩展 {item.Id} {item.Release.Version} 要求最低宿主版本 {item.Release.MinHostVersion}，当前工作台版本不兼容。");
                }

                var assetPath = Path.Combine(_bundleRoot, item.Asset);
                EnsureDirectFile(assetPath, $"Bundled Extension 资产 {item.Id}");
                var packageBytes = await ReadPackageAsync(assetPath, item.Release.Size, cancellationToken);
                var verificationRequest = new ExtensionPackageVerificationRequest
                {
                    PackageBytes = packageBytes,
                    CatalogItem = item.ToCatalogItem(),
                    Release = item.Release
                };

                if (active.TryGetValue(item.Id, out var installed))
                {
                    EnsureIdentityMatches(item, installed);
                    if (SemanticVersion.Parse(installed.Version).CompareTo(SemanticVersion.Parse(item.Release.Version)) > 0)
                    {
                        if (!_hostCompatibility.IsCompatible(installed.MinHostVersion))
                        {
                            throw new InvalidOperationException(
                                $"已安装扩展 {installed.Id} {installed.Version} 要求最低宿主版本 {installed.MinHostVersion}，当前工作台版本不兼容。");
                        }

                        // Bundle 即使不参与激活，也属于正式安装介质的一部分，必须完成原始 ZIP 验签。
                        await _packageVerifier.VerifyAsync(verificationRequest, cancellationToken);
                        await _healthChecker.CheckAsync(installed, cancellationToken);
                        _logger.Info($"已安装扩展 {item.Id} {installed.Version} 高于安装包内置版本 {item.Release.Version}，内置包验签通过并保持当前版本，不执行降级。");
                        continue;
                    }
                }

                var result = await _installer.InstallAsync(verificationRequest, cancellationToken);

                active[item.Id] = result.Manifest;
                _logger.Info(result.AlreadyInstalled
                    ? $"内置扩展 {result.Manifest.Id} {result.Manifest.Version} 已验证并保持启用。"
                    : $"内置扩展 {result.Manifest.Id} {result.Manifest.Version} 已安装并通过健康检查。");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Error("Bundled Extension 初始化失败", exception);
            throw;
        }
    }

    private void EnsureBundleRoot()
    {
        if (!Directory.Exists(_bundleRoot))
            throw new DirectoryNotFoundException($"BundledExtensions 根目录不存在：{_bundleRoot}");
        RejectReparsePoint(_bundleRoot, "BundledExtensions 根目录");
    }

    private void EnsureDirectFile(string path, string description)
    {
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(fullPath), _bundleRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{description}必须位于 BundledExtensions 根目录：{fullPath}");
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            RejectReparsePoint(fullPath, description);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"{description}不存在：{fullPath}", fullPath);
    }

    private static void EnsureIdentityMatches(BundledExtensionItem bundled, ExtensionManifest installed)
    {
        if (!string.Equals(bundled.PublisherId, installed.PublisherId, StringComparison.Ordinal) ||
            bundled.Kind != installed.Kind)
        {
            throw new InvalidOperationException(
                $"内置扩展 {bundled.Id} 与已安装版本的发布者或类别身份不一致，已阻止启动。");
        }
    }

    private static async Task<string> ReadBoundedTextAsync(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedFileAsync(path, maximumBytes, expectedBytes: null, cancellationToken);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"Bundled Extension 清单不是有效的 UTF-8 文本：{path}", exception);
        }
    }

    private static Task<byte[]> ReadPackageAsync(
        string path,
        long declaredSize,
        CancellationToken cancellationToken)
    {
        if (declaredSize <= 0 || declaredSize > ExtensionPackageLimits.MaximumPackageBytes)
        {
            throw new InvalidDataException(
                $"Bundled Extension 资产声明大小必须在 1 到 {ExtensionPackageLimits.MaximumPackageBytes} 字节之间：{path}");
        }

        return ReadBoundedFileAsync(path, ExtensionPackageLimits.MaximumPackageBytes, declaredSize, cancellationToken);
    }

    private static async Task<byte[]> ReadBoundedFileAsync(
        string path,
        long maximumBytes,
        long? expectedBytes,
        CancellationToken cancellationToken)
    {
        var fileLength = new FileInfo(path).Length;
        if (fileLength > maximumBytes)
            throw new InvalidDataException($"Bundled Extension 文件超过 {maximumBytes} 字节安全限制：{path}");
        if (expectedBytes is not null && fileLength != expectedBytes.Value)
        {
            throw new InvalidDataException(
                $"Bundled Extension 资产大小不一致：清单为 {expectedBytes.Value} 字节，实际为 {fileLength} 字节。路径：{path}");
        }

        var capacity = checked((int)fileLength);
        var bytes = new byte[capacity];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken);
            if (read == 0)
                throw new InvalidDataException($"Bundled Extension 文件读取提前结束：{path}");
            offset += read;
        }

        if (await stream.ReadAsync(new byte[1], cancellationToken) != 0)
            throw new InvalidDataException($"Bundled Extension 文件在读取期间增长，实际大小超过清单声明：{path}");
        return bytes;
    }

    private static void RejectReparsePoint(string path, string description)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException($"{description}不能是重解析点：{path}");
    }
}
