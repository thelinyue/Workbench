using System.Text.Json.Serialization;

namespace HephaestusWorkbench.Core.Models;

/// <summary>SSH 设备使用的认证方式。凭据内容永远不属于该持久化模型。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SshAuthenticationMethod
{
    Password,
    PrivateKey
}

/// <summary>一次 SSH 连接历史的最终结果。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SshConnectionOutcome
{
    Connected,
    Disconnected,
    AuthenticationFailed,
    HostKeyRejected,
    TimedOut,
    Failed
}

/// <summary>独立命令通道产生的输出类型，普通交互终端不使用该枚举推断命令结果。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RemoteCommandOutputStream
{
    Stdout,
    Stderr
}

/// <summary>
/// 可保存的 SSH 设备配置。这里只保存 Credential Manager target 和私钥路径，
/// 不保存密码、私钥口令或其他凭据内容。
/// </summary>
public sealed record class SshDevice
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Host { get; init; }
    public int Port { get; init; } = 22;
    public required string Username { get; init; }
    public SshAuthenticationMethod AuthenticationMethod { get; init; }
    public string? PrivateKeyPath { get; init; }
    public string? CredentialTarget { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

/// <summary>Host Key TOFU 存储记录；同一 host/port 只允许一个已确认指纹。</summary>
public sealed record class SshHostKey
{
    public required string Host { get; init; }
    public int Port { get; init; } = 22;
    public required string KeyAlgorithm { get; init; }
    public required string Fingerprint { get; init; }
    public DateTime FirstSeenAt { get; init; }
    public DateTime LastSeenAt { get; init; }
}

/// <summary>SSH 连接历史，不记录用户输入、终端内容或凭据。</summary>
public sealed record class SshConnectionHistory
{
    public required string Id { get; init; }
    public string? DeviceId { get; init; }
    public required string Host { get; init; }
    public int Port { get; init; } = 22;
    public required string Username { get; init; }
    public DateTime ConnectedAt { get; init; }
    public DateTime? DisconnectedAt { get; init; }
    public SshConnectionOutcome Outcome { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>建立交互终端或独立命令连接所需的非敏感参数。</summary>
public sealed record SshConnectionRequest(
    string? DeviceId,
    string Host,
    int Port,
    string Username,
    SshAuthenticationMethod AuthenticationMethod,
    string? PrivateKeyPath,
    string? CredentialTarget);

/// <summary>已建立连接的稳定身份，用于终端标签和审计展示。</summary>
public sealed record SshConnectionIdentity(string? DeviceId, string Host, int Port, string Username);

/// <summary>独立命令执行请求；可变输入保持为参数 token，不接受拼接后的 shell 字符串。</summary>
public sealed record RemoteCommandRequest(
    SshConnectionRequest Connection,
    string Executable,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout);

/// <summary>独立命令通道的一段 stdout 或 stderr 输出。</summary>
public sealed record RemoteCommandOutputChunk(long Sequence, RemoteCommandOutputStream Stream, string Text);

/// <summary>独立命令通道的最终状态。</summary>
public sealed record RemoteCommandResult(int ExitCode, TimeSpan Duration);
