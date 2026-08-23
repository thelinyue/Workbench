using System.Text.Json;
using HephaestusWorkbench.PluginSDK;

var input = await Console.In.ReadToEndAsync();
var request = AnalysisProcessProtocol.ParseRequest(input);
Directory.CreateDirectory(request.ExtractDirectory);
await File.WriteAllTextAsync(Path.Combine(request.ExtractDirectory, "fixture.started"), Environment.ProcessId.ToString());

var mode = File.Exists(request.SourcePath)
    ? (await File.ReadAllTextAsync(request.SourcePath)).Trim()
    : "success";

if (mode == "sleep")
{
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

if (mode != "missing-report")
{
    var reportDirectory = Path.Combine(request.ExtractDirectory, "Report");
    Directory.CreateDirectory(reportDirectory);
    await File.WriteAllTextAsync(Path.Combine(reportDirectory, "index.html"), "<html>fixture</html>");
}

var response = mode switch
{
    "mismatch" => new AnalysisProcessResponse
    {
        Protocol = AnalysisProcessProtocol.Version,
        RequestId = "another-request",
        Succeeded = true
    },
    "failure" => new AnalysisProcessResponse
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
