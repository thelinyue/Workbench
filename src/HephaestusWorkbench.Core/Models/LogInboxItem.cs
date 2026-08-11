namespace HephaestusWorkbench.Core.Models;

/// <summary>日志收件箱中的文件，不落库，目录扫描即可恢复。</summary>
public sealed class LogInboxItem
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required string DeviceId { get; init; }
    public DateTime LogTime { get; init; }
    public long FileSize { get; init; }
    public bool IsValidArchive { get; set; }
    public string? ErrorMessage { get; set; }
    public string StatusText => IsValidArchive ? "可分析" : ErrorMessage ?? "待检查";
    public string FileSizeText => FileSize >= 1024L * 1024 ? $"{FileSize / 1024d / 1024:N2} MB" : $"{FileSize / 1024d:N2} KB";
}
