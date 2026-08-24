using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;
using AnalysisTaskStatus = HephaestusWorkbench.Core.Models.TaskStatus;

namespace HephaestusWorkbench.Tests;

public sealed class SqliteAnalysisLifecycleRepositoryTests
{
    [Fact]
    public async Task CreateAsync_CommitsCaseAndTaskTogether()
    {
        var root = CreateRoot();
        try
        {
            var paths = new DataPaths(root);
            var factory = new SqliteConnectionFactory(paths);
            await new DatabaseInitializer(factory).InitializeAsync();
            var lifecycle = new SqliteAnalysisLifecycleRepository(factory);
            var now = DateTime.Now;
            var analysisCase = NewCase("case-transaction", now, CaseStatus.Ready);
            var task = NewTask(analysisCase.Id, AnalysisTaskStatus.Waiting);

            await lifecycle.CreateAsync(analysisCase, task);

            await using var connection = await factory.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM analysis_cases WHERE id = $id UNION ALL SELECT COUNT(*) FROM analysis_tasks WHERE id = $taskId";
            command.Parameters.AddWithValue("$id", analysisCase.Id);
            command.Parameters.AddWithValue("$taskId", task.Id);
            await using var reader = await command.ExecuteReaderAsync();
            var counts = new List<long>();
            while (await reader.ReadAsync()) counts.Add(reader.GetInt64(0));
            Assert.Equal(new[] { 1L, 1L }, counts);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RecoverInterruptedAsync_MarksWaitingAndRunningTasksAsFailed()
    {
        var root = CreateRoot();
        try
        {
            var paths = new DataPaths(root);
            var factory = new SqliteConnectionFactory(paths);
            await new DatabaseInitializer(factory).InitializeAsync();
            var lifecycle = new SqliteAnalysisLifecycleRepository(factory);
            var now = DateTime.Now;
            var analysisCase = NewCase("case-recovery", now, CaseStatus.Ready);
            var task = NewTask(analysisCase.Id, AnalysisTaskStatus.Waiting);
            await lifecycle.CreateAsync(analysisCase, task);

            var recovered = await lifecycle.RecoverInterruptedAsync(now.AddMinutes(1));

            Assert.Equal(1, recovered);
            await using var connection = await factory.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT c.status, t.status, t.error_message FROM analysis_cases c JOIN analysis_tasks t ON t.case_id = c.id WHERE c.id = $id";
            command.Parameters.AddWithValue("$id", analysisCase.Id);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(nameof(CaseStatus.Failed), reader.GetString(0));
            Assert.Equal(nameof(AnalysisTaskStatus.Failed), reader.GetString(1));
            Assert.Contains("上次退出", reader.GetString(2));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task DeleteByCaseIdsAsync_DeletesReportsTasksAndCasesInOneTransaction()
    {
        var root = CreateRoot();
        try
        {
            var paths = new DataPaths(root);
            var factory = new SqliteConnectionFactory(paths);
            await new DatabaseInitializer(factory).InitializeAsync();
            var cases = new SqliteCaseRepository(factory);
            var tasks = new SqliteTaskRepository(factory);
            var reports = new SqliteReportRepository(factory);
            var lifecycle = new SqliteAnalysisLifecycleRepository(factory);
            var now = DateTime.Now;
            await cases.InsertAsync(NewCase("case-delete", now, CaseStatus.Completed));
            await tasks.InsertAsync(NewTask("case-delete", AnalysisTaskStatus.Completed));
            await reports.InsertAsync(new Report
            {
                Id = "report-delete",
                CaseId = "case-delete",
                Path = Path.Combine(root, "report"),
                CreateTime = now
            });

            await lifecycle.DeleteByCaseIdsAsync(new[] { "case-delete" });

            Assert.Equal(0, await CountRowsAsync(factory, "reports"));
            Assert.Equal(0, await CountRowsAsync(factory, "analysis_tasks"));
            Assert.Equal(0, await CountRowsAsync(factory, "analysis_cases"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }
    private static AnalysisCase NewCase(string id, DateTime now, CaseStatus status) => new()
    {
        Id = id,
        DisplayName = id,
        OriginalName = "sample.tgz",
        DeviceId = "device",
        LogTime = now,
        Status = status,
        SourcePath = "Inbox/sample.tgz",
        ExtractPath = "Inbox/sample",
        CreateTime = now,
        UpdateTime = now
    };

    private static AnalysisTask NewTask(string caseId, AnalysisTaskStatus status) => new()
    {
        Id = $"task-{caseId}",
        CaseId = caseId,
        PluginId = "log-analyzer",
        Status = status
    };

    private static async Task<long> CountRowsAsync(SqliteConnectionFactory factory, string table)
    {
        await using var connection = await factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)(await command.ExecuteScalarAsync())!;
    }
    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
