using System.Text.Json.Nodes;
using System.Text.Json;
using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Tests;

public sealed class AnalysisProcessProtocolTests
{
    [Theory]
    [InlineData(AnalysisScope.Comprehensive, "comprehensive")]
    [InlineData(AnalysisScope.Storage, "storage")]
    public void Request_UsesVersionedScopeContract(AnalysisScope scope, string expectedScope)
    {
        var request = new AnalysisProcessRequest
        {
            Protocol = AnalysisProcessProtocol.Version,
            RequestId = "request-1",
            CaseId = "case-1",
            SourcePath = @"C:\Logs\device.tgz",
            OutputDirectory = @"C:\Data\Cases\case-1",
            ExtractDirectory = @"C:\Data\Cases\case-1\Extract",
            Scope = scope
        };

        var json = JsonSerializer.Serialize(request);

        Assert.Contains("\"protocol\":\"analysis-process-v1\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"scope\":\"{expectedScope}\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("reportPath", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("legacy-v1", "comprehensive")]
    [InlineData("analysis-process-v1", "custom")]
    public void ParseRequest_RejectsUnknownProtocolOrScope(string protocol, string scope)
    {
        var json = $$"""
            {
              "protocol": "{{protocol}}",
              "requestId": "request-1",
              "caseId": "case-1",
              "sourcePath": "C:/Logs/device.tgz",
              "outputDirectory": "C:/Data/Cases/case-1",
              "extractDirectory": "C:/Data/Cases/case-1/Extract",
              "scope": "{{scope}}"
            }
            """;

        Assert.Throws<ExtensionContractException>(() => AnalysisProcessProtocol.ParseRequest(json));
    }

    [Fact]
    public void Response_DoesNotAllowPluginToChooseReportEntry()
    {
        var response = new AnalysisProcessResponse
        {
            Protocol = AnalysisProcessProtocol.Version,
            RequestId = "request-1",
            Succeeded = true
        };

        var json = JsonSerializer.Serialize(response);

        Assert.Contains("\"succeeded\":true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("report", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("requestId")]
    [InlineData("caseId")]
    [InlineData("sourcePath")]
    [InlineData("outputDirectory")]
    [InlineData("extractDirectory")]
    public void ParseRequest_RejectsNullRequiredString(string member)
    {
        var root = JsonNode.Parse("""
            {
              "protocol": "analysis-process-v1",
              "requestId": "request-1",
              "caseId": "case-1",
              "sourcePath": "C:/Logs/device.tgz",
              "outputDirectory": "C:/Data/Cases/case-1",
              "extractDirectory": "C:/Data/Cases/case-1/Extract",
              "scope": "comprehensive"
            }
            """)!.AsObject();
        root[member] = null;

        Assert.Throws<ExtensionContractException>(() => AnalysisProcessProtocol.ParseRequest(root.ToJsonString()));
    }

    [Fact]
    public void ParseResponse_RejectsNullRequestId()
    {
        var json = """
            {
              "protocol": "analysis-process-v1",
              "requestId": null,
              "succeeded": true
            }
            """;

        Assert.Throws<ExtensionContractException>(() => AnalysisProcessProtocol.ParseResponse(json));
    }

    [Theory]
    [InlineData(true, "failed", "发生错误")]
    [InlineData(false, null, null)]
    [InlineData(false, "failed", null)]
    public void ParseResponse_RejectsContradictoryOrIncompleteStatus(bool succeeded, string? errorCode, string? errorMessage)
    {
        var root = new JsonObject
        {
            ["protocol"] = "analysis-process-v1",
            ["requestId"] = "request-1",
            ["succeeded"] = succeeded,
            ["errorCode"] = errorCode,
            ["errorMessage"] = errorMessage
        };

        Assert.Throws<ExtensionContractException>(() => AnalysisProcessProtocol.ParseResponse(root.ToJsonString()));
    }

    [Fact]
    public void ParseResponse_AcceptsCompleteFailureStatus()
    {
        var response = AnalysisProcessProtocol.ParseResponse("""
            {
              "protocol": "analysis-process-v1",
              "requestId": "request-1",
              "succeeded": false,
              "errorCode": "analysisFailed",
              "errorMessage": "日志分析失败。"
            }
            """);

        Assert.False(response.Succeeded);
        Assert.Equal("analysisFailed", response.ErrorCode);
    }
}
