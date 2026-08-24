using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Core.Services;

namespace HephaestusWorkbench.Services;

/// <summary>创建独占 SSH.NET PTY Shell；每次调用均创建新的底层连接，不与命令执行共享。</summary>
public sealed class SshNetTerminalService : ISshTerminalService
{
    private readonly SshNetConnectionOpener _connections;

    public SshNetTerminalService(ISshHostKeyRepository hostKeys, ICredentialStore credentials)
        : this(hostKeys, credentials, new SshNetClientFactory())
    {
    }

    internal SshNetTerminalService(
        ISshHostKeyRepository hostKeys,
        ICredentialStore credentials,
        ISshNetClientFactory clients) =>
        _connections = new SshNetConnectionOpener(hostKeys, credentials, clients);

    public async Task<ITerminalSession> ConnectAsync(
        SshConnectionRequest request,
        SshCredentialSecret? credential,
        CancellationToken cancellationToken = default)
    {
        var client = await _connections.OpenAsync(request, credential, cancellationToken).ConfigureAwait(false);
        try
        {
            var shell = client.OpenShell("xterm-256color", 80, 24);
            return new SshNetTerminalSession(
                new SshConnectionIdentity(request.DeviceId, request.Host, request.Port, request.Username),
                client,
                shell);
        }
        catch
        {
            client.Disconnect();
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>拥有 Shell 与 SSH client 的完整生命周期；释放顺序确保读取先结束，再断开连接。</summary>
internal sealed class SshNetTerminalSession : ITerminalSession
{
    private readonly ISshNetClient _client;
    private readonly ISshShellChannel _shell;
    private int _disposed;

    public SshNetTerminalSession(SshConnectionIdentity identity, ISshNetClient client, ISshShellChannel shell)
    {
        ConnectionIdentity = identity;
        _client = client;
        _shell = shell;
        InteractiveChannel = new SshNetInteractiveChannel(shell);
    }

    public SshConnectionIdentity ConnectionIdentity { get; }
    public IInteractiveChannel InteractiveChannel { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            await _shell.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                _client.Disconnect();
            }
            finally
            {
                await _client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

internal sealed class SshNetInteractiveChannel(ISshShellChannel shell) : IInteractiveChannel
{
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        shell.ReadAsync(buffer, cancellationToken);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        shell.WriteAsync(data, cancellationToken);

    public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default) =>
        shell.ResizeAsync(columns, rows, cancellationToken);
}
