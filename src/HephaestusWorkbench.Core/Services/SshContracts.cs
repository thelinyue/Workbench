using HephaestusWorkbench.Core.Models;

namespace HephaestusWorkbench.Core.Services;

/// <summary>创建独占 SSH 连接和 PTY Shell；每个返回会话只服务一个终端标签。</summary>
public interface ISshTerminalService
{
    Task<ITerminalSession> ConnectAsync(
        SshConnectionRequest request,
        SshCredentialSecret? credential,
        CancellationToken cancellationToken = default);
}

/// <summary>交互终端会话。关闭标签时必须异步释放底层连接和读取任务。</summary>
public interface ITerminalSession : IAsyncDisposable
{
    SshConnectionIdentity ConnectionIdentity { get; }
    IInteractiveChannel InteractiveChannel { get; }
}

/// <summary>面向字节流的交互 Shell 通道，不承诺 stdout/stderr 拆分或 Exit Code。</summary>
public interface IInteractiveChannel
{
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default);
}

/// <summary>使用独立 SSH exec connection 执行结构化命令，不借用交互终端连接。</summary>
public interface ICommandExecutionService
{
    Task<RemoteCommandResult> ExecuteAsync(
        RemoteCommandRequest request,
        SshCredentialSecret? credential,
        Func<RemoteCommandOutputChunk, CancellationToken, ValueTask> onOutput,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 仅在内存中短暂传递的凭据内容。ToString 永远返回脱敏文本，防止异常或日志意外泄露。
/// </summary>
public sealed class SshCredentialSecret
{
    public SshCredentialSecret(string value) => Value = value ?? throw new ArgumentNullException(nameof(value));
    public string Value { get; }
    public override string ToString() => "[已隐藏 SSH 凭据]";
}

/// <summary>从系统凭据存储读取出的用户名和敏感内容。</summary>
public sealed record SshStoredCredential(string UserName, SshCredentialSecret Secret);

/// <summary>封装 Windows Credential Manager；SQLite 和 JSON 只能保存 target，不能保存凭据内容。</summary>
public interface ICredentialStore
{
    Task WriteAsync(
        string target,
        string userName,
        SshCredentialSecret secret,
        CancellationToken cancellationToken = default);

    Task<SshStoredCredential?> ReadAsync(string target, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string target, CancellationToken cancellationToken = default);
}
