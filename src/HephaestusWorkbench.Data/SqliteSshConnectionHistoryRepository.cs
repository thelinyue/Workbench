using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using Microsoft.Data.Sqlite;

namespace HephaestusWorkbench.Data;

/// <summary>
/// 持久化 SSH 连接历史。记录只包含连接元数据和最终结果，
/// 不保存用户命令、终端输出或任何凭据内容。
/// </summary>
public sealed class SqliteSshConnectionHistoryRepository : ISshConnectionHistoryRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteSshConnectionHistoryRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task InsertAsync(SshConnectionHistory history, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ssh_connection_history
                (id, device_id, host, port, username, connected_at, disconnected_at, outcome, error_message)
            VALUES
                ($id, $device_id, $host, $port, $username, $connected_at, $disconnected_at, $outcome, $error_message)
            """;
        command.Parameters.AddWithValue("$id", history.Id);
        command.Parameters.AddWithValue("$device_id", (object?)history.DeviceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$host", history.Host);
        command.Parameters.AddWithValue("$port", history.Port);
        command.Parameters.AddWithValue("$username", history.Username);
        command.Parameters.AddWithValue("$connected_at", SqliteValue.Date(history.ConnectedAt));
        command.Parameters.AddWithValue("$disconnected_at", (object?)SqliteValue.Date(history.DisconnectedAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$outcome", SshSqliteEnum.ConnectionOutcomeToString(history.Outcome));
        command.Parameters.AddWithValue("$error_message", (object?)history.ErrorMessage ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CompleteAsync(
        string id,
        DateTime disconnectedAt,
        SshConnectionOutcome outcome,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // 完成连接时不得改写目标、用户或开始时间，确保历史身份不可被后续状态更新篡改。
        command.CommandText = """
            UPDATE ssh_connection_history
            SET disconnected_at = $disconnected_at,
                outcome = $outcome,
                error_message = $error_message
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$disconnected_at", SqliteValue.Date(disconnectedAt));
        command.Parameters.AddWithValue("$outcome", SshSqliteEnum.ConnectionOutcomeToString(outcome));
        command.Parameters.AddWithValue("$error_message", (object?)errorMessage ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SshConnectionHistory>> ListRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "SSH 连接历史查询数量必须大于 0。");
        }

        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, device_id, host, port, username, connected_at, disconnected_at, outcome, error_message
            FROM ssh_connection_history
            ORDER BY connected_at DESC, id ASC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<SshConnectionHistory>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(Read(reader));
        }

        return items;
    }

    /// <summary>
    /// 在 SQLite 内过滤成功会话并按设备或未保存目标去重，避免失败连接出现在设备抽屉。
    /// 未保存目标只使用 host、port、username 作为身份，绝不读取或返回敏感凭据。
    /// </summary>
    public async Task<IReadOnlyList<SshRecentConnection>> ListRecentSuccessfulAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "最近成功 SSH 连接查询数量必须大于 0。");

        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH ranked AS
            (
                SELECT device_id, host, port, username, connected_at, id,
                       ROW_NUMBER() OVER
                       (
                           PARTITION BY COALESCE(device_id, ''),
                                        CASE WHEN device_id IS NULL THEN host ELSE '' END,
                                        CASE WHEN device_id IS NULL THEN port ELSE 0 END,
                                        CASE WHEN device_id IS NULL THEN username ELSE '' END
                           ORDER BY connected_at DESC, id ASC
                       ) AS rank_number
                FROM ssh_connection_history
                WHERE outcome IN ($connected, $disconnected)
            )
            SELECT device_id, host, port, username, connected_at
            FROM ranked
            WHERE rank_number = 1
            ORDER BY connected_at DESC, id ASC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$connected", SshSqliteEnum.ConnectionOutcomeToString(SshConnectionOutcome.Connected));
        command.Parameters.AddWithValue("$disconnected", SshSqliteEnum.ConnectionOutcomeToString(SshConnectionOutcome.Disconnected));
        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<SshRecentConnection>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new SshRecentConnection(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                SqliteValue.ParseDate(reader.GetValue(4))));
        }
        return items;
    }

    private static SshConnectionHistory Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        DeviceId = reader.IsDBNull(1) ? null : reader.GetString(1),
        Host = reader.GetString(2),
        Port = reader.GetInt32(3),
        Username = reader.GetString(4),
        ConnectedAt = SqliteValue.ParseDate(reader.GetValue(5)),
        DisconnectedAt = SqliteValue.ParseNullableDate(reader.IsDBNull(6) ? null : reader.GetValue(6)),
        Outcome = SshSqliteEnum.ParseConnectionOutcome(reader.GetString(7)),
        ErrorMessage = reader.IsDBNull(8) ? null : reader.GetString(8)
    };
}
