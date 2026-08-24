using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using AnalysisTaskStatus = HephaestusWorkbench.Core.Models.TaskStatus;

namespace HephaestusWorkbench.Data;

public sealed class SqliteTaskRepository : IAnalysisTaskRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteTaskRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<AnalysisTask>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, case_id, plugin_id, analysis_scope, status, start_time, end_time, report_path, error_message FROM analysis_tasks ORDER BY COALESCE(start_time, '') DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<AnalysisTask>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader));
        return result;
    }

    public async Task<AnalysisTask?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, case_id, plugin_id, analysis_scope, status, start_time, end_time, report_path, error_message FROM analysis_tasks WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task InsertAsync(AnalysisTask item, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO analysis_tasks (id, case_id, plugin_id, analysis_scope, status, start_time, end_time, report_path, error_message)
            VALUES ($id, $case_id, $plugin_id, $analysis_scope, $status, $start_time, $end_time, $report_path, $error_message)
            """;
        Add(command, item);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateAsync(AnalysisTask item, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE analysis_tasks SET status = $status, start_time = $start_time, end_time = $end_time,
                report_path = $report_path, error_message = $error_message WHERE id = $id
            """;
        Add(command, item);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Add(Microsoft.Data.Sqlite.SqliteCommand command, AnalysisTask item)
    {
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$case_id", item.CaseId);
        command.Parameters.AddWithValue("$plugin_id", item.PluginId);
        command.Parameters.AddWithValue("$analysis_scope", item.AnalysisScope.ToString());
        command.Parameters.AddWithValue("$status", item.Status.ToString());
        command.Parameters.AddWithValue("$start_time", (object?)SqliteValue.Date(item.StartTime) ?? DBNull.Value);
        command.Parameters.AddWithValue("$end_time", (object?)SqliteValue.Date(item.EndTime) ?? DBNull.Value);
        command.Parameters.AddWithValue("$report_path", (object?)item.ReportPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$error_message", (object?)item.ErrorMessage ?? DBNull.Value);
    }

    private static AnalysisTask Read(Microsoft.Data.Sqlite.SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        CaseId = reader.GetString(1),
        PluginId = reader.GetString(2),
        AnalysisScope = Enum.Parse<AnalysisScope>(reader.GetString(3)),
        Status = Enum.Parse<AnalysisTaskStatus>(reader.GetString(4)),
        StartTime = SqliteValue.ParseNullableDate(reader.IsDBNull(5) ? null : reader.GetValue(5)),
        EndTime = SqliteValue.ParseNullableDate(reader.IsDBNull(6) ? null : reader.GetValue(6)),
        ReportPath = reader.IsDBNull(7) ? null : reader.GetString(7),
        ErrorMessage = reader.IsDBNull(8) ? null : reader.GetString(8)
    };
}
