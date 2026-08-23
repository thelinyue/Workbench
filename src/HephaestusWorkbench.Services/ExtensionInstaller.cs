using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Services;

/// <summary>统一扩展安装事务完成后的最小结果。</summary>
public sealed class ExtensionInstallResult
{
    public required ExtensionManifest Manifest { get; init; }

    public required string VersionDirectory { get; init; }

    public bool AlreadyInstalled { get; init; }
}

/// <summary>删除安装器拥有的随机暂存目录；实现必须避免跟随目录中的重解析点。</summary>
public interface IExtensionStagingCleaner
{
    void Delete(string stagingDirectory);
}

/// <summary>将已完成验证的暂存目录原子移动为正式版本目录。</summary>
public interface IExtensionVersionDirectoryMover
{
    void Move(string stagingDirectory, string versionDirectory);
}

/// <summary>正式版本目录移动器；保持窄边界，默认仅执行同盘 <see cref="Directory.Move(string, string)"/>。</summary>
public sealed class ExtensionVersionDirectoryMover : IExtensionVersionDirectoryMover
{
    public void Move(string stagingDirectory, string versionDirectory)
        => Directory.Move(stagingDirectory, versionDirectory);
}

/// <summary>正式文件系统暂存目录清理器。任何清理失败都会上抛给安装事务，不会静默遗留目录。</summary>
public sealed class ExtensionStagingCleaner : IExtensionStagingCleaner
{
    public void Delete(string stagingDirectory)
    {
        if (string.IsNullOrWhiteSpace(stagingDirectory))
            throw new ArgumentException("扩展安装暂存目录不能为空。", nameof(stagingDirectory));

        DeleteWithoutFollowingLinks(Path.GetFullPath(stagingDirectory));
    }

    private static void DeleteWithoutFollowingLinks(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }

        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            File.Delete(path);
            return;
        }
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            Directory.Delete(path);
            return;
        }

        foreach (var child in Directory.EnumerateFileSystemEntries(path))
            DeleteWithoutFollowingLinks(child);
        Directory.Delete(path);
    }
}

/// <summary>
/// 将已经下载到内存的正式扩展包按“验签、同盘暂存、安全解压、不可变落盘、Registry 激活”的顺序安装。
/// 安装器不下载文件、不解释旧 manifest，也不直接修改 current.json；活动版本切换和健康检查统一交给 ExtensionRegistry。
/// </summary>
public sealed class ExtensionInstaller
{
    private const string PackageMetadataFileName = "package.json";
    private static readonly JsonSerializerOptions MetadataWriteOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions MetadataReadOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³"
    };

    private readonly string _extensionsRoot;
    private readonly IExtensionPackageVerifier _packageVerifier;
    private readonly ExtensionRegistry _registry;
    private readonly IExtensionStagingCleaner _stagingCleaner;
    private readonly IExtensionVersionDirectoryMover _versionDirectoryMover;
    private readonly SemaphoreSlim _installGate = new(1, 1);

    public ExtensionInstaller(
        string extensionsRoot,
        IExtensionPackageVerifier packageVerifier,
        ExtensionRegistry registry,
        IExtensionStagingCleaner? stagingCleaner = null,
        IExtensionVersionDirectoryMover? versionDirectoryMover = null)
    {
        if (string.IsNullOrWhiteSpace(extensionsRoot))
            throw new ArgumentException("扩展目录根路径不能为空。", nameof(extensionsRoot));

        _extensionsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(extensionsRoot));
        _packageVerifier = packageVerifier ?? throw new ArgumentNullException(nameof(packageVerifier));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _stagingCleaner = stagingCleaner ?? new ExtensionStagingCleaner();
        _versionDirectoryMover = versionDirectoryMover ?? new ExtensionVersionDirectoryMover();
    }

    /// <summary>
    /// 安装一个 Catalog release。ZIP 字节会在验签前复制，确保验签、解压和落盘使用同一份不可变快照。
    /// 版本目录一旦原子落盘便不会因激活失败而删除，以便保留诊断证据并允许后续重试。
    /// </summary>
    public async Task<ExtensionInstallResult> InstallAsync(
        ExtensionPackageVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
            throw new InvalidDataException("扩展安装请求不能为空。");
        if (request.PackageBytes is null || request.CatalogItem is null || request.Release is null)
            throw new InvalidDataException("扩展安装请求缺少 ZIP、Catalog 条目或发布信息。");

        cancellationToken.ThrowIfCancellationRequested();
        // 私有快照只由安装器读取；验签器收到另一份副本，不能在验签期间篡改随后将被解压的字节。
        var packageBytes = request.PackageBytes.ToArray();
        var verificationRequest = new ExtensionPackageVerificationRequest
        {
            PackageBytes = packageBytes.ToArray(),
            CatalogItem = request.CatalogItem,
            Release = request.Release
        };
        var verified = await _packageVerifier.VerifyAsync(verificationRequest, cancellationToken);
        if (verified?.Manifest is null || string.IsNullOrWhiteSpace(verified.PackageSha256))
            throw new InvalidDataException("扩展包验签服务没有返回有效的 manifest 或 SHA-256。");

        var localPackageSha256 = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        if (!string.Equals(localPackageSha256, verified.PackageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("扩展包验签结果的 SHA-256 与安装器私有 ZIP 快照不一致，已拒绝安装。");

        var extensionDirectory = GetExtensionDirectory(verified.Manifest.Id);
        var versionDirectory = GetVersionDirectory(extensionDirectory, verified.Manifest.Version);
        await _installGate.WaitAsync(cancellationToken);
        string? stagingDirectory = null;
        try
        {
            EnsureExtensionsRootIsSafe();
            Directory.CreateDirectory(_extensionsRoot);
            EnsureExtensionsRootIsSafe();

            EnsureExtensionDirectoryIsSafe(extensionDirectory);
            if (PathExists(versionDirectory))
            {
                EnsureVersionDirectoryIsSafe(extensionDirectory, versionDirectory);
                await EnsureExistingVersionMatchesAsync(
                    versionDirectory,
                    verified.Manifest,
                    localPackageSha256,
                    cancellationToken);
                var existingManifest = await ActivateWithContentionRetryAsync(
                    verified.Manifest.Id,
                    verified.Manifest.Version,
                    localPackageSha256,
                    cancellationToken);
                return new ExtensionInstallResult
                {
                    Manifest = existingManifest,
                    VersionDirectory = versionDirectory,
                    AlreadyInstalled = true
                };
            }

            stagingDirectory = Path.Combine(_extensionsRoot, $".install-{Guid.NewGuid():N}");
            EnsureDirectChild(_extensionsRoot, stagingDirectory, "扩展安装暂存目录");
            Directory.CreateDirectory(stagingDirectory);
            RejectReparsePointIfExists(stagingDirectory, "扩展安装暂存目录");

            await ExtractPackageAsync(packageBytes, stagingDirectory, cancellationToken);
            var stagedManifest = await ReadAndValidateRootManifestAsync(stagingDirectory, cancellationToken);
            EnsureManifestMatchesVerification(stagedManifest, verified.Manifest);
            await WritePackageMetadataAsync(stagingDirectory, localPackageSha256, cancellationToken);

            EnsureExtensionDirectoryIsSafe(extensionDirectory, createIfMissing: true);
            EnsureVersionDirectoryIsSafe(extensionDirectory, versionDirectory);
            var alreadyInstalled = false;
            try
            {
                _versionDirectoryMover.Move(stagingDirectory, versionDirectory);
                stagingDirectory = null;
            }
            catch (IOException) when (PathExists(versionDirectory))
            {
                // 另一个宿主进程可能抢先完成同版本落盘；不能覆盖，必须重新验证 package.json 与 manifest 后才可幂等继续。
                EnsureExtensionDirectoryIsSafe(extensionDirectory);
                EnsureVersionDirectoryIsSafe(extensionDirectory, versionDirectory);
                await EnsureExistingVersionMatchesAsync(
                    versionDirectory,
                    verified.Manifest,
                    localPackageSha256,
                    cancellationToken);
                alreadyInstalled = true;
            }

            var manifest = await ActivateWithContentionRetryAsync(
                verified.Manifest.Id,
                verified.Manifest.Version,
                localPackageSha256,
                cancellationToken);
            return new ExtensionInstallResult
            {
                Manifest = manifest,
                VersionDirectory = versionDirectory,
                AlreadyInstalled = alreadyInstalled
            };
        }
        catch (Exception installException)
        {
            var failedStagingDirectory = stagingDirectory;
            stagingDirectory = null;
            try
            {
                if (failedStagingDirectory is not null)
                    _stagingCleaner.Delete(failedStagingDirectory);
            }
            catch (Exception cleanupException)
            {
                throw new InvalidOperationException(
                    $"扩展安装失败，且清理暂存目录也失败。原始错误：{installException.Message}；清理错误：{cleanupException.Message}",
                    new AggregateException(installException, cleanupException));
            }

            throw;
        }
        finally
        {
            try
            {
                if (stagingDirectory is not null)
                    _stagingCleaner.Delete(stagingDirectory);
            }
            catch (Exception cleanupException)
            {
                throw new InvalidOperationException(
                    $"扩展安装已完成，但清理暂存目录失败：{cleanupException.Message}",
                    cleanupException);
            }
            finally
            {
                _installGate.Release();
            }
        }
    }

    /// <summary>
    /// 不同宿主进程可能在同版本目录竞态恢复后短暂同时读取 current.json；仅对文件占用类 IOException（包括 Registry 的事务包装）做有限重试。
    /// </summary>
    private async Task<ExtensionManifest> ActivateWithContentionRetryAsync(
        string id,
        string version,
        string packageSha256,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _registry.ActivateAsync(id, version, packageSha256, cancellationToken);
            }
            catch (Exception exception) when (attempt < maximumAttempts && IsActivationFileContention(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }
        }
    }
    private static bool IsActivationFileContention(Exception exception)
        => exception is IOException ||
           exception is InvalidOperationException { InnerException: IOException };

    private async Task ExtractPackageAsync(
        byte[] packageBytes,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        using var packageStream = new MemoryStream(packageBytes, writable: false);
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: false);
            _ = archive.Entries.Count;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            throw new InvalidDataException("扩展 ZIP 已损坏或格式无效，无法开始安装。", exception);
        }

        using (archive)
        {
            var entries = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectReparseEntry(entry);
                var normalized = NormalizeEntryPath(entry.FullName, out var isDirectory);
                if (string.Equals(normalized, PackageMetadataFileName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"扩展 ZIP 不能包含宿主保留文件 {PackageMetadataFileName}。");
                RejectDuplicateOrConflictingEntry(entries, normalized, isDirectory);

                var segments = normalized.Split(Path.DirectorySeparatorChar);
                var destination = Path.GetFullPath(Path.Combine(stagingDirectory, normalized));
                EnsureDescendant(stagingDirectory, destination, "扩展 ZIP 条目");

                if (isDirectory)
                {
                    CreateSafeDirectories(stagingDirectory, segments);
                    continue;
                }

                if (segments.Length > 1)
                    CreateSafeDirectories(stagingDirectory, segments[..^1]);
                RejectReparsePointIfExists(destination, "扩展 ZIP 文件");
                if (PathExists(destination))
                    throw new InvalidDataException($"扩展 ZIP 包含重复或冲突路径：{entry.FullName}");

                try
                {
                    await using var source = entry.Open();
                    await using var target = new FileStream(
                        destination,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 81920,
                        useAsync: true);
                    await source.CopyToAsync(target, cancellationToken);
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException)
                {
                    throw new InvalidDataException($"扩展 ZIP 条目解压失败：{entry.FullName}。", exception);
                }

                RejectReparsePointIfExists(destination, "扩展 ZIP 文件");
            }
        }
    }

    private static string NormalizeEntryPath(string entryName, out bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(entryName))
            throw new InvalidDataException("扩展 ZIP 包含空路径条目。");

        var portablePath = entryName.Replace('\\', '/');
        if (portablePath.StartsWith("/", StringComparison.Ordinal) ||
            portablePath.StartsWith("//", StringComparison.Ordinal) ||
            (portablePath.Length >= 2 && char.IsAsciiLetter(portablePath[0]) && portablePath[1] == ':') ||
            Path.IsPathRooted(entryName))
        {
            throw new InvalidDataException($"扩展 ZIP 条目不能使用绝对路径：{entryName}");
        }

        isDirectory = portablePath.EndsWith("/", StringComparison.Ordinal);
        if (isDirectory) portablePath = portablePath[..^1];
        var segments = portablePath.Split('/');
        if (segments.Length == 0 || segments.Any(segment => segment.Length == 0))
            throw new InvalidDataException($"扩展 ZIP 条目路径无效：{entryName}");

        foreach (var segment in segments)
        {
            if (segment is "." or "..")
                throw new InvalidDataException($"扩展 ZIP 条目存在路径穿越：{entryName}");
            if (segment.Contains(':', StringComparison.Ordinal))
                throw new InvalidDataException($"扩展 ZIP 条目不能声明 NTFS 备用数据流：{entryName}");
            if (segment.EndsWith(' ') || segment.EndsWith('.'))
                throw new InvalidDataException($"扩展 ZIP 条目包含 Windows 会自动规范化的路径：{entryName}");
            if (segment.Any(character => character < 32 || "<>\"|?*".Contains(character, StringComparison.Ordinal)))
                throw new InvalidDataException($"扩展 ZIP 条目包含 Windows 不允许的文件名字符：{entryName}");

            var deviceName = segment.Split('.', 2)[0].TrimEnd(' ', '.');
            if (WindowsReservedNames.Contains(deviceName))
                throw new InvalidDataException($"扩展 ZIP 条目使用了 Windows 保留名称：{entryName}");
        }

        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static void RejectDuplicateOrConflictingEntry(
        Dictionary<string, bool> entries,
        string normalized,
        bool isDirectory)
    {
        if (!entries.TryAdd(normalized, isDirectory))
            throw new InvalidDataException($"扩展 ZIP 包含重复的规范化路径：{normalized}");

        var ancestor = Path.GetDirectoryName(normalized);
        while (!string.IsNullOrEmpty(ancestor))
        {
            if (entries.TryGetValue(ancestor, out var ancestorIsDirectory) && !ancestorIsDirectory)
                throw new InvalidDataException($"扩展 ZIP 包含文件与目录冲突路径：{normalized}");
            ancestor = Path.GetDirectoryName(ancestor);
        }

        if (!isDirectory)
        {
            var prefix = normalized + Path.DirectorySeparatorChar;
            if (entries.Keys.Any(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"扩展 ZIP 包含文件与目录冲突路径：{normalized}");
        }
    }

    private static void RejectReparseEntry(ZipArchiveEntry entry)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        var hasWindowsReparseFlag = (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;
        if (unixMode == UnixSymbolicLink || hasWindowsReparseFlag)
            throw new InvalidDataException($"扩展 ZIP 条目不能是重解析点或符号链接：{entry.FullName}");
    }

    private static void CreateSafeDirectories(string root, IReadOnlyList<string> segments)
    {
        var current = root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current))
                throw new InvalidDataException($"扩展 ZIP 目录与文件冲突：{current}");
            if (!Directory.Exists(current)) Directory.CreateDirectory(current);
            RejectReparsePointIfExists(current, "扩展 ZIP 目录");
        }
    }

    private static async Task<ExtensionManifest> ReadAndValidateRootManifestAsync(
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(stagingDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
            throw new InvalidDataException("扩展 ZIP 根目录缺少 manifest.json。");
        RejectReparsePointIfExists(manifestPath, "扩展 manifest.json");

        try
        {
            return ExtensionManifestParser.Parse(
                await File.ReadAllTextAsync(manifestPath, cancellationToken),
                stagingDirectory);
        }
        catch (ExtensionContractException exception)
        {
            throw new InvalidDataException($"扩展 ZIP 根目录 manifest.json 无效：{exception.Message}", exception);
        }
    }

    private static void EnsureManifestMatchesVerification(
        ExtensionManifest extracted,
        ExtensionManifest verified)
    {
        var dependenciesMatch = extracted.Dependencies.Count == verified.Dependencies.Count &&
                                extracted.Dependencies.Zip(verified.Dependencies).All(pair =>
                                    string.Equals(pair.First.Id, pair.Second.Id, StringComparison.Ordinal) &&
                                    string.Equals(pair.First.Version, pair.Second.Version, StringComparison.Ordinal));
        if (extracted.SchemaVersion != verified.SchemaVersion ||
            !string.Equals(extracted.Id, verified.Id, StringComparison.Ordinal) ||
            !string.Equals(extracted.Name, verified.Name, StringComparison.Ordinal) ||
            !string.Equals(extracted.Version, verified.Version, StringComparison.Ordinal) ||
            !string.Equals(extracted.PublisherId, verified.PublisherId, StringComparison.Ordinal) ||
            extracted.Kind != verified.Kind ||
            !string.Equals(extracted.HostApiVersion, verified.HostApiVersion, StringComparison.Ordinal) ||
            !string.Equals(extracted.MinHostVersion, verified.MinHostVersion, StringComparison.Ordinal) ||
            extracted.Runtime.Kind != verified.Runtime.Kind ||
            !string.Equals(extracted.Runtime.Protocol, verified.Runtime.Protocol, StringComparison.Ordinal) ||
            !string.Equals(extracted.Runtime.Entry, verified.Runtime.Entry, StringComparison.Ordinal) ||
            !extracted.Capabilities.SequenceEqual(verified.Capabilities, StringComparer.Ordinal) ||
            !extracted.Permissions.SequenceEqual(verified.Permissions, StringComparer.Ordinal) ||
            !dependenciesMatch)
        {
            throw new InvalidDataException("解压后的根 manifest.json 与验签结果不一致。");
        }
    }

    private async Task EnsureExistingVersionMatchesAsync(
        string versionDirectory,
        ExtensionManifest verifiedManifest,
        string packageSha256,
        CancellationToken cancellationToken)
    {
        EnsureVersionDirectoryIsSafe(GetExtensionDirectory(verifiedManifest.Id), versionDirectory);
        var metadataPath = Path.Combine(versionDirectory, PackageMetadataFileName);
        if (!File.Exists(metadataPath))
        {
            throw new InvalidOperationException(
                $"扩展版本 {verifiedManifest.Id} {verifiedManifest.Version} 已存在但缺少不可变 package.json，禁止覆盖。即使内容相同也必须由用户检查现有目录。");
        }
        RejectReparsePointIfExists(metadataPath, "扩展版本 package.json");

        PackageMetadata metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<PackageMetadata>(
                           await File.ReadAllTextAsync(metadataPath, cancellationToken),
                           MetadataReadOptions)
                       ?? throw new JsonException("package.json 内容为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"扩展版本 {verifiedManifest.Id} {verifiedManifest.Version} 的 package.json 无效，禁止覆盖：{exception.Message}",
                exception);
        }

        if (metadata.SchemaVersion != 2 || !IsSha256(metadata.Sha256))
            throw new InvalidOperationException($"扩展版本 {verifiedManifest.Id} {verifiedManifest.Version} 的 package.json 不符合 schema v2，禁止覆盖。");
        if (!string.Equals(metadata.Sha256, packageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"拒绝安装相同扩展版本 {verifiedManifest.Id} {verifiedManifest.Version}：已落盘 package SHA-256 与请求值不同。");

        var manifest = await ReadAndValidateRootManifestAsync(versionDirectory, cancellationToken);
        EnsureManifestMatchesVerification(manifest, verifiedManifest);
    }

    private static async Task WritePackageMetadataAsync(
        string stagingDirectory,
        string packageSha256,
        CancellationToken cancellationToken)
    {
        if (!IsSha256(packageSha256))
            throw new InvalidDataException("扩展包验签结果中的 SHA-256 无效。");

        var metadataPath = Path.Combine(stagingDirectory, PackageMetadataFileName);
        await using var stream = new FileStream(
            metadataPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true);
        await JsonSerializer.SerializeAsync(
            stream,
            new PackageMetadata { SchemaVersion = 2, Sha256 = packageSha256.ToLowerInvariant() },
            MetadataWriteOptions,
            cancellationToken);
    }

    private string GetExtensionDirectory(string id)
    {
        var path = Path.GetFullPath(Path.Combine(_extensionsRoot, id));
        EnsureDirectChild(_extensionsRoot, path, "扩展目录");
        return path;
    }

    private static string GetVersionDirectory(string extensionDirectory, string version)
    {
        var path = Path.GetFullPath(Path.Combine(extensionDirectory, version));
        EnsureDirectChild(extensionDirectory, path, "扩展版本目录");
        return path;
    }

    private void EnsureExtensionsRootIsSafe()
        => RejectReparsePointIfExists(_extensionsRoot, "扩展根目录");

    private void EnsureExtensionDirectoryIsSafe(string extensionDirectory, bool createIfMissing = false)
    {
        EnsureExtensionsRootIsSafe();
        EnsureDirectChild(_extensionsRoot, extensionDirectory, "扩展目录");
        if (File.Exists(extensionDirectory))
            throw new InvalidOperationException($"扩展目录路径被普通文件占用：{extensionDirectory}");
        if (createIfMissing && !Directory.Exists(extensionDirectory)) Directory.CreateDirectory(extensionDirectory);
        RejectReparsePointIfExists(extensionDirectory, "扩展目录");
    }

    private static void EnsureVersionDirectoryIsSafe(string extensionDirectory, string versionDirectory)
    {
        EnsureDirectChild(extensionDirectory, versionDirectory, "扩展版本目录");
        RejectReparsePointIfExists(extensionDirectory, "扩展目录");
        RejectReparsePointIfExists(versionDirectory, "扩展版本目录");
    }

    private static void EnsureDirectChild(string parent, string child, string description)
    {
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var normalizedChild = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
        if (!string.Equals(Path.GetDirectoryName(normalizedChild), normalizedParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{description}必须是指定目录的直接子目录：{normalizedChild}");
    }

    private static void EnsureDescendant(string root, string path, string description)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var relative = Path.GetRelativePath(normalizedRoot, Path.GetFullPath(path));
        if (Path.IsPathRooted(relative) || relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{description}越过了安装暂存目录：{path}");
        }
    }

    private static void RejectReparsePointIfExists(string path, string description)
    {
        if (PathExists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException($"{description}不能是重解析点：{path}");
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(character => Uri.IsHexDigit(character));

    private sealed class PackageMetadata
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("sha256")]
        public required string Sha256 { get; init; }
    }
}
