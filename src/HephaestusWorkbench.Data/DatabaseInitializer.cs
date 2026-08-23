namespace HephaestusWorkbench.Data;

/// <summary>
/// 初始化 v2.0.0 全新工作区数据库。
/// 正式版不执行旧表迁移，也不创建旧版设置、插件登记或报告会话表。
/// </summary>
public sealed class DatabaseInitializer
{
    private readonly SqliteConnectionFactory _factory;

    public DatabaseInitializer(SqliteConnectionFactory factory) => _factory = factory;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            CREATE TABLE IF NOT EXISTS analysis_cases (
                id TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                original_name TEXT NOT NULL,
                device_id TEXT NOT NULL,
                log_time TEXT NOT NULL,
                status TEXT NOT NULL,
                source_path TEXT NOT NULL,
                extract_path TEXT NOT NULL,
                report_path TEXT NULL,
                error_message TEXT NULL,
                create_time TEXT NOT NULL,
                update_time TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS analysis_tasks (
                id TEXT PRIMARY KEY,
                case_id TEXT NOT NULL,
                plugin_id TEXT NOT NULL,
                analysis_scope TEXT NOT NULL,
                status TEXT NOT NULL,
                start_time TEXT NULL,
                end_time TEXT NULL,
                report_path TEXT NULL,
                error_message TEXT NULL,
                FOREIGN KEY(case_id) REFERENCES analysis_cases(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS reports (
                id TEXT PRIMARY KEY,
                case_id TEXT NOT NULL,
                path TEXT NOT NULL,
                plugin_id TEXT NULL,
                plugin_name TEXT NULL,
                plugin_version TEXT NULL,
                create_time TEXT NOT NULL,
                last_opened_at TEXT NULL,
                FOREIGN KEY(case_id) REFERENCES analysis_cases(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS ssh_devices (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                host TEXT NOT NULL,
                port INTEGER NOT NULL,
                username TEXT NOT NULL,
                authentication_method TEXT NOT NULL,
                private_key_path TEXT NULL,
                credential_target TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ssh_host_keys (
                host TEXT NOT NULL,
                port INTEGER NOT NULL,
                key_algorithm TEXT NOT NULL,
                fingerprint TEXT NOT NULL,
                first_seen_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                PRIMARY KEY(host, port)
            );
            CREATE TABLE IF NOT EXISTS ssh_connection_history (
                id TEXT PRIMARY KEY,
                device_id TEXT NULL,
                host TEXT NOT NULL,
                port INTEGER NOT NULL,
                username TEXT NOT NULL,
                connected_at TEXT NOT NULL,
                disconnected_at TEXT NULL,
                outcome TEXT NOT NULL,
                error_message TEXT NULL,
                FOREIGN KEY(device_id) REFERENCES ssh_devices(id) ON DELETE SET NULL
            );
            CREATE TABLE IF NOT EXISTS maintenance_operations (
                id TEXT PRIMARY KEY,
                workflow_id TEXT NOT NULL,
                workflow_version TEXT NOT NULL,
                extension_id TEXT NOT NULL,
                extension_version TEXT NOT NULL,
                device_id TEXT NOT NULL,
                status TEXT NOT NULL,
                started_at TEXT NOT NULL,
                completed_at TEXT NULL,
                outcome_summary TEXT NULL,
                operation_directory TEXT NOT NULL,
                FOREIGN KEY(device_id) REFERENCES ssh_devices(id) ON DELETE RESTRICT
            );
            CREATE TABLE IF NOT EXISTS maintenance_operation_steps (
                id TEXT PRIMARY KEY,
                operation_id TEXT NOT NULL,
                step_index INTEGER NOT NULL,
                name TEXT NOT NULL,
                status TEXT NOT NULL,
                executable TEXT NOT NULL,
                arguments_json TEXT NOT NULL,
                stdout_path TEXT NULL,
                stderr_path TEXT NULL,
                exit_code INTEGER NULL,
                duration_ms INTEGER NULL,
                started_at TEXT NULL,
                completed_at TEXT NULL,
                FOREIGN KEY(operation_id) REFERENCES maintenance_operations(id) ON DELETE CASCADE,
                UNIQUE(operation_id, step_index)
            );
            CREATE INDEX IF NOT EXISTS idx_tasks_case_id ON analysis_tasks(case_id);
            CREATE INDEX IF NOT EXISTS idx_cases_update_time ON analysis_cases(update_time DESC);
            CREATE INDEX IF NOT EXISTS idx_reports_create_time ON reports(create_time DESC);
            CREATE INDEX IF NOT EXISTS idx_ssh_history_connected_at ON ssh_connection_history(connected_at DESC);
            CREATE INDEX IF NOT EXISTS idx_maintenance_started_at ON maintenance_operations(started_at DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
