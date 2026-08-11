namespace HephaestusWorkbench.Services;

/// <summary>统一中文日志出口，便于界面显示和后续接入文件日志。</summary>
public sealed class WorkbenchLogger
{
    private readonly string _logFile;
    private readonly object _sync = new();

    public WorkbenchLogger(string dataRoot)
    {
        var directory = Path.Combine(dataRoot, "Logs");
        Directory.CreateDirectory(directory);
        _logFile = Path.Combine(directory, "workbench.log");
    }

    public event EventHandler<string>? MessageWritten;

    public void Info(string message) => Write("信息", message);
    public void Error(string message, Exception? exception = null) => Write("错误", exception is null ? message : $"{message}：{exception.Message}");

    private void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
        lock (_sync) File.AppendAllText(_logFile, line + Environment.NewLine);
        MessageWritten?.Invoke(this, line);
    }
}
