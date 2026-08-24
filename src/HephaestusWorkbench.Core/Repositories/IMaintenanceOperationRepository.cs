using HephaestusWorkbench.Core.Models;

namespace HephaestusWorkbench.Core.Repositories;

/// <summary>维护操作仓储；操作与初始步骤必须原子创建，启动恢复不得自动重放。</summary>
public interface IMaintenanceOperationRepository
{
    Task CreateAsync(MaintenanceOperation operation, bool isReadOnly, CancellationToken cancellationToken = default);
    Task<MaintenanceOperation?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MaintenanceOperation>> ListRecentAsync(int limit, CancellationToken cancellationToken = default);
    Task UpdateOperationAsync(string id, MaintenanceOperationStatus status, DateTime? completedAt, string? outcomeSummary, CancellationToken cancellationToken = default);
    Task UpdateStepAsync(MaintenanceOperationStepUpdate update, CancellationToken cancellationToken = default);
    Task<bool> HasActiveOperationAsync(string deviceId, CancellationToken cancellationToken = default);
    Task<int> RecoverInterruptedAsync(DateTime recoveredAt, CancellationToken cancellationToken = default);
}
