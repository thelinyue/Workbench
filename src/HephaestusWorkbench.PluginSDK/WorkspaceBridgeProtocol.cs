using System.Text.Json;
using System.Text.Json.Serialization;

namespace HephaestusWorkbench.PluginSDK;

/// <summary>
/// Workspace Host Bridge 的版本化请求信封。宿主收到消息后还必须校验消息来源、manifest 权限和发布者信任范围。
/// </summary>
public sealed class WorkspaceBridgeRequest
{
    [JsonPropertyName("protocolVersion")]
    public required string ProtocolVersion { get; init; }

    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    public required JsonElement Params { get; init; }
}

/// <summary>Workspace Bridge 的结构化错误，不向扩展泄露宿主异常对象或调用栈。</summary>
public sealed class WorkspaceBridgeError
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

/// <summary>Workspace Host Bridge 响应；requestId 用于把异步响应关联到唯一请求。</summary>
public sealed class WorkspaceBridgeResponse
{
    [JsonPropertyName("protocolVersion")]
    public required string ProtocolVersion { get; init; }

    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Result { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkspaceBridgeError? Error { get; init; }
}

/// <summary>Workspace Bridge 的严格 JSON 边界，拒绝缺失信封字段、未知字段、错误版本和矛盾响应。</summary>
public static class WorkspaceBridgeProtocol
{
    public const string Version = "workspace-bridge-v1";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static WorkspaceBridgeRequest ParseRequest(string json)
    {
        var request = Deserialize<WorkspaceBridgeRequest>(json, "请求");
        ValidateEnvelope(request.ProtocolVersion, request.RequestId, "请求");
        if (string.IsNullOrWhiteSpace(request.Method))
            throw new ExtensionContractException("Workspace Bridge 请求的 method 不能为空。");
        if (request.Params.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw new ExtensionContractException("Workspace Bridge 请求的 params 不能为空。");
        return request;
    }

    public static WorkspaceBridgeResponse ParseResponse(string json)
    {
        var response = Deserialize<WorkspaceBridgeResponse>(json, "响应");
        ValidateEnvelope(response.ProtocolVersion, response.RequestId, "响应");

        using var document = JsonDocument.Parse(json);
        var hasResult = document.RootElement.TryGetProperty("result", out _);
        var hasError = document.RootElement.TryGetProperty("error", out _);
        if (hasResult == hasError)
            throw new ExtensionContractException("Workspace Bridge 响应必须且只能包含 result 或 error 之一。");
        if (hasError && (response.Error is null || string.IsNullOrWhiteSpace(response.Error.Code) || string.IsNullOrWhiteSpace(response.Error.Message)))
            throw new ExtensionContractException("Workspace Bridge 错误响应必须包含 code 和 message。");

        return response;
    }

    private static T Deserialize<T>(string json, string displayName)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ExtensionContractException($"Workspace Bridge {displayName}内容不能为空。");
        try
        {
            return JsonSerializer.Deserialize<T>(json, SerializerOptions)
                ?? throw new JsonException($"Workspace Bridge {displayName}内容为空。");
        }
        catch (JsonException exception)
        {
            throw new ExtensionContractException($"Workspace Bridge {displayName} JSON 不符合 {Version} 契约：{exception.Message}");
        }
    }

    private static void ValidateEnvelope(string? protocolVersion, string? requestId, string displayName)
    {
        if (!string.Equals(protocolVersion, Version, StringComparison.Ordinal))
            throw new ExtensionContractException($"Workspace Bridge {displayName}必须使用 {Version} 协议。");
        if (string.IsNullOrWhiteSpace(requestId))
            throw new ExtensionContractException($"Workspace Bridge {displayName}的 requestId 不能为空。");
    }
}
