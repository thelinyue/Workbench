using HephaestusWorkbench.Core.Models;

namespace HephaestusWorkbench.Services;

/// <summary>单个日志文件的检查结果，供监控目录之外的快捷分析入口复用收件箱校验规则。</summary>
public sealed record LogFileInspectionResult(LogInboxItem? Item, string? ErrorMessage)
{
    public bool IsValid => Item is { IsValidArchive: true };
}

/// <summary>
/// 监控一个或多个日志目录并维护内存收件箱。
/// 监控目录通过 schema v2 workspace.json 持久化，服务不访问 SQLite 设置。
/// </summary>
public sealed class LogInboxService : IDisposable
{
    private readonly LogFileParser _parser;
    private readonly ArchiveValidator _validator;
    private readonly WorkbenchConfigurationService _configuration;
    private readonly WorkbenchLogger _logger;
    private readonly string _defaultWatchDirectory;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly List<FileSystemWatcher> _watchers = new();
    private IReadOnlyList<LogInboxItem> _items = Array.Empty<LogInboxItem>();
    private int _invalidItemCount;

    public LogInboxService(
        LogFileParser parser,
        ArchiveValidator validator,
        WorkbenchConfigurationService configuration,
        WorkbenchLogger logger,
        string defaultWatchDirectory)
    {
        _parser = parser;
        _validator = validator;
        _configuration = configuration;
        _logger = logger;
        _defaultWatchDirectory = Path.GetFullPath(defaultWatchDirectory);
    }

    public string WatchDirectory => WatchDirectories.FirstOrDefault() ?? string.Empty;
    public IReadOnlyList<string> WatchDirectories { get; private set; } = Array.Empty<string>();
    public bool IsConfigured => WatchDirectories.Count > 0;
    public bool IsUsingDefaultDirectory => WatchDirectories.Count == 1
        && string.Equals(WatchDirectory, _defaultWatchDirectory, StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<LogInboxItem> Items => _items;
    public int InvalidItemCount => _invalidItemCount;
    public event EventHandler? ItemsChanged;
    public event EventHandler? ConfigurationChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var configuredDirectories = await LoadDirectoriesAsync(cancellationToken);
        StopWatchers();
        WatchDirectories = NormalizeDirectories(configuredDirectories);
        _items = Array.Empty<LogInboxItem>();
        _invalidItemCount = 0;
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);

        if (!IsConfigured)
        {
            ItemsChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        foreach (var directory in WatchDirectories)
        {
            try
            {
                Directory.CreateDirectory(directory);
                StartWatcher(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                _logger.Error($"启动日志目录监控失败：{directory}", ex);
            }
        }

        await RefreshAsync(cancellationToken);
    }

    public Task SetWatchDirectoryAsync(string directory, CancellationToken cancellationToken = default)
        => SetWatchDirectoriesAsync(new[] { directory }, cancellationToken);

    public async Task SetWatchDirectoriesAsync(IEnumerable<string> directories, CancellationToken cancellationToken = default)
    {
        var normalizedDirectories = NormalizeDirectories(directories);
        if (normalizedDirectories.Count == 0) throw new ArgumentException("至少需要一个日志监控目录。", nameof(directories));

        foreach (var directory in normalizedDirectories) Directory.CreateDirectory(directory);
        await SaveDirectoriesAsync(normalizedDirectories, cancellationToken);
        StopWatchers();
        WatchDirectories = normalizedDirectories;
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        foreach (var directory in WatchDirectories) StartWatcher(directory);
        await RefreshAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LogInboxItem>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _items = Array.Empty<LogInboxItem>();
            ItemsChanged?.Invoke(this, EventArgs.Empty);
            return _items;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var result = new Dictionary<string, LogInboxItem>(StringComparer.OrdinalIgnoreCase);
            var invalidItemCount = 0;
            foreach (var directory in WatchDirectories)
            {
                if (!Directory.Exists(directory)) continue;
                foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!LogFileParser.HasSupportedExtension(Path.GetFileName(path))) continue;
                    if (!_parser.TryParse(path, out var item, out var parseError) || item is null)
                    {
                        invalidItemCount++;
                        var info = new FileInfo(path);
                        result[Path.GetFullPath(path)] = new LogInboxItem
                        {
                            FilePath = info.FullName,
                            FileName = info.Name,
                            DeviceId = "无法识别",
                            LogTime = info.LastWriteTime,
                            FileSize = info.Exists ? info.Length : 0,
                            IsValidArchive = false,
                            ErrorMessage = parseError ?? "日志文件名无法识别。"
                        };
                        continue;
                    }
                    var validation = await _validator.ValidateAsync(path, cancellationToken);
                    item.IsValidArchive = validation.IsValid;
                    item.ErrorMessage = validation.Error;
                    if (!validation.IsValid) invalidItemCount++;
                    result[Path.GetFullPath(path)] = item;
                }
            }

            _items = result.Values.OrderByDescending(x => x.LogTime).ToArray();
            _invalidItemCount = invalidItemCount;
            ItemsChanged?.Invoke(this, EventArgs.Empty);
            return _items;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error("扫描日志目录失败", ex);
            return _items;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// 检查任意位置的单个日志文件，不修改监控目录，也不复制或接管文件。
    /// 通过校验后，调用方可把返回的项目交给 CaseAnalysisService 原地分析。
    /// </summary>
    public async Task<LogFileInspectionResult> InspectFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new LogFileInspectionResult(null, "请选择一个日志文件。");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new LogFileInspectionResult(null, $"日志文件路径无效：{ex.Message}");
        }

        if (!LogFileParser.IsSupportedFileName(Path.GetFileName(fullPath)))
            return new LogFileInspectionResult(null, "只支持选择 .tgz 或 .tgz.temp 日志压缩包。");
        if (!File.Exists(fullPath))
            return new LogFileInspectionResult(null, $"日志文件不存在：{fullPath}");
        if (!_parser.TryParse(fullPath, out var item, out var parseError) || item is null)
            return new LogFileInspectionResult(null, parseError ?? "日志文件名无法识别。");

        var validation = await _validator.ValidateAsync(fullPath, cancellationToken);
        item.IsValidArchive = validation.IsValid;
        item.ErrorMessage = validation.Error;
        return new LogFileInspectionResult(item, validation.Error);
    }

    public async Task DeleteAsync(LogInboxItem item, CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            if (!File.Exists(item.FilePath))
            {
                _logger.Info($"删除原始日志文件跳过：文件不存在，{item.FilePath}");
                return;
            }

            File.Delete(item.FilePath);
            _logger.Info($"删除完成：原始日志文件，{item.FilePath}；未删除案例、解压目录或分析报告");
        }, cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<string>> LoadDirectoriesAsync(CancellationToken cancellationToken)
    {
        var workspace = await _configuration.EnsureWorkspaceAsync(cancellationToken: cancellationToken);
        return workspace.MonitorPaths;
    }

    private Task SaveDirectoriesAsync(IReadOnlyList<string> directories, CancellationToken cancellationToken)
    {
        return _configuration.SaveWorkspaceAsync(new WorkspaceConfig
        {
            DataPath = _configuration.DataRoot,
            MonitorPaths = directories.ToList()
        }, cancellationToken);
    }

    private void StartWatcher(string directory)
    {
        var watcher = new FileSystemWatcher(directory, "*")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        watcher.Created += OnFileChanged;
        watcher.Changed += OnFileChanged;
        watcher.Renamed += OnFileRenamed;
        _watchers.Add(watcher);
    }

    private async void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!LogFileParser.HasSupportedExtension(Path.GetFileName(e.FullPath))) return;
        await WaitForStableAsync(e.FullPath);
        try { await RefreshAsync(); } catch (Exception ex) { _logger.Error("日志变化刷新失败", ex); }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e) => OnFileChanged(sender, e);

    private static async Task WaitForStableAsync(string path)
    {
        long previous = -1;
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(300);
            try
            {
                var current = new FileInfo(path).Length;
                if (current == previous) return;
                previous = current;
            }
            catch (IOException) { }
        }
    }

    private void StopWatchers()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
    }

    private static IReadOnlyList<string> NormalizeDirectories(IEnumerable<string> directories)
        => directories
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => Path.GetFullPath(x.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public void Dispose()
    {
        StopWatchers();
        _refreshLock.Dispose();
    }
}
