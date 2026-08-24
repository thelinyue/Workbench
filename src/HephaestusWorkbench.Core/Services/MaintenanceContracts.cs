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

/// <summary>Maintenance-M1 只冻结执行边界；真实 SSH exec 实现由后续里程碑提供。</summary>
public interface IMaintenanceExecutor
{
    IAsyncEnumerable<MaintenanceOperationEvent> ExecuteAsync(
        ExecutionPlan plan,
        CancellationToken cancellationToken = default);
}
