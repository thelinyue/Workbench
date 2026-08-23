using System.Text.Json;
using System.Text.Json.Nodes;
using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Tests;

public sealed class WorkspaceBridgeProtocolTests
{
    [Fact]
    public void Request_UsesVersionedJsonRpcEnvelope()
    {
        using var document = JsonDocument.Parse("""{ "path": "rules/current.json" }""");
        var request = new WorkspaceBridgeRequest
        {
            ProtocolVersion = WorkspaceBridgeProtocol.Version,
            RequestId = "bridge-1",
            Method = "workspace.readText",
            Params = document.RootElement.Clone()
        };

        var json = JsonSerializer.Serialize(request);

        Assert.Contains("\"protocolVersion\":\"workspace-bridge-v1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"requestId\":\"bridge-1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"method\":\"workspace.readText\"", json, StringComparison.Ordinal);
        Assert.Contains("\"params\":", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("protocolVersion")]
    [InlineData("params")]
    public void ParseRequest_RejectsMissingEnvelopeMember(string member)
    {
        var root = JsonNode.Parse("""
            {
              "protocolVersion": "workspace-bridge-v1",
              "requestId": "bridge-1",
              "method": "workspace.readText",
              "params": {}
            }
            """)!.AsObject();
        Assert.True(root.Remove(member));

        Assert.Throws<ExtensionContractException>(() => WorkspaceBridgeProtocol.ParseRequest(root.ToJsonString()));
    }

    [Fact]
    public void ParseRequest_RejectsUnknownProtocolVersion()
    {
        var json = """
            {
              "protocolVersion": "legacy-bridge-v1",
              "requestId": "bridge-1",
              "method": "workspace.readText",
              "params": {}
            }
            """;

        Assert.Throws<ExtensionContractException>(() => WorkspaceBridgeProtocol.ParseRequest(json));
    }

    [Fact]
    public void ErrorResponse_PreservesRequestIdentity()
    {
        var response = new WorkspaceBridgeResponse
        {
            ProtocolVersion = WorkspaceBridgeProtocol.Version,
            RequestId = "bridge-1",
            Error = new WorkspaceBridgeError
            {
                Code = "permissionDenied",
                Message = "扩展未获得此方法所需权限。"
            }
        };

        var json = JsonSerializer.Serialize(response);

        Assert.Contains("\"requestId\":\"bridge-1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"code\":\"permissionDenied\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("requestId")]
    [InlineData("method")]
    [InlineData("params")]
    public void ParseRequest_RejectsNullEnvelopeMember(string member)
    {
        var root = JsonNode.Parse("""
            {
              "protocolVersion": "workspace-bridge-v1",
              "requestId": "bridge-1",
              "method": "workspace.readText",
              "params": {}
            }
            """)!.AsObject();
        root[member] = null;

        Assert.Throws<ExtensionContractException>(() => WorkspaceBridgeProtocol.ParseRequest(root.ToJsonString()));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ParseResponse_RequiresExactlyOneOfResultOrError(bool includeResult, bool includeError)
    {
        var root = new JsonObject
        {
            ["protocolVersion"] = "workspace-bridge-v1",
            ["requestId"] = "bridge-1"
        };
        if (includeResult) root["result"] = new JsonObject { ["content"] = "ok" };
        if (includeError)
        {
            root["error"] = new JsonObject
            {
                ["code"] = "permissionDenied",
                ["message"] = "没有权限。"
            };
        }

        Assert.Throws<ExtensionContractException>(() => WorkspaceBridgeProtocol.ParseResponse(root.ToJsonString()));
    }

    [Fact]
    public void ParseResponse_AcceptsStructuredError()
    {
        var response = WorkspaceBridgeProtocol.ParseResponse("""
            {
              "protocolVersion": "workspace-bridge-v1",
              "requestId": "bridge-1",
              "error": { "code": "permissionDenied", "message": "没有权限。" }
            }
            """);

        Assert.Equal("permissionDenied", response.Error!.Code);
    }

    [Fact]
    public void ParseResponse_AcceptsExplicitNullResult()
    {
        var response = WorkspaceBridgeProtocol.ParseResponse("""
            {
              "protocolVersion": "workspace-bridge-v1",
              "requestId": "bridge-1",
              "result": null
            }
            """);

        Assert.Null(response.Error);
        Assert.Null(response.Result);
    }
}
