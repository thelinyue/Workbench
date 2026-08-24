using System.Text.Json.Serialization;

namespace HephaestusWorkbench.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaintenanceRiskLevel { ReadOnly, High }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaintenanceCommandArgumentKind { Literal, Input, Discovery }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaintenanceOperationStatus { Planned, Running, StopRequested, Succeeded, Failed, OutcomeUnknown }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaintenanceStepStatus { Pending, Running, Succeeded, Failed, Skipped, OutcomeUnknown }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaintenanceOperationEventKind { OperationStatusChanged, StepStatusChanged, Message }

/// <summary>Discovery 产生的稳定目标。设备路径只能用于展示，执行身份必须使用 stableIdentity。</summary>
public sealed record StableMaintenanceTarget(string Kind, string DisplayName, string StableIdentity);

/// <summary>执行计划生成前的权限、目标和诊断快照，不携带 SSH 或扩展实现类型。</summary>
public sealed record class PreflightResult
{
    public required string TargetType { get; init; }
    public required string RemoteUsername { get; init; }
    public bool IsRoot { get; init; }
    public bool IsPasswordlessSudoAvailable { get; init; }
    public IReadOnlyDictionary<string, string> SystemInformation { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> DiscoveryValues { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<StableMaintenanceTarget> StableTargets { get; init; } = Array.Empty<StableMaintenanceTarget>();
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>Host 从签名扩展内容转换出的工作流输入快照。</summary>
public sealed record MaintenanceWorkflowInputSnapshot(string Id, string Label, string Type, bool Required);

/// <summary>将命令配置参数名绑定到用户输入或 Discovery key。</summary>
public sealed record MaintenanceArgumentBindingSnapshot(string Parameter, string Source);

/// <summary>工作流步骤快照只引用高层 action 与 profile id，不包含自由命令文本。</summary>
public sealed record MaintenanceWorkflowStepSnapshot(
    string Id,
    string Name,
    string Action,
    string CommandProfileId,
    IReadOnlyList<MaintenanceArgumentBindingSnapshot> Bindings);

/// <summary>通过 Catalog 严格校验后交给 Planner 的宿主自有工作流快照。</summary>
public sealed record MaintenanceWorkflowSnapshot(
    string Id,
    string Name,
    string Version,
    string TargetType,
    MaintenanceRiskLevel RiskLevel,
    IReadOnlyList<MaintenanceWorkflowInputSnapshot> Inputs,
    IReadOnlyList<MaintenanceWorkflowStepSnapshot> Steps);

/// <summary>命令参数保持独立 token；Planner 不执行模板或 shell 字符串替换。</summary>
public sealed record MaintenanceCommandArgumentTokenSnapshot(MaintenanceCommandArgumentKind Kind, string Value);

/// <summary>通过 Catalog 严格校验后的结构化命令配置快照。</summary>
public sealed record MaintenanceCommandProfileSnapshot(
    string Id,
    string TargetType,
    string Action,
    string Executable,
    IReadOnlyList<MaintenanceCommandArgumentTokenSnapshot> Arguments);

/// <summary>Planner 的全部输入；调用方必须传入已取得版本租约对应的扩展身份。</summary>
public sealed record class MaintenancePlanningRequest
{
    public required string PlanId { get; init; }
    public required MaintenanceWorkflowSnapshot Workflow { get; init; }
    public required IReadOnlyDictionary<string, MaintenanceCommandProfileSnapshot> CommandProfiles { get; init; }
    public required string ExtensionId { get; init; }
    public required string ExtensionVersion { get; init; }
    public required string DeviceId { get; init; }
    public required string SelectedStableIdentity { get; init; }
    public required IReadOnlyDictionary<string, string> UserInputs { get; init; }
    public required IReadOnlyDictionary<string, string> DiscoveryValues { get; init; }
    public required PreflightResult Preflight { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>不可变执行步骤；参数已经绑定为独立 token，但尚未执行。</summary>
public sealed record ExecutionStep(
    string Id,
    int Index,
    string Name,
    string Executable,
    IReadOnlyList<string> Arguments,
    bool IsReadOnly);

/// <summary>用户确认后不可修改的执行计划快照。</summary>
public sealed record class ExecutionPlan
{
    public required string Id { get; init; }
    public required string WorkflowId { get; init; }
    public required string WorkflowVersion { get; init; }
    public required string ExtensionId { get; init; }
    public required string ExtensionVersion { get; init; }
    public required string DeviceId { get; init; }
    public required string TargetType { get; init; }
    public MaintenanceRiskLevel RiskLevel { get; init; }
    public required StableMaintenanceTarget Target { get; init; }
    public bool RequiresDeviceNameConfirmation { get; init; }
    public DateTime CreatedAt { get; init; }
    public IReadOnlyList<ExecutionStep> Steps { get; init; } = Array.Empty<ExecutionStep>();
}

/// <summary>策略判断结果。错误文本可直接用于中文日志或后续确认界面。</summary>
public sealed record MaintenancePolicyDecision(bool IsAllowed, IReadOnlyList<string> Errors);

/// <summary>执行入口参数；高风险确认文本和自动执行标志必须在执行前策略复核中使用。</summary>
public sealed record class MaintenanceExecutionRequest
{
    public required ExecutionPlan Plan { get; init; }
    public string? ConfirmationDisplayName { get; init; }
    public bool Automatic { get; init; }
}

/// <summary>持久化的一次维护操作；stdout/stderr 内容始终保存在外部文件。</summary>
public sealed record class MaintenanceOperation
{
    public required string Id { get; init; }
    public required string WorkflowId { get; init; }
    public required string WorkflowVersion { get; init; }
    public required string ExtensionId { get; init; }
    public required string ExtensionVersion { get; init; }
    public required string DeviceId { get; init; }
    public MaintenanceOperationStatus Status { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string? OutcomeSummary { get; init; }
    public required string OperationDirectory { get; init; }
    public IReadOnlyList<MaintenanceOperationStep> Steps { get; init; } = Array.Empty<MaintenanceOperationStep>();
}

/// <summary>持久化的步骤状态和输出文件相对路径，不保存输出正文。</summary>
public sealed record class MaintenanceOperationStep
{
    public required string Id { get; init; }
    public required string OperationId { get; init; }
    public int Index { get; init; }
    public required string Name { get; init; }
    public MaintenanceStepStatus Status { get; init; }
    public required string Executable { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public string? StdoutPath { get; init; }
    public string? StderrPath { get; init; }
    public int? ExitCode { get; init; }
    public TimeSpan? Duration { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
}

/// <summary>仅允许更新步骤运行结果，不能改写 executable、arguments 或步骤身份。</summary>
public sealed record class MaintenanceOperationStepUpdate
{
    public required string StepId { get; init; }
    public MaintenanceStepStatus Status { get; init; }
    public string? StdoutPath { get; init; }
    public string? StderrPath { get; init; }
    public int? ExitCode { get; init; }
    public TimeSpan? Duration { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
}

/// <summary>Executor 对外发布的可序列化状态事件。</summary>
public sealed record MaintenanceOperationEvent(
    string OperationId,
    string? StepId,
    MaintenanceOperationEventKind Kind,
    DateTime Timestamp,
    string? Message,
    MaintenanceOperationStatus? OperationStatus,
    MaintenanceStepStatus? StepStatus);
