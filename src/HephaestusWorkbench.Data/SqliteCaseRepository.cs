using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;

namespace HephaestusWorkbench.Data;

public sealed class SqliteCaseRepository : IAnalysisCaseRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteCaseRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<AnalysisCase>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, display_name, original_name, device_id, log_time, status, source_path, extract_path, report_path, error_message, create_time, update_time FROM analysis_cases ORDER BY update_time DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<AnalysisCase>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader));
        return result;
    }

    public async Task<AnalysisCase?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, display_name, original_name, device_id, log_time, status, source_path, extract_path, report_path, error_message, create_time, update_time FROM analysis_cases WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task InsertAsync(AnalysisCase item, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO analysis_cases (id, display_name, original_name, device_id, log_time, status, source_path, extract_path, report_path, error_message, create_time, update_time)
            VALUES ($id, $display_name, $original_name, $device_id, $log_time, $status, $source_path, $extract_path, $report_path, $error_message, $create_time, $update_time)
            """;
        Add(command, item);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateAsync(AnalysisCase item, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE analysis_cases SET display_name = $display_name, status = $status, report_path = $report_path,
                error_message = $error_message, update_time = $update_time, source_path = $source_path, extract_path = $extract_path
            WHERE id = $id
            """;
        Add(command, item);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM analysis_cases WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Add(Microsoft.Data.Sqlite.SqliteCommand command, AnalysisCase item)
    {
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$display_name", item.DisplayName);
        command.Parameters.AddWithValue("$original_name", item.OriginalName);
        command.Parameters.AddWithValue("$device_id", item.DeviceId);
        command.Parameters.AddWithValue("$log_time", SqliteValue.Date(item.LogTime));
        command.Parameters.AddWithValue("$status", item.Status.ToString());
        command.Parameters.AddWithValue("$source_path", item.SourcePath);
        command.Parameters.AddWithValue("$extract_path", item.ExtractPath);
        command.Parameters.AddWithValue("$report_path", (object?)item.ReportPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$error_message", (object?)item.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$create_time", SqliteValue.Date(item.CreateTime));
        command.Parameters.AddWithValue("$update_time", SqliteValue.Date(item.UpdateTime));
    }

    private static AnalysisCase Read(Microsoft.Data.Sqlite.SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        DisplayName = reader.GetString(1),
        OriginalName = reader.GetString(2),
        DeviceId = reader.GetString(3),
        LogTime = SqliteValue.ParseDate(reader.GetValue(4)),
        Status = Enum.Parse<CaseStatus>(reader.GetString(5)),
        SourcePath = reader.GetString(6),
        ExtractPath = reader.GetString(7),
        ReportPath = reader.IsDBNull(8) ? null : reader.GetString(8),
        ErrorMessage = reader.IsDBNull(9) ? null : reader.GetString(9),
        CreateTime = SqliteValue.ParseDate(reader.GetValue(10)),
        UpdateTime = SqliteValue.ParseDate(reader.GetValue(11))
    };
}
