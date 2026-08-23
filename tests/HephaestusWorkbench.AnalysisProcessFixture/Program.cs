using System.Text.Json;
using HephaestusWorkbench.PluginSDK;

if (args is ["child", var childMarkerPath])
{
    await WriteMarkerAsync(childMarkerPath);
    await Task.Delay(TimeSpan.FromSeconds(30));
    return;
}

var input = await Console.In.ReadToEndAsync();
var request = AnalysisProcessProtocol.ParseRequest(input);
Directory.CreateDirectory(request.ExtractDirectory);
await WriteMarkerAsync(Path.Combine(request.ExtractDirectory, "fixture.started"));

var mode = File.Exists(request.SourcePath)
    ? (await File.ReadAllTextAsync(request.SourcePath)).Trim()
    : "success";

if (mode is "sleep" or "sleep-tree")
{
    if (mode == "sleep-tree")
    {
        var childMarker = Path.Combine(request.ExtractDirectory, "fixture.child");
        var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "child", childMarker }
        });
        if (child is null) throw new InvalidOperationException("无法启动测试子进程。");
    }
    await Task.Delay(TimeSpan.FromSeconds(30));
    return;
}

if (mode == "invalid-json")
{
    await Console.Out.WriteAsync("not-json");
    return;
}

if (mode == "large-stdout")
{
    await Console.Out.WriteAsync(new string('x', 2 * 1024 * 1024));
    return;
}

if (mode == "delayed-failure")
    await Task.Delay(TimeSpan.FromMilliseconds(500));

if (mode == "dual-output")
{
    await Console.Error.WriteAsync(new string('e', 256 * 1024));
    await Console.Out.WriteAsync(new string(' ', 256 * 1024));
}

if (mode != "missing-report")
{
    var reportDirectory = Path.Combine(request.ExtractDirectory, "Report");
    Directory.CreateDirectory(reportDirectory);
    await File.WriteAllTextAsync(Path.Combine(reportDirectory, "index.html"), "<html>fixture</html>");
    if (mode == "overwrite-then-fail")
        await File.WriteAllTextAsync(Path.Combine(reportDirectory, "asset.txt"), "new asset");
}

if (mode == "report-root-file-failure")
{
    var reportRoot = Path.Combine(request.ExtractDirectory, "Report");
    if (Directory.Exists(reportRoot)) Directory.Delete(reportRoot, recursive: true);
    await File.WriteAllTextAsync(reportRoot, "blocking file");
}

if (mode == "report-path")
{
    await Console.Out.WriteAsync($$"""{"protocol":"analysis-process-v1","requestId":"{{request.RequestId}}","succeeded":true,"reportPath":"outside.html"}""");
    return;
}

var response = mode switch
{
    "mismatch" => new AnalysisProcessResponse
    {
        Protocol = AnalysisProcessProtocol.Version,
        RequestId = "another-request",
        Succeeded = true
    },
    "failure" or "delayed-failure" or "overwrite-then-fail" or "report-root-file-failure" => new AnalysisProcessResponse
    {
        Protocol = AnalysisProcessProtocol.Version,
        RequestId = request.RequestId,
        Succeeded = false,
        ErrorCode = "analysisFailed",
        ErrorMessage = "测试分析失败。"
    },
    _ => new AnalysisProcessResponse
    {
        Protocol = AnalysisProcessProtocol.Version,
        RequestId = request.RequestId,
        Succeeded = true
    }
};

if (mode == "failure") await Console.Error.WriteAsync("fixture stderr");
await Console.Out.WriteAsync(JsonSerializer.Serialize(response));
Environment.ExitCode = mode == "nonzero" ? 7 : 0;

static async Task WriteMarkerAsync(string path)
{
    var temporaryPath = path + ".tmp";
    await File.WriteAllTextAsync(temporaryPath, Environment.ProcessId.ToString());
    File.Move(temporaryPath, path, overwrite: true);
}
