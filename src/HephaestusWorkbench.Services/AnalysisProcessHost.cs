using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 分析进程宿主的稳定结果。报告目录只由宿主根据解压目录推导，扩展响应不能覆盖或指定任意报告入口。
/// </summary>
public sealed record AnalysisProcessHostResult(
    bool Succeeded,
    bool Cancelled,
    string? ReportDirectory,
    string? ErrorMessage,
    int? ExitCode);

/// <summary>
/// 通过 analysis-process-v1 在独立进程中运行日志分析扩展。
/// 宿主负责校验扩展契约、限制进程输出、处理取消并固定解析 Extract/Report/index.html，
/// 从而避免扩展把任意文件声明为可打开的报告。
/// </summary>
public sealed class AnalysisProcessHost
{
    private const int MaximumCapturedCharacters = 1024 * 1024;
    private readonly WorkbenchLogger _logger;

    public AnalysisProcessHost(WorkbenchLogger logger)
    {
        _logger = logger;
    }

    public async Task<AnalysisProcessHostResult> RunAsync(
        ExtensionManifest manifest,
        AnalysisProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        Process? process = null;
        try
        {
            ValidateManifest(manifest);
            var normalizedRequest = ValidateRequest(request);
            var reportDirectory = ResolveReportDirectory(normalizedRequest);
            Directory.CreateDirectory(reportDirectory);

            process = CreateProcess(manifest.EntryPath!, manifest.DirectoryPath);
            if (!process.Start())
                return Failure("无法启动日志分析扩展进程。", null);

            var standardOutputTask = ReadBoundedAsync(process.StandardOutput, "标准输出", process, cancellationToken);
            var standardErrorTask = ReadBoundedAsync(process.StandardError, "错误输出", process, cancellationToken);

            var requestJson = JsonSerializer.Serialize(normalizedRequest);
            await process.StandardInput.WriteAsync(requestJson.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();

            await Task.WhenAll(
                process.WaitForExitAsync(cancellationToken),
                standardOutputTask,
                standardErrorTask);

            var exitCode = process.ExitCode;
            var standardOutput = await standardOutputTask;
            var standardError = await standardErrorTask;
            if (exitCode != 0)
            {
                var diagnostic = string.IsNullOrWhiteSpace(standardError)
                    ? string.Empty
                    : $"：{ShortenDiagnostic(standardError)}";
                return Failure($"日志分析扩展返回失败退出码 {exitCode}{diagnostic}", exitCode);
            }

            var response = AnalysisProcessProtocol.ParseResponse(standardOutput);
            if (!string.Equals(response.RequestId, normalizedRequest.RequestId, StringComparison.Ordinal))
                return Failure("日志分析扩展响应的请求标识与当前任务不一致。", exitCode);
            if (!response.Succeeded)
                return Failure($"日志分析扩展执行失败：{response.ErrorMessage}", exitCode);

            var reportEntry = Path.Combine(reportDirectory, "index.html");
            if (!File.Exists(reportEntry))
                return Failure("日志分析完成，但未生成固定入口 Report/index.html。", exitCode);

            _logger.Info($"日志分析扩展完成：{normalizedRequest.CaseId}");
            return new AnalysisProcessHostResult(true, false, reportDirectory, null, exitCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            _logger.Info($"日志分析任务已取消：{request.CaseId}");
            return new AnalysisProcessHostResult(false, true, null, "分析任务已取消。", TryGetExitCode(process));
        }
        catch (AnalysisProcessOutputLimitException exception)
        {
            TryKill(process);
            _logger.Error("日志分析扩展输出超过宿主限制，已终止进程", exception);
            return Failure(exception.Message, TryGetExitCode(process));
        }
        catch (Exception exception)
        {
            TryKill(process);
            _logger.Error($"日志分析扩展运行失败：{manifest.Name}", exception);
            return Failure($"日志分析扩展运行失败：{exception.Message}", TryGetExitCode(process));
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void ValidateManifest(ExtensionManifest manifest)
    {
        ExtensionContractValidator.ValidateManifest(manifest);
        if (manifest.Kind != ExtensionKind.Analysis ||
            manifest.Runtime.Kind != ExtensionRuntimeKind.Process ||
            !manifest.SupportsCapability("analysis.engine") ||
            !string.Equals(manifest.Runtime.Protocol, AnalysisProcessProtocol.Version, StringComparison.Ordinal))
        {
            throw new ExtensionContractException("只能使用 analysis-process-v1 的日志分析引擎扩展。");
        }

        if (string.IsNullOrWhiteSpace(manifest.EntryPath) || !File.Exists(manifest.EntryPath))
            throw new FileNotFoundException("日志分析扩展入口不存在。", manifest.EntryPath);
    }

    private static AnalysisProcessRequest ValidateRequest(AnalysisProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var json = JsonSerializer.Serialize(request);
        return AnalysisProcessProtocol.ParseRequest(json);
    }

    private static string ResolveReportDirectory(AnalysisProcessRequest request)
    {
        var extractDirectory = Path.GetFullPath(request.ExtractDirectory);
        var reportDirectory = Path.Combine(extractDirectory, "Report");
        var declaredOutputDirectory = Path.GetFullPath(request.OutputDirectory);
        if (!string.Equals(reportDirectory, declaredOutputDirectory, StringComparison.OrdinalIgnoreCase))
            throw new ExtensionContractException("分析请求的输出目录必须是解压目录下的 Report 目录。");
        return reportDirectory;
    }

    private static Process CreateProcess(string entryPath, string workingDirectory)
    {
        return new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = entryPath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        string streamName,
        Process process,
        CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var buffer = new char[8192];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) return result.ToString();
            if (result.Length + read > MaximumCapturedCharacters)
            {
                TryKill(process);
                throw new AnalysisProcessOutputLimitException($"日志分析扩展的{streamName}输出超过 1 MiB 限制，已终止进程。");
            }
            result.Append(buffer, 0, read);
        }
    }

    private static string ShortenDiagnostic(string value)
    {
        const int maximumLength = 2000;
        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength ? trimmed : $"{trimmed[..maximumLength]}…";
    }

    private static AnalysisProcessHostResult Failure(string message, int? exitCode)
        => new(false, false, null, message, exitCode);

    private static int? TryGetExitCode(Process? process)
    {
        try
        {
            return process is { HasExited: true } ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 取消和异常收敛路径只做尽力终止；原始中文错误由调用方保留并显示。
        }
    }

    private sealed class AnalysisProcessOutputLimitException(string message) : Exception(message);
}
