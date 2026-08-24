using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;
using Microsoft.Data.Sqlite;

namespace HephaestusWorkbench.Tests;

public sealed class SqliteMaintenanceOperationRepositoryTests
{
    [Fact]
    public async Task CreateGetListAndUpdatesPersistTokensAndRelativeOutputPaths()
    {
        using var env = await RepoEnv.CreateAsync();
        var repository = new SqliteMaintenanceOperationRepository(env.Factory);
        await repository.CreateAsync(Operation("operation-1", MaintenanceOperationStatus.Planned, "device-1",
            Step("step-1", 0, MaintenanceStepStatus.Pending, ["--device", "value with space"])), false);
        await repository.UpdateOperationAsync("operation-1", MaintenanceOperationStatus.Running, null, null);
        var started = DateTime.UtcNow;
        await repository.UpdateStepAsync(new MaintenanceOperationStepUpdate
        {
            StepId = "step-1", Status = MaintenanceStepStatus.Succeeded,
            StdoutPath = "Operations/operation-1/step-0.stdout.log",
            StderrPath = "Operations/operation-1/step-0.stderr.log",
            ExitCode = 0, Duration = TimeSpan.FromMilliseconds(125),
            StartedAt = started, CompletedAt = started.AddMilliseconds(125)
        });

        var saved = await repository.GetAsync("operation-1");
        Assert.NotNull(saved);
        Assert.Equal(MaintenanceOperationStatus.Running, saved.Status);
        var step = Assert.Single(saved.Steps);
        Assert.Equal(new[] { "--device", "value with space" }, step.Arguments);
        Assert.Equal("Operations/operation-1/step-0.stdout.log", step.StdoutPath);
        Assert.Equal(TimeSpan.FromMilliseconds(125), step.Duration);
        Assert.Equal("operation-1", Assert.Single(await repository.ListRecentAsync(10)).Id);

        await using var connection = await env.Factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT arguments_json FROM maintenance_operation_steps WHERE id = 'step-1'";
        Assert.Equal("[\"--device\",\"value with space\"]", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CreateIsTransactionalWhenAnyStepFails()
    {
        using var env = await RepoEnv.CreateAsync();
        var repository = new SqliteMaintenanceOperationRepository(env.Factory);
        var operation = Operation("transaction-failure", MaintenanceOperationStatus.Planned, "device-1",
            Step("step-a", 0, MaintenanceStepStatus.Pending, []),
            Step("step-b", 0, MaintenanceStepStatus.Pending, []));

        await Assert.ThrowsAsync<SqliteException>(() => repository.CreateAsync(operation, false));
        Assert.Null(await repository.GetAsync(operation.Id));
    }

    [Fact]
    public async Task RecoverInterruptedMarksOperationsAndRunningStepsOutcomeUnknown()
    {
        using var env = await RepoEnv.CreateAsync();
        var repository = new SqliteMaintenanceOperationRepository(env.Factory);
        await repository.CreateAsync(Operation("running", MaintenanceOperationStatus.Running, "device-1",
            Step("running-step", 0, MaintenanceStepStatus.Running, [])), true);
        await repository.CreateAsync(Operation("stopping", MaintenanceOperationStatus.StopRequested, "device-2",
            Step("stopping-step", 0, MaintenanceStepStatus.Running, [])), true);
        await repository.CreateAsync(Operation("planned", MaintenanceOperationStatus.Planned, "device-1",
            Step("planned-step", 0, MaintenanceStepStatus.Pending, [])), true);

        Assert.Equal(2, await repository.RecoverInterruptedAsync(DateTime.UtcNow));
        Assert.Equal(MaintenanceOperationStatus.OutcomeUnknown, (await repository.GetAsync("running"))!.Status);
        Assert.Equal(MaintenanceStepStatus.OutcomeUnknown, (await repository.GetAsync("running"))!.Steps[0].Status);
        Assert.Equal(MaintenanceOperationStatus.OutcomeUnknown, (await repository.GetAsync("stopping"))!.Status);
        Assert.Equal(MaintenanceOperationStatus.Planned, (await repository.GetAsync("planned"))!.Status);
    }

    [Fact]
    public async Task ReadsFailClosedForUnknownStatusesAndCorruptedArguments()
    {
        using var env = await RepoEnv.CreateAsync();
        var repository = new SqliteMaintenanceOperationRepository(env.Factory);
        await repository.CreateAsync(Operation("corrupt", MaintenanceOperationStatus.Planned, "device-1",
            Step("corrupt-step", 0, MaintenanceStepStatus.Pending, [])), true);

        await env.ExecuteAsync("UPDATE maintenance_operations SET status = 'Mystery' WHERE id = 'corrupt'");
        Assert.Contains("操作状态", (await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetAsync("corrupt"))).Message);
        await env.ExecuteAsync("UPDATE maintenance_operations SET status = 'Planned'; UPDATE maintenance_operation_steps SET status = 'Mystery'");
        Assert.Contains("步骤状态", (await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetAsync("corrupt"))).Message);
        await env.ExecuteAsync("UPDATE maintenance_operation_steps SET status = 'Pending', arguments_json = '{bad json'");
        Assert.Contains("参数", (await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetAsync("corrupt"))).Message);
    }

    [Fact]
    public async Task RejectsAbsoluteTraversalPathsAndEnforcesDeviceChangeLock()
    {
        using var env = await RepoEnv.CreateAsync();
        var repository = new SqliteMaintenanceOperationRepository(env.Factory);
        var absolute = Operation("absolute", MaintenanceOperationStatus.Planned, "device-1",
            Step("absolute-step", 0, MaintenanceStepStatus.Pending, [])) with
        { OperationDirectory = Path.GetFullPath(Path.Combine(env.Root, "absolute")) };
        Assert.Contains("相对路径", (await Assert.ThrowsAsync<InvalidDataException>(
            () => repository.CreateAsync(absolute, true))).Message);

        await repository.CreateAsync(Operation("active", MaintenanceOperationStatus.Running, "device-1",
            Step("active-step", 0, MaintenanceStepStatus.Running, [])), true);
        Assert.Contains("运行", (await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CreateAsync(
            Operation("change", MaintenanceOperationStatus.Planned, "device-1",
                Step("change-step", 0, MaintenanceStepStatus.Pending, [])), false))).Message);
        await repository.CreateAsync(Operation("readonly", MaintenanceOperationStatus.Planned, "device-1",
            Step("readonly-step", 0, MaintenanceStepStatus.Pending, [])), true);
        Assert.True(await repository.HasActiveOperationAsync("device-1"));

        Assert.Contains("相对路径", (await Assert.ThrowsAsync<InvalidDataException>(() => repository.UpdateStepAsync(
            new MaintenanceOperationStepUpdate
            {
                StepId = "readonly-step", Status = MaintenanceStepStatus.Running,
                StdoutPath = "../secret.txt", StartedAt = DateTime.UtcNow
            }))).Message);
    }

    private static MaintenanceOperation Operation(string id, MaintenanceOperationStatus status, string deviceId,
        params MaintenanceOperationStep[] steps) => new()
    {
        Id = id, WorkflowId = "workflow", WorkflowVersion = "1.0.0",
        ExtensionId = "extension", ExtensionVersion = "1.0.0", DeviceId = deviceId,
        Status = status, StartedAt = DateTime.UtcNow, OperationDirectory = $"Operations/{id}",
        Steps = steps.Select(step => step with { OperationId = id }).ToArray()
    };

    private static MaintenanceOperationStep Step(string id, int index, MaintenanceStepStatus status,
        IReadOnlyList<string> arguments) => new()
    {
        Id = id, OperationId = "pending", Index = index, Name = "测试步骤", Status = status,
        Executable = "/usr/bin/true", Arguments = arguments
    };

    private sealed class RepoEnv : IDisposable
    {
        private RepoEnv(string root, SqliteConnectionFactory factory) { Root = root; Factory = factory; }
        public string Root { get; }
        public SqliteConnectionFactory Factory { get; }

        public static async Task<RepoEnv> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
            var factory = new SqliteConnectionFactory(new DataPaths(root));
            await new DatabaseInitializer(factory).InitializeAsync();
            await using var connection = await factory.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ssh_devices
                    (id, name, host, port, username, authentication_method, private_key_path, credential_target, created_at, updated_at)
                VALUES
                    ('device-1', '设备一', 'host-1', 22, 'root', 'Password', NULL, NULL, $now, $now),
                    ('device-2', '设备二', 'host-2', 22, 'root', 'Password', NULL, NULL, $now, $now);
                """;
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
            return new RepoEnv(root, factory);
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = await Factory.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
