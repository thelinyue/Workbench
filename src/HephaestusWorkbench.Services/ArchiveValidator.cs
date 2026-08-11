using System.Formats.Tar;
using System.IO.Compression;

namespace HephaestusWorkbench.Services;

/// <summary>在创建 Case 前验证 gzip/tar，避免坏包进入后台分析队列。</summary>
public sealed class ArchiveValidator
{
    public async Task<(bool IsValid, string? Error)> ValidateAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var file = File.OpenRead(path);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress, leaveOpen: false);
            using var reader = new TarReader(gzip, leaveOpen: false);
            var hasEntry = false;
            while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is not null) hasEntry = true;
            return hasEntry ? (true, null) : (false, "压缩包为空，无法进行分析。 ");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return (false, $"日志压缩包损坏或无法读取：{ex.Message}");
        }
    }
}
