using System.Text.Json.Serialization;

namespace HephaestusWorkbench.PluginSDK;

/// <summary>维护工作流风险等级标识。</summary>
public static class MaintenanceRiskLevels
{
    public const string ReadOnly = "readOnly";
    public const string High = "high";
}

/// <summary>命令参数 token 的来源。宿主根据来源绑定并统一转义，扩展不能拼接自由命令字符串。</summary>
public static class CommandArgumentKinds
{
    public const string Literal = "literal";
    public const string Input = "input";
    public const string Discovery = "discovery";
}

/// <summary>维护工作流输入声明。</summary>
public sealed class WorkflowInputDefinition
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("required")]
    public bool Required { get; init; }
}

/// <summary>把工作流输入或发现结果绑定到命令配置中的独立参数。</summary>
public sealed class WorkflowArgumentBinding
{
    [JsonPropertyName("parameter")]
    public required string Parameter { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }
}

/// <summary>维护工作流步骤只引用高层 action 和命令配置，不携带可执行命令文本。</summary>
public sealed class WorkflowStepDefinition
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("action")]
    public required string Action { get; init; }

    [JsonPropertyName("commandProfileId")]
    public required string CommandProfileId { get; init; }

    [JsonPropertyName("bindings")]
    public IReadOnlyList<WorkflowArgumentBinding> Bindings { get; init; } = Array.Empty<WorkflowArgumentBinding>();
}

/// <summary>
/// 签名 Maintenance Extension 提供的工作流定义。宿主仍需执行 Discovery、Preflight、Policy 和身份复核。
/// </summary>
public sealed class WorkflowDefinition
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("targetType")]
    public required string TargetType { get; init; }

    [JsonPropertyName("riskLevel")]
    public required string RiskLevel { get; init; }

    [JsonPropertyName("inputs")]
    public IReadOnlyList<WorkflowInputDefinition> Inputs { get; init; } = Array.Empty<WorkflowInputDefinition>();

    [JsonPropertyName("steps")]
    public IReadOnlyList<WorkflowStepDefinition> Steps { get; init; } = Array.Empty<WorkflowStepDefinition>();
}

/// <summary>命令配置中的一个独立参数 token。</summary>
public sealed class CommandArgumentToken
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("value")]
    public required string Value { get; init; }
}

/// <summary>
/// Host 可审核的结构化命令配置。禁止提供 command、shell 或脚本拼接字段，最终执行计划由宿主生成。
/// </summary>
public sealed class CommandProfile
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("targetType")]
    public required string TargetType { get; init; }

    [JsonPropertyName("action")]
    public required string Action { get; init; }

    [JsonPropertyName("executable")]
    public required string Executable { get; init; }

    [JsonPropertyName("arguments")]
    public IReadOnlyList<CommandArgumentToken> Arguments { get; init; } = Array.Empty<CommandArgumentToken>();
}
