using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;

namespace HephaestusWorkbench.Services;

/// <summary>后台任务中心，限制并行插件数量，避免大日志同时运行耗尽机器资源。</summary>
public sealed class TaskCenter
{
    private readonly IAnalysisTaskRepository _tasks;
    private readonly SemaphoreSlim _slots = new(2, 2);
    private readonly Dictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public TaskCenter(IAnalysisTaskRepository tasks) => _tasks = tasks;
    public event EventHandler? TaskChanged;

    public Task EnqueueAsync(AnalysisTask task, Func<CancellationToken, Task> action)
    {
        var cancellation = new CancellationTokenSource();
        lock (_sync) _cancellations[task.Id] = cancellation;
        return Task.Run(async () =>
        {
            var acquired = false;
            try
            {
                await _slots.WaitAsync(cancellation.Token);
                acquired = true;
                await action(cancellation.Token);
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (acquired) _slots.Release();
                cancellation.Dispose();
                lock (_sync) _cancellations.Remove(task.Id);
                TaskChanged?.Invoke(this, EventArgs.Empty);
            }
        });
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
}
