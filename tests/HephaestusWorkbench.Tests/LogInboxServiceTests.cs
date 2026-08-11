using HephaestusWorkbench.Core.Repositories;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class LogInboxServiceTests
{
    [Fact]
    public async Task StartAsync_UsesDataInboxWhenNoManualDirectoryIsSaved()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var paths = new HephaestusWorkbench.Data.DataPaths(root);
        paths.EnsureCreated();
        var settings = new MemorySettingsStore();

        try
        {
            using var service = new LogInboxService(
                new LogFileParser(),
                new ArchiveValidator(),
                settings,
                new WorkbenchLogger(root),
                paths.InboxDirectory);

            await service.StartAsync();

            Assert.Equal(paths.InboxDirectory, service.WatchDirectory);
            Assert.True(service.IsConfigured);
            Assert.True(service.IsUsingDefaultDirectory);
            Assert.Empty(service.Items);
            Assert.Null(await settings.GetAsync("watch_directory"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SetWatchDirectoryAsync_SwitchesFromDefaultToManualDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var paths = new HephaestusWorkbench.Data.DataPaths(root);
        paths.EnsureCreated();
        var manualDirectory = Path.Combine(root, "ExternalInbox");
        var settings = new MemorySettingsStore();

        try
        {
            using var service = new LogInboxService(
                new LogFileParser(),
                new ArchiveValidator(),
                settings,
                new WorkbenchLogger(root),
                paths.InboxDirectory);

            await service.StartAsync();
            await service.SetWatchDirectoryAsync(manualDirectory);

            Assert.Equal(Path.GetFullPath(manualDirectory), service.WatchDirectory);
            Assert.False(service.IsUsingDefaultDirectory);
            Assert.True(Directory.Exists(manualDirectory));
            Assert.Equal(Path.GetFullPath(manualDirectory), await settings.GetAsync("watch_directory"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SetWatchDirectoriesAsync_AggregatesValidLogsFromMultipleDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var paths = new HephaestusWorkbench.Data.DataPaths(root);
        paths.EnsureCreated();
        var first = Path.Combine(root, "First");
        var second = Path.Combine(root, "Second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        await WriteValidArchiveAsync(Path.Combine(first, "Demidiag_H43001J59E003A8E_2608111426.tgz"));
        await WriteValidArchiveAsync(Path.Combine(second, "diag_H43001J59E003A8E_2608111403.tgz"));

        try
        {
            var configuration = new WorkbenchConfigurationService(paths);
            using var service = new LogInboxService(
                new LogFileParser(),
                new ArchiveValidator(),
                configuration,
                new WorkbenchLogger(root),
                paths.InboxDirectory);

            await service.SetWatchDirectoriesAsync(new[] { first, second });

            Assert.Equal(2, service.WatchDirectories.Count);
            Assert.Equal(2, service.Items.Count);
            Assert.All(service.Items, x => Assert.Equal("H43001J59E003A8E", x.DeviceId));
            Assert.Contains(service.Items, x => x.LogTime == new DateTime(2026, 8, 11, 14, 26, 0));
            Assert.Contains(service.Items, x => x.LogTime == new DateTime(2026, 8, 11, 14, 3, 0));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InspectFileAsync_ValidatesLogOutsideWatchDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var paths = new HephaestusWorkbench.Data.DataPaths(root);
        paths.EnsureCreated();
        var selectedPath = Path.Combine(root, "Downloads", "diag_DEVICE01_2608111530.tgz");
        Directory.CreateDirectory(Path.GetDirectoryName(selectedPath)!);
        await WriteValidArchiveAsync(selectedPath);

        try
        {
            using var service = new LogInboxService(
                new LogFileParser(),
                new ArchiveValidator(),
                new MemorySettingsStore(),
                new WorkbenchLogger(root),
                paths.InboxDirectory);

            var result = await service.InspectFileAsync(selectedPath);

            Assert.True(result.IsValid);
            Assert.NotNull(result.Item);
            Assert.Equal(Path.GetFullPath(selectedPath), result.Item.FilePath);
            Assert.Equal("DEVICE01", result.Item.DeviceId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InspectFileAsync_ReturnsChineseErrorsForUnsupportedMissingAndCorruptFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var paths = new HephaestusWorkbench.Data.DataPaths(root);
        paths.EnsureCreated();
        var corruptPath = Path.Combine(root, "diag_DEVICE01_2608111530.tgz");
        await File.WriteAllTextAsync(corruptPath, "not-a-tar-gzip");

        try
        {
            using var service = new LogInboxService(
                new LogFileParser(),
                new ArchiveValidator(),
                new MemorySettingsStore(),
                new WorkbenchLogger(root),
                paths.InboxDirectory);

            var unsupported = await service.InspectFileAsync(Path.Combine(root, "diag_DEVICE01_2608111530.zip"));
            var missing = await service.InspectFileAsync(Path.Combine(root, "diag_DEVICE01_2608111531.tgz"));
            var corrupt = await service.InspectFileAsync(corruptPath);

            Assert.Contains("只支持", unsupported.ErrorMessage);
            Assert.Contains("不存在", missing.ErrorMessage);
            Assert.Contains("损坏或无法读取", corrupt.ErrorMessage);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RefreshAsync_CountsAndKeepsUnrecognizedLogsForInboxReview()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var paths = new HephaestusWorkbench.Data.DataPaths(root);
        paths.EnsureCreated();
        var invalidPath = Path.Combine(paths.InboxDirectory, "bad-name.tgz");
        await File.WriteAllTextAsync(invalidPath, "invalid");

        try
        {
            using var service = new LogInboxService(
                new LogFileParser(),
                new ArchiveValidator(),
                new MemorySettingsStore(),
                new WorkbenchLogger(root),
                paths.InboxDirectory);

            await service.StartAsync();

            Assert.Equal(1, service.InvalidItemCount);
            var item = Assert.Single(service.Items);
            Assert.False(item.IsValidArchive);
            Assert.Equal("bad-name.tgz", item.FileName);
            Assert.Contains("文件名不符合", item.ErrorMessage);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteAsync_RemovesOnlyOriginalLogAndKeepsAnalysisArtifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var paths = new HephaestusWorkbench.Data.DataPaths(root);
        paths.EnsureCreated();
        var source = Path.Combine(paths.InboxDirectory, "diag_DEVICE01_2608111530.tgz");
        var extractMarker = Path.Combine(root, "Cases", "case-1", "Extract", "keep.txt");
        var reportMarker = Path.Combine(root, "Reports", "case-1", "keep.html");
        Directory.CreateDirectory(Path.GetDirectoryName(extractMarker)!);
        Directory.CreateDirectory(Path.GetDirectoryName(reportMarker)!);
        await WriteValidArchiveAsync(source);
        await File.WriteAllTextAsync(extractMarker, "extract");
        await File.WriteAllTextAsync(reportMarker, "report");

        try
        {
            using var service = new LogInboxService(
                new LogFileParser(),
                new ArchiveValidator(),
                new MemorySettingsStore(),
                new WorkbenchLogger(root),
                paths.InboxDirectory);
            await service.StartAsync();

            await service.DeleteAsync(Assert.Single(service.Items));

            Assert.False(File.Exists(source));
            Assert.True(File.Exists(extractMarker));
            Assert.True(File.Exists(reportMarker));
            Assert.Empty(service.Items);
            var log = await File.ReadAllTextAsync(Path.Combine(root, "Logs", "workbench.log"));
            Assert.Contains("删除完成：原始日志文件", log);
            Assert.Contains("未删除案例、解压目录或分析报告", log);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteAsync_MissingOriginalLogWritesChineseSkipMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var paths = new HephaestusWorkbench.Data.DataPaths(root);
        paths.EnsureCreated();
        var missing = Path.Combine(paths.InboxDirectory, "diag_DEVICE01_2608111530.tgz");

        try
        {
            using var service = new LogInboxService(
                new LogFileParser(),
                new ArchiveValidator(),
                new MemorySettingsStore(),
                new WorkbenchLogger(root),
                paths.InboxDirectory);
            await service.StartAsync();

            await service.DeleteAsync(new HephaestusWorkbench.Core.Models.LogInboxItem
            {
                FilePath = missing,
                FileName = Path.GetFileName(missing),
                DeviceId = "DEVICE01"
            });

            Assert.Empty(service.Items);
            var log = await File.ReadAllTextAsync(Path.Combine(root, "Logs", "workbench.log"));
            Assert.Contains("删除原始日志文件跳过：文件不存在", log);
            Assert.Contains(missing, log);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WriteValidArchiveAsync(string path)
    {
        await using var file = File.Create(path);
        await using var gzip = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: false);
        using var tar = new TarWriter(gzip, leaveOpen: false);
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "log.txt")
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes("test"))
        });
    }

    private sealed class MemorySettingsStore : ISettingsStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(_values.GetValueOrDefault(key));

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }
}
