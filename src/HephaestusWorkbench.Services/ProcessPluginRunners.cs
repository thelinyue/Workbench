using System.Diagnostics;
using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Services;

internal static class ProcessPluginRunnerUtilities
{
    public static async Task<(int ExitCode, string StandardError)> ExecuteAsync(
        string executable,
        string workingDirectory,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动分析插件进程。");
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, await errorTask);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 取消路径只需尽力终止子进程，原始取消状态由上层任务中心记录。
            }
            throw;
        }
    }
}

/// <summary>适配 TDD 约定的 --case/--input/--output 插件。</summary>
public sealed class StandardExePluginRunner : IPluginRunner
{
    private readonly WorkbenchLogger _logger;

    public StandardExePluginRunner(WorkbenchLogger logger) => _logger = logger;

    public async Task<PluginExecutionResult> RunAsync(PluginManifest manifest, PluginExecutionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(context.OutputPath);
            var result = await ProcessPluginRunnerUtilities.ExecuteAsync(
                manifest.EntryPath,
                manifest.DirectoryPath,
                new[] { "--case", context.CaseId, "--input", context.SourcePath, "--output", context.OutputPath },
                cancellationToken);
            if (result.ExitCode != 0)
                return new PluginExecutionResult(result.ExitCode, null, string.IsNullOrWhiteSpace(result.StandardError) ? "标准分析插件返回失败退出码。" : result.StandardError.Trim());
            var report = Path.Combine(context.OutputPath, "report.html");
            return File.Exists(report)
                ? new PluginExecutionResult(0, context.OutputPath, null)
                : new PluginExecutionResult(0, null, "插件执行成功，但没有生成 report.html。");
        }
        catch (OperationCanceledException)
        {
            return new PluginExecutionResult(-1, null, "分析任务已取消。", Cancelled: true);
        }
        catch (Exception ex)
        {
            _logger.Error($"标准插件执行失败：{manifest.Name}", ex);
            return new PluginExecutionResult(-1, null, $"插件启动失败：{ex.Message}");
        }
    }
}

/// <summary>
/// 兼容现有 log_analyzer.exe。保留 -d 输入参数，并通过 -o 将完整报告直接输出到工作台报告目录。
/// 解压文件仍由插件在原始日志目录生成，工作台不搬运或复制解压目录。
/// </summary>
public sealed class LegacyLogAnalyzerRunner : IPluginRunner
{
    private readonly WorkbenchLogger _logger;

    public LegacyLogAnalyzerRunner(WorkbenchLogger logger) => _logger = logger;

    public async Task<PluginExecutionResult> RunAsync(PluginManifest manifest, PluginExecutionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ProcessPluginRunnerUtilities.ExecuteAsync(
                manifest.EntryPath,
                context.WorkingDirectory,
                BuildLegacyArguments(context),
                cancellationToken);
            if (result.ExitCode != 0)
                return new PluginExecutionResult(result.ExitCode, null, string.IsNullOrWhiteSpace(result.StandardError) ? "日志分析插件执行失败。" : result.StandardError.Trim());

            var generatedReport = Path.Combine(context.OutputPath, "report.html");
            if (!File.Exists(generatedReport))
                return new PluginExecutionResult(result.ExitCode, null, "日志分析完成，但未找到指定输出目录中的 report.html。");

            _logger.Info($"日志分析插件完成：{context.CaseId}");
            return new PluginExecutionResult(0, context.OutputPath, null);
        }
        catch (OperationCanceledException)
        {
            return new PluginExecutionResult(-1, null, "分析任务已取消。", Cancelled: true);
        }
        catch (Exception ex)
        {
            _logger.Error($"现有日志分析插件执行失败：{manifest.Name}", ex);
            return new PluginExecutionResult(-1, null, $"日志分析插件执行失败：{ex.Message}");
        }
    }

    private static IEnumerable<string> BuildLegacyArguments(PluginExecutionContext context)
    {
        yield return "-d";
        yield return context.SourcePath;
        yield return "-o";
        yield return context.OutputPath;
        if (!string.IsNullOrWhiteSpace(context.RulesPath))
        {
            yield return "--rules";
            yield return context.RulesPath;
        }
    }
}
