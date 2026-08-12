using System.Diagnostics;
using System.IO;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.App;

/// <summary>封装 Windows 目录打开行为，统一完成路径校验、中文日志和错误结果返回。</summary>
public sealed class DirectoryOpenService
{
    private readonly WorkbenchLogger _logger;
    private readonly Action<ProcessStartInfo> _startProcess;

    public DirectoryOpenService(WorkbenchLogger logger, Action<ProcessStartInfo>? startProcess = null)
    {
        _logger = logger;
        _startProcess = startProcess ?? (startInfo => Process.Start(startInfo));
    }

    public DirectoryOpenResult OpenExtractDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            const string message = "解压目录路径为空，无法打开。";
            _logger.Error(message);
            return new DirectoryOpenResult(false, message);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            var message = $"解压目录路径无效：{path}";
            _logger.Error(message, ex);
            return new DirectoryOpenResult(false, message);
        }

        if (!Directory.Exists(fullPath))
        {
            var message = $"解压目录不存在或已被清理：{fullPath}";
            _logger.Error(message);
            return new DirectoryOpenResult(false, message);
        }

        try
        {
            _startProcess(new ProcessStartInfo { FileName = fullPath, UseShellExecute = true });
            _logger.Info($"已打开解压目录：{fullPath}");
            return new DirectoryOpenResult(true, null);
        }
        catch (Exception ex)
        {
            var message = $"打开解压目录失败：{fullPath}";
            _logger.Error(message, ex);
            return new DirectoryOpenResult(false, $"{message}\n{ex.Message}");
        }
    }
}

public sealed record DirectoryOpenResult(bool Succeeded, string? ErrorMessage);
