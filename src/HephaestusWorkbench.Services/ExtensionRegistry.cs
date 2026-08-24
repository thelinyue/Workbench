using System.Text.Json;
using System.Text.Json.Serialization;
using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 以 Extensions/&lt;id&gt;/current.json 为唯一发现入口管理扩展版本。
/// Registry 不递归扫描 manifest；current.json 表示活动版本，current.json.bak 表示可回滚版本，
/// 版本目录是否可删除还会额外受任务租约约束。
/// </summary>
public sealed class ExtensionRegistry
{
    private static readonly JsonSerializerOptions CurrentSerializerOptions = new()
    {
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions PackageMetadataSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _extensionsRoot;
    private readonly IExtensionHealthChecker _healthChecker;
    private readonly IExtensionTrustStore _trustStore;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly Dictionary<string, ActiveExtensionVersion> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Id, string Version), int> _leaseCounts = new();
    private readonly List<string> _issues = new();

    public ExtensionRegistry(
        string extensionsRoot,
        IExtensionHealthChecker healthChecker,
        IExtensionTrustStore trustStore)
    {
        if (string.IsNullOrWhiteSpace(extensionsRoot))
            throw new ArgumentException("扩展目录根路径不能为空。", nameof(extensionsRoot));

        _extensionsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(extensionsRoot));
        _healthChecker = healthChecker ?? throw new ArgumentNullException(nameof(healthChecker));
        _trustStore = trustStore ?? throw new ArgumentNullException(nameof(trustStore));
    }

    public IReadOnlyList<string> Issues
    {
        get
        {
            lock (_stateGate)
                return _issues.ToArray();
        }
    }

    /// <summary>从扩展根的一级子目录读取 current.json，并恢复启动时遗留的 pending 状态。</summary>
    public async Task<IReadOnlyList<ExtensionManifest>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var active = new Dictionary<string, ActiveExtensionVersion>(StringComparer.Ordinal);
            var issues = new List<string>();

            EnsureExtensionsRootIsSafe();
            if (Directory.Exists(_extensionsRoot))
            {
                foreach (var extensionDirectory in Directory
                             .EnumerateDirectories(_extensionsRoot, "*", SearchOption.TopDirectoryOnly)
                             .OrderBy(path => path, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        EnsureExtensionDirectoryIsSafe(extensionDirectory);
                        var currentPath = Path.Combine(extensionDirectory, "current.json");
                        if (!File.Exists(currentPath))
                            continue;

                        ExtensionCurrentDocument current;
                        try
                        {
                            current = await ReadCurrentAsync(currentPath, cancellationToken);
                            ValidateDirectoryIdentity(extensionDirectory, current);
                        }
                        catch (Exception currentException) when (currentException is not OperationCanceledException)
                        {
                            // pending 从不执行；即使其 trustedKeyId 因崩溃写入不完整，也应优先尝试独立验证 healthy backup。
                            if (!await IsPendingDocumentAsync(currentPath, cancellationToken))
                                throw;

                            try
                            {
                                var recovered = await RecoverHealthyBackupAsync(
                                    extensionDirectory,
                                    currentPath,
                                    cancellationToken);
                                active[recovered.Manifest.Id] = recovered;
                            }
                            catch (Exception rollbackException) when (rollbackException is not OperationCanceledException)
                            {
                                issues.Add(
                                    $"扩展 {Path.GetFileName(extensionDirectory)} 的 current.json 处于待验证状态且结构不完整，" +
                                    $"但没有可用的健康回滚版本：{rollbackException.Message}；pending 错误：{currentException.Message}");
                            }
                            continue;
                        }

                        ActiveExtensionVersion loaded;
                        if (current.State == ExtensionActivationState.Pending)
                        {
                            try
                            {
                                loaded = await RecoverHealthyBackupAsync(
                                    extensionDirectory,
                                    currentPath,
                                    cancellationToken);
                            }
                            catch (Exception exception) when (exception is not OperationCanceledException)
                            {
                                issues.Add($"扩展 {current.Id} 的 current.json 处于待验证状态，但没有可用的健康回滚版本：{exception.Message}");
                                continue;
                            }
                        }
                        else
                        {
                            loaded = await ReadAuthorizedVersionAsync(extensionDirectory, current, cancellationToken);
                        }

                        active[loaded.Manifest.Id] = loaded;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        issues.Add($"扩展目录 {Path.GetFileName(extensionDirectory)} 加载失败：{exception.Message}");
                    }
                }
            }

            lock (_stateGate)
            {
                _active.Clear();
                foreach (var pair in active)
                    _active.Add(pair.Key, pair.Value);

                _issues.Clear();
                _issues.AddRange(issues);
                return _active.Values
                    .Select(item => item.Manifest)
                    .OrderBy(item => item.Id, StringComparer.Ordinal)
                    .ToArray();
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// 激活已落盘的版本：仅供同程序集安装事务传入验签结果中的 trustedKeyId；
    /// 先保存旧 current.json 为 backup，再写 pending 并执行正式加载验证，任一步失败则恢复原健康版本。
    /// </summary>
    internal async Task<ExtensionManifest> ActivateAsync(
        ExtensionPackageVerificationResult verification,
        CancellationToken cancellationToken = default)
    {
        if (verification?.Manifest is null ||
            string.IsNullOrWhiteSpace(verification.PackageSha256) ||
            string.IsNullOrWhiteSpace(verification.TrustedKeyId))
        {
            throw new InvalidDataException("扩展激活必须接收完整的验签结果。");
        }

        var pending = ValidateRequestedCurrent(
            verification.Manifest.Id,
            verification.Manifest.Version,
            verification.PackageSha256,
            verification.TrustedKeyId);
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var extensionDirectory = Path.Combine(_extensionsRoot, pending.Id);
            EnsureExtensionDirectoryIsSafe(extensionDirectory);
            var currentPath = Path.Combine(extensionDirectory, "current.json");
            var backupPath = Path.Combine(extensionDirectory, "current.json.bak");
            var candidate = await ReadAuthorizedVersionAsync(extensionDirectory, pending, cancellationToken);
            EnsureVerificationBinding(candidate.Manifest, verification.Manifest);

            ExtensionCurrentDocument? rollback = null;
            ActiveExtensionVersion? rollbackVersion = null;
            if (File.Exists(currentPath))
            {
                var existing = await ReadCurrentAsync(currentPath, cancellationToken);
                ValidateDirectoryIdentity(extensionDirectory, existing);
                RejectDifferentHashForSameVersion(existing, pending);
                if (existing.State == ExtensionActivationState.Healthy)
                {
                    var activeVersion = await ReadAuthorizedVersionAsync(extensionDirectory, existing, cancellationToken);
                    EnsureIdentityContinuity(candidate.Manifest, activeVersion.Manifest);
                    if (IsSamePackage(existing, pending))
                    {
                        // 幂等激活仍执行正式健康检查，但不能重写 current/backup，否则会用活动版本覆盖真正的回滚版本。
                        await _healthChecker.CheckAsync(candidate.Manifest, cancellationToken);
                        lock (_stateGate)
                            _active[pending.Id] = candidate;
                        return candidate.Manifest;
                    }

                    rollbackVersion = activeVersion;
                    rollback = existing;
                }
                else if (!IsSamePackage(existing, pending))
                {
                    throw new InvalidOperationException(
                        $"扩展 {pending.Id} 当前有另一个待验证版本，不能开始新的激活事务。");
                }
            }

            if (File.Exists(backupPath))
            {
                var existingBackup = await ReadCurrentAsync(backupPath, cancellationToken);
                ValidateDirectoryIdentity(extensionDirectory, existingBackup);
                RejectDifferentHashForSameVersion(existingBackup, pending);
                if (rollback is null && existingBackup.State == ExtensionActivationState.Healthy)
                {
                    rollbackVersion = await ReadAuthorizedVersionAsync(extensionDirectory, existingBackup, cancellationToken);
                    EnsureIdentityContinuity(candidate.Manifest, rollbackVersion.Manifest);
                    rollback = existingBackup;
                }
            }

            if (rollback is not null && File.Exists(currentPath))
                await WriteCurrentAtomicAsync(backupPath, rollback, cancellationToken);

            try
            {
                await WriteCurrentAtomicAsync(currentPath, pending, cancellationToken);
                await _healthChecker.CheckAsync(candidate.Manifest, cancellationToken);

                var healthy = CreateCurrent(
                    pending.Id,
                    pending.Version,
                    pending.PackageSha256,
                    pending.TrustedKeyId,
                    ExtensionActivationState.Healthy);
                await WriteCurrentAtomicAsync(currentPath, healthy, cancellationToken);
                lock (_stateGate)
                    _active[pending.Id] = candidate;
                return candidate.Manifest;
            }
            catch (Exception activationException)
            {
                try
                {
                    if (rollback is not null)
                    {
                        await WriteCurrentAtomicAsync(currentPath, rollback, CancellationToken.None);
                        if (rollbackVersion is not null)
                        {
                            lock (_stateGate)
                                _active[pending.Id] = rollbackVersion;
                        }
                    }
                    else
                    {
                        // 没有旧健康版本时保留 pending 指针和完整版本目录，允许下一次启动或用户操作重试。
                        lock (_stateGate)
                            _active.Remove(pending.Id);
                    }
                }
                catch (Exception rollbackException)
                {
                    throw new InvalidOperationException(
                        $"扩展 {pending.Id} {pending.Version} 激活失败，且恢复回滚版本失败：{rollbackException.Message}",
                        new AggregateException(activationException, rollbackException));
                }

                if (activationException is OperationCanceledException)
                    throw;

                var recoveryMessage = rollback is null
                    ? "没有可回滚版本，扩展保持未激活状态，已保留待验证版本以便重试"
                    : "已恢复回滚版本";
                throw new InvalidOperationException(
                    $"扩展 {pending.Id} {pending.Version} 激活失败，{recoveryMessage}：{activationException.Message}",
                    activationException);
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>为新任务租用当前 healthy 版本；租约不会随后续激活切换而改变。</summary>
    public ExtensionVersionLease LeaseCurrentVersion(string id)
    {
        lock (_stateGate)
        {
            if (!_active.TryGetValue(id, out var active))
                throw new InvalidOperationException($"扩展 {id} 当前没有可租用的 healthy 版本。");

            // 新任务不能复用 LoadAsync 时的授权快照；密钥撤销或范围收紧必须立即阻止新租约。
            var authorization = ResolveRuntimeAuthorization(active.TrustedKeyId, active.Manifest);
            var key = (active.Manifest.Id, active.Manifest.Version);
            _leaseCounts.TryGetValue(key, out var count);
            _leaseCounts[key] = count + 1;
            return new ExtensionVersionLease(active.Manifest, authorization, () => ReleaseLease(key));
        }
    }

    /// <summary>
    /// 判断版本是否可清理。活动版本、current.json.bak 指向的回滚版本以及仍被任务租用的版本均不可删除。
    /// </summary>
    public bool CanDeleteVersion(string id, string version)
    {
        lock (_stateGate)
        {
            if (_leaseCounts.ContainsKey((id, version)))
                return false;
        }

        var extensionDirectory = Path.Combine(_extensionsRoot, id);
        try
        {
            EnsureExtensionDirectoryIsSafe(extensionDirectory);
        }
        catch
        {
            return false;
        }
        return !DocumentProtectsVersion(Path.Combine(extensionDirectory, "current.json"), id, version) &&
               !DocumentProtectsVersion(Path.Combine(extensionDirectory, "current.json.bak"), id, version);
    }

    private static void EnsureVerificationBinding(
        ExtensionManifest installed,
        ExtensionManifest verified)
    {
        if (string.Equals(installed.Id, verified.Id, StringComparison.Ordinal) &&
            string.Equals(installed.Version, verified.Version, StringComparison.Ordinal) &&
            string.Equals(installed.PublisherId, verified.PublisherId, StringComparison.Ordinal) &&
            installed.Kind == verified.Kind)
        {
            return;
        }

        throw new InvalidOperationException(
            $"扩展 {installed.Id} 的落盘 manifest 与验签结果身份不一致，已拒绝激活。");
    }

    /// <summary>
    /// 候选版本在激活锁内必须与当前健康版本保持发布者和类别连续，避免下载、验签期间活动版本变化后被覆盖。
    /// </summary>
    private static void EnsureIdentityContinuity(ExtensionManifest candidate, ExtensionManifest active)
    {
        if (string.Equals(candidate.PublisherId, active.PublisherId, StringComparison.Ordinal) &&
            candidate.Kind == active.Kind)
        {
            return;
        }

        throw new InvalidOperationException(
            $"扩展 {candidate.Id} 存在身份冲突：候选发布者/类别为 {candidate.PublisherId}/{candidate.Kind}，" +
            $"当前活动版本为 {active.PublisherId}/{active.Kind}。已拒绝激活。");
    }

    private static ExtensionCurrentDocument ValidateRequestedCurrent(
        string id,
        string version,
        string packageSha256,
        string trustedKeyId)
    {
        var document = CreateCurrent(id, version, packageSha256, trustedKeyId, ExtensionActivationState.Pending);
        return ExtensionCurrentParser.Parse(JsonSerializer.Serialize(document));
    }

    private static ExtensionCurrentDocument CreateCurrent(
        string id,
        string version,
        string packageSha256,
        string trustedKeyId,
        ExtensionActivationState state)
        => new()
        {
            SchemaVersion = 2,
            Id = id,
            Version = version,
            PackageSha256 = packageSha256,
            TrustedKeyId = trustedKeyId,
            State = state
        };

    private async Task<ActiveExtensionVersion> RecoverHealthyBackupAsync(
        string extensionDirectory,
        string currentPath,
        CancellationToken cancellationToken)
    {
        var backupPath = Path.Combine(extensionDirectory, "current.json.bak");
        var backup = await ReadCurrentAsync(backupPath, cancellationToken);
        ValidateDirectoryIdentity(extensionDirectory, backup);
        if (backup.State != ExtensionActivationState.Healthy)
            throw new InvalidOperationException("回滚文档不是 healthy 状态。");

        var loaded = await ReadAuthorizedVersionAsync(extensionDirectory, backup, cancellationToken);
        await WriteCurrentAtomicAsync(currentPath, backup, cancellationToken);
        return loaded;
    }

    private static async Task<bool> IsPendingDocumentAsync(
        string currentPath,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(currentPath, cancellationToken));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("state", out var state) &&
                   state.ValueKind == JsonValueKind.String &&
                   string.Equals(state.GetString(), "pending", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<ExtensionCurrentDocument> ReadCurrentAsync(
        string path,
        CancellationToken cancellationToken)
    {
        EnsureExtensionDirectoryIsSafe(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
            throw new FileNotFoundException($"扩展版本文档不存在：{path}", path);

        return ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(path, cancellationToken));
    }

    private async Task<ExtensionManifest> ReadMatchingManifestAsync(
        string extensionDirectory,
        ExtensionCurrentDocument current,
        CancellationToken cancellationToken)
    {
        if (current.State != ExtensionActivationState.Healthy && current.State != ExtensionActivationState.Pending)
            throw new InvalidOperationException($"扩展 {current.Id} 的激活状态无效。");

        var versionDirectory = Path.Combine(extensionDirectory, current.Version);
        EnsureVersionDirectoryIsSafe(extensionDirectory, versionDirectory);
        var manifestPath = Path.Combine(versionDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"扩展 {current.Id} {current.Version} 缺少 manifest.json。", manifestPath);

        var manifest = ExtensionManifestParser.Parse(
            await File.ReadAllTextAsync(manifestPath, cancellationToken),
            versionDirectory);
        if (!string.Equals(manifest.Id, current.Id, StringComparison.Ordinal) ||
            !string.Equals(manifest.Version, current.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"扩展 manifest 的 id/version 与 current.json 不匹配：current={current.Id} {current.Version}，manifest={manifest.Id} {manifest.Version}。");
        }

        return manifest;
    }

    /// <summary>
    /// 同时核对版本目录的宿主 package.json、manifest 与当前 Trust Store，形成可供活动状态保存的最小绑定。
    /// package.json 的 SHA-256 必须与 current.json 一致，避免活动指针与实际安装包元数据脱节。
    /// </summary>
    private async Task<ActiveExtensionVersion> ReadAuthorizedVersionAsync(
        string extensionDirectory,
        ExtensionCurrentDocument current,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadMatchingManifestAsync(extensionDirectory, current, cancellationToken);
        var versionDirectory = Path.Combine(extensionDirectory, current.Version);
        var metadataPath = Path.Combine(versionDirectory, "package.json");
        if (!File.Exists(metadataPath))
            throw new FileNotFoundException($"扩展 {current.Id} {current.Version} 缺少宿主 package.json。", metadataPath);
        RejectReparsePointIfExists(metadataPath, "扩展版本 package.json");

        PackageMetadata metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<PackageMetadata>(
                           await File.ReadAllTextAsync(metadataPath, cancellationToken),
                           PackageMetadataSerializerOptions)
                       ?? throw new JsonException("package.json 内容为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"扩展 {current.Id} {current.Version} 的 package.json 无效：{exception.Message}", exception);
        }

        if (metadata.SchemaVersion != 2 ||
            string.IsNullOrWhiteSpace(metadata.Sha256) ||
            metadata.Sha256.Length != 64 ||
            !metadata.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException($"扩展 {current.Id} {current.Version} 的 package.json 不符合 schema v2。");
        }
        if (!string.Equals(metadata.Sha256, current.PackageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"扩展 {current.Id} 的相同扩展版本 {current.Version} 存在 package.json SHA-256 与 current.json 不一致。");

        _ = ResolveRuntimeAuthorization(current.TrustedKeyId, manifest);
        return new ActiveExtensionVersion(manifest, current.TrustedKeyId);
    }

    /// <summary>
    /// 使用宿主当前信任表重新解析签名身份，并把发布者、类别和权限逐项绑定到 manifest。
    /// Catalog 与 manifest 都不能自行恢复已撤销或超出范围的授权。
    /// </summary>
    private ExtensionRuntimeAuthorization ResolveRuntimeAuthorization(
        string trustedKeyId,
        ExtensionManifest manifest)
    {
        if (!_trustStore.TryGetTrustedKey(trustedKeyId, out var trustedKey))
            throw new InvalidOperationException($"扩展 {manifest.Id} 的签名 keyId 未知或已从宿主信任表移除：{trustedKeyId}。");
        if (!string.Equals(trustedKey.PublisherId, manifest.PublisherId, StringComparison.Ordinal))
            throw new InvalidOperationException($"扩展 {manifest.Id} 的发布者与受信任签名密钥不一致。");
        if (!trustedKey.Scope.AllowedKinds.Contains(manifest.Kind))
            throw new InvalidOperationException($"扩展 {manifest.Id} 的类别 {manifest.Kind} 超出签名密钥授权范围。");

        var allowedPermissions = new HashSet<string>(trustedKey.Scope.Permissions, StringComparer.Ordinal);
        foreach (var permission in manifest.Permissions)
        {
            if (!allowedPermissions.Contains(permission))
                throw new InvalidOperationException($"扩展 {manifest.Id} 请求的权限 {permission} 超出签名密钥授权范围。");
        }

        return new ExtensionRuntimeAuthorization(
            trustedKey.KeyId,
            trustedKey.PublisherId,
            trustedKey.Scope.AllowedKinds,
            trustedKey.Scope.Permissions);
    }

    private static void ValidateDirectoryIdentity(string extensionDirectory, ExtensionCurrentDocument current)
    {
        var directoryId = Path.GetFileName(extensionDirectory);
        if (!string.Equals(directoryId, current.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"扩展目录名 {directoryId} 与 current.json id {current.Id} 不匹配。");
        }
    }

    private void EnsureExtensionsRootIsSafe()
    {
        RejectReparsePointIfExists(_extensionsRoot, "扩展根目录");
    }

    private void EnsureExtensionDirectoryIsSafe(string extensionDirectory)
    {
        EnsureExtensionsRootIsSafe();
        var root = Path.GetFullPath(_extensionsRoot);
        var directory = Path.GetFullPath(extensionDirectory);
        if (!string.Equals(Path.GetDirectoryName(directory), root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"扩展目录必须是扩展根目录的直接子目录：{directory}");
        RejectReparsePointIfExists(directory, "扩展目录");
    }

    private void EnsureVersionDirectoryIsSafe(string extensionDirectory, string versionDirectory)
    {
        EnsureExtensionDirectoryIsSafe(extensionDirectory);
        var extension = Path.GetFullPath(extensionDirectory);
        var version = Path.GetFullPath(versionDirectory);
        if (!string.Equals(Path.GetDirectoryName(version), extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"扩展版本目录必须位于扩展目录内：{version}");
        RejectReparsePointIfExists(version, "扩展版本目录");
    }

    private static void RejectReparsePointIfExists(string path, string description)
    {
        if ((File.Exists(path) || Directory.Exists(path)) &&
            File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException($"{description}不能是重解析点：{path}");
        }
    }

    private static bool IsSamePackage(ExtensionCurrentDocument left, ExtensionCurrentDocument right)
        => string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
           string.Equals(left.Version, right.Version, StringComparison.Ordinal) &&
           string.Equals(left.PackageSha256, right.PackageSha256, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.TrustedKeyId, right.TrustedKeyId, StringComparison.Ordinal);

    private static void RejectDifferentHashForSameVersion(
        ExtensionCurrentDocument existing,
        ExtensionCurrentDocument requested)
    {
        if (string.Equals(existing.Id, requested.Id, StringComparison.Ordinal) &&
            string.Equals(existing.Version, requested.Version, StringComparison.Ordinal) &&
            !string.Equals(existing.PackageSha256, requested.PackageSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"拒绝激活相同扩展版本 {requested.Id} {requested.Version}：现有 package SHA-256 与请求值不同。");
        }
    }

    private async Task WriteCurrentAtomicAsync(
        string path,
        ExtensionCurrentDocument document,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"无法确定扩展版本文档目录：{path}");
        EnsureExtensionDirectoryIsSafe(directory);
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(document, CurrentSerializerOptions),
                cancellationToken);

            EnsureExtensionDirectoryIsSafe(directory);
            if (File.Exists(path))
                File.Replace(temporaryPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static bool DocumentProtectsVersion(string path, string id, string version)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            var document = ExtensionCurrentParser.Parse(File.ReadAllText(path));
            return string.Equals(document.Id, id, StringComparison.Ordinal) &&
                   string.Equals(document.Version, version, StringComparison.Ordinal);
        }
        catch
        {
            // current/current.bak 无法解释时采取保守策略，避免清理掉可能仍在使用的版本。
            return true;
        }
    }

    private void ReleaseLease((string Id, string Version) key)
    {
        lock (_stateGate)
        {
            if (!_leaseCounts.TryGetValue(key, out var count))
                return;

            if (count == 1)
                _leaseCounts.Remove(key);
            else
                _leaseCounts[key] = count - 1;
        }
    }

    private sealed record ActiveExtensionVersion(ExtensionManifest Manifest, string TrustedKeyId);

    private sealed class PackageMetadata
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("sha256")]
        public required string Sha256 { get; init; }
    }
}
