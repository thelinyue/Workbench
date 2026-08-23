namespace HephaestusWorkbench.Data;

/// <summary>数据库启动初始化器，采用幂等 DDL，保证用户升级时不会覆盖历史数据。</summary>
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
                status TEXT NOT NULL,
                start_time TEXT NULL,
                end_time TEXT NULL,
                report_path TEXT NULL,
                error_message TEXT NULL,
                FOREIGN KEY(case_id) REFERENCES analysis_cases(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS plugin_info (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                version TEXT NOT NULL,
                type TEXT NOT NULL,
                path TEXT NOT NULL,
                entry TEXT NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS reports (
                id TEXT PRIMARY KEY,
                case_id TEXT NOT NULL,
                path TEXT NOT NULL,
                report_key TEXT NOT NULL DEFAULT 'legacy',
                title TEXT NOT NULL DEFAULT '综合日志分析报告',
                kind TEXT NOT NULL DEFAULT 'log-analysis',
                entry_file TEXT NOT NULL DEFAULT 'report.html',
                is_default INTEGER NOT NULL DEFAULT 1,
                plugin_id TEXT NULL,
                plugin_name TEXT NULL,
                plugin_version TEXT NULL,
                create_time TEXT NOT NULL,
                FOREIGN KEY(case_id) REFERENCES analysis_cases(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS report_sessions (
                id TEXT PRIMARY KEY,
                report_id TEXT NOT NULL UNIQUE,
                order_index INTEGER NOT NULL,
                is_active INTEGER NOT NULL DEFAULT 0,
                scroll_position REAL NOT NULL DEFAULT 0,
                last_open_time TEXT NOT NULL,
                FOREIGN KEY(report_id) REFERENCES reports(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS app_settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_tasks_case_id ON analysis_tasks(case_id);
            CREATE INDEX IF NOT EXISTS idx_cases_update_time ON analysis_cases(update_time DESC);
            CREATE INDEX IF NOT EXISTS idx_reports_create_time ON reports(create_time DESC);
            CREATE INDEX IF NOT EXISTS idx_report_sessions_order ON report_sessions(order_index);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        // 旧版数据库的 reports 表没有 plugin_id。SQLite 不支持 ADD COLUMN IF NOT EXISTS，
        // 因此先检查表结构，再以幂等方式迁移并用最近一次任务的插件信息回填。
        // 多报告协议新增列均提供旧 report.html 的兼容默认值，升级后历史报告无需重建即可继续打开。
        foreach (var migration in new[]
        {
            (Column: "report_key", Sql: "ALTER TABLE reports ADD COLUMN report_key TEXT NOT NULL DEFAULT 'legacy';"),
            (Column: "title", Sql: "ALTER TABLE reports ADD COLUMN title TEXT NOT NULL DEFAULT '综合日志分析报告';"),
            (Column: "kind", Sql: "ALTER TABLE reports ADD COLUMN kind TEXT NOT NULL DEFAULT 'log-analysis';"),
            (Column: "entry_file", Sql: "ALTER TABLE reports ADD COLUMN entry_file TEXT NOT NULL DEFAULT 'report.html';"),
            (Column: "is_default", Sql: "ALTER TABLE reports ADD COLUMN is_default INTEGER NOT NULL DEFAULT 1;")
        })
        {
            if (await ColumnExistsAsync(connection, "reports", migration.Column, cancellationToken)) continue;
            await using var alter = connection.CreateCommand();
            alter.CommandText = migration.Sql;
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!await ColumnExistsAsync(connection, "reports", "plugin_id", cancellationToken))
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE reports ADD COLUMN plugin_id TEXT NULL;";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!await ColumnExistsAsync(connection, "reports", "plugin_name", cancellationToken))
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE reports ADD COLUMN plugin_name TEXT NULL;";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!await ColumnExistsAsync(connection, "reports", "plugin_version", cancellationToken))
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE reports ADD COLUMN plugin_version TEXT NULL;";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var backfill = connection.CreateCommand();
        backfill.CommandText = """
            UPDATE reports
            SET plugin_id = (
                SELECT t.plugin_id
                FROM analysis_tasks t
                WHERE t.case_id = reports.case_id
                ORDER BY COALESCE(t.end_time, t.start_time) DESC
                LIMIT 1
            )
            WHERE plugin_id IS NULL;
            """;
        await backfill.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> ColumnExistsAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
