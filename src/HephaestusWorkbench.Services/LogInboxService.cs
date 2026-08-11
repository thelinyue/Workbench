using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 监控一个或多个日志目录并维护内存收件箱。
/// 目录配置写入 workspace.json；旧版本仍可通过 watch_directory 键完成兼容迁移。
/// </summary>
public sealed class LogInboxService : IDisposable
{
    private readonly LogFileParser _parser;
    private readonly ArchiveValidator _validator;
    private readonly ISettingsStore? _legacySettings;
    private readonly WorkbenchConfigurationService? _configuration;
    private readonly WorkbenchLogger _logger;
    private readonly string _defaultWatchDirectory;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly List<FileSystemWatcher> _watchers = new();
    private IReadOnlyList<LogInboxItem> _items = Array.Empty<LogInboxItem>();

    // 保留旧构造函数，便于旧版调用方和单元测试平滑迁移。
    public LogInboxService(
        LogFileParser parser,
        ArchiveValidator validator,
        ISettingsStore settings,
        WorkbenchLogger logger,
        string defaultWatchDirectory)
    {
        _parser = parser;
        _validator = validator;
        _legacySettings = settings;
        _logger = logger;
        _defaultWatchDirectory = Path.GetFullPath(defaultWatchDirectory);
    }

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
    public event EventHandler? ItemsChanged;
    public event EventHandler? ConfigurationChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var configuredDirectories = await LoadDirectoriesAsync(cancellationToken);
        StopWatchers();
        WatchDirectories = NormalizeDirectories(configuredDirectories);
        _items = Array.Empty<LogInboxItem>();
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
            foreach (var directory in WatchDirectories)
            {
                if (!Directory.Exists(directory)) continue;
                foreach (var path in Directory.EnumerateFiles(directory, "*.tgz", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!_parser.TryParse(path, out var item, out _) || item is null) continue;
                    var validation = await _validator.ValidateAsync(path, cancellationToken);
                    item.IsValidArchive = validation.IsValid;
                    item.ErrorMessage = validation.Error;
                    result[Path.GetFullPath(path)] = item;
                }
            }

            _items = result.Values.OrderByDescending(x => x.LogTime).ToArray();
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

    public async Task DeleteAsync(LogInboxItem item, CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            if (File.Exists(item.FilePath)) File.Delete(item.FilePath);
        }, cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<string>> LoadDirectoriesAsync(CancellationToken cancellationToken)
    {
        if (_configuration is not null)
        {
            var workspace = await _configuration.EnsureWorkspaceAsync(cancellationToken: cancellationToken);
            return workspace.MonitorPaths;
        }

        var configuredDirectory = await _legacySettings!.GetAsync("watch_directory", cancellationToken);
        return string.IsNullOrWhiteSpace(configuredDirectory)
            ? new[] { _defaultWatchDirectory }
            : new[] { configuredDirectory! };
    }

    private Task SaveDirectoriesAsync(IReadOnlyList<string> directories, CancellationToken cancellationToken)
    {
        if (_configuration is not null)
        {
            return _configuration.SaveWorkspaceAsync(new HephaestusWorkbench.Core.Models.WorkspaceConfig
            {
                DataPath = _configuration.DataRoot,
                MonitorPaths = directories.ToList()
            }, cancellationToken);
        }

        return _legacySettings!.SetAsync("watch_directory", directories[0], cancellationToken);
    }

    private void StartWatcher(string directory)
    {
        var watcher = new FileSystemWatcher(directory, "*.tgz")
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
