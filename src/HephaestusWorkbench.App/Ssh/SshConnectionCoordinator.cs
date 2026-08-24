using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Core.Services;

namespace HephaestusWorkbench.App.Ssh;

internal interface IHostKeyConfirmationService
{
    Task<bool> ConfirmAsync(SshHostKeyObservation observation, CancellationToken cancellationToken = default);
}

/// <summary>
/// 协调 Host Key TOFU 和连接历史。未知密钥仅在用户明确确认后写入仓储并重试一次；
/// 已变化密钥硬失败。每次物理连接独立写历史，且错误摘要不写入终端内容或凭据。
/// </summary>
internal sealed class SshConnectionCoordinator(
    ISshTerminalService terminalService,
    ISshHostKeyRepository hostKeys,
    ISshConnectionHistoryRepository history,
    IHostKeyConfirmationService confirmation,
    Func<DateTime> now)
{
    internal async Task<ITerminalSession> ConnectAsync(
        SshConnectionRequest request,
        SshCredentialSecret? credential,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await ConnectTrackedAsync(request, credential, cancellationToken).ConfigureAwait(false);
        }
        catch (SshHostKeyValidationException exception) when (exception.Reason == SshHostKeyFailureReason.Unknown)
        {
            if (!await confirmation.ConfirmAsync(exception.Observation, cancellationToken).ConfigureAwait(false))
                throw new OperationCanceledException("用户取消了未知 SSH Host Key 的信任确认。", exception, cancellationToken);

            var observedAt = now();
            await hostKeys.UpsertAsync(new SshHostKey
            {
                Host = exception.Observation.Host,
                Port = exception.Observation.Port,
                KeyAlgorithm = exception.Observation.KeyAlgorithm,
                Fingerprint = exception.Observation.Fingerprint,
                FirstSeenAt = observedAt,
                LastSeenAt = observedAt
            }, cancellationToken).ConfigureAwait(false);

            // TOFU 只允许在确认后重试一次；若观察值变化，M2 会按 Changed 再次硬失败。
            return await ConnectTrackedAsync(request, credential, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ITerminalSession> ConnectTrackedAsync(
        SshConnectionRequest request,
        SshCredentialSecret? credential,
        CancellationToken cancellationToken)
    {
        var historyId = Guid.NewGuid().ToString("N");
        await history.InsertAsync(new SshConnectionHistory
        {
            Id = historyId,
            DeviceId = request.DeviceId,
            Host = request.Host,
            Port = request.Port,
            Username = request.Username,
            ConnectedAt = now(),
            Outcome = SshConnectionOutcome.Connected
        }, cancellationToken).ConfigureAwait(false);

        try
        {
            var session = await terminalService.ConnectAsync(request, credential, cancellationToken).ConfigureAwait(false);
            return new HistoryTrackedTerminalSession(session, history, historyId, now);
        }
        catch (Exception exception)
        {
            await history.CompleteAsync(
                historyId,
                now(),
                MapOutcome(exception),
                SafeError(exception),
                cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static SshConnectionOutcome MapOutcome(Exception exception)
    {
        if (exception is SshHostKeyValidationException) return SshConnectionOutcome.HostKeyRejected;
        if (exception is TimeoutException) return SshConnectionOutcome.TimedOut;
        if (exception.GetType().Name.Contains("Authentication", StringComparison.OrdinalIgnoreCase))
            return SshConnectionOutcome.AuthenticationFailed;
        return SshConnectionOutcome.Failed;
    }

    private static string SafeError(Exception exception) => MapOutcome(exception) switch
    {
        SshConnectionOutcome.HostKeyRejected => "SSH Host Key 校验失败。",
        SshConnectionOutcome.AuthenticationFailed => "SSH 认证失败。",
        SshConnectionOutcome.TimedOut => "SSH 连接超时。",
        _ => "SSH 连接失败。"
    };
}

internal sealed class HistoryTrackedTerminalSession(
    ITerminalSession inner,
    ISshConnectionHistoryRepository history,
    string historyId,
    Func<DateTime> now) : ITerminalSession
{
    private int _disposed;
    public SshConnectionIdentity ConnectionIdentity => inner.ConnectionIdentity;
    public IInteractiveChannel InteractiveChannel => inner.InteractiveChannel;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await inner.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            await history.CompleteAsync(
                historyId,
                now(),
                SshConnectionOutcome.Disconnected,
                null).ConfigureAwait(false);
        }
    }
}
