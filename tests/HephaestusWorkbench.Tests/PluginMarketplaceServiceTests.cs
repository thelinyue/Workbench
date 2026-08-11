using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class PluginMarketplaceServiceTests
{
    [Fact]
    public async Task RefreshAsync_UsesLastValidCacheWhenNetworkFails()
    {
        await WithServiceAsync(async context =>
        {
            context.Handler.Response = JsonResponse(CatalogJson("0".PadLeft(64, 'a'), 1));
            var online = await context.Service.RefreshAsync();
            Assert.False(online.IsFromCache);
            Assert.True(File.Exists(context.Paths.MarketplaceCatalogCacheFile));

            context.Handler.Exception = new HttpRequestException("模拟断网");
            var cached = await context.Service.RefreshAsync();
            Assert.True(cached.IsFromCache);
            Assert.Single(cached.Plugins);
            Assert.Contains("缓存", cached.Warning);
        });
    }

    [Fact]
    public async Task InstallAsync_VerifiesPackageAndRegistersMarketplacePlugin()
    {
        await WithServiceAsync(async context =>
        {
            var package = CreatePackage("sample", "1.0", "sample.exe", "plugin");
            context.Handler.Response = BinaryResponse(package);
            var item = Item("sample", "1.0", package);

            await context.Service.InstallOrUpdateAsync(item);

            Assert.True(File.Exists(Path.Combine(context.Paths.PluginsDirectory, "sample", "sample.exe")));
            var config = await context.Configuration.EnsurePluginConfigAsync();
            var registered = Assert.Single(config.Plugins);
            Assert.Equal(PluginInstallSource.Marketplace, registered.Source);
            Assert.Equal("sample", config.DefaultPluginId);
        });
    }

    [Fact]
    public async Task InstallAsync_RejectsWrongHashWithoutChangingPlugins()
    {
        await WithServiceAsync(async context =>
        {
            var package = CreatePackage("sample", "1.0", "sample.exe", "plugin");
            context.Handler.Response = BinaryResponse(package);
            var item = Item("sample", "1.0", package) with { Sha256 = new string('0', 64) };

            var error = await Assert.ThrowsAsync<InvalidDataException>(() => context.Service.InstallOrUpdateAsync(item));

            Assert.Contains("SHA-256", error.Message);
            Assert.False(Directory.Exists(Path.Combine(context.Paths.PluginsDirectory, "sample")));
        });
    }

    [Fact]
    public async Task InstallAsync_RejectsZipTraversal()
    {
        await WithServiceAsync(async context =>
        {
            byte[] package;
            using (var memory = new MemoryStream())
            {
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
                {
                    WriteEntry(zip, "manifest.json", "{\"id\":\"sample\",\"name\":\"测试\",\"version\":\"1.0\",\"type\":\"Exe\",\"entry\":\"sample.exe\"}");
                    WriteEntry(zip, "sample.exe", "plugin");
                    WriteEntry(zip, "../outside.txt", "bad");
                }
                package = memory.ToArray();
            }
            context.Handler.Response = BinaryResponse(package);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() => context.Service.InstallOrUpdateAsync(Item("sample", "1.0", package)));

            Assert.Contains("越界路径", error.Message);
            Assert.False(File.Exists(Path.Combine(context.Paths.PluginsDirectory, "outside.txt")));
        });
    }

    [Fact]
    public async Task DefaultPlugin_CannotBeDisabledOrUninstalled()
    {
        await WithServiceAsync(async context =>
        {
            var package = CreatePackage("sample", "1.0", "sample.exe", "plugin");
            context.Handler.Response = BinaryResponse(package);
            await context.Service.InstallOrUpdateAsync(Item("sample", "1.0", package));

            await Assert.ThrowsAsync<InvalidOperationException>(() => context.Service.SetEnabledAsync("sample", false));
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.Service.UninstallAsync("sample"));
        });
    }

    private static async Task WithServiceAsync(Func<TestContext, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var paths = new DataPaths(root);
        paths.EnsureCreated();
        try
        {
            var logger = new WorkbenchLogger(root);
            var catalog = new PluginCatalog(paths, logger);
            var configuration = new WorkbenchConfigurationService(paths);
            var handler = new StubHandler();
            var service = new PluginMarketplaceService(paths, catalog, configuration, new TaskCenter(new EmptyTaskRepository()), logger, httpClient: new HttpClient(handler));
            await action(new TestContext(paths, configuration, handler, service));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static MarketplacePlugin Item(string id, string version, byte[] package) => new()
    {
        Id = id,
        Name = "测试插件",
        Version = version,
        Type = PluginType.Exe,
        PackageUrl = "https://github.com/example/plugin.zip",
        Sha256 = Convert.ToHexString(SHA256.HashData(package)),
        PackageSize = package.LongLength,
        MinimumAppVersion = "1.1.0"
    };

    private static byte[] CreatePackage(string id, string version, string entry, string content)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "manifest.json", $"{{\"id\":\"{id}\",\"name\":\"测试插件\",\"version\":\"{version}\",\"type\":\"Exe\",\"entry\":\"{entry}\"}}");
            WriteEntry(zip, entry, content);
        }
        return memory.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static HttpResponseMessage BinaryResponse(byte[] value) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(value) };
    private static HttpResponseMessage JsonResponse(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static string CatalogJson(string hash, long size) => $$"""
        { "schemaVersion": 1, "plugins": [ { "id": "sample", "name": "测试插件", "description": "测试", "version": "1.0", "type": "Exe", "packageUrl": "https://github.com/example/plugin.zip", "sha256": "{{hash}}", "packageSize": {{size}}, "minimumAppVersion": "1.1.0" } ] }
        """;

    private sealed record TestContext(DataPaths Paths, WorkbenchConfigurationService Configuration, StubHandler Handler, PluginMarketplaceService Service);

    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.NotFound);
        public Exception? Exception { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Exception is not null) return Task.FromException<HttpResponseMessage>(Exception);
            Response.RequestMessage = request;
            return Task.FromResult(Response);
        }
    }

    private sealed class EmptyTaskRepository : IAnalysisTaskRepository
    {
        public Task<IReadOnlyList<AnalysisTask>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AnalysisTask>>(Array.Empty<AnalysisTask>());
        public Task<AnalysisTask?> GetAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<AnalysisTask?>(null);
        public Task InsertAsync(AnalysisTask item, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(AnalysisTask item, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
