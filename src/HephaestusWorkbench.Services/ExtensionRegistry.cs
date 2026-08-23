using System.Text.Json;
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

    private readonly string _extensionsRoot;
    private readonly IExtensionHealthChecker _healthChecker;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly Dictionary<string, ExtensionManifest> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Id, string Version), int> _leaseCounts = new();
    private readonly List<string> _issues = new();

    public ExtensionRegistry(string extensionsRoot, IExtensionHealthChecker healthChecker)
    {
        if (string.IsNullOrWhiteSpace(extensionsRoot))
            throw new ArgumentException("扩展目录根路径不能为空。", nameof(extensionsRoot));

        _extensionsRoot = Path.GetFullPath(extensionsRoot);
        _healthChecker = healthChecker ?? throw new ArgumentNullException(nameof(healthChecker));
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
            var active = new Dictionary<string, ExtensionManifest>(StringComparer.Ordinal);
            var issues = new List<string>();

            if (Directory.Exists(_extensionsRoot))
            {
                foreach (var extensionDirectory in Directory
                             .EnumerateDirectories(_extensionsRoot, "*", SearchOption.TopDirectoryOnly)
                             .OrderBy(path => path, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var currentPath = Path.Combine(extensionDirectory, "current.json");
                    if (!File.Exists(currentPath))
                        continue;

                    try
                    {
                        var current = await ReadCurrentAsync(currentPath, cancellationToken);
                        ValidateDirectoryIdentity(extensionDirectory, current);

                        ExtensionManifest manifest;
                        if (current.State == ExtensionActivationState.Pending)
                        {
                            var backupPath = Path.Combine(extensionDirectory, "current.json.bak");
                            try
                            {
                                var backup = await ReadCurrentAsync(backupPath, cancellationToken);
                                ValidateDirectoryIdentity(extensionDirectory, backup);
                                if (backup.State != ExtensionActivationState.Healthy)
                                    throw new InvalidOperationException("回滚文档不是 healthy 状态。");

                                manifest = await ReadMatchingManifestAsync(extensionDirectory, backup, cancellationToken);
                                await WriteCurrentAtomicAsync(currentPath, backup, cancellationToken);
                            }
                            catch (Exception exception) when (exception is not OperationCanceledException)
                            {
                                issues.Add($"扩展 {current.Id} 的 current.json 处于待验证状态，但没有可用的健康回滚版本：{exception.Message}");
                                continue;
                            }
                        }
                        else
                        {
                            manifest = await ReadMatchingManifestAsync(extensionDirectory, current, cancellationToken);
                        }

                        active[manifest.Id] = manifest;
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
                return _active.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// 激活已落盘的版本：先保存旧 current.json 为 backup，再写 pending 并执行正式加载验证；
    /// 验证成功写 healthy，任一步失败则恢复原健康版本。
    /// </summary>
    public async Task<ExtensionManifest> ActivateAsync(
        string id,
        string version,
        string packageSha256,
        CancellationToken cancellationToken = default)
    {
        var pending = ValidateRequestedCurrent(id, version, packageSha256);
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var extensionDirectory = Path.Combine(_extensionsRoot, pending.Id);
            var currentPath = Path.Combine(extensionDirectory, "current.json");
            var backupPath = Path.Combine(extensionDirectory, "current.json.bak");
            var candidate = await ReadMatchingManifestAsync(extensionDirectory, pending, cancellationToken);

            ExtensionCurrentDocument? rollback = null;
            ExtensionManifest? rollbackManifest = null;
            if (File.Exists(currentPath))
            {
                var existing = await ReadCurrentAsync(currentPath, cancellationToken);
                ValidateDirectoryIdentity(extensionDirectory, existing);
                RejectDifferentHashForSameVersion(existing, pending);
                if (existing.State != ExtensionActivationState.Healthy)
                    throw new InvalidOperationException($"扩展 {pending.Id} 当前仍处于待验证状态，不能开始新的激活事务。");

                rollbackManifest = await ReadMatchingManifestAsync(extensionDirectory, existing, cancellationToken);
                rollback = existing;
            }

            if (File.Exists(backupPath))
            {
                var existingBackup = await ReadCurrentAsync(backupPath, cancellationToken);
                ValidateDirectoryIdentity(extensionDirectory, existingBackup);
                RejectDifferentHashForSameVersion(existingBackup, pending);
                if (rollback is null && existingBackup.State == ExtensionActivationState.Healthy)
                {
                    rollbackManifest = await ReadMatchingManifestAsync(extensionDirectory, existingBackup, cancellationToken);
                    rollback = existingBackup;
                }
            }

            if (rollback is not null && File.Exists(currentPath))
                await WriteCurrentAtomicAsync(backupPath, rollback, cancellationToken);

            try
            {
                await WriteCurrentAtomicAsync(currentPath, pending, cancellationToken);
                await _healthChecker.CheckAsync(candidate, cancellationToken);

                var healthy = CreateCurrent(
                    pending.Id,
                    pending.Version,
                    pending.PackageSha256,
                    ExtensionActivationState.Healthy);
                await WriteCurrentAtomicAsync(currentPath, healthy, cancellationToken);
                lock (_stateGate)
                    _active[pending.Id] = candidate;
                return candidate;
            }
            catch (Exception activationException)
            {
                try
                {
                    if (rollback is not null)
                    {
                        await WriteCurrentAtomicAsync(currentPath, rollback, CancellationToken.None);
                        if (rollbackManifest is not null)
                        {
                            lock (_stateGate)
                                _active[pending.Id] = rollbackManifest;
                        }
                    }
                    else
                    {
                        if (File.Exists(currentPath))
                            File.Delete(currentPath);
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

                throw new InvalidOperationException(
                    $"扩展 {pending.Id} {pending.Version} 激活失败，已恢复回滚版本：{activationException.Message}",
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
            if (!_active.TryGetValue(id, out var manifest))
                throw new InvalidOperationException($"扩展 {id} 当前没有可租用的 healthy 版本。");

            var key = (manifest.Id, manifest.Version);
            _leaseCounts.TryGetValue(key, out var count);
            _leaseCounts[key] = count + 1;
            return new ExtensionVersionLease(manifest, () => ReleaseLease(key));
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
        return !DocumentProtectsVersion(Path.Combine(extensionDirectory, "current.json"), id, version) &&
               !DocumentProtectsVersion(Path.Combine(extensionDirectory, "current.json.bak"), id, version);
    }

    private static ExtensionCurrentDocument ValidateRequestedCurrent(string id, string version, string packageSha256)
    {
        var document = CreateCurrent(id, version, packageSha256, ExtensionActivationState.Pending);
        return ExtensionCurrentParser.Parse(JsonSerializer.Serialize(document));
    }

    private static ExtensionCurrentDocument CreateCurrent(
        string id,
        string version,
        string packageSha256,
        ExtensionActivationState state)
        => new()
        {
            SchemaVersion = 2,
            Id = id,
            Version = version,
            PackageSha256 = packageSha256,
            State = state
        };

    private static async Task<ExtensionCurrentDocument> ReadCurrentAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"扩展版本文档不存在：{path}", path);

        return ExtensionCurrentParser.Parse(await File.ReadAllTextAsync(path, cancellationToken));
    }

    private static async Task<ExtensionManifest> ReadMatchingManifestAsync(
        string extensionDirectory,
        ExtensionCurrentDocument current,
        CancellationToken cancellationToken)
    {
        if (current.State != ExtensionActivationState.Healthy && current.State != ExtensionActivationState.Pending)
            throw new InvalidOperationException($"扩展 {current.Id} 的激活状态无效。");

        var versionDirectory = Path.Combine(extensionDirectory, current.Version);
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

    private static void ValidateDirectoryIdentity(string extensionDirectory, ExtensionCurrentDocument current)
    {
        var directoryId = Path.GetFileName(extensionDirectory);
        if (!string.Equals(directoryId, current.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"扩展目录名 {directoryId} 与 current.json id {current.Id} 不匹配。");
        }
    }

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

    private static async Task WriteCurrentAtomicAsync(
        string path,
        ExtensionCurrentDocument document,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"无法确定扩展版本文档目录：{path}");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(document, CurrentSerializerOptions),
                cancellationToken);

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
}
