using System.Globalization;
using System.Text.RegularExpressions;
using HephaestusWorkbench.Core.Models;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 严格解析日志文件名末尾的设备序列号和时间，前缀仅作为来源标识而不参与识别。
/// </summary>
public sealed partial class LogFileParser
{
    [GeneratedRegex(@"^.+_(?<device>[A-Za-z0-9]+)_(?<time>\d{10}|\d{12})\.tgz(?:\.temp)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    /// <summary>判断文件名是否属于工作台支持的日志压缩包格式。</summary>
    public static bool IsSupportedFileName(string fileName)
        => FileNamePattern().IsMatch(fileName);

    /// <summary>判断文件是否应进入收件箱检测，包括名称不合法但需要展示错误的文件。</summary>
    public static bool HasSupportedExtension(string fileName)
        => fileName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".tgz.temp", StringComparison.OrdinalIgnoreCase);

    public bool TryParse(string path, out LogInboxItem? item, out string? error)
    {
        item = null;
        error = null;
        var fileName = Path.GetFileName(path);
        var match = FileNamePattern().Match(fileName);
        if (!match.Success)
        {
            error = "文件名不符合“任意前缀_设备序列号_YYYYMMDDHHMM.tgz[.temp]”或“任意前缀_设备序列号_YYMMDDHHMM.tgz[.temp]”格式。";
            return false;
        }

        if (!TryParseLogTime(match.Groups["time"].Value, out var logTime))
        {
            error = "日志文件名中的时间无效。";
            return false;
        }

        var info = new FileInfo(path);
        item = new LogInboxItem
        {
            FilePath = info.FullName,
            FileName = info.Name,
            DeviceId = match.Groups["device"].Value,
            LogTime = logTime,
            FileSize = info.Exists ? info.Length : 0
        };
        return true;
    }

    /// <summary>将 10 位旧式时间明确解释为 20YY，避免依赖 .NET 的两位年份窗口。</summary>
    private static bool TryParseLogTime(string value, out DateTime logTime)
    {
        if (value.Length == 10
            && int.TryParse(value[..2], NumberStyles.None, CultureInfo.InvariantCulture, out var shortYear))
        {
            value = $"{2000 + shortYear:D4}{value[2..]}";
        }

        return DateTime.TryParseExact(
            value,
            "yyyyMMddHHmm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out logTime);
    }
}
