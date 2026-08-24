using System.Text.Json;
using System.Text.Json.Serialization;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Services;
using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 从已通过 ExtensionRegistry 校验并持有版本租约的 maintenance content 目录读取固定定义文件。
/// 本类不扫描任意路径，不接受 manifest 自定义入口，并拒绝重解析点、超限文件和未知 JSON 字段。
/// </summary>
public sealed class MaintenanceContentCatalog : IMaintenanceCatalog
{
    public const int MaximumDefinitionBytes = 256 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _versionDirectory;

    public MaintenanceContentCatalog(string versionDirectory, string extensionId, string extensionVersion)
    {
        if (string.IsNullOrWhiteSpace(versionDirectory)) throw new ArgumentException("维护扩展版本目录不能为空。", nameof(versionDirectory));
        if (!IsIdentifier(extensionId)) throw new ArgumentException("维护扩展 id 无效。", nameof(extensionId));
        if (string.IsNullOrWhiteSpace(extensionVersion)) throw new ArgumentException("维护扩展版本不能为空。", nameof(extensionVersion));
        _versionDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(versionDirectory));
    }

    public async Task<MaintenanceWorkflowSnapshot> ResolveWorkflowAsync(string id, CancellationToken cancellationToken = default)
    {
        var definition = await ReadAsync<WorkflowDefinition>("workflows", id, cancellationToken);
        ValidateWorkflow(definition, id);
        return new MaintenanceWorkflowSnapshot(
            definition.Id, definition.Name, definition.Version, definition.TargetType,
            ParseRisk(definition.RiskLevel),
            Array.AsReadOnly(definition.Inputs.Select(item => new MaintenanceWorkflowInputSnapshot(item.Id, item.Label, item.Type, item.Required)).ToArray()),
            Array.AsReadOnly(definition.Steps.Select(step => new MaintenanceWorkflowStepSnapshot(
                step.Id, step.Name, step.Action, step.CommandProfileId,
                Array.AsReadOnly(step.Bindings.Select(binding => new MaintenanceArgumentBindingSnapshot(binding.Parameter, binding.Source)).ToArray()))).ToArray()));
    }

    public async Task<MaintenanceCommandProfileSnapshot> ResolveCommandProfileAsync(string id, CancellationToken cancellationToken = default)
    {
        var profile = await ReadAsync<CommandProfile>("command-profiles", id, cancellationToken);
        ValidateProfile(profile, id);
        return new MaintenanceCommandProfileSnapshot(
            profile.Id, profile.TargetType, profile.Action, profile.Executable,
            Array.AsReadOnly(profile.Arguments.Select(token => new MaintenanceCommandArgumentTokenSnapshot(
                ParseArgumentKind(token.Kind), token.Value)).ToArray()));
    }

    private async Task<T> ReadAsync<T>(string category, string id, CancellationToken cancellationToken)
    {
        if (!IsIdentifier(id)) throw new InvalidDataException($"维护定义标识无效：{id}");
        EnsureNotReparse(_versionDirectory, "维护扩展版本目录");
        var categoryDirectory = Path.Combine(_versionDirectory, category);
        EnsureDirectChild(_versionDirectory, categoryDirectory, "维护定义目录");
        EnsureNotReparse(categoryDirectory, "维护定义目录");
        var path = Path.Combine(categoryDirectory, id + ".json");
        EnsureDirectChild(categoryDirectory, path, "维护定义文件");
        EnsureNotReparse(path, "维护定义文件");
        if (!File.Exists(path)) throw new FileNotFoundException($"维护定义不存在：{id}", path);

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > MaximumDefinitionBytes)
            throw new InvalidDataException($"维护定义 {id} 的文件大小必须在 1 到 {MaximumDefinitionBytes} 字节之间。");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);

        try
        {
            using var document = JsonDocument.Parse(bytes);
            RejectDuplicateProperties(document.RootElement, "$", id);
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new JsonException("定义内容为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"维护定义 {id} 的 JSON 结构无效：{exception.Message}", exception);
        }
    }

    private static void ValidateWorkflow(WorkflowDefinition workflow, string requestedId)
    {
        if (workflow.SchemaVersion != 2) throw new InvalidDataException("维护工作流 schemaVersion 必须为 2。");
        if (!string.Equals(workflow.Id, requestedId, StringComparison.Ordinal)) throw new InvalidDataException("维护工作流 id 与文件名不一致。");
        RequireText(workflow.Name, "维护工作流 name");
        RequireText(workflow.Version, "维护工作流 version");
        RequireText(workflow.TargetType, "维护工作流 targetType");
        _ = ParseRisk(workflow.RiskLevel);
        if (workflow.Inputs is null || workflow.Steps is null || workflow.Steps.Count == 0)
            throw new InvalidDataException("维护工作流 inputs 和 steps 不能为空，且至少需要一个步骤。");
        EnsureUnique(workflow.Inputs.Select(item => item?.Id), "维护工作流 input id");
        EnsureUnique(workflow.Steps.Select(item => item?.Id), "维护工作流 step id");
        foreach (var input in workflow.Inputs)
        {
            if (input is null) throw new InvalidDataException("维护工作流 inputs 不能包含 null。");
            RequireText(input.Id, "维护工作流 input id"); RequireText(input.Label, "维护工作流 input label"); RequireText(input.Type, "维护工作流 input type");
        }
        foreach (var step in workflow.Steps)
        {
            if (step is null || step.Bindings is null) throw new InvalidDataException("维护工作流 step 或 bindings 不能为空。");
            RequireText(step.Id, "维护工作流 step id"); RequireText(step.Name, "维护工作流 step name");
            RequireText(step.Action, "维护工作流 action"); RequireText(step.CommandProfileId, "维护工作流 commandProfileId");
            EnsureUnique(step.Bindings.Select(item => item?.Parameter), $"步骤 {step.Id} 的 binding parameter");
            foreach (var binding in step.Bindings)
            {
                if (binding is null) throw new InvalidDataException($"步骤 {step.Id} 的 bindings 不能包含 null。");
                RequireText(binding.Parameter, "binding parameter"); RequireText(binding.Source, "binding source");
            }
        }
    }

    private static void ValidateProfile(CommandProfile profile, string requestedId)
    {
        if (profile.SchemaVersion != 2) throw new InvalidDataException("命令配置 schemaVersion 必须为 2。");
        if (!string.Equals(profile.Id, requestedId, StringComparison.Ordinal)) throw new InvalidDataException("命令配置 id 与文件名不一致。");
        RequireText(profile.TargetType, "命令配置 targetType"); RequireText(profile.Action, "命令配置 action"); RequireText(profile.Executable, "命令配置 executable");
        if (profile.Arguments is null) throw new InvalidDataException("命令配置 arguments 不能为空。");
        foreach (var token in profile.Arguments)
        {
            if (token is null) throw new InvalidDataException("命令配置 arguments 不能包含 null。");
            _ = ParseArgumentKind(token.Kind); RequireText(token.Value, "命令参数 value");
        }
    }

    private static MaintenanceRiskLevel ParseRisk(string value) => value switch
    {
        MaintenanceRiskLevels.ReadOnly => MaintenanceRiskLevel.ReadOnly,
        MaintenanceRiskLevels.High => MaintenanceRiskLevel.High,
        _ => throw new InvalidDataException($"未知的维护风险级别：{value}")
    };

    private static MaintenanceCommandArgumentKind ParseArgumentKind(string value) => value switch
    {
        CommandArgumentKinds.Literal => MaintenanceCommandArgumentKind.Literal,
        CommandArgumentKinds.Input => MaintenanceCommandArgumentKind.Input,
        CommandArgumentKinds.Discovery => MaintenanceCommandArgumentKind.Discovery,
        _ => throw new InvalidDataException($"未知的命令参数类型：{value}")
    };

    private static void RejectDuplicateProperties(JsonElement element, string path, string id)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException($"{path} 包含重复字段 {property.Name}。");
                RejectDuplicateProperties(property.Value, path + "." + property.Name, id);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item, $"{path}[{index++}]", id);
        }
    }

    private static void EnsureDirectChild(string parent, string child, string description)
    {
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var normalizedChild = Path.GetFullPath(child);
        if (!string.Equals(Path.GetDirectoryName(normalizedChild), normalizedParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{description}必须直接位于受信目录内：{normalizedChild}");
    }

    private static void EnsureNotReparse(string path, string description)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return;
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException($"{description}不能是重解析点：{path}");
    }

    private static bool IsIdentifier(string? value) => value is { Length: > 0 } &&
        value.Split('.', '-').All(segment => segment.Length > 0 && segment.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9'));

    private static void EnsureUnique(IEnumerable<string?> values, string description)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || !seen.Add(value)) throw new InvalidDataException($"{description} 为空或重复：{value}");
        }
    }

    private static void RequireText(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"{description} 不能为空。");
    }
}
