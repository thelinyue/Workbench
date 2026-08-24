using HephaestusWorkbench.Core.Models;

namespace HephaestusWorkbench.Core.Repositories;

/// <summary>保存 SSH 设备的非敏感配置。</summary>
public interface ISshDeviceRepository
{
    Task<IReadOnlyList<SshDevice>> ListAsync(CancellationToken cancellationToken = default);
    Task<SshDevice?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task UpsertAsync(SshDevice device, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>保存用户明确确认过的 Host Key TOFU 记录。</summary>
public interface ISshHostKeyRepository
{
    Task<SshHostKey?> GetAsync(string host, int port, CancellationToken cancellationToken = default);
    Task UpsertAsync(SshHostKey hostKey, CancellationToken cancellationToken = default);
}

/// <summary>保存 SSH 连接结果；不记录交互命令、终端内容或凭据。</summary>
public interface ISshConnectionHistoryRepository
{
    Task InsertAsync(SshConnectionHistory history, CancellationToken cancellationToken = default);
    Task CompleteAsync(
        string id,
        DateTime disconnectedAt,
        SshConnectionOutcome outcome,
        string? errorMessage,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SshConnectionHistory>> ListRecentAsync(int limit, CancellationToken cancellationToken = default);
}
