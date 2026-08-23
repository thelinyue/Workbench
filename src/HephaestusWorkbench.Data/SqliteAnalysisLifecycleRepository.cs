using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using AnalysisTaskStatus = HephaestusWorkbench.Core.Models.TaskStatus;

namespace HephaestusWorkbench.Data;

/// <summary>
/// 负责案例分析生命周期中的跨表事务。
/// 案例、任务和报告分别有查询仓储，但它们的状态转换必须在同一个 SQLite 事务中完成，
/// 这样应用崩溃或磁盘写入失败时不会留下“有任务无案例”或“案例已完成但无报告”的半成品状态。
/// </summary>
public sealed class SqliteAnalysisLifecycleRepository : IAnalysisLifecycleRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteAnalysisLifecycleRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task CreateAsync(AnalysisCase analysisCase, AnalysisTask task, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO analysis_cases (id, display_name, original_name, device_id, log_time, status, source_path, extract_path, report_path, error_message, create_time, update_time)
            VALUES ($id, $display_name, $original_name, $device_id, $log_time, $status, $source_path, $extract_path, $report_path, $error_message, $create_time, $update_time)
            """, AddCaseParameters, cancellationToken);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO analysis_tasks (id, case_id, plugin_id, status, start_time, end_time, report_path, error_message)
            VALUES ($id, $case_id, $plugin_id, $status, $start_time, $end_time, $report_path, $error_message)
            """, AddTaskParameters, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        void AddCaseParameters(Microsoft.Data.Sqlite.SqliteCommand command)
        {
            command.Parameters.AddWithValue("$id", analysisCase.Id);
            command.Parameters.AddWithValue("$display_name", analysisCase.DisplayName);
            command.Parameters.AddWithValue("$original_name", analysisCase.OriginalName);
            command.Parameters.AddWithValue("$device_id", analysisCase.DeviceId);
            command.Parameters.AddWithValue("$log_time", SqliteValue.Date(analysisCase.LogTime));
            command.Parameters.AddWithValue("$status", analysisCase.Status.ToString());
            command.Parameters.AddWithValue("$source_path", analysisCase.SourcePath);
            command.Parameters.AddWithValue("$extract_path", analysisCase.ExtractPath);
            command.Parameters.AddWithValue("$report_path", (object?)analysisCase.ReportPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$error_message", (object?)analysisCase.ErrorMessage ?? DBNull.Value);
            command.Parameters.AddWithValue("$create_time", SqliteValue.Date(analysisCase.CreateTime));
            command.Parameters.AddWithValue("$update_time", SqliteValue.Date(analysisCase.UpdateTime));
        }

        void AddTaskParameters(Microsoft.Data.Sqlite.SqliteCommand command)
        {
            command.Parameters.AddWithValue("$id", task.Id);
            command.Parameters.AddWithValue("$case_id", task.CaseId);
            command.Parameters.AddWithValue("$plugin_id", task.PluginId);
            command.Parameters.AddWithValue("$status", task.Status.ToString());
            command.Parameters.AddWithValue("$start_time", (object?)SqliteValue.Date(task.StartTime) ?? DBNull.Value);
            command.Parameters.AddWithValue("$end_time", (object?)SqliteValue.Date(task.EndTime) ?? DBNull.Value);
            command.Parameters.AddWithValue("$report_path", (object?)task.ReportPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$error_message", (object?)task.ErrorMessage ?? DBNull.Value);
        }
    }

    public async Task MarkRunningAsync(AnalysisCase analysisCase, AnalysisTask task, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction, """
            UPDATE analysis_cases
            SET status = $status, update_time = $update_time, error_message = $error_message
            WHERE id = $id;
            """, command =>
        {
            command.Parameters.AddWithValue("$status", analysisCase.Status.ToString());
            command.Parameters.AddWithValue("$update_time", SqliteValue.Date(analysisCase.UpdateTime));
            command.Parameters.AddWithValue("$error_message", (object?)analysisCase.ErrorMessage ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", analysisCase.Id);
        }, cancellationToken);
        await ExecuteAsync(connection, transaction, """
            UPDATE analysis_tasks
            SET status = $status, start_time = $start_time, error_message = $error_message
            WHERE id = $id;
            """, command =>
        {
            command.Parameters.AddWithValue("$status", task.Status.ToString());
            command.Parameters.AddWithValue("$start_time", (object?)SqliteValue.Date(task.StartTime) ?? DBNull.Value);
            command.Parameters.AddWithValue("$error_message", (object?)task.ErrorMessage ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", task.Id);
        }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task CompleteAsync(AnalysisCase analysisCase, AnalysisTask task, Report? report, CancellationToken cancellationToken = default)
        => CompleteAsync(analysisCase, task, report is null ? Array.Empty<Report>() : new[] { report }, cancellationToken);

    public async Task CompleteAsync(AnalysisCase analysisCase, AnalysisTask task, IReadOnlyList<Report> reports, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction, """
            UPDATE analysis_cases
            SET status = $status, report_path = $report_path, error_message = $error_message, update_time = $update_time
            WHERE id = $id;
            """, command =>
        {
            command.Parameters.AddWithValue("$status", analysisCase.Status.ToString());
            command.Parameters.AddWithValue("$report_path", (object?)analysisCase.ReportPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$error_message", (object?)analysisCase.ErrorMessage ?? DBNull.Value);
            command.Parameters.AddWithValue("$update_time", SqliteValue.Date(analysisCase.UpdateTime));
            command.Parameters.AddWithValue("$id", analysisCase.Id);
        }, cancellationToken);

        foreach (var report in reports)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO reports (id, case_id, path, report_key, title, kind, entry_file, is_default, plugin_id, plugin_name, plugin_version, create_time)
                VALUES ($id, $case_id, $path, $report_key, $title, $kind, $entry_file, $is_default, $plugin_id, $plugin_name, $plugin_version, $create_time);
                """, command =>
            {
                command.Parameters.AddWithValue("$id", report.Id);
                command.Parameters.AddWithValue("$case_id", report.CaseId);
                command.Parameters.AddWithValue("$path", report.Path);
                command.Parameters.AddWithValue("$report_key", report.ReportKey);
                command.Parameters.AddWithValue("$title", report.Title);
                command.Parameters.AddWithValue("$kind", report.Kind);
                command.Parameters.AddWithValue("$entry_file", report.EntryFile);
                command.Parameters.AddWithValue("$is_default", report.IsDefault ? 1 : 0);
                command.Parameters.AddWithValue("$plugin_id", (object?)report.PluginId ?? DBNull.Value);
                command.Parameters.AddWithValue("$plugin_name", (object?)report.PluginName ?? DBNull.Value);
                command.Parameters.AddWithValue("$plugin_version", (object?)report.PluginVersion ?? DBNull.Value);
                command.Parameters.AddWithValue("$create_time", SqliteValue.Date(report.CreateTime));
            }, cancellationToken);
        }

        await ExecuteAsync(connection, transaction, """
            UPDATE analysis_tasks
            SET status = $status, end_time = $end_time, report_path = $report_path, error_message = $error_message
            WHERE id = $id;
            """, command =>
        {
            command.Parameters.AddWithValue("$status", task.Status.ToString());
            command.Parameters.AddWithValue("$end_time", (object?)SqliteValue.Date(task.EndTime) ?? DBNull.Value);
            command.Parameters.AddWithValue("$report_path", (object?)task.ReportPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$error_message", (object?)task.ErrorMessage ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", task.Id);
        }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// 显式删除完整分析生命周期的数据库记录，不依赖历史数据库是否配置了外键级联。
    /// 删除顺序必须先处理报告会话，再处理报告、任务和案例，避免旧库留下孤儿记录。
    /// </summary>
    public async Task DeleteByCaseIdsAsync(IReadOnlyCollection<string> caseIds, CancellationToken cancellationToken = default)
    {
        var ids = caseIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 0) return;

        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var parameters = string.Join(", ", ids.Select((_, index) => $"$case_id_{index}"));

        await ExecuteAsync(connection, transaction,
            $"DELETE FROM report_sessions WHERE report_id IN (SELECT id FROM reports WHERE case_id IN ({parameters}));",
            AddCaseIdParameters,
            cancellationToken);
        await ExecuteAsync(connection, transaction,
            $"DELETE FROM reports WHERE case_id IN ({parameters});",
            AddCaseIdParameters,
            cancellationToken);
        await ExecuteAsync(connection, transaction,
            $"DELETE FROM analysis_tasks WHERE case_id IN ({parameters});",
            AddCaseIdParameters,
            cancellationToken);
        await ExecuteAsync(connection, transaction,
            $"DELETE FROM analysis_cases WHERE id IN ({parameters});",
            AddCaseIdParameters,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        void AddCaseIdParameters(Microsoft.Data.Sqlite.SqliteCommand command)
        {
            for (var index = 0; index < ids.Length; index++)
                command.Parameters.AddWithValue($"$case_id_{index}", ids[index]);
        }
    }
    public async Task<int> RecoverInterruptedAsync(DateTime recoveredAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var taskCount = await ExecuteAsync(connection, transaction, """
            UPDATE analysis_tasks
            SET status = $status, end_time = $end_time, error_message = $error_message
            WHERE status IN ('Waiting', 'Running');
            """, command =>
        {
            command.Parameters.AddWithValue("$status", AnalysisTaskStatus.Failed.ToString());
            command.Parameters.AddWithValue("$end_time", SqliteValue.Date(recoveredAt));
            command.Parameters.AddWithValue("$error_message", "应用上次退出时任务未完成，已标记为失败。");
        }, cancellationToken);
        await ExecuteAsync(connection, transaction, """
            UPDATE analysis_cases
            SET status = 'Failed', update_time = $update_time, error_message = $error_message
            WHERE status IN ('Ready', 'Running')
              AND id IN (SELECT case_id FROM analysis_tasks WHERE status = 'Failed' AND error_message = $error_message);
            """, command =>
        {
            command.Parameters.AddWithValue("$update_time", SqliteValue.Date(recoveredAt));
            command.Parameters.AddWithValue("$error_message", "应用上次退出时任务未完成，已标记为失败。");
        }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return taskCount;
    }

    private static async Task<int> ExecuteAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string sql,
        Action<Microsoft.Data.Sqlite.SqliteCommand> addParameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        addParameters(command);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
