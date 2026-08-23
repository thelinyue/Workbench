using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

/// <summary>
/// 验证后台任务中心的资源所有权和完成通知边界，避免租约归还失败破坏任务队列状态。
/// </summary>
public sealed class TaskCenterTests
{
    [Fact]
    public async Task EnqueueAsync_WhenOwnedResourceDisposeThrows_StillFinalizesSuccessfulTask()
    {
        var root = CreateRoot();
        var releaseFirst = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseThird = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? secondRun = null;
        Task? thirdRun = null;

        try
        {
            var logger = new WorkbenchLogger(root);
            string? releaseLog = null;
            logger.MessageWritten += (_, message) =>
            {
                if (message.Contains("释放后台任务资源失败", StringComparison.Ordinal))
                    releaseLog = message;
            };

            var center = new TaskCenter(new EmptyTaskRepository(), logger);
            var taskChangedCount = 0;
            center.TaskChanged += (_, _) => Interlocked.Increment(ref taskChangedCount);

            var first = CreateTask("task-dispose-success", "analysis.dispose-success");
            var firstEntered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstRun = center.EnqueueAsync(
                first,
                async token =>
                {
                    firstEntered.TrySetResult(null);
                    await releaseFirst.Task.WaitAsync(token);
                },
                new ThrowingDisposable("模拟租约释放失败"));

            var secondEntered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            secondRun = center.EnqueueAsync(
                CreateTask("task-slot-blocker", "analysis.slot-blocker"),
                async token =>
                {
                    secondEntered.TrySetResult(null);
                    await releaseSecond.Task.WaitAsync(token);
                });

            await Task.WhenAll(firstEntered.Task, secondEntered.Task).WaitAsync(TimeSpan.FromSeconds(2));
            var firstCompletion = center.WaitForCompletionAsync(first.Id);

            var thirdEntered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            thirdRun = center.EnqueueAsync(
                CreateTask("task-slot-successor", "analysis.slot-successor"),
                async token =>
                {
                    thirdEntered.TrySetResult(null);
                    await releaseThird.Task.WaitAsync(token);
                });

            releaseFirst.TrySetResult(null);
            await thirdEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var completionError = await Record.ExceptionAsync(
                () => firstCompletion.WaitAsync(TimeSpan.FromMilliseconds(300)));
            var workerError = await Record.ExceptionAsync(
                () => firstRun.WaitAsync(TimeSpan.FromSeconds(2)));
            var pluginStillActive = center.IsPluginActive(first.PluginId);
            var cancellationStillRegistered = center.Cancel(first.Id);
            var changedAfterFirst = Volatile.Read(ref taskChangedCount);

            releaseSecond.TrySetResult(null);
            releaseThird.TrySetResult(null);
            await Task.WhenAll(secondRun, thirdRun).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Null(completionError);
            Assert.Null(workerError);
            Assert.False(pluginStillActive);
            Assert.False(cancellationStillRegistered);
            Assert.Equal(1, changedAfterFirst);
            Assert.NotNull(releaseLog);
            Assert.Contains(first.Id, releaseLog, StringComparison.Ordinal);
            Assert.Contains("模拟租约释放失败", releaseLog, StringComparison.Ordinal);
        }
        finally
        {
            releaseFirst.TrySetResult(null);
            releaseSecond.TrySetResult(null);
            releaseThird.TrySetResult(null);
            if (secondRun is not null)
                await IgnoreFailureAsync(secondRun);
            if (thirdRun is not null)
                await IgnoreFailureAsync(thirdRun);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnqueueAsync_WhenActionAndDisposeThrow_PreservesOriginalActionFailure()
    {
        var center = new TaskCenter(new EmptyTaskRepository());
        var actionEntered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAction = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var originalFailure = new InvalidOperationException("模拟原始分析失败");
        var task = CreateTask("task-action-failure", "analysis.action-failure");
        var taskChangedCount = 0;
        center.TaskChanged += (_, _) => Interlocked.Increment(ref taskChangedCount);

        var worker = center.EnqueueAsync(
            task,
            async token =>
            {
                actionEntered.TrySetResult(null);
                await releaseAction.Task.WaitAsync(token);
                throw originalFailure;
            },
            new ThrowingDisposable("模拟租约释放失败"));

        await actionEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var completion = center.WaitForCompletionAsync(task.Id);
        releaseAction.TrySetResult(null);

        var completionError = await Record.ExceptionAsync(
            () => completion.WaitAsync(TimeSpan.FromMilliseconds(300)));
        var workerError = await Record.ExceptionAsync(
            () => worker.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Same(originalFailure, completionError);
        Assert.Same(originalFailure, workerError);
        Assert.False(center.IsPluginActive(task.PluginId));
        Assert.False(center.Cancel(task.Id));
        Assert.Equal(1, Volatile.Read(ref taskChangedCount));
    }

    private static AnalysisTask CreateTask(string id, string pluginId) => new()
    {
        Id = id,
        CaseId = $"case-{id}",
        PluginId = pluginId
    };

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // RED 阶段也必须释放并发阻塞，不让预期失败污染测试进程。
        }
    }

    private sealed class ThrowingDisposable(string message) : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException(message);
    }

    private sealed class EmptyTaskRepository : IAnalysisTaskRepository
    {
        public Task<IReadOnlyList<AnalysisTask>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AnalysisTask>>(Array.Empty<AnalysisTask>());

        public Task<AnalysisTask?> GetAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult<AnalysisTask?>(null);

        public Task InsertAsync(AnalysisTask item, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UpdateAsync(AnalysisTask item, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
