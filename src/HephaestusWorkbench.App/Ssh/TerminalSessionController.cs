using System.IO;
using System.Text;
using System.Threading.Channels;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Services;

namespace HephaestusWorkbench.App.Ssh;

internal enum TerminalConnectionState
{
    Connected,
    Reconnecting,
    Disconnected
}

internal sealed record TerminalReconnectOptions(int MaxAttempts, TimeSpan Delay)
{
    internal static TerminalReconnectOptions From(AppSettingsConfig settings) =>
        settings.ReconnectBehavior == SshReconnectBehavior.AutomaticThreeAttempts
            ? new TerminalReconnectOptions(3, TimeSpan.FromSeconds(1))
            : new TerminalReconnectOptions(0, TimeSpan.Zero);
}

/// <summary>
/// 在一个终端标签内桥接 SSH 字节流与浏览器表面。SSH 读取先进入容量为 4 的有界 Channel，
/// 每次只向 JS 发布一个 Base64 分块，并等待 terminal.write 回调产生的同 sequence ACK 后继续。
/// 关闭标签会取消读取、ACK 等待和重连延迟，并释放当前会话与浏览器表面。
/// </summary>
internal sealed class TerminalSessionController : IAsyncDisposable
{
    private const int ReadBufferSize = 8192;
    private const int OutputQueueCapacity = 4;
    private static readonly byte[] ReconnectedNotice = Encoding.UTF8.GetBytes("\r\n──────── 连接已恢复，这是新的 Shell；原前台进程不会恢复。────────\r\n");

    private readonly Func<CancellationToken, Task<ITerminalSession>> _reconnect;
    private readonly ITerminalSurface _surface;
    private readonly TerminalReconnectOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Channel<byte[]> _output = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(OutputQueueCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = true
    });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _ackGate = new();
    private ITerminalSession _session;
    private TaskCompletionSource<bool>? _ack;
    private long _pendingSequence;
    private long _nextSequence;
    private Task? _runTask;
    private int _disposed;

    internal TerminalSessionController(
        ITerminalSession session,
        Func<CancellationToken, Task<ITerminalSession>> reconnect,
        ITerminalSurface surface,
        TerminalReconnectOptions options,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _session = session;
        _reconnect = reconnect;
        _surface = surface;
        _options = options;
        _delay = delay ?? Task.Delay;
    }

    internal Task Completion => _runTask ?? Task.CompletedTask;

    /// <summary>通知标签当前会话是否已恢复或已停止，事件不携带任何终端数据或凭据。</summary>
    internal event Action<TerminalConnectionState>? ConnectionStateChanged;

    internal Task StartAsync()
    {
        if (_runTask is not null) return Task.CompletedTask;
        _surface.MessageReceived += OnMessageReceived;
        _runTask = RunAsync(_shutdown.Token);
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var producer = ProduceAsync(cancellationToken);
        var consumer = ConsumeAsync(cancellationToken);
        try { await Task.WhenAll(producer, consumer).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ProduceAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[ReadBufferSize];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var count = await _session.InteractiveChannel.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (count > 0)
                    {
                        await _output.Writer.WriteAsync(buffer.AsSpan(0, count).ToArray(), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (TerminalReconnectPolicy.IsTransient(exception))
                {
                    // 暂态读取异常与正常 EOF 都进入相同的新 Shell 重连流程。
                }

                if (!await TryReconnectAsync(cancellationToken).ConfigureAwait(false))
                {
                    ConnectionStateChanged?.Invoke(TerminalConnectionState.Disconnected);
                    break;
                }
            }
        }
        finally
        {
            _output.Writer.TryComplete();
        }
    }

    private async Task<bool> TryReconnectAsync(CancellationToken cancellationToken)
    {
        ConnectionStateChanged?.Invoke(TerminalConnectionState.Reconnecting);
        await _session.DisposeAsync().ConfigureAwait(false);
        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            try
            {
                if (_options.Delay > TimeSpan.Zero)
                    await _delay(_options.Delay, cancellationToken).ConfigureAwait(false);
                _session = await _reconnect(cancellationToken).ConfigureAwait(false);
                await _output.Writer.WriteAsync(ReconnectedNotice, cancellationToken).ConfigureAwait(false);
                ConnectionStateChanged?.Invoke(TerminalConnectionState.Connected);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (TerminalReconnectPolicy.IsTransient(exception) && attempt < _options.MaxAttempts)
            {
                // 继续下一次有限重连；不重放任何终端输入。
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        await foreach (var chunk in _output.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var sequence = Interlocked.Increment(ref _nextSequence);
            Task ackTask;
            lock (_ackGate)
            {
                _pendingSequence = sequence;
                _ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                ackTask = _ack.Task;
            }

            await _surface.SendAsync(TerminalWebMessageProtocol.CreateOutput(sequence, chunk), cancellationToken).ConfigureAwait(false);
            await ackTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_ackGate)
            {
                _ack = null;
                _pendingSequence = 0;
            }
        }
    }

    private void OnMessageReceived(object? sender, string json)
    {
        TerminalInboundMessage message;
        try { message = TerminalWebMessageProtocol.ParseInbound(json); }
        catch (InvalidDataException) { return; }

        switch (message.Type)
        {
            case TerminalInboundMessageType.Ack:
                lock (_ackGate)
                {
                    if (_ack is not null && message.Sequence == _pendingSequence)
                        _ack.TrySetResult(true);
                }
                break;
            case TerminalInboundMessageType.Input:
                _ = WriteInputAsync(message.Data!, _shutdown.Token);
                break;
            case TerminalInboundMessageType.Resize:
                _ = ResizeAsync(message.Columns!.Value, message.Rows!.Value, _shutdown.Token);
                break;
        }
    }

    private async Task WriteInputAsync(string base64, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            await _session.InteractiveChannel.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        try { await _session.InteractiveChannel.ResizeAsync(columns, rows, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    /// <summary>
    /// 停止 SSH 会话但保留浏览器终端表面，供“重新连接”在同一标签继续显示旧输出。
    /// 此操作只能调用一次；之后控制器不再接收终端消息。
    /// </summary>
    internal Task StopSessionAsync() => StopAsync(disposeSurface: false);

    public ValueTask DisposeAsync() => new(StopAsync(disposeSurface: true));

    private async Task StopAsync(bool disposeSurface)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _surface.MessageReceived -= OnMessageReceived;
        _shutdown.Cancel();
        lock (_ackGate) _ack?.TrySetCanceled(_shutdown.Token);
        if (_runTask is not null)
        {
            try { await _runTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        await _session.DisposeAsync().ConfigureAwait(false);
        if (disposeSurface) await _surface.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }
}

/// <summary>只允许网络中断、连接异常和超时重连；认证、Host Key 与用户取消必须立即停止。</summary>
internal static class TerminalReconnectPolicy
{
    internal static bool IsTransient(Exception exception)
    {
        if (exception is OperationCanceledException or SshHostKeyValidationException or UnauthorizedAccessException)
            return false;
        var name = exception.GetType().Name;
        if (name.Contains("Authentication", StringComparison.OrdinalIgnoreCase)) return false;
        return exception is IOException or TimeoutException ||
               name.Contains("Connection", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Socket", StringComparison.OrdinalIgnoreCase);
    }
}
