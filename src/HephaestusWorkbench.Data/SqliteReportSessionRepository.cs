using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;

namespace HephaestusWorkbench.Data;

/// <summary>以整组替换方式保存报告工作区，保证 Tab 顺序和激活项始终一致。</summary>
public sealed class SqliteReportSessionRepository : IReportSessionRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteReportSessionRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<ReportSession>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, report_id, order_index, is_active, scroll_position, last_open_time FROM report_sessions ORDER BY order_index";
        var result = new List<ReportSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ReportSession
            {
                Id = reader.GetString(0),
                ReportId = reader.GetString(1),
                OrderIndex = reader.GetInt32(2),
                IsActive = reader.GetInt32(3) != 0,
                ScrollPosition = reader.GetDouble(4),
                LastOpenTime = SqliteValue.ParseDate(reader.GetValue(5))
            });
        }
        return result;
    }

    public async Task ReplaceAsync(IReadOnlyList<ReportSession> sessions, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM report_sessions";
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var item in sessions)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO report_sessions (id, report_id, order_index, is_active, scroll_position, last_open_time)
                VALUES ($id, $report_id, $order_index, $is_active, $scroll_position, $last_open_time)
                """;
            command.Parameters.AddWithValue("$id", item.Id);
            command.Parameters.AddWithValue("$report_id", item.ReportId);
            command.Parameters.AddWithValue("$order_index", item.OrderIndex);
            command.Parameters.AddWithValue("$is_active", item.IsActive ? 1 : 0);
            command.Parameters.AddWithValue("$scroll_position", item.ScrollPosition);
            command.Parameters.AddWithValue("$last_open_time", SqliteValue.Date(item.LastOpenTime));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
