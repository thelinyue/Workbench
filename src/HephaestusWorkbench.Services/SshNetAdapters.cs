using HephaestusWorkbench.Core.Models;

namespace HephaestusWorkbench.Services;

/// <summary>
/// SSH.NET 的内部可测试边界。所有类型均限制在 Services 程序集内部，避免将第三方库类型泄露到 Core 契约。
/// </summary>
internal sealed record SshClientConfiguration(SshConnectionRequest Request, string? Secret);

internal sealed record SshHostKeyCandidate(string KeyAlgorithm, string Fingerprint);

internal interface ISshNetClientFactory
{
    ISshNetClient Create(SshClientConfiguration configuration);
}

internal interface ISshNetClient : IAsyncDisposable
{
    Task ConnectAsync(Func<SshHostKeyCandidate, bool> validateHostKey, CancellationToken cancellationToken);
    ISshShellChannel OpenShell(string terminalName, int columns, int rows);
    ISshCommandChannel CreateCommand(string commandText);
    void Disconnect();
}

internal interface ISshShellChannel : IAsyncDisposable
{
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
    ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken);
}

internal interface ISshCommandChannel : IAsyncDisposable
{
    Stream StandardOutput { get; }
    Stream StandardError { get; }
    int? ExitStatus { get; }
    Task ExecuteAsync(CancellationToken cancellationToken);
}
