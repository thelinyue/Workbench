using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;

namespace HephaestusWorkbench.Services;

/// <summary>后台任务中心，限制并行插件数量，避免大日志同时运行耗尽机器资源。</summary>
public sealed class TaskCenter
{
    private readonly IAnalysisTaskRepository _tasks;
    private readonly WorkbenchLogger? _logger;
    private readonly SemaphoreSlim _slots = new(2, 2);
    private readonly Dictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _taskPlugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TaskCompletionSource<object?>> _completions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    /// <summary>
    /// 创建后台任务中心。日志器保持可选以兼容现有组合根，但提供时会记录资源释放异常。
    /// </summary>
    public TaskCenter(IAnalysisTaskRepository tasks, WorkbenchLogger? logger = null)
    {
        _tasks = tasks;
        _logger = logger;
    }
    public event EventHandler? TaskChanged;

    public Task EnqueueAsync(
        AnalysisTask task,
        Func<CancellationToken, Task> action,
        IDisposable? ownedResource = null)
    {
        var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _cancellations[task.Id] = cancellation;
            _taskPlugins[task.Id] = task.PluginId;
            _completions[task.Id] = completion;
        }
        return Task.Run(async () =>
        {
            var acquired = false;
            var cancelled = false;
            Exception? failure = null;
            try
            {
                await _slots.WaitAsync(cancellation.Token);
                acquired = true;
                await action(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception ex)
            {
                failure = ex;
                throw;
            }
            finally
            {
                if (acquired) _slots.Release();

                // ownedResource 的所有权在 EnqueueAsync 成功返回后属于队列外层。
                // 即使任务在等待并发槽时取消、action 从未执行，也必须在这里统一归还。
                try
                {
                    ownedResource?.Dispose();
                }
                catch (Exception ex)
                {
                    // 租约释放属于队列内部收尾，失败只能记录，不能改变 action 的业务结果或阻断状态清理。
                    TryLogOwnedResourceDisposeFailure(task.Id, ex);
                }
                cancellation.Dispose();
                lock (_sync)
                {
                    _cancellations.Remove(task.Id);
                    _taskPlugins.Remove(task.Id);
                    _completions.Remove(task.Id);
                }

                // completion 在资源和队列状态清理后才收敛，调用方等待完成后即可观察最终状态。
                if (failure is not null)
                    completion.TrySetException(failure);
                else if (cancelled)
                    completion.TrySetCanceled();
                else
                    completion.TrySetResult(null);
                TaskChanged?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    private void TryLogOwnedResourceDisposeFailure(string taskId, Exception exception)
    {
        try
        {
            _logger?.Error($"释放后台任务资源失败，任务状态仍将正常收敛：{taskId}", exception);
        }
        catch
        {
            // 日志输出本身也属于非关键收尾，不能再次打断槽位、字典和 completion 的释放。
        }
    }

    /// <summary>
    /// 等待指定分析任务真正结束，避免重新分析时仍然打开旧报告。
    /// </summary>
    public Task WaitForCompletionAsync(string taskId, CancellationToken cancellationToken = default)
    {
        Task? completion;
        lock (_sync) completion = _completions.GetValueOrDefault(taskId)?.Task;
        return completion is null ? Task.CompletedTask : completion.WaitAsync(cancellationToken);
    }

    public bool Cancel(string taskId)
    {
        lock (_sync)
        {
            if (!_cancellations.TryGetValue(taskId, out var cancellation)) return false;
            cancellation.Cancel();
            return true;
        }
    }

    public bool IsPluginActive(string pluginId)
    {
        lock (_sync) return _taskPlugins.Values.Any(x => string.Equals(x, pluginId, StringComparison.OrdinalIgnoreCase));
    }
}
