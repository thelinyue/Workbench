using System.Diagnostics;
using System.Text;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Core.Services;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 使用独立 SSH.NET exec connection 执行结构化命令。所有 token 均由 Host 统一进行 POSIX 单引号转义，
/// stdout/stderr 分流逐块回调并等待确认，避免将完整输出无限积存在内存。
/// </summary>
public sealed class SshNetCommandExecutionService : ICommandExecutionService
{
    private const int OutputBufferSize = 4096;
    private readonly SshNetConnectionOpener _connections;

    public SshNetCommandExecutionService(ISshHostKeyRepository hostKeys, ICredentialStore credentials)
        : this(hostKeys, credentials, new SshNetClientFactory())
    {
    }

    internal SshNetCommandExecutionService(
        ISshHostKeyRepository hostKeys,
        ICredentialStore credentials,
        ISshNetClientFactory clients) =>
        _connections = new SshNetConnectionOpener(hostKeys, credentials, clients);

    public async Task<RemoteCommandResult> ExecuteAsync(
        RemoteCommandRequest request,
        SshCredentialSecret? credential,
        Func<RemoteCommandOutputChunk, CancellationToken, ValueTask> onOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onOutput);
        if (request.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request), "SSH 命令超时时间必须大于零。");

        var commandText = PosixCommandBuilder.Build(request.Executable, request.Arguments);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        var stopwatch = Stopwatch.StartNew();
        var client = await _connections.OpenAsync(request.Connection, credential, timeout.Token).ConfigureAwait(false);

        try
        {
            await using var command = client.CreateCommand(commandText);
            using var callbackGate = new SemaphoreSlim(1, 1);
            long sequence = 0;
            var execution = ExecuteCommandAsync();
            var stdout = PumpAsync(command.StandardOutput, RemoteCommandOutputStream.Stdout);
            var stderr = PumpAsync(command.StandardError, RemoteCommandOutputStream.Stderr);
            await Task.WhenAll(execution, stdout, stderr).ConfigureAwait(false);
            stopwatch.Stop();
            return new RemoteCommandResult(
                command.ExitStatus ?? throw new InvalidOperationException("SSH 命令结束后未返回 Exit Code。"),
                stopwatch.Elapsed);

            async Task ExecuteCommandAsync()
            {
                try
                {
                    await command.ExecuteAsync(timeout.Token).ConfigureAwait(false);
                }
                catch
                {
                    await timeout.CancelAsync().ConfigureAwait(false);
                    throw;
                }
            }

            async Task PumpAsync(Stream stream, RemoteCommandOutputStream outputStream)
            {
                try
                {
                    using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, OutputBufferSize, leaveOpen: true);
                    var buffer = new char[OutputBufferSize];
                    while (true)
                    {
                        var read = await reader.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
                        if (read == 0)
                            return;

                        await callbackGate.WaitAsync(timeout.Token).ConfigureAwait(false);
                        try
                        {
                            var chunk = new RemoteCommandOutputChunk(++sequence, outputStream, new string(buffer, 0, read));
                            await onOutput(chunk, timeout.Token).ConfigureAwait(false);
                        }
                        finally
                        {
                            callbackGate.Release();
                        }
                    }
                }
                catch
                {
                    await timeout.CancelAsync().ConfigureAwait(false);
                    throw;
                }
            }
        }
        finally
        {
            try
            {
                client.Disconnect();
            }
            finally
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

/// <summary>将 executable 与每个 argument 分别转义为单一 POSIX shell token，禁止自由 shell 命令入口。</summary>
internal static class PosixCommandBuilder
{
    public static string Build(string executable, IReadOnlyList<string> arguments)
    {
        if (string.IsNullOrWhiteSpace(executable))
            throw new ArgumentException("远程命令 executable 不能为空。", nameof(executable));
        ArgumentNullException.ThrowIfNull(arguments);
        if (ContainsNull(executable) || arguments.Any(ContainsNull))
            throw new ArgumentException("远程命令 token 不能包含 NUL 字符。", nameof(arguments));

        var executableName = executable.Replace('\\', '/').Split('/').Last();
        if (string.Equals(executableName, "sh", StringComparison.Ordinal) &&
            arguments.Count > 0 && string.Equals(arguments[0], "-c", StringComparison.Ordinal))
            throw new ArgumentException("禁止通过 sh -c 执行自由 shell 字符串。", nameof(arguments));

        return string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote));
    }

    private static bool ContainsNull(string value) => value.IndexOf('\0') >= 0;
    private static string Quote(string token) => $"'{token.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
}
