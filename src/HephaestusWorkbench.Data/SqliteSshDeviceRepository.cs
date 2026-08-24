using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using Microsoft.Data.Sqlite;

namespace HephaestusWorkbench.Data;

/// <summary>
/// 持久化 SSH 设备的非敏感配置。该仓储只接收 Credential Manager target，
/// 不接收也不保存密码、私钥口令等凭据内容。
/// </summary>
public sealed class SqliteSshDeviceRepository : ISshDeviceRepository
{
    private const string Columns = "id, name, host, port, username, authentication_method, private_key_path, credential_target, created_at, updated_at";
    private readonly SqliteConnectionFactory _factory;

    public SqliteSshDeviceRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<SshDevice>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM ssh_devices ORDER BY updated_at DESC, id ASC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<SshDevice>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(Read(reader));
        }

        return items;
    }

    public async Task<SshDevice?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM ssh_devices WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task UpsertAsync(SshDevice device, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ssh_devices
                (id, name, host, port, username, authentication_method, private_key_path, credential_target, created_at, updated_at)
            VALUES
                ($id, $name, $host, $port, $username, $authentication_method, $private_key_path, $credential_target, $created_at, $updated_at)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                host = excluded.host,
                port = excluded.port,
                username = excluded.username,
                authentication_method = excluded.authentication_method,
                private_key_path = excluded.private_key_path,
                credential_target = excluded.credential_target,
                created_at = excluded.created_at,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$id", device.Id);
        command.Parameters.AddWithValue("$name", device.Name);
        command.Parameters.AddWithValue("$host", device.Host);
        command.Parameters.AddWithValue("$port", device.Port);
        command.Parameters.AddWithValue("$username", device.Username);
        command.Parameters.AddWithValue("$authentication_method", SshSqliteEnum.AuthenticationMethodToString(device.AuthenticationMethod));
        command.Parameters.AddWithValue("$private_key_path", (object?)device.PrivateKeyPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$credential_target", (object?)device.CredentialTarget ?? DBNull.Value);
        command.Parameters.AddWithValue("$created_at", SqliteValue.Date(device.CreatedAt));
        command.Parameters.AddWithValue("$updated_at", SqliteValue.Date(device.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ssh_devices WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SshDevice Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.GetString(1),
        Host = reader.GetString(2),
        Port = reader.GetInt32(3),
        Username = reader.GetString(4),
        AuthenticationMethod = SshSqliteEnum.ParseAuthenticationMethod(reader.GetString(5)),
        PrivateKeyPath = reader.IsDBNull(6) ? null : reader.GetString(6),
        CredentialTarget = reader.IsDBNull(7) ? null : reader.GetString(7),
        CreatedAt = SqliteValue.ParseDate(reader.GetValue(8)),
        UpdatedAt = SqliteValue.ParseDate(reader.GetValue(9))
    };
}

/// <summary>集中定义 SSH 枚举的稳定文本映射，数据库出现未知值时拒绝继续解释。</summary>
internal static class SshSqliteEnum
{
    public static string AuthenticationMethodToString(SshAuthenticationMethod value) => value switch
    {
        SshAuthenticationMethod.Password => nameof(SshAuthenticationMethod.Password),
        SshAuthenticationMethod.PrivateKey => nameof(SshAuthenticationMethod.PrivateKey),
        _ => throw new InvalidDataException($"无法保存未知的 SSH 认证方式：{value}。")
    };

    public static SshAuthenticationMethod ParseAuthenticationMethod(string value) => value switch
    {
        nameof(SshAuthenticationMethod.Password) => SshAuthenticationMethod.Password,
        nameof(SshAuthenticationMethod.PrivateKey) => SshAuthenticationMethod.PrivateKey,
        _ => throw new InvalidDataException($"数据库包含未知的 SSH 认证方式：{value}。")
    };

    public static string ConnectionOutcomeToString(SshConnectionOutcome value) => value switch
    {
        SshConnectionOutcome.Connected => nameof(SshConnectionOutcome.Connected),
        SshConnectionOutcome.Disconnected => nameof(SshConnectionOutcome.Disconnected),
        SshConnectionOutcome.AuthenticationFailed => nameof(SshConnectionOutcome.AuthenticationFailed),
        SshConnectionOutcome.HostKeyRejected => nameof(SshConnectionOutcome.HostKeyRejected),
        SshConnectionOutcome.TimedOut => nameof(SshConnectionOutcome.TimedOut),
        SshConnectionOutcome.Failed => nameof(SshConnectionOutcome.Failed),
        _ => throw new InvalidDataException($"无法保存未知的 SSH 连接结果：{value}。")
    };

    public static SshConnectionOutcome ParseConnectionOutcome(string value) => value switch
    {
        nameof(SshConnectionOutcome.Connected) => SshConnectionOutcome.Connected,
        nameof(SshConnectionOutcome.Disconnected) => SshConnectionOutcome.Disconnected,
        nameof(SshConnectionOutcome.AuthenticationFailed) => SshConnectionOutcome.AuthenticationFailed,
        nameof(SshConnectionOutcome.HostKeyRejected) => SshConnectionOutcome.HostKeyRejected,
        nameof(SshConnectionOutcome.TimedOut) => SshConnectionOutcome.TimedOut,
        nameof(SshConnectionOutcome.Failed) => SshConnectionOutcome.Failed,
        _ => throw new InvalidDataException($"数据库包含未知的 SSH 连接结果：{value}。")
    };
}
