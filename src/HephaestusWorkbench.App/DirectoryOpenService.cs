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
        => OpenDirectory(path, "解压目录", "不存在或已被清理");

    /// <summary>打开当前工作空间目录，并在失败时返回可直接展示给用户的中文错误。</summary>
    public DirectoryOpenResult OpenWorkspaceDirectory(string path)
        => OpenDirectory(path, "工作空间目录", "不存在或无法访问");

    private DirectoryOpenResult OpenDirectory(string path, string directoryName, string missingReason)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var message = $"{directoryName}路径为空，无法打开。";
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
            var message = $"{directoryName}路径无效：{path}";
            _logger.Error(message, ex);
            return new DirectoryOpenResult(false, message);
        }

        if (!Directory.Exists(fullPath))
        {
            var message = $"{directoryName}{missingReason}：{fullPath}";
            _logger.Error(message);
            return new DirectoryOpenResult(false, message);
        }

        try
        {
            _startProcess(new ProcessStartInfo { FileName = fullPath, UseShellExecute = true });
            _logger.Info($"已打开{directoryName}：{fullPath}");
            return new DirectoryOpenResult(true, null);
        }
        catch (Exception ex)
        {
            var message = $"打开{directoryName}失败：{fullPath}";
            _logger.Error(message, ex);
            return new DirectoryOpenResult(false, $"{message}\n{ex.Message}");
        }
    }
}

public sealed record DirectoryOpenResult(bool Succeeded, string? ErrorMessage);
