using HephaestusWorkbench.Core.Models;

namespace HephaestusWorkbench.Core.Services;

/// <summary>按固定 id 解析已经过扩展安装、签名和版本租约校验的维护内容。</summary>
public interface IMaintenanceCatalog
{
    Task<MaintenanceWorkflowSnapshot> ResolveWorkflowAsync(string id, CancellationToken cancellationToken = default);
    Task<MaintenanceCommandProfileSnapshot> ResolveCommandProfileAsync(string id, CancellationToken cancellationToken = default);
}

public interface IMaintenancePlanner
{
    ExecutionPlan CreatePlan(MaintenancePlanningRequest request);
}

public interface IMaintenancePolicy
{
    MaintenancePolicyDecision Evaluate(ExecutionPlan plan, PreflightResult preflight, bool automatic);
    MaintenancePolicyDecision VerifyExecution(ExecutionPlan plan, PreflightResult preflight, string? confirmationDisplayName, bool automatic);
}

/// <summary>执行计划前必须重新 Discovery 并通过 Host Policy；远端状态不明时不得自动重放。</summary>
public interface IMaintenanceExecutor
{
    IAsyncEnumerable<MaintenanceOperationEvent> ExecuteAsync(
        MaintenanceExecutionRequest request,
        CancellationToken cancellationToken = default);
}
