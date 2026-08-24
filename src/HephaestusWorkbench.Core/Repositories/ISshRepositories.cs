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

    /// <summary>
    /// 返回最近成功建立过 SSH 会话的去重投影。默认实现确保旧的仓储测试替身仍可工作；
    /// 正式 SQLite 仓储会在数据库侧完成筛选与去重，避免把失败尝试展示为最近设备。
    /// </summary>
    async Task<IReadOnlyList<SshRecentConnection>> ListRecentSuccessfulAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "最近成功 SSH 连接查询数量必须大于 0。");

        var history = await ListRecentAsync(500, cancellationToken).ConfigureAwait(false);
        return history
            .Where(item => item.Outcome is SshConnectionOutcome.Connected or SshConnectionOutcome.Disconnected)
            .GroupBy(item => item.DeviceId is null
                ? $"target:{item.Host}\u001f{item.Port}\u001f{item.Username}"
                : $"device:{item.DeviceId}", StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.ConnectedAt).ThenBy(item => item.Id, StringComparer.Ordinal).First())
            .OrderByDescending(item => item.ConnectedAt)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Take(limit)
            .Select(item => new SshRecentConnection(item.DeviceId, item.Host, item.Port, item.Username, item.ConnectedAt))
            .ToArray();
    }
}
