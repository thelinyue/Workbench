using HephaestusWorkbench.Data;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 创建全新的 v2 工作区。数据库和配置先写入目标目录同盘的宿主 staging，全部成功后再提交；
/// 这样数据库阶段后的失败或进程中断不会把目标目录变成无法重试的伪 Legacy。
/// Bundled Extension 必须在后续启动阶段使用与在线安装完全相同的验签、安装和激活事务。
/// </summary>
public sealed class WorkbenchInitializationService
{
    private const string StagingMarkerFileName = ".hephaestus-workbench-initialization";
    private const string StagingMarkerContent = "HephaestusWorkbench.WorkspaceInitialization.v2";

    public async Task InitializeAsync(
        string dataRoot,
        IEnumerable<string>? monitorPaths = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dataRoot)) throw new ArgumentException("数据目录不能为空。", nameof(dataRoot));

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)) + Path.DirectorySeparatorChar;
        var programRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)) + Path.DirectorySeparatorChar;
        if (normalizedRoot.StartsWith(programRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("数据目录不能位于程序安装目录中。");

        // 向导允许用户改选目录，因此必须在任何目录、日志、staging 或数据库写入前再次执行 v2 门禁。
        var inspection = await new WorkspaceVersionGate().InspectAsync(dataRoot, cancellationToken);
        if (inspection.Status == WorkspaceVersionStatus.Legacy)
            throw new InvalidOperationException($"所选数据目录包含旧版本或无法确认版本的数据，请手工清理后重试：{inspection.DataRoot}");

        var targetRoot = inspection.DataRoot;
        var targetParent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(targetRoot));
        if (string.IsNullOrWhiteSpace(targetParent))
            throw new InvalidOperationException("数据目录必须有可用的上级目录，无法在同盘创建初始化临时目录。");

        try
        {
            Directory.CreateDirectory(targetParent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"无法准备数据目录的上级目录：{ex.Message}", ex);
        }

        var stagingPrefix = GetStagingPrefix(targetRoot);
        CleanupStaleStaging(targetParent, stagingPrefix);

        if (inspection.Status == WorkspaceVersionStatus.Ready)
        {
            TryDeleteCommittedMarker(targetRoot);
            progress?.Report("初始化完成。");
            return;
        }

        var stagingRoot = Path.Combine(targetParent, stagingPrefix + Guid.NewGuid().ToString("N"));
        WorkbenchLogger? logger = null;
        var stagingCreated = false;

        try
        {
            Directory.CreateDirectory(stagingRoot);
            stagingCreated = true;
            await File.WriteAllTextAsync(
                Path.Combine(stagingRoot, StagingMarkerFileName),
                StagingMarkerContent,
                cancellationToken);

            var paths = new DataPaths(targetRoot, stagingRoot);
            paths.EnsureCreated();
            logger = new WorkbenchLogger(paths.StorageRoot);

            progress?.Report("正在初始化目录…");
            logger.Info("开始在同盘临时目录初始化工作台。");

            var factory = new SqliteConnectionFactory(paths);
            progress?.Report("正在初始化数据库…");
            await new DatabaseInitializer(factory).InitializeAsync(cancellationToken);

            var configuredMonitorPaths = monitorPaths?.ToArray();
            if (configuredMonitorPaths is null || configuredMonitorPaths.Length == 0)
                configuredMonitorPaths = new[] { Path.Combine(targetRoot, "Inbox") };

            var configuration = new WorkbenchConfigurationService(paths);
            progress?.Report("正在写入工作区配置…");
            await configuration.EnsureWorkspaceAsync(configuredMonitorPaths, cancellationToken);
            await configuration.EnsureAppSettingsAsync(cancellationToken);
            await new ExtensionSettingsStore(paths).EnsureAsync(cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            logger.Info("工作台临时目录初始化完成，正在提交到目标目录。");
            CommitStaging(stagingRoot, targetRoot);
            stagingCreated = false;
            TryDeleteCommittedMarker(targetRoot);

            progress?.Report("初始化完成。");
        }
        catch (OperationCanceledException ex)
        {
            TryLogError(logger, "工作台初始化已取消", ex);
            if (stagingCreated) TryDeleteCurrentStaging(stagingRoot);
            throw new OperationCanceledException("工作台初始化已取消。", ex, cancellationToken);
        }
        catch (Exception ex)
        {
            TryLogError(logger, "工作台初始化失败", ex);
            if (stagingCreated) TryDeleteCurrentStaging(stagingRoot);
            throw new InvalidOperationException($"工作台初始化失败：{ex.Message}", ex);
        }
    }

    private static string GetStagingPrefix(string targetRoot)
    {
        var targetName = Path.GetFileName(Path.TrimEndingDirectorySeparator(targetRoot));
        return $".{targetName}.hephaestus-init-";
    }

    /// <summary>
    /// 只清理带有精确宿主标记且不是重解析点的遗留 staging；同名前缀但来源不明的目录保持原样。
    /// </summary>
    private static void CleanupStaleStaging(string targetParent, string stagingPrefix)
    {
        try
        {
            foreach (var stagingRoot in Directory.EnumerateDirectories(targetParent, stagingPrefix + "*"))
            {
                if (IsConfirmedHostStaging(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"无法检查或清理工作台初始化临时目录：{ex.Message}", ex);
        }
    }

    private static bool IsConfirmedHostStaging(string stagingRoot)
    {
        try
        {
            if ((File.GetAttributes(stagingRoot) & FileAttributes.ReparsePoint) != 0) return false;
            var marker = Path.Combine(stagingRoot, StagingMarkerFileName);
            return File.Exists(marker)
                && string.Equals(File.ReadAllText(marker), StagingMarkerContent, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 提交前再次确认目标仍为空。用户预先创建的空目录会先移除再用同盘目录移动替换；
    /// 若移动失败则尽力恢复原来的空目录，不迁移、备份或删除任何非空数据。
    /// </summary>
    private static void CommitStaging(string stagingRoot, string targetRoot)
    {
        var removedEmptyTarget = false;
        if (Directory.Exists(targetRoot))
        {
            if (Directory.EnumerateFileSystemEntries(targetRoot).Any())
                throw new InvalidOperationException($"初始化期间目标目录出现了新文件，已停止提交且未改动这些文件：{targetRoot}");

            Directory.Delete(targetRoot, recursive: false);
            removedEmptyTarget = true;
        }

        try
        {
            Directory.Move(stagingRoot, targetRoot);
        }
        catch
        {
            if (removedEmptyTarget && Directory.Exists(targetRoot) is false)
            {
                try
                {
                    Directory.CreateDirectory(targetRoot);
                }
                catch
                {
                    // 保留原始提交异常；恢复空目录失败时不能掩盖真正原因。
                }
            }
            throw;
        }
    }

    private static void TryDeleteCurrentStaging(string stagingRoot)
    {
        try
        {
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
        }
        catch
        {
            // 清理失败不能掩盖初始化或取消的原始错误；带宿主标记的目录会在下次调用时再次清理。
        }
    }

    private static void TryDeleteCommittedMarker(string targetRoot)
    {
        try
        {
            var marker = Path.Combine(targetRoot, StagingMarkerFileName);
            if (File.Exists(marker)
                && string.Equals(File.ReadAllText(marker), StagingMarkerContent, StringComparison.Ordinal))
            {
                File.Delete(marker);
            }
        }
        catch
        {
            // 同盘移动已经完成，标记清理失败不应把一个完整的 v2 工作区报告为初始化失败。
        }
    }

    private static void TryLogError(WorkbenchLogger? logger, string message, Exception exception)
    {
        try
        {
            logger?.Error(message, exception);
        }
        catch
        {
            // 日志写入失败不能覆盖真正的初始化错误。
        }
    }
}
