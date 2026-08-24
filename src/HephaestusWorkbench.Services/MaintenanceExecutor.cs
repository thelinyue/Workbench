using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Core.Services;
using HephaestusWorkbench.Data;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 执行宿主生成的不可变维护计划。每个步骤通过 <see cref="ICommandExecutionService"/> 创建独立 SSH exec 连接，
/// stdout/stderr 分别写入工作空间 Operations 目录，SQLite 只接收相对路径和执行摘要。
/// 执行期间一旦连接或本地输出状态无法确认，操作立即进入 OutcomeUnknown，且本类绝不自动重放步骤。
/// </summary>
public sealed partial class MaintenanceExecutor(
    DataPaths paths,
    ISshDeviceRepository devices,
    IMaintenanceDiscoveryService discovery,
    IMaintenancePolicy policy,
    ICommandExecutionService commands,
    IMaintenanceOperationRepository operations,
    TimeSpan? commandTimeout = null) : IMaintenanceExecutor
{
    private readonly TimeSpan _commandTimeout = commandTimeout ?? TimeSpan.FromMinutes(10);

    public async IAsyncEnumerable<MaintenanceOperationEvent> ExecuteAsync(
        MaintenanceExecutionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Plan);
        var plan = request.Plan;
        ValidatePlan(plan);
        var device = await devices.GetAsync(plan.DeviceId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"未找到维护计划引用的 SSH 设备“{plan.DeviceId}”。");

        var connection = new SshConnectionRequest(
            device.Id,
            device.Host,
            device.Port,
            device.Username,
            device.AuthenticationMethod,
            device.PrivateKeyPath,
            device.CredentialTarget);
        var preflight = await discovery.DiscoverAsync(plan.TargetType, connection, null, cancellationToken).ConfigureAwait(false);
        var policyDecision = policy.VerifyExecution(
            plan,
            preflight,
            request.ConfirmationDisplayName,
            request.Automatic);
        if (!policyDecision.IsAllowed)
            throw new InvalidOperationException($"维护执行前身份复核未通过：{string.Join("；", policyDecision.Errors)}");

        var operationDirectory = Path.Combine(paths.OperationsDirectory, plan.Id);
        Directory.CreateDirectory(operationDirectory);
        var relativeOperationDirectory = ToRelativePath(operationDirectory);
        var startedAt = DateTime.UtcNow;
        var persistedSteps = plan.Steps.Select(step => CreatePersistedStep(plan.Id, relativeOperationDirectory, step)).ToArray();
        var operation = new MaintenanceOperation
        {
            Id = plan.Id,
            WorkflowId = plan.WorkflowId,
            WorkflowVersion = plan.WorkflowVersion,
            ExtensionId = plan.ExtensionId,
            ExtensionVersion = plan.ExtensionVersion,
            DeviceId = plan.DeviceId,
            Status = MaintenanceOperationStatus.Planned,
            StartedAt = startedAt,
            OperationDirectory = relativeOperationDirectory,
            Steps = persistedSteps
        };

        await operations.CreateAsync(operation, plan.RiskLevel == MaintenanceRiskLevel.ReadOnly, cancellationToken).ConfigureAwait(false);
        await operations.UpdateOperationAsync(plan.Id, MaintenanceOperationStatus.Running, null, null, cancellationToken).ConfigureAwait(false);
        yield return OperationEvent(plan.Id, MaintenanceOperationStatus.Running, "维护操作开始执行。");

        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var step = plan.Steps[index];
            var persisted = persistedSteps[index];
            if (cancellationToken.IsCancellationRequested)
            {
                await MarkStoppedAsync(plan.Id).ConfigureAwait(false);
                yield return OperationEvent(plan.Id, MaintenanceOperationStatus.Failed, "操作已按用户请求停止。", persisted.Id);
                yield break;
            }

            var stepStartedAt = DateTime.UtcNow;
            await operations.UpdateStepAsync(new MaintenanceOperationStepUpdate
            {
                StepId = persisted.Id,
                Status = MaintenanceStepStatus.Running,
                StdoutPath = persisted.StdoutPath,
                StderrPath = persisted.StderrPath,
                StartedAt = stepStartedAt
            }, CancellationToken.None).ConfigureAwait(false);
            yield return StepEvent(plan.Id, persisted.Id, MaintenanceStepStatus.Running, $"开始执行步骤“{step.Name}”。");

            var stdoutAbsolute = ResolveOutputPath(persisted.StdoutPath!);
            var stderrAbsolute = ResolveOutputPath(persisted.StderrPath!);
            var executionToken = step.IsReadOnly ? cancellationToken : CancellationToken.None;
            var outcome = await ExecuteAndPersistStepAsync(
                plan.Id,
                connection,
                step,
                persisted,
                stepStartedAt,
                stdoutAbsolute,
                stderrAbsolute,
                executionToken,
                cancellationToken).ConfigureAwait(false);

            yield return StepEvent(plan.Id, persisted.Id, outcome.StepStatus, outcome.StepMessage);
            if (outcome.OperationStatus is not null)
                yield return OperationEvent(plan.Id, outcome.OperationStatus.Value, outcome.OperationMessage!, persisted.Id);
            if (!outcome.ShouldContinue)
                yield break;

            if (cancellationToken.IsCancellationRequested)
            {
                await MarkStoppedAsync(plan.Id).ConfigureAwait(false);
                yield return OperationEvent(plan.Id, MaintenanceOperationStatus.Failed, "当前步骤已经结束，操作已按用户请求停止。", persisted.Id);
                yield break;
            }
        }

        var succeededAt = DateTime.UtcNow;
        await operations.UpdateOperationAsync(plan.Id, MaintenanceOperationStatus.Succeeded, succeededAt, "全部维护步骤执行成功。", CancellationToken.None).ConfigureAwait(false);
        yield return OperationEvent(plan.Id, MaintenanceOperationStatus.Succeeded, "全部维护步骤执行成功。");
    }

    private async Task<StepRunOutcome> ExecuteAndPersistStepAsync(
        string operationId,
        SshConnectionRequest connection,
        ExecutionStep step,
        MaintenanceOperationStep persisted,
        DateTime startedAt,
        string stdoutPath,
        string stderrPath,
        CancellationToken executionToken,
        CancellationToken stopToken)
    {
        try
        {
            var result = await ExecuteStepAsync(connection, step, stdoutPath, stderrPath, executionToken).ConfigureAwait(false);
            var completedAt = DateTime.UtcNow;
            var stepStatus = result.ExitCode == 0 ? MaintenanceStepStatus.Succeeded : MaintenanceStepStatus.Failed;
            var stepMessage = result.ExitCode == 0
                ? $"步骤“{step.Name}”执行成功。"
                : $"步骤“{step.Name}”执行失败，Exit Code {result.ExitCode}。";
            await operations.UpdateStepAsync(new MaintenanceOperationStepUpdate
            {
                StepId = persisted.Id,
                Status = stepStatus,
                StdoutPath = persisted.StdoutPath,
                StderrPath = persisted.StderrPath,
                ExitCode = result.ExitCode,
                Duration = result.Duration,
                StartedAt = startedAt,
                CompletedAt = completedAt
            }, CancellationToken.None).ConfigureAwait(false);

            if (result.ExitCode == 0)
                return new StepRunOutcome(stepStatus, stepMessage, null, null, true);

            await operations.UpdateOperationAsync(operationId, MaintenanceOperationStatus.Failed, completedAt, stepMessage, CancellationToken.None).ConfigureAwait(false);
            return new StepRunOutcome(stepStatus, stepMessage, MaintenanceOperationStatus.Failed, stepMessage, false);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested && step.IsReadOnly)
        {
            var completedAt = DateTime.UtcNow;
            const string stepMessage = "只读步骤已按用户请求中止。";
            const string operationMessage = "操作已按用户请求停止。";
            await operations.UpdateStepAsync(new MaintenanceOperationStepUpdate
            {
                StepId = persisted.Id,
                Status = MaintenanceStepStatus.Failed,
                StdoutPath = persisted.StdoutPath,
                StderrPath = persisted.StderrPath,
                StartedAt = startedAt,
                CompletedAt = completedAt
            }, CancellationToken.None).ConfigureAwait(false);
            await operations.UpdateOperationAsync(operationId, MaintenanceOperationStatus.Failed, completedAt, operationMessage, CancellationToken.None).ConfigureAwait(false);
            return new StepRunOutcome(MaintenanceStepStatus.Failed, stepMessage, MaintenanceOperationStatus.Failed, operationMessage, false);
        }
        catch (Exception)
        {
            var completedAt = DateTime.UtcNow;
            const string summary = "SSH 执行中断，远端状态无法确认；操作不会自动重放。";
            await operations.UpdateStepAsync(new MaintenanceOperationStepUpdate
            {
                StepId = persisted.Id,
                Status = MaintenanceStepStatus.OutcomeUnknown,
                StdoutPath = persisted.StdoutPath,
                StderrPath = persisted.StderrPath,
                StartedAt = startedAt,
                CompletedAt = completedAt
            }, CancellationToken.None).ConfigureAwait(false);
            await operations.UpdateOperationAsync(operationId, MaintenanceOperationStatus.OutcomeUnknown, completedAt, summary, CancellationToken.None).ConfigureAwait(false);
            return new StepRunOutcome(MaintenanceStepStatus.OutcomeUnknown, summary, MaintenanceOperationStatus.OutcomeUnknown, summary, false);
        }
    }

    private async Task<RemoteCommandResult> ExecuteStepAsync(
        SshConnectionRequest connection,
        ExecutionStep step,
        string stdoutPath,
        string stderrPath,
        CancellationToken cancellationToken)
    {
        await using var stdout = CreateOutputWriter(stdoutPath);
        await using var stderr = CreateOutputWriter(stderrPath);
        var request = new RemoteCommandRequest(connection, step.Executable, step.Arguments, _commandTimeout);
        var result = await commands.ExecuteAsync(request, null, async (chunk, token) =>
        {
            var writer = chunk.Stream == RemoteCommandOutputStream.Stdout ? stdout : stderr;
            await writer.WriteAsync(chunk.Text.AsMemory(), token).ConfigureAwait(false);
            await writer.FlushAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        await stdout.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        await stderr.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    private async Task MarkStoppedAsync(string operationId)
    {
        await operations.UpdateOperationAsync(
            operationId,
            MaintenanceOperationStatus.Failed,
            DateTime.UtcNow,
            "操作已按用户请求停止。",
            CancellationToken.None).ConfigureAwait(false);
    }

    private MaintenanceOperationStep CreatePersistedStep(string operationId, string operationDirectory, ExecutionStep step)
    {
        var filePrefix = step.Index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
        return new MaintenanceOperationStep
        {
            Id = $"{operationId}:{step.Id}",
            OperationId = operationId,
            Index = step.Index,
            Name = step.Name,
            Status = MaintenanceStepStatus.Pending,
            Executable = step.Executable,
            Arguments = step.Arguments.ToArray(),
            StdoutPath = $"{operationDirectory}/{filePrefix}.stdout.log",
            StderrPath = $"{operationDirectory}/{filePrefix}.stderr.log"
        };
    }

    private string ResolveOutputPath(string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(paths.StorageRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(paths.OperationsDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("维护输出路径必须位于 Operations 目录内。");
        return fullPath;
    }

    private string ToRelativePath(string absolutePath) =>
        Path.GetRelativePath(paths.StorageRoot, absolutePath).Replace(Path.DirectorySeparatorChar, '/');

    private static StreamWriter CreateOutputWriter(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous), new UTF8Encoding(false));
    }

    private static void ValidatePlan(ExecutionPlan plan)
    {
        if (!SafeIdentifier().IsMatch(plan.Id))
            throw new InvalidDataException("维护操作标识只能包含字母、数字、点、下划线和短横线。");
        if (plan.Steps.Count == 0)
            throw new InvalidDataException("维护执行计划至少需要一个步骤。");
        if (plan.Steps.Select(step => step.Id).Distinct(StringComparer.Ordinal).Count() != plan.Steps.Count)
            throw new InvalidDataException("维护执行计划包含重复步骤标识。");
    }

    private static MaintenanceOperationEvent OperationEvent(
        string operationId,
        MaintenanceOperationStatus status,
        string message,
        string? stepId = null) =>
        new(operationId, stepId, MaintenanceOperationEventKind.OperationStatusChanged, DateTime.UtcNow, message, status, null);

    private static MaintenanceOperationEvent StepEvent(
        string operationId,
        string stepId,
        MaintenanceStepStatus status,
        string message) =>
        new(operationId, stepId, MaintenanceOperationEventKind.StepStatusChanged, DateTime.UtcNow, message, null, status);

    private sealed record StepRunOutcome(
        MaintenanceStepStatus StepStatus,
        string StepMessage,
        MaintenanceOperationStatus? OperationStatus,
        string? OperationMessage,
        bool ShouldContinue);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifier();
}
