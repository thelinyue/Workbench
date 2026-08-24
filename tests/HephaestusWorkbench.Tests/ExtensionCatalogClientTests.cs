using System.Net;
using System.Net.Http;
using System.Text;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class ExtensionCatalogClientTests
{
    [Fact]
    public async Task RefreshAsync_ParsesStrictV2CatalogAndWritesCache()
    {
        using var environment = new TestEnvironment(_ => JsonResponse(ValidCatalog()));

        var result = await environment.Client.RefreshAsync();

        Assert.False(result.IsFromCache);
        Assert.Null(result.Warning);
        Assert.Single(result.Catalog.Extensions);
        Assert.True(File.Exists(environment.Paths.ExtensionCatalogCacheFile));
    }

    [Fact]
    public async Task RefreshAsync_WhenNetworkFails_UsesPreviouslyValidatedCache()
    {
        using var environment = new TestEnvironment(_ => throw new HttpRequestException("网络不可用"));
        Directory.CreateDirectory(Path.GetDirectoryName(environment.Paths.ExtensionCatalogCacheFile)!);
        await File.WriteAllTextAsync(environment.Paths.ExtensionCatalogCacheFile, ValidCatalog());

        var result = await environment.Client.RefreshAsync();

        Assert.True(result.IsFromCache);
        Assert.Contains("缓存", result.Warning, StringComparison.Ordinal);
        Assert.Single(result.Catalog.Extensions);
    }

    [Fact]
    public async Task RefreshAsync_RejectsV1CatalogAndDoesNotCacheIt()
    {
        using var environment = new TestEnvironment(_ => JsonResponse("""{"schemaVersion":1,"plugins":[]}"""));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => environment.Client.RefreshAsync());

        Assert.Contains("v2", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(environment.Paths.ExtensionCatalogCacheFile));
    }

    [Fact]
    public async Task DownloadPackageAsync_ReadsExactlyDeclaredReleaseSize()
    {
        var package = Encoding.UTF8.GetBytes("signed-package");
        using var environment = new TestEnvironment(_ => BytesResponse(package));
        var (item, release) = CatalogEntry(package.Length);
        var progress = new List<ExtensionDownloadProgress>();

        var downloaded = await environment.Client.DownloadPackageAsync(item, release, new Progress<ExtensionDownloadProgress>(progress.Add));

        Assert.Equal(package, downloaded);
    }

    [Fact]
    public async Task DownloadPackageAsync_RejectsBodyThatExceedsDeclaredSize()
    {
        var package = Encoding.UTF8.GetBytes("too-large");
        using var environment = new TestEnvironment(_ => BytesResponse(package));
        var (item, release) = CatalogEntry(package.Length - 1);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            environment.Client.DownloadPackageAsync(item, release));

        Assert.Contains("大小", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadPackageAsync_RejectsInsecureRedirectTarget()
    {
        var package = Encoding.UTF8.GetBytes("package");
        using var environment = new TestEnvironment(_ => BytesResponse(package, "http://example.invalid/package.zip"));
        var (item, release) = CatalogEntry(package.Length);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            environment.Client.DownloadPackageAsync(item, release));

        Assert.Contains("HTTPS", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadPackageAsync_RejectsInsecureRedirectBeforeCheckingFailureStatus()
    {
        var package = Encoding.UTF8.GetBytes("package");
        using var environment = new TestEnvironment(_ =>
            BytesResponse(package, "http://example.invalid/package.zip", HttpStatusCode.NotFound));
        var (item, release) = CatalogEntry(package.Length);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            environment.Client.DownloadPackageAsync(item, release));

        Assert.Contains("HTTPS", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadPackageAsync_WhenNetworkRequestFails_ReturnsChineseInvalidDataError()
    {
        using var environment = new TestEnvironment(_ => throw new HttpRequestException("connection reset"));
        var (item, release) = CatalogEntry(7);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            environment.Client.DownloadPackageAsync(item, release));

        Assert.Contains("扩展 log-analyzer 下载请求失败", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadPackageAsync_WhenServerReturnsFailureStatus_ReturnsChineseInvalidDataError()
    {
        var package = Encoding.UTF8.GetBytes("package");
        using var environment = new TestEnvironment(_ =>
            BytesResponse(package, statusCode: HttpStatusCode.ServiceUnavailable));
        var (item, release) = CatalogEntry(package.Length);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            environment.Client.DownloadPackageAsync(item, release));

        Assert.Contains("HTTP 状态码 503", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadPackageAsync_WhenResponseStreamReadFails_ReturnsChineseInvalidDataError()
    {
        using var environment = new TestEnvironment(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ThrowingReadStream()),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/package.zip")
        });
        var (item, release) = CatalogEntry(7);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            environment.Client.DownloadPackageAsync(item, release));

        Assert.Contains("读取扩展 log-analyzer 的下载内容失败", error.Message, StringComparison.Ordinal);
    }

    private static (PluginSDK.ExtensionCatalogItem Item, PluginSDK.ExtensionRelease Release) CatalogEntry(long size)
    {
        var release = new PluginSDK.ExtensionRelease
        {
            Version = "2.0.0",
            MinHostVersion = "2.0.0",
            Url = "https://example.invalid/package.zip",
            Size = size,
            Sha256 = new string('a', 64),
            Signature = new PluginSDK.ExtensionPackageSignature
            {
                KeyId = "test-key",
                Signature = Convert.ToBase64String(new byte[64])
            }
        };
        return (new PluginSDK.ExtensionCatalogItem
        {
            Id = "log-analyzer",
            Name = "日志分析",
            Description = "测试",
            PublisherId = "thelinyue",
            Kind = PluginSDK.ExtensionKind.Analysis,
            Releases = [release]
        }, release);
    }

    private static string ValidCatalog() => """
        {
          "schemaVersion": 2,
          "extensions": [
            {
              "id": "log-analyzer",
              "name": "日志分析",
              "description": "综合日志分析",
              "publisherId": "thelinyue",
              "kind": "analysis",
              "releases": [
                {
                  "version": "2.0.0",
                  "minHostVersion": "2.0.0",
                  "url": "https://example.invalid/log-analyzer.zip",
                  "size": 12,
                  "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "signature": {
                    "keyId": "test-key",
                    "signature": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=="
                  }
                }
              ]
            }
          ]
        }
        """;

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/catalog.json")
        };

    private static HttpResponseMessage BytesResponse(
        byte[] bytes,
        string uri = "https://example.invalid/package.zip",
        HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new ByteArrayContent(bytes),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, uri)
        };

    private sealed class TestEnvironment : IDisposable
    {
        public TestEnvironment(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            Root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
            Paths = new DataPaths(Root);
            var logger = new WorkbenchLogger(Root);
            Client = new ExtensionCatalogClient(
                Paths,
                logger,
                new HttpClient(new StubHandler(responseFactory)));
        }

        public string Root { get; }
        public DataPaths Paths { get; }
        public ExtensionCatalogClient Client { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class ThrowingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new IOException("stream read failed");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("stream read failed"));

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responseFactory(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
