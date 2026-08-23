using System.Text.Json;
using System.Text.Json.Serialization;

namespace HephaestusWorkbench.PluginSDK;

/// <summary>
/// 单一日志分析扩展支持的分析范围。综合分析是当前报告能力，存储分析由同一扩展的后续版本按能力启用。
/// </summary>
[JsonConverter(typeof(LowerCamelCaseEnumConverter<AnalysisScope>))]
public enum AnalysisScope
{
    Comprehensive,
    Storage
}

/// <summary>
/// analysis-process-v1 请求。宿主提供输入与工作目录，扩展必须把最终报告写入 Extract/Report/index.html。
/// </summary>
public sealed class AnalysisProcessRequest
{
    [JsonPropertyName("protocol")]
    public required string Protocol { get; init; }

    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("caseId")]
    public required string CaseId { get; init; }

    [JsonPropertyName("sourcePath")]
    public required string SourcePath { get; init; }

    [JsonPropertyName("outputDirectory")]
    public required string OutputDirectory { get; init; }

    [JsonPropertyName("extractDirectory")]
    public required string ExtractDirectory { get; init; }

    [JsonPropertyName("scope")]
    public required AnalysisScope Scope { get; init; }
}

/// <summary>
/// analysis-process-v1 最终响应。报告入口由宿主固定解析，因此协议中故意不提供 reportPath 或任意入口字段。
/// </summary>
public sealed class AnalysisProcessResponse
{
    [JsonPropertyName("protocol")]
    public required string Protocol { get; init; }

    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("succeeded")]
    public bool Succeeded { get; init; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("errorMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// analysis-process-v1 的严格 JSON 边界。所有进程输入输出必须经此处解析，未知字段、错误协议和未知范围均被拒绝。
/// </summary>
public static class AnalysisProcessProtocol
{
    public const string Version = "analysis-process-v1";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static AnalysisProcessRequest ParseRequest(string json)
    {
        var request = Deserialize<AnalysisProcessRequest>(json, "分析请求");
        ValidateProtocol(request.Protocol, "分析请求");
        ValidateRequiredStrings("分析请求", request.RequestId, request.CaseId, request.SourcePath, request.OutputDirectory, request.ExtractDirectory);
        return request;
    }

    public static AnalysisProcessResponse ParseResponse(string json)
    {
        var response = Deserialize<AnalysisProcessResponse>(json, "分析响应");
        ValidateProtocol(response.Protocol, "分析响应");
        ValidateRequiredStrings("分析响应", response.RequestId);
        if (response.Succeeded && (response.ErrorCode is not null || response.ErrorMessage is not null))
            throw new ExtensionContractException("成功的分析响应不能包含错误字段。");
        if (!response.Succeeded && (string.IsNullOrWhiteSpace(response.ErrorCode) || string.IsNullOrWhiteSpace(response.ErrorMessage)))
            throw new ExtensionContractException("失败的分析响应必须包含 errorCode 和 errorMessage。");
        return response;
    }

    private static T Deserialize<T>(string json, string displayName)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ExtensionContractException($"{displayName}内容不能为空。");
        try
        {
            return JsonSerializer.Deserialize<T>(json, SerializerOptions)
                ?? throw new JsonException($"{displayName}内容为空。");
        }
        catch (JsonException exception)
        {
            throw new ExtensionContractException($"{displayName} JSON 不符合 {Version} 契约：{exception.Message}");
        }
    }

    private static void ValidateRequiredStrings(string displayName, params string?[] values)
    {
        if (values.Any(string.IsNullOrWhiteSpace))
        {
            throw new ExtensionContractException($"{displayName}的必填字符串字段不能为空。");
        }
    }

    private static void ValidateProtocol(string? protocol, string displayName)
    {
        if (!string.Equals(protocol, Version, StringComparison.Ordinal))
        {
            throw new ExtensionContractException($"{displayName}必须使用 {Version} 协议。");
        }
    }
}
