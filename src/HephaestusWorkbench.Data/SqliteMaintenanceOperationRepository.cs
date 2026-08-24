using System.Text.Json;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using Microsoft.Data.Sqlite;

namespace HephaestusWorkbench.Data;

/// <summary>
/// 使用 v2 固定表持久化维护操作。操作与步骤原子创建；输出正文只写外部文件，SQLite 仅保存相对路径。
/// 启动恢复把中断状态标记为 OutcomeUnknown，绝不自动继续或重放命令。
/// </summary>
public sealed class SqliteMaintenanceOperationRepository : IMaintenanceOperationRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteMaintenanceOperationRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task CreateAsync(MaintenanceOperation operation, bool isReadOnly, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ValidateRelativePath(operation.OperationDirectory, "操作目录", allowNull: false);
        ValidateOperationStatus(operation.Status);
        foreach (var step in operation.Steps)
        {
            ValidateStepStatus(step.Status);
            if (!string.Equals(step.OperationId, operation.Id, StringComparison.Ordinal))
                throw new InvalidDataException($"维护步骤 {step.Id} 的 operationId 与操作不一致。");
            ValidateRelativePath(step.StdoutPath, "stdout 路径", allowNull: true);
            ValidateRelativePath(step.StderrPath, "stderr 路径", allowNull: true);
        }

        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (!isReadOnly && await HasActiveOperationAsync(connection, transaction, operation.DeviceId, cancellationToken))
                throw new InvalidOperationException($"设备 {operation.DeviceId} 已有运行中或等待停止的维护操作，不能创建新的变更操作。");

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO maintenance_operations
                        (id, workflow_id, workflow_version, extension_id, extension_version, device_id, status,
                         started_at, completed_at, outcome_summary, operation_directory)
                    VALUES
                        ($id, $workflow_id, $workflow_version, $extension_id, $extension_version, $device_id, $status,
                         $started_at, $completed_at, $outcome_summary, $operation_directory)
                    """;
                command.Parameters.AddWithValue("$id", operation.Id);
                command.Parameters.AddWithValue("$workflow_id", operation.WorkflowId);
                command.Parameters.AddWithValue("$workflow_version", operation.WorkflowVersion);
                command.Parameters.AddWithValue("$extension_id", operation.ExtensionId);
                command.Parameters.AddWithValue("$extension_version", operation.ExtensionVersion);
                command.Parameters.AddWithValue("$device_id", operation.DeviceId);
                command.Parameters.AddWithValue("$status", operation.Status.ToString());
                command.Parameters.AddWithValue("$started_at", SqliteValue.Date(operation.StartedAt));
                command.Parameters.AddWithValue("$completed_at", (object?)SqliteValue.Date(operation.CompletedAt) ?? DBNull.Value);
                command.Parameters.AddWithValue("$outcome_summary", (object?)operation.OutcomeSummary ?? DBNull.Value);
                command.Parameters.AddWithValue("$operation_directory", operation.OperationDirectory);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var step in operation.Steps)
                await InsertStepAsync(connection, transaction, step, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<MaintenanceOperation?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        var operation = await ReadOperationAsync(connection, id, cancellationToken);
        if (operation is null) return null;
        return operation with { Steps = await ReadStepsAsync(connection, operation.Id, cancellationToken) };
    }

    public async Task<IReadOnlyList<MaintenanceOperation>> ListRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit), limit, "维护操作查询数量必须大于 0。");
        await using var connection = await _factory.OpenAsync(cancellationToken);
        var operations = new List<MaintenanceOperation>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, workflow_id, workflow_version, extension_id, extension_version, device_id, status,
                       started_at, completed_at, outcome_summary, operation_directory
                FROM maintenance_operations ORDER BY started_at DESC, id ASC LIMIT $limit
                """;
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) operations.Add(ReadOperation(reader));
        }
        for (var index = 0; index < operations.Count; index++)
            operations[index] = operations[index] with { Steps = await ReadStepsAsync(connection, operations[index].Id, cancellationToken) };
        return operations;
    }

    public async Task UpdateOperationAsync(
        string id,
        MaintenanceOperationStatus status,
        DateTime? completedAt,
        string? outcomeSummary,
        CancellationToken cancellationToken = default)
    {
        ValidateOperationStatus(status);
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE maintenance_operations
            SET status = $status, completed_at = $completed_at, outcome_summary = $outcome_summary
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$completed_at", (object?)SqliteValue.Date(completedAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$outcome_summary", (object?)outcomeSummary ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateStepAsync(MaintenanceOperationStepUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ValidateStepStatus(update.Status);
        ValidateRelativePath(update.StdoutPath, "stdout 路径", allowNull: true);
        ValidateRelativePath(update.StderrPath, "stderr 路径", allowNull: true);
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE maintenance_operation_steps
            SET status = $status, stdout_path = $stdout_path, stderr_path = $stderr_path,
                exit_code = $exit_code, duration_ms = $duration_ms,
                started_at = $started_at, completed_at = $completed_at
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", update.StepId);
        command.Parameters.AddWithValue("$status", update.Status.ToString());
        command.Parameters.AddWithValue("$stdout_path", (object?)update.StdoutPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$stderr_path", (object?)update.StderrPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$exit_code", (object?)update.ExitCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration_ms", update.Duration is null ? DBNull.Value : checked((long)update.Duration.Value.TotalMilliseconds));
        command.Parameters.AddWithValue("$started_at", (object?)SqliteValue.Date(update.StartedAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$completed_at", (object?)SqliteValue.Date(update.CompletedAt) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> HasActiveOperationAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        return await HasActiveOperationAsync(connection, null, deviceId, cancellationToken);
    }

    public async Task<int> RecoverInterruptedAsync(DateTime recoveredAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var steps = connection.CreateCommand())
            {
                steps.Transaction = transaction;
                steps.CommandText = """
                    UPDATE maintenance_operation_steps
                    SET status = 'OutcomeUnknown', completed_at = $recovered_at
                    WHERE status = 'Running' AND operation_id IN (
                        SELECT id FROM maintenance_operations WHERE status IN ('Running', 'StopRequested'))
                    """;
                steps.Parameters.AddWithValue("$recovered_at", SqliteValue.Date(recoveredAt));
                await steps.ExecuteNonQueryAsync(cancellationToken);
            }

            int count;
            await using (var operations = connection.CreateCommand())
            {
                operations.Transaction = transaction;
                operations.CommandText = """
                    UPDATE maintenance_operations
                    SET status = 'OutcomeUnknown', completed_at = $recovered_at,
                        outcome_summary = '客户端启动时发现操作曾被中断，结果状态未知，未自动重放。'
                    WHERE status IN ('Running', 'StopRequested')
                    """;
                operations.Parameters.AddWithValue("$recovered_at", SqliteValue.Date(recoveredAt));
                count = await operations.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return count;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task InsertStepAsync(SqliteConnection connection, SqliteTransaction transaction, MaintenanceOperationStep step, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO maintenance_operation_steps
                (id, operation_id, step_index, name, status, executable, arguments_json,
                 stdout_path, stderr_path, exit_code, duration_ms, started_at, completed_at)
            VALUES
                ($id, $operation_id, $step_index, $name, $status, $executable, $arguments_json,
                 $stdout_path, $stderr_path, $exit_code, $duration_ms, $started_at, $completed_at)
            """;
        command.Parameters.AddWithValue("$id", step.Id);
        command.Parameters.AddWithValue("$operation_id", step.OperationId);
        command.Parameters.AddWithValue("$step_index", step.Index);
        command.Parameters.AddWithValue("$name", step.Name);
        command.Parameters.AddWithValue("$status", step.Status.ToString());
        command.Parameters.AddWithValue("$executable", step.Executable);
        command.Parameters.AddWithValue("$arguments_json", JsonSerializer.Serialize(step.Arguments));
        command.Parameters.AddWithValue("$stdout_path", (object?)step.StdoutPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$stderr_path", (object?)step.StderrPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$exit_code", (object?)step.ExitCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration_ms", step.Duration is null ? DBNull.Value : checked((long)step.Duration.Value.TotalMilliseconds));
        command.Parameters.AddWithValue("$started_at", (object?)SqliteValue.Date(step.StartedAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$completed_at", (object?)SqliteValue.Date(step.CompletedAt) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> HasActiveOperationAsync(SqliteConnection connection, SqliteTransaction? transaction, string deviceId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM maintenance_operations WHERE device_id = $device_id AND status IN ('Running', 'StopRequested'))";
        command.Parameters.AddWithValue("$device_id", deviceId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task<MaintenanceOperation?> ReadOperationAsync(SqliteConnection connection, string id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, workflow_id, workflow_version, extension_id, extension_version, device_id, status,
                   started_at, completed_at, outcome_summary, operation_directory
            FROM maintenance_operations WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadOperation(reader) : null;
    }

    private static MaintenanceOperation ReadOperation(SqliteDataReader reader)
    {
        var operationDirectory = reader.GetString(10);
        ValidateRelativePath(operationDirectory, "持久化操作目录", allowNull: false);
        return new MaintenanceOperation
        {
            Id = reader.GetString(0), WorkflowId = reader.GetString(1), WorkflowVersion = reader.GetString(2),
            ExtensionId = reader.GetString(3), ExtensionVersion = reader.GetString(4), DeviceId = reader.GetString(5),
            Status = ParseOperationStatus(reader.GetString(6)), StartedAt = SqliteValue.ParseDate(reader.GetValue(7)),
            CompletedAt = SqliteValue.ParseNullableDate(reader.IsDBNull(8) ? null : reader.GetValue(8)),
            OutcomeSummary = reader.IsDBNull(9) ? null : reader.GetString(9), OperationDirectory = operationDirectory
        };
    }

    private static async Task<IReadOnlyList<MaintenanceOperationStep>> ReadStepsAsync(SqliteConnection connection, string operationId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, operation_id, step_index, name, status, executable, arguments_json,
                   stdout_path, stderr_path, exit_code, duration_ms, started_at, completed_at
            FROM maintenance_operation_steps WHERE operation_id = $operation_id ORDER BY step_index ASC, id ASC
            """;
        command.Parameters.AddWithValue("$operation_id", operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var steps = new List<MaintenanceOperationStep>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var stdout = reader.IsDBNull(7) ? null : reader.GetString(7);
            var stderr = reader.IsDBNull(8) ? null : reader.GetString(8);
            ValidateRelativePath(stdout, "持久化 stdout 路径", true);
            ValidateRelativePath(stderr, "持久化 stderr 路径", true);
            steps.Add(new MaintenanceOperationStep
            {
                Id = reader.GetString(0), OperationId = reader.GetString(1), Index = reader.GetInt32(2),
                Name = reader.GetString(3), Status = ParseStepStatus(reader.GetString(4)), Executable = reader.GetString(5),
                Arguments = ParseArguments(reader.GetString(6)), StdoutPath = stdout, StderrPath = stderr,
                ExitCode = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                Duration = reader.IsDBNull(10) ? null : TimeSpan.FromMilliseconds(reader.GetInt64(10)),
                StartedAt = SqliteValue.ParseNullableDate(reader.IsDBNull(11) ? null : reader.GetValue(11)),
                CompletedAt = SqliteValue.ParseNullableDate(reader.IsDBNull(12) ? null : reader.GetValue(12))
            });
        }
        return steps;
    }

    private static IReadOnlyList<string> ParseArguments(string json)
    {
        try
        {
            var values = JsonSerializer.Deserialize<string[]>(json) ?? throw new JsonException("数组为空。");
            if (values.Any(value => value is null)) throw new JsonException("数组包含 null。");
            return Array.AsReadOnly(values);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"维护步骤参数 JSON 已损坏：{exception.Message}", exception);
        }
    }

    private static MaintenanceOperationStatus ParseOperationStatus(string value) =>
        Enum.TryParse<MaintenanceOperationStatus>(value, false, out var status) && Enum.IsDefined(status)
            ? status : throw new InvalidDataException($"数据库包含未知维护操作状态：{value}");

    private static MaintenanceStepStatus ParseStepStatus(string value) =>
        Enum.TryParse<MaintenanceStepStatus>(value, false, out var status) && Enum.IsDefined(status)
            ? status : throw new InvalidDataException($"数据库包含未知维护步骤状态：{value}");

    private static void ValidateOperationStatus(MaintenanceOperationStatus status)
    {
        if (!Enum.IsDefined(status)) throw new InvalidDataException($"未知维护操作状态：{status}");
    }

    private static void ValidateStepStatus(MaintenanceStepStatus status)
    {
        if (!Enum.IsDefined(status)) throw new InvalidDataException($"未知维护步骤状态：{status}");
    }

    private static void ValidateRelativePath(string? path, string description, bool allowNull)
    {
        if (path is null && allowNull) return;
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) ||
            path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment => segment is ".." or "."))
            throw new InvalidDataException($"{description}必须是工作区内不含路径穿越的相对路径。");
    }
}
