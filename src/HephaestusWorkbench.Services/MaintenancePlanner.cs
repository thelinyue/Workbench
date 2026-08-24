using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Services;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 将已验证工作流和命令配置绑定为不可变计划。用户输入和 Discovery 值只能成为独立参数 token，
/// 不能进入 executable、模板替换或自由 Shell 字符串。
/// </summary>
public sealed class MaintenancePlanner : IMaintenancePlanner
{
    private static readonly HashSet<string> ShellNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "sh", "bash", "dash", "zsh", "ash", "ksh"
    };

    public ExecutionPlan CreatePlan(MaintenancePlanningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Preflight.Errors.Count > 0)
            throw new InvalidOperationException($"Preflight 未通过：{string.Join("；", request.Preflight.Errors)}");
        if (!string.Equals(request.Workflow.TargetType, request.Preflight.TargetType, StringComparison.Ordinal))
            throw new InvalidOperationException("工作流 targetType 与 Preflight 目标类型不一致。");

        foreach (var input in request.Workflow.Inputs.Where(item => item.Required))
        {
            if (!request.UserInputs.TryGetValue(input.Id, out var value) || string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"必填输入 {input.Label}（{input.Id}）尚未提供。");
        }

        var targets = request.Preflight.StableTargets
            .Where(target => string.Equals(target.StableIdentity, request.SelectedStableIdentity, StringComparison.Ordinal))
            .ToArray();
        if (targets.Length != 1 || string.IsNullOrWhiteSpace(targets[0].StableIdentity))
            throw new InvalidOperationException("选定 stableIdentity 必须唯一匹配一个稳定目标。");

        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        var steps = new List<ExecutionStep>();
        for (var index = 0; index < request.Workflow.Steps.Count; index++)
        {
            var step = request.Workflow.Steps[index];
            if (!stepIds.Add(step.Id)) throw new InvalidOperationException($"工作流步骤 id 重复：{step.Id}");
            if (!request.CommandProfiles.TryGetValue(step.CommandProfileId, out var profile))
                throw new InvalidOperationException($"步骤 {step.Id} 引用的命令配置不存在：{step.CommandProfileId}");
            if (!string.Equals(profile.Id, step.CommandProfileId, StringComparison.Ordinal))
                throw new InvalidOperationException($"步骤 {step.Id} 的 profile id 不一致。");
            if (!string.Equals(profile.TargetType, request.Workflow.TargetType, StringComparison.Ordinal))
                throw new InvalidOperationException($"步骤 {step.Id} 的 profile targetType 与工作流不一致。");
            if (!string.Equals(profile.Action, step.Action, StringComparison.Ordinal))
                throw new InvalidOperationException($"步骤 {step.Id} 的 profile action 与工作流 action 不一致。");

            RejectFreeShell(profile);
            var bindings = new Dictionary<string, MaintenanceArgumentBindingSnapshot>(StringComparer.Ordinal);
            foreach (var binding in step.Bindings)
            {
                if (!bindings.TryAdd(binding.Parameter, binding))
                    throw new InvalidOperationException($"步骤 {step.Id} 的参数 binding 重复：{binding.Parameter}");
            }

            var dynamicParameters = profile.Arguments
                .Where(token => token.Kind is MaintenanceCommandArgumentKind.Input or MaintenanceCommandArgumentKind.Discovery)
                .Select(token => token.Value)
                .ToHashSet(StringComparer.Ordinal);
            var unknownBinding = bindings.Keys.FirstOrDefault(parameter => !dynamicParameters.Contains(parameter));
            if (unknownBinding is not null)
                throw new InvalidOperationException($"步骤 {step.Id} 包含未知参数 binding：{unknownBinding}");

            var arguments = new List<string>(profile.Arguments.Count);
            foreach (var token in profile.Arguments)
            {
                arguments.Add(token.Kind switch
                {
                    MaintenanceCommandArgumentKind.Literal => token.Value,
                    MaintenanceCommandArgumentKind.Input => ResolveBoundValue(step.Id, token.Value, bindings, request.UserInputs, "用户输入"),
                    MaintenanceCommandArgumentKind.Discovery => ResolveBoundValue(step.Id, token.Value, bindings, request.DiscoveryValues, "发现值"),
                    _ => throw new InvalidOperationException($"步骤 {step.Id} 包含未知命令参数类型。")
                });
            }

            if (IsShell(profile.Executable) && arguments.Any(argument => string.Equals(argument, "-c", StringComparison.Ordinal)))
                throw new InvalidOperationException($"步骤 {step.Id} 禁止通过 Shell -c 执行自由命令。");

            steps.Add(new ExecutionStep(
                step.Id, index, step.Name, profile.Executable,
                Array.AsReadOnly(arguments.ToArray()),
                request.Workflow.RiskLevel == MaintenanceRiskLevel.ReadOnly));
        }

        return new ExecutionPlan
        {
            Id = request.PlanId,
            WorkflowId = request.Workflow.Id,
            WorkflowVersion = request.Workflow.Version,
            ExtensionId = request.ExtensionId,
            ExtensionVersion = request.ExtensionVersion,
            DeviceId = request.DeviceId,
            TargetType = request.Workflow.TargetType,
            RiskLevel = request.Workflow.RiskLevel,
            Target = targets[0] with { },
            RequiresDeviceNameConfirmation = request.Workflow.RiskLevel == MaintenanceRiskLevel.High,
            CreatedAt = request.CreatedAt,
            Steps = Array.AsReadOnly(steps.ToArray())
        };
    }

    private static string ResolveBoundValue(
        string stepId,
        string parameter,
        IReadOnlyDictionary<string, MaintenanceArgumentBindingSnapshot> bindings,
        IReadOnlyDictionary<string, string> values,
        string sourceDescription)
    {
        if (!bindings.TryGetValue(parameter, out var binding))
            throw new InvalidOperationException($"步骤 {stepId} 的参数 {parameter} 缺少 binding。");
        if (!values.TryGetValue(binding.Source, out var value))
            throw new InvalidOperationException($"步骤 {stepId} 引用了未知{sourceDescription}：{binding.Source}");
        return value;
    }

    private static void RejectFreeShell(MaintenanceCommandProfileSnapshot profile)
    {
        if (ContainsShellExpression(profile.Executable) ||
            profile.Arguments.Any(token => token.Kind == MaintenanceCommandArgumentKind.Literal && ContainsShellExpression(token.Value)))
        {
            throw new InvalidOperationException($"命令配置 {profile.Id} 禁止包含反引号或 $() Shell 表达式。");
        }
        if (IsShell(profile.Executable) && profile.Arguments.Any(token =>
                token.Kind == MaintenanceCommandArgumentKind.Literal && string.Equals(token.Value, "-c", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"命令配置 {profile.Id} 禁止使用 Shell -c。");
        }
    }

    private static bool ContainsShellExpression(string value) => value.Contains('`') || value.Contains("$(", StringComparison.Ordinal);
    private static bool IsShell(string executable) => ShellNames.Contains(Path.GetFileName(executable));
}

/// <summary>执行前统一实施目标类型、自动维护权限、高风险确认和稳定身份复核。</summary>
public sealed class MaintenancePolicy : IMaintenancePolicy
{
    public MaintenancePolicyDecision Evaluate(ExecutionPlan plan, PreflightResult preflight, bool automatic)
    {
        var errors = new List<string>();
        if (!string.Equals(plan.TargetType, "linux-open-ssh", StringComparison.Ordinal) ||
            !string.Equals(preflight.TargetType, "linux-open-ssh", StringComparison.Ordinal))
            errors.Add("维护只支持 linux-open-ssh 目标。");
        if (preflight.Errors.Count > 0) errors.AddRange(preflight.Errors);
        if (automatic && !preflight.IsRoot && !preflight.IsPasswordlessSudoAvailable)
            errors.Add("自动维护要求 root 或可用的 sudo -n 权限。");

        switch (plan.RiskLevel)
        {
            case MaintenanceRiskLevel.ReadOnly:
                break;
            case MaintenanceRiskLevel.High:
                if (!plan.RequiresDeviceNameConfirmation) errors.Add("高风险计划必须要求设备名称二次确认。");
                break;
            default:
                errors.Add("未知维护风险级别，已拒绝执行。");
                break;
        }
        return Decision(errors);
    }

    public MaintenancePolicyDecision VerifyExecution(
        ExecutionPlan plan,
        PreflightResult preflight,
        string? confirmationDisplayName,
        bool automatic)
    {
        var errors = Evaluate(plan, preflight, automatic).Errors.ToList();
        var matches = preflight.StableTargets.Where(target =>
            string.Equals(target.StableIdentity, plan.Target.StableIdentity, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
            errors.Add("执行前身份复核未唯一匹配计划中的 stableIdentity。");
        if (plan.RiskLevel == MaintenanceRiskLevel.High &&
            !string.Equals(confirmationDisplayName, plan.Target.DisplayName, StringComparison.Ordinal))
            errors.Add($"高风险计划必须输入设备名称 {plan.Target.DisplayName} 进行二次确认。");
        return Decision(errors);
    }

    private static MaintenancePolicyDecision Decision(List<string> errors) =>
        new(errors.Count == 0, Array.AsReadOnly(errors.ToArray()));
}
