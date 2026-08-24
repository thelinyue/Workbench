using HephaestusWorkbench.Core.Models;
using Renci.SshNet;

namespace HephaestusWorkbench.Services;

/// <summary>将 SSH.NET 2026.0.0 封装在程序集内部，仅向上层暴露稳定的自有适配接口。</summary>
internal sealed class SshNetClientFactory : ISshNetClientFactory
{
    public ISshNetClient Create(SshClientConfiguration configuration)
    {
        var request = configuration.Request;
        AuthenticationMethod authentication = request.AuthenticationMethod switch
        {
            SshAuthenticationMethod.Password when configuration.Secret is not null =>
                new PasswordAuthenticationMethod(request.Username, configuration.Secret),
            SshAuthenticationMethod.Password =>
                throw new InvalidOperationException("密码认证需要提供密码或可读取的凭据目标。"),
            SshAuthenticationMethod.PrivateKey when string.IsNullOrWhiteSpace(request.PrivateKeyPath) =>
                throw new InvalidOperationException("私钥认证需要提供私钥文件路径。"),
            SshAuthenticationMethod.PrivateKey =>
                new PrivateKeyAuthenticationMethod(
                    request.Username,
                    configuration.Secret is null
                        ? new PrivateKeyFile(request.PrivateKeyPath!)
                        : new PrivateKeyFile(request.PrivateKeyPath!, configuration.Secret)),
            _ => throw new InvalidOperationException("不支持的 SSH 认证方式。")
        };

        var connection = new ConnectionInfo(request.Host, request.Port, request.Username, authentication);
        return new SshNetClientAdapter(new SshClient(connection));
    }
}

internal sealed class SshNetClientAdapter(SshClient client) : ISshNetClient
{
    public async Task ConnectAsync(Func<SshHostKeyCandidate, bool> validateHostKey, CancellationToken cancellationToken)
    {
        void OnHostKeyReceived(object? _, Renci.SshNet.Common.HostKeyEventArgs args)
        {
            var fingerprint = args.FingerPrintSHA256.StartsWith("SHA256:", StringComparison.Ordinal)
                ? args.FingerPrintSHA256
                : $"SHA256:{args.FingerPrintSHA256}";
            args.CanTrust = validateHostKey(new SshHostKeyCandidate(args.HostKeyName, fingerprint));
        }

        client.HostKeyReceived += OnHostKeyReceived;
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            client.HostKeyReceived -= OnHostKeyReceived;
        }
    }

    public ISshShellChannel OpenShell(string terminalName, int columns, int rows) =>
        new SshNetShellChannel(client.CreateShellStream(
            terminalName,
            checked((uint)columns),
            checked((uint)rows),
            0,
            0,
            64 * 1024));

    public ISshCommandChannel CreateCommand(string commandText) => new SshNetCommandChannel(client.CreateCommand(commandText));

    public void Disconnect()
    {
        if (client.IsConnected)
            client.Disconnect();
    }

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class SshNetShellChannel(ShellStream shell) : ISshShellChannel
{
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        shell.ReadAsync(buffer, cancellationToken);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        shell.WriteAsync(data, cancellationToken);

    public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (columns <= 0 || rows <= 0)
            throw new ArgumentOutOfRangeException(nameof(columns), "终端列数和行数必须大于零。");
        shell.ChangeWindowSize(checked((uint)columns), checked((uint)rows), 0, 0);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        shell.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class SshNetCommandChannel(SshCommand command) : ISshCommandChannel
{
    public Stream StandardOutput => command.OutputStream;
    public Stream StandardError => command.ExtendedOutputStream;
    public int? ExitStatus => command.ExitStatus;
    public Task ExecuteAsync(CancellationToken cancellationToken) => command.ExecuteAsync(cancellationToken);
    public ValueTask DisposeAsync()
    {
        command.Dispose();
        return ValueTask.CompletedTask;
    }
}
