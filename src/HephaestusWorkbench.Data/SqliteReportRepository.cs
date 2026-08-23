using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;

namespace HephaestusWorkbench.Data;

/// <summary>
/// v2 报告仓储。报告记录保存生成时的插件名称快照，查询时不依赖已移除的插件登记表。
/// </summary>
public sealed class SqliteReportRepository : IReportRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteReportRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task InsertAsync(Report item, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO reports (id, case_id, path, plugin_id, plugin_name, plugin_version, create_time, last_opened_at) VALUES ($id, $case_id, $path, $plugin_id, $plugin_name, $plugin_version, $create_time, $last_opened_at)";
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$case_id", item.CaseId);
        command.Parameters.AddWithValue("$path", item.Path);
        command.Parameters.AddWithValue("$plugin_id", (object?)item.PluginId ?? DBNull.Value);
        command.Parameters.AddWithValue("$plugin_name", (object?)item.PluginName ?? DBNull.Value);
        command.Parameters.AddWithValue("$plugin_version", (object?)item.PluginVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$create_time", SqliteValue.Date(item.CreateTime));
        command.Parameters.AddWithValue("$last_opened_at", (object?)SqliteValue.Date(item.LastOpenedAt) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Report?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, case_id, path, plugin_id, plugin_name, plugin_version, create_time, last_opened_at FROM reports WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadReport(reader) : null;
    }

    public async Task<Report?> GetByCaseIdAsync(string caseId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, case_id, path, plugin_id, plugin_name, plugin_version, create_time, last_opened_at FROM reports WHERE case_id = $case_id ORDER BY create_time DESC LIMIT 1";
        command.Parameters.AddWithValue("$case_id", caseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadReport(reader) : null;
    }

    public async Task<IReadOnlyList<ReportSummary>> ListAsync(ReportQuery query, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            conditions.Add("(c.display_name LIKE $keyword OR c.device_id LIKE $keyword OR COALESCE(r.plugin_name, r.plugin_id, '') LIKE $keyword)");
            command.Parameters.AddWithValue("$keyword", $"%{query.Keyword.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(query.DeviceId))
        {
            conditions.Add("c.device_id LIKE $device_id");
            command.Parameters.AddWithValue("$device_id", $"%{query.DeviceId.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(query.PluginId))
        {
            conditions.Add("r.plugin_id = $plugin_id");
            command.Parameters.AddWithValue("$plugin_id", query.PluginId);
        }
        if (query.StartDate is not null)
        {
            conditions.Add("r.create_time >= $start_date");
            command.Parameters.AddWithValue("$start_date", SqliteValue.Date(query.StartDate.Value.Date));
        }
        if (query.EndDate is not null)
        {
            conditions.Add("r.create_time < $end_date");
            command.Parameters.AddWithValue("$end_date", SqliteValue.Date(query.EndDate.Value.Date.AddDays(1)));
        }

        command.CommandText = $"""
            SELECT r.id, r.case_id, c.display_name, c.device_id, r.path, c.extract_path, r.plugin_id,
                   COALESCE(r.plugin_name, r.plugin_id, '未知插件'), r.create_time
            FROM reports r
            INNER JOIN analysis_cases c ON c.id = r.case_id
            {(conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions))}
            ORDER BY r.create_time DESC;
            """;
        var result = new List<ReportSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // v2 报告路径由分析生命周期写入，打开时仍会再次校验其必须等于 Extract/Report。
            var path = reader.GetString(4);
            result.Add(new ReportSummary
            {
                Id = reader.GetString(0),
                CaseId = reader.GetString(1),
                CaseName = reader.GetString(2),
                DeviceId = reader.GetString(3),
                Path = path,
                ExtractPath = reader.GetString(5),
                PluginId = reader.IsDBNull(6) ? null : reader.GetString(6),
                PluginName = reader.GetString(7),
                CreateTime = SqliteValue.ParseDate(reader.GetValue(8)),
                IsAvailable = File.Exists(Path.Combine(path, "index.html"))
            });
        }
        return result;
    }

    public async Task UpdateLastOpenedAtAsync(string id, DateTime openedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE reports SET last_opened_at = $last_opened_at WHERE id = $id";
        command.Parameters.AddWithValue("$last_opened_at", SqliteValue.Date(openedAt));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    private static Report ReadReport(Microsoft.Data.Sqlite.SqliteDataReader reader)
        => new()
        {
            Id = reader.GetString(0),
            CaseId = reader.GetString(1),
            Path = reader.GetString(2),
            PluginId = reader.IsDBNull(3) ? null : reader.GetString(3),
            PluginName = reader.IsDBNull(4) ? null : reader.GetString(4),
            PluginVersion = reader.IsDBNull(5) ? null : reader.GetString(5),
            CreateTime = SqliteValue.ParseDate(reader.GetValue(6)),
            LastOpenedAt = reader.IsDBNull(7) ? null : SqliteValue.ParseDate(reader.GetValue(7))
        };
}
