using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace HephaestusWorkbench.Setup;

/// <summary>
/// 联网下载安装载荷并执行完整性校验。安装器只信任构建时写入程序集的大小和 SHA-256，
/// Release 页面或网络返回的元数据不能替代这两个固定值。
/// </summary>
internal static class PayloadPackage
{
    public const string DownloadUrl = "https://github.com/thelinyue/Hephaestus-Workbench-Releases/releases/download/v1.1.0/HephaestusWorkbench-v1.1.0-win-x64.zip";
    private const long MaximumPayloadBytes = 500L * 1024 * 1024;

    public static async Task DownloadAsync(string destination, SetupLogger logger, HttpClient? httpClient = null)
    {
        var expectedHash = ReadMetadata("PayloadSha256");
        if (!long.TryParse(ReadMetadata("PayloadSize"), out var expectedSize) || expectedSize <= 0 || string.IsNullOrWhiteSpace(expectedHash))
            throw new InvalidOperationException("安装器缺少主程序校验信息，请重新下载官方安装器。");
        await DownloadAsync(destination, expectedHash, expectedSize, logger, httpClient);
    }

    internal static async Task DownloadAsync(string destination, string expectedHash, long expectedSize, SetupLogger logger, HttpClient? httpClient = null)
    {
        if (expectedSize <= 0 || expectedSize > MaximumPayloadBytes) throw new InvalidDataException("主程序包大小配置无效。");
        var ownsClient = httpClient is null;
        var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        try
        {
            using var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("主程序只能通过 HTTPS 下载。");
            if (response.Content.Headers.ContentLength is { } contentLength && contentLength != expectedSize)
                throw new InvalidDataException($"主程序包大小不符，期望 {expectedSize} 字节，实际 {contentLength} 字节。");

            await using var source = await response.Content.ReadAsStreamAsync();
            await using var output = File.Create(destination);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                total += read;
                if (total > expectedSize || total > MaximumPayloadBytes) throw new InvalidDataException("主程序包超过预期大小。");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read));
            }
            if (total != expectedSize) throw new InvalidDataException($"主程序包大小不符，期望 {expectedSize} 字节，实际 {total} 字节。");
            var actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("主程序包 SHA-256 校验失败，安装已停止。");
            logger.Info($"主程序下载和校验完成：{total} 字节。");
        }
        finally
        {
            if (ownsClient) client.Dispose();
        }
    }

    internal static void Extract(string payloadFile, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        var targetRoot = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(payloadFile);
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(targetDirectory, entry.FullName));
            if (!destination.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("主程序包包含越界文件路径。");
            if (string.IsNullOrEmpty(entry.Name)) Directory.CreateDirectory(destination);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }
        }
    }

    private static string? ReadMetadata(string key) => Assembly.GetExecutingAssembly()
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.Ordinal))?.Value;
}
