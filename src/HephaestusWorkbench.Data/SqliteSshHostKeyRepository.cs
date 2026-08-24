using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using Microsoft.Data.Sqlite;

namespace HephaestusWorkbench.Data;

/// <summary>
/// 持久化用户已确认的 SSH Host Key。更新同一 host/port 时只推进最近观察时间，
/// 首次信任时间始终由数据库中已有记录决定，避免后续握手覆盖审计起点。
/// </summary>
public sealed class SqliteSshHostKeyRepository : ISshHostKeyRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteSshHostKeyRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<SshHostKey?> GetAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT host, port, key_algorithm, fingerprint, first_seen_at, last_seen_at FROM ssh_host_keys WHERE host = $host AND port = $port";
        command.Parameters.AddWithValue("$host", host);
        command.Parameters.AddWithValue("$port", port);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task UpsertAsync(SshHostKey hostKey, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ssh_host_keys
                (host, port, key_algorithm, fingerprint, first_seen_at, last_seen_at)
            VALUES
                ($host, $port, $key_algorithm, $fingerprint, $first_seen_at, $last_seen_at)
            ON CONFLICT(host, port) DO UPDATE SET
                key_algorithm = excluded.key_algorithm,
                fingerprint = excluded.fingerprint,
                last_seen_at = excluded.last_seen_at
            """;
        command.Parameters.AddWithValue("$host", hostKey.Host);
        command.Parameters.AddWithValue("$port", hostKey.Port);
        command.Parameters.AddWithValue("$key_algorithm", hostKey.KeyAlgorithm);
        command.Parameters.AddWithValue("$fingerprint", hostKey.Fingerprint);
        command.Parameters.AddWithValue("$first_seen_at", SqliteValue.Date(hostKey.FirstSeenAt));
        command.Parameters.AddWithValue("$last_seen_at", SqliteValue.Date(hostKey.LastSeenAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SshHostKey Read(SqliteDataReader reader) => new()
    {
        Host = reader.GetString(0),
        Port = reader.GetInt32(1),
        KeyAlgorithm = reader.GetString(2),
        Fingerprint = reader.GetString(3),
        FirstSeenAt = SqliteValue.ParseDate(reader.GetValue(4)),
        LastSeenAt = SqliteValue.ParseDate(reader.GetValue(5))
    };
}
