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
