using System.Text;
using System.Text.Json;
using HephaestusWorkbench.App.Ssh;
using HephaestusWorkbench.App.ViewModels;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Core.Services;

namespace HephaestusWorkbench.Tests;

public sealed class SshTerminalM3Tests
{
    [Fact]
    public void TerminalProtocol_ValidatesVersionTypeAndRequiredFields()
    {
        var input = TerminalWebMessageProtocol.ParseInbound("""
            {"version":"terminal-v1","type":"input","requestId":"input-1","data":"5Lit5paH"}
            """);
        var resize = TerminalWebMessageProtocol.ParseInbound("""
            {"version":"terminal-v1","type":"resize","requestId":"resize-1","columns":132,"rows":40}
            """);
        var ack = TerminalWebMessageProtocol.ParseInbound("""
            {"version":"terminal-v1","type":"ack","sequence":7}
            """);

        Assert.Equal(TerminalInboundMessageType.Input, input.Type);
        Assert.Equal("中文", Encoding.UTF8.GetString(Convert.FromBase64String(input.Data!)));
        Assert.Equal((132, 40), (resize.Columns, resize.Rows));
        Assert.Equal(7, ack.Sequence);
        Assert.Throws<InvalidDataException>(() => TerminalWebMessageProtocol.ParseInbound(
            """{"version":"legacy","type":"ack","sequence":1}"""));
        Assert.Throws<InvalidDataException>(() => TerminalWebMessageProtocol.ParseInbound(
            """{"version":"terminal-v1","type":"input","requestId":"input-1"}"""));
    }

    [Fact]
    public async Task Controller_WaitsForJavascriptAckBeforeSendingNextChunk()
    {
        var channel = new FakeInteractiveChannel();
        channel.QueueRead("第一块");
        channel.QueueRead("第二块");
        var surface = new FakeTerminalSurface();
        await using var controller = new TerminalSessionController(
            new FakeTerminalSession("one", channel),
            _ => Task.FromException<ITerminalSession>(new IOException("断线")),
            surface,
            new TerminalReconnectOptions(0, TimeSpan.Zero));

        await controller.StartAsync();
        await surface.WaitForMessageCountAsync(1);
        await Task.Delay(80);
        Assert.Single(surface.Messages.Where(IsOutput));

        var first = Parse(surface.Messages.Single(IsOutput));
        surface.Receive($$"""{"version":"terminal-v1","type":"ack","sequence":{{first.GetProperty("sequence").GetInt64()}}}""");
        await surface.WaitForMessageCountAsync(2);

        Assert.Equal(2, surface.Messages.Count(IsOutput));
    }

    [Fact]
    public async Task Controller_ForwardsUtf8InputAndResizeWithoutParsingCommands()
    {
        var channel = new FakeInteractiveChannel();
        var surface = new FakeTerminalSurface();
        await using var controller = new TerminalSessionController(
            new FakeTerminalSession("one", channel),
            _ => Task.FromException<ITerminalSession>(new IOException("断线")),
            surface,
            new TerminalReconnectOptions(0, TimeSpan.Zero));
        await controller.StartAsync();

        surface.Receive("""{"version":"terminal-v1","type":"input","requestId":"i-1","data":"bHMgLWwgJiYgZWNobyDkuK3mlocK"}""".Replace("K\"", "=\""));
        surface.Receive("""{"version":"terminal-v1","type":"resize","requestId":"r-1","columns":120,"rows":36}""");
        await channel.WaitForWriteAsync();
        await channel.WaitForResizeAsync();

        Assert.Equal("ls -l && echo 中文", Encoding.UTF8.GetString(channel.Written.ToArray()));
        Assert.Equal((120, 36), channel.LastResize);
    }

    [Fact]
    public async Task Controller_RetriesAtMostThreeTimesAndWritesNewShellNotice()
    {
        var first = new FakeInteractiveChannel();
        first.CompleteReads();
        var recovered = new FakeInteractiveChannel();
        var attempts = 0;
        var surface = new FakeTerminalSurface(autoAck: true);
        await using var controller = new TerminalSessionController(
            new FakeTerminalSession("first", first),
            _ =>
            {
                attempts++;
                return attempts < 3
                    ? Task.FromException<ITerminalSession>(new TimeoutException("暂态超时"))
                    : Task.FromResult<ITerminalSession>(new FakeTerminalSession("recovered", recovered));
            },
            surface,
            new TerminalReconnectOptions(3, TimeSpan.Zero));

        await controller.StartAsync();
        await surface.WaitForTextAsync("连接已恢复，这是新的 Shell；原前台进程不会恢复。");

        Assert.Equal(3, attempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Controller_DoesNotRetryAuthenticationOrHostKeyFailures(bool hostKeyFailure)
    {
        var channel = new FakeInteractiveChannel();
        channel.CompleteReads();
        var attempts = 0;
        Exception error = hostKeyFailure
            ? new SshHostKeyValidationException(
                SshHostKeyFailureReason.Changed,
                new SshHostKeyObservation("host", 22, "ssh-ed25519", "SHA256:new"),
                "ssh-ed25519",
                "SHA256:old")
            : new FakeAuthenticationException();
        var surface = new FakeTerminalSurface();
        await using var controller = new TerminalSessionController(
            new FakeTerminalSession("one", channel),
            _ =>
            {
                attempts++;
                return Task.FromException<ITerminalSession>(error);
            },
            surface,
            new TerminalReconnectOptions(3, TimeSpan.Zero));

        await controller.StartAsync();
        await controller.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Coordinator_UnknownHostKeyRequiresConfirmationThenPersistsAndRetriesOnce()
    {
        var service = new FakeTerminalService();
        var observation = new SshHostKeyObservation("server.example", 22, "ssh-ed25519", "SHA256:key");
        service.Results.Enqueue(new SshHostKeyValidationException(SshHostKeyFailureReason.Unknown, observation));
        service.Results.Enqueue(new FakeTerminalSession("trusted", new FakeInteractiveChannel()));
        var hostKeys = new FakeHostKeyRepository();
        var prompt = new FakeHostKeyPrompt(true);
        var history = new FakeHistoryRepository();
        var coordinator = new SshConnectionCoordinator(service, hostKeys, history, prompt, () => DateTime.UtcNow);

        await using var session = await coordinator.ConnectAsync(Connection(), new SshCredentialSecret("secret"));

        Assert.Equal(2, service.ConnectCount);
        Assert.Equal(observation.Fingerprint, Assert.Single(hostKeys.Upserted).Fingerprint);
        Assert.Equal(1, prompt.CallCount);
        Assert.Equal(SshConnectionOutcome.HostKeyRejected, history.Completed.Single().Outcome);
    }

    [Fact]
    public async Task Coordinator_ChangedHostKeyHardFailsWithoutPromptOrRetry()
    {
        var service = new FakeTerminalService();
        service.Results.Enqueue(new SshHostKeyValidationException(
            SshHostKeyFailureReason.Changed,
            new SshHostKeyObservation("server.example", 22, "ssh-ed25519", "SHA256:new"),
            "ssh-ed25519",
            "SHA256:old"));
        var prompt = new FakeHostKeyPrompt(true);
        var coordinator = new SshConnectionCoordinator(
            service, new FakeHostKeyRepository(), new FakeHistoryRepository(), prompt, () => DateTime.UtcNow);

        await Assert.ThrowsAsync<SshHostKeyValidationException>(() =>
            coordinator.ConnectAsync(Connection(), new SshCredentialSecret("secret")));

        Assert.Equal(1, service.ConnectCount);
        Assert.Equal(0, prompt.CallCount);
    }

    [Fact]
    public async Task ViewModel_SaveDeviceAndSaveCredentialAreIndependent()
    {
        var service = new FakeTerminalService(alwaysSucceed: true);
        var devices = new FakeDeviceRepository();
        var credentials = new FakeCredentialStore();
        await using var viewModel = CreateViewModel(service, devices, credentials);
        viewModel.Name = "生产机";
        viewModel.Host = "server.example";
        viewModel.Username = "root";
        viewModel.Password = "plain-password";
        viewModel.SaveDevice = true;
        viewModel.SaveCredential = false;

        await viewModel.ConnectAsync();

        Assert.Null(Assert.Single(devices.Upserted).CredentialTarget);
        Assert.Empty(credentials.Writes);
        Assert.DoesNotContain("plain-password", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ViewModel_ExplicitSaveCredentialWritesOnlyCredentialManagerTargetToDevice()
    {
        var service = new FakeTerminalService(alwaysSucceed: true);
        var devices = new FakeDeviceRepository();
        var credentials = new FakeCredentialStore();
        await using var viewModel = CreateViewModel(service, devices, credentials);
        viewModel.Name = "生产机";
        viewModel.Host = "server.example";
        viewModel.Username = "root";
        viewModel.Password = "plain-password";
        viewModel.SaveDevice = true;
        viewModel.SaveCredential = true;

        await viewModel.ConnectAsync();

        var device = Assert.Single(devices.Upserted);
        Assert.NotNull(device.CredentialTarget);
        Assert.DoesNotContain("plain-password", JsonSerializer.Serialize(device), StringComparison.Ordinal);
        Assert.Equal(device.CredentialTarget, Assert.Single(credentials.Writes).Target);
    }

    [Fact]
    public async Task ViewModel_AppliesSavedPreferencesToNewConnectionsAndTabs()
    {
        var service = new FakeTerminalService(alwaysSucceed: true);
        await using var viewModel = CreateViewModel(service, new FakeDeviceRepository(), new FakeCredentialStore());

        viewModel.ApplyPreferences(new SshTerminalPreferences(2222, "Consolas", 18, SshReconnectBehavior.Disabled));
        viewModel.Name = "偏好测试";
        viewModel.Host = "server.example";
        viewModel.Username = "root";
        viewModel.Password = "secret";
        await viewModel.ConnectAsync();

        Assert.Equal(2222, service.Requests.Single().Port);
        Assert.Equal("Consolas", viewModel.Tabs.Single().FontFamily);
        Assert.Equal(18, viewModel.Tabs.Single().FontSize);
    }

    [Fact]
    public async Task ViewModel_PostConnectPersistenceFailureDisposesEstablishedSession()
    {
        var service = new FakeTerminalService(alwaysSucceed: true);
        await using var viewModel = CreateViewModel(service, new FailingDeviceRepository(), new FakeCredentialStore());
        viewModel.Name = "生产机";
        viewModel.Host = "server.example";
        viewModel.Username = "root";
        viewModel.Password = "plain-password";
        viewModel.SaveDevice = true;

        await viewModel.ConnectAsync();

        Assert.True(Assert.Single(service.CreatedSessions).Disposed);
        Assert.Empty(viewModel.Tabs);
        Assert.Contains("保存设备失败", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ViewModel_TenTabsAreIndependentAndDisposeAllOnShellExit()
    {
        var service = new FakeTerminalService(alwaysSucceed: true);
        await using var viewModel = CreateViewModel(service, new FakeDeviceRepository(), new FakeCredentialStore());
        viewModel.Host = "server.example";
        viewModel.Username = "root";
        viewModel.Password = "secret";

        for (var index = 0; index < 10; index++)
        {
            viewModel.Name = $"终端 {index + 1}";
            await viewModel.ConnectAsync();
        }

        Assert.Equal(10, viewModel.Tabs.Count);
        var sessions = service.CreatedSessions.ToArray();
        await viewModel.CloseTabAsync(viewModel.Tabs[3]);
        Assert.True(sessions[3].Disposed);
        Assert.All(sessions.Where((_, index) => index != 3), session => Assert.False(session.Disposed));

        await viewModel.DisposeAsync();
        Assert.All(sessions, session => Assert.True(session.Disposed));
    }

    [Fact]
    public async Task ViewModel_设备默认连接复用同一活动设备会话()
    {
        var service = new FakeTerminalService(alwaysSucceed: true);
        var devices = new FakeDeviceRepository();
        var credentials = new FakeCredentialStore();
        await using var viewModel = CreateViewModel(service, devices, credentials);
        viewModel.Name = "NAS-01";
        viewModel.Host = "cn68-relay.ugnas.com";
        viewModel.Port = 38977;
        viewModel.Username = "root";
        viewModel.Password = "secret";
        viewModel.SaveDevice = true;
        viewModel.SaveCredential = true;
        await viewModel.ConnectAsync();
        var activeTab = Assert.Single(viewModel.Tabs);
        var device = Assert.Single(viewModel.SavedDevices);

        viewModel.ConnectDeviceCommand.Execute(device);
        await WaitUntilAsync(() => ReferenceEquals(activeTab, viewModel.SelectedTab));

        Assert.Single(viewModel.Tabs);
        Assert.Same(activeTab, viewModel.SelectedTab);
        Assert.Equal(1, service.ConnectCount);
    }

    [Fact]
    public async Task ViewModel_在新标签连接始终创建独立会话()
    {
        var service = new FakeTerminalService(alwaysSucceed: true);
        var devices = new FakeDeviceRepository();
        var credentials = new FakeCredentialStore();
        await using var viewModel = CreateViewModel(service, devices, credentials);
        viewModel.Name = "Edge-02";
        viewModel.Host = "cn68-relay.ugnas.com";
        viewModel.Port = 38977;
        viewModel.Username = "root";
        viewModel.Password = "secret";
        viewModel.SaveDevice = true;
        viewModel.SaveCredential = true;
        await viewModel.ConnectAsync();
        var firstTab = Assert.Single(viewModel.Tabs);
        var device = Assert.Single(viewModel.SavedDevices);

        viewModel.ConnectDeviceInNewTabCommand.Execute(device);
        await WaitUntilAsync(() => viewModel.Tabs.Count == 2);

        Assert.Equal(2, viewModel.Tabs.Count);
        Assert.NotSame(firstTab, viewModel.SelectedTab);
        Assert.Equal(2, service.ConnectCount);
    }

    [Fact]
    public async Task ViewModel_删除保存设备时删除凭据且不关闭活动标签()
    {
        var service = new FakeTerminalService(alwaysSucceed: true);
        var devices = new FakeDeviceRepository();
        var credentials = new FakeCredentialStore();
        await using var viewModel = CreateViewModel(service, devices, credentials);
        viewModel.Name = "NAS-01";
        viewModel.Host = "cn68-relay.ugnas.com";
        viewModel.Port = 38977;
        viewModel.Username = "root";
        viewModel.Password = "secret";
        viewModel.SaveDevice = true;
        viewModel.SaveCredential = true;
        await viewModel.ConnectAsync();
        var activeTab = Assert.Single(viewModel.Tabs);
        var device = Assert.Single(viewModel.SavedDevices);
        var credentialTarget = Assert.IsType<string>(device.CredentialTarget);

        viewModel.DeleteDeviceCommand.Execute(device);
        await WaitUntilAsync(() => devices.DeletedIds.Contains(device.Id));

        Assert.Contains(credentialTarget, credentials.DeletedTargets);
        Assert.Contains(device.Id, devices.DeletedIds);
        Assert.Empty(viewModel.SavedDevices);
        Assert.Single(viewModel.Tabs);
        Assert.Same(activeTab, viewModel.SelectedTab);
        Assert.False(service.CreatedSessions.Single().Disposed);
    }

    [Fact]
    public async Task ViewModel_未保存凭据断线后不得被自动重连闭包复用()
    {
        var channel = new FakeInteractiveChannel();
        var service = new FakeTerminalService();
        service.Results.Enqueue(new FakeTerminalSession("initial", channel));
        var settings = new AppSettingsConfig();
        settings.ReconnectBehavior = SshReconnectBehavior.AutomaticThreeAttempts;
        await using var viewModel = CreateViewModel(service, new FakeDeviceRepository(), new FakeCredentialStore(), settings);
        viewModel.Name = "临时连接";
        viewModel.Host = "cn68-relay.ugnas.com";
        viewModel.Port = 38977;
        viewModel.Username = "root";
        viewModel.Password = "仅本次使用的密码";
        viewModel.SaveDevice = false;

        await viewModel.ConnectAsync();
        var tab = Assert.Single(viewModel.Tabs);
        var surface = new FakeTerminalSurface();
        await tab.AttachSurfaceAsync(surface);
        channel.CompleteReads();
        await WaitUntilAsync(() => tab.IsDisconnected);

        Assert.Equal(1, service.ConnectCount);
        Assert.True(tab.IsDisconnected);
        Assert.Equal("已断开", tab.SessionState);
    }

    [Fact]
    public void ConnectionHistoryContractContainsNoCommandOutputOrCredentialFields()
    {
        var names = typeof(SshConnectionHistory).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain(names, name => name.Contains("Command", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Output", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Credential", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>创建 SSH 页面测试模型；允许测试明确指定自动重连策略，避免依赖全局默认值。</summary>
    private static SshTerminalViewModel CreateViewModel(
        ISshTerminalService service,
        ISshDeviceRepository devices,
        ICredentialStore credentials,
        AppSettingsConfig? settings = null) => new(
            service,
            devices,
            new FakeHostKeyRepository(),
            new FakeHistoryRepository(),
            credentials,
            new FakeHostKeyPrompt(true),
            settings ?? new AppSettingsConfig(),
            Path.Combine(Path.GetTempPath(), "hephaestus-terminal-tests", Guid.NewGuid().ToString("N")),
            delay: (_, _) => Task.CompletedTask);

    /// <summary>等待由 ICommand 触发的异步交互完成，超时信息保留中文以便定位界面行为。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < timeout)
            await Task.Delay(10);

        Assert.True(condition(), "等待 SSH 工作区异步交互完成超时。");
    }

    private static SshConnectionRequest Connection() => new(
        null, "server.example", 22, "root", SshAuthenticationMethod.Password, null, null);

    private static bool IsOutput(string json) => Parse(json).GetProperty("type").GetString() == "output";
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class FakeAuthenticationException : Exception { }

    private sealed class FakeTerminalService(bool alwaysSucceed = false) : ISshTerminalService
    {
        public Queue<object> Results { get; } = new();
        public List<FakeTerminalSession> CreatedSessions { get; } = [];
        public List<SshConnectionRequest> Requests { get; } = [];
        public int ConnectCount { get; private set; }
        public Task<ITerminalSession> ConnectAsync(SshConnectionRequest request, SshCredentialSecret? credential, CancellationToken cancellationToken = default)
        {
            ConnectCount++;
            Requests.Add(request);
            if (Results.Count > 0)
            {
                var result = Results.Dequeue();
                if (result is Exception error) return Task.FromException<ITerminalSession>(error);
                return Task.FromResult((ITerminalSession)result);
            }
            if (!alwaysSucceed) throw new InvalidOperationException("没有排队的连接结果。");
            var session = new FakeTerminalSession(
                $"session-{ConnectCount}",
                new FakeInteractiveChannel(),
                new SshConnectionIdentity(request.DeviceId, request.Host, request.Port, request.Username));
            CreatedSessions.Add(session);
            return Task.FromResult<ITerminalSession>(session);
        }
    }

    private sealed class FakeTerminalSession(
        string id,
        FakeInteractiveChannel channel,
        SshConnectionIdentity? connectionIdentity = null) : ITerminalSession
    {
        public SshConnectionIdentity ConnectionIdentity { get; } = connectionIdentity ?? new(id, "server.example", 22, "root");
        public IInteractiveChannel InteractiveChannel => channel;
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; channel.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class FakeInteractiveChannel : IInteractiveChannel
    {
        private readonly System.Threading.Channels.Channel<byte[]> _reads = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        private byte[]? _current;
        private int _offset;
        private readonly TaskCompletionSource _write = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resize = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MemoryStream Written { get; } = new();
        public (int Columns, int Rows) LastResize { get; private set; }
        public void QueueRead(string text) => _reads.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
        public void CompleteReads() => _reads.Writer.TryComplete();
        public void Dispose() => _reads.Writer.TryComplete();
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_current is null || _offset >= _current.Length)
            {
                if (!await _reads.Reader.WaitToReadAsync(cancellationToken)) return 0;
                if (!_reads.Reader.TryRead(out _current)) return 0;
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            Written.Write(data.Span);
            _write.TrySetResult();
            return ValueTask.CompletedTask;
        }
        public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
        {
            LastResize = (columns, rows);
            _resize.TrySetResult();
            return ValueTask.CompletedTask;
        }
        public Task WaitForWriteAsync() => _write.Task.WaitAsync(TimeSpan.FromSeconds(2));
        public Task WaitForResizeAsync() => _resize.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class FakeTerminalSurface(bool autoAck = false) : ITerminalSurface
    {
        public event EventHandler<string>? MessageReceived;
        public List<string> Messages { get; } = [];
        public bool Disposed { get; private set; }
        public Task SendAsync(string json, CancellationToken cancellationToken)
        {
            Messages.Add(json);
            if (autoAck && IsOutput(json))
            {
                var sequence = Parse(json).GetProperty("sequence").GetInt64();
                Task.Run(() => Receive($$"""{"version":"terminal-v1","type":"ack","sequence":{{sequence}}}"""));
            }
            return Task.CompletedTask;
        }
        public void Receive(string json) => MessageReceived?.Invoke(this, json);
        public async Task WaitForMessageCountAsync(int count)
        {
            var timeout = DateTime.UtcNow.AddSeconds(2);
            while (Messages.Count < count && DateTime.UtcNow < timeout) await Task.Delay(10);
            Assert.True(Messages.Count >= count, $"等待 {count} 条消息超时，实际 {Messages.Count} 条。");
        }
        public async Task WaitForTextAsync(string text)
        {
            var timeout = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < timeout)
            {
                foreach (var message in Messages.Where(IsOutput))
                {
                    var data = Convert.FromBase64String(Parse(message).GetProperty("data").GetString()!);
                    if (Encoding.UTF8.GetString(data).Contains(text, StringComparison.Ordinal)) return;
                }
                await Task.Delay(10);
            }
            Assert.Fail($"未收到终端提示：{text}");
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class FakeHostKeyPrompt(bool accept) : IHostKeyConfirmationService
    {
        public int CallCount { get; private set; }
        public Task<bool> ConfirmAsync(SshHostKeyObservation observation, CancellationToken cancellationToken = default)
        { CallCount++; return Task.FromResult(accept); }
    }

    private sealed class FakeHostKeyRepository : ISshHostKeyRepository
    {
        public List<SshHostKey> Upserted { get; } = [];
        public Task<SshHostKey?> GetAsync(string host, int port, CancellationToken cancellationToken = default) => Task.FromResult<SshHostKey?>(null);
        public Task UpsertAsync(SshHostKey hostKey, CancellationToken cancellationToken = default) { Upserted.Add(hostKey); return Task.CompletedTask; }
    }

    private sealed class FakeHistoryRepository : ISshConnectionHistoryRepository
    {
        public List<SshConnectionHistory> Inserted { get; } = [];
        public List<(string Id, SshConnectionOutcome Outcome, string? Error)> Completed { get; } = [];
        public Task InsertAsync(SshConnectionHistory history, CancellationToken cancellationToken = default) { Inserted.Add(history); return Task.CompletedTask; }
        public Task CompleteAsync(string id, DateTime disconnectedAt, SshConnectionOutcome outcome, string? errorMessage, CancellationToken cancellationToken = default)
        { Completed.Add((id, outcome, errorMessage)); return Task.CompletedTask; }
        public Task<IReadOnlyList<SshConnectionHistory>> ListRecentAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SshConnectionHistory>>(Inserted);
    }

    private sealed class FakeDeviceRepository : ISshDeviceRepository
    {
        public List<SshDevice> Upserted { get; } = [];
        public List<string> DeletedIds { get; } = [];
        public Task<IReadOnlyList<SshDevice>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SshDevice>>(Upserted);
        public Task<SshDevice?> GetAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(Upserted.FirstOrDefault(x => x.Id == id));
        public Task UpsertAsync(SshDevice device, CancellationToken cancellationToken = default)
        {
            Upserted.RemoveAll(existing => existing.Id == device.Id);
            Upserted.Add(device);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            DeletedIds.Add(id);
            Upserted.RemoveAll(device => device.Id == id);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingDeviceRepository : ISshDeviceRepository
    {
        public Task<IReadOnlyList<SshDevice>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SshDevice>>([]);
        public Task<SshDevice?> GetAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<SshDevice?>(null);
        public Task UpsertAsync(SshDevice device, CancellationToken cancellationToken = default) => Task.FromException(new InvalidOperationException("保存设备失败"));
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, SshStoredCredential> _stored = [];
        public List<(string Target, string UserName, string Secret)> Writes { get; } = [];
        public List<string> DeletedTargets { get; } = [];
        public Task WriteAsync(string target, string userName, SshCredentialSecret secret, CancellationToken cancellationToken = default)
        {
            Writes.Add((target, userName, secret.Value));
            _stored[target] = new SshStoredCredential(userName, secret);
            return Task.CompletedTask;
        }
        public Task<SshStoredCredential?> ReadAsync(string target, CancellationToken cancellationToken = default) =>
            Task.FromResult(_stored.GetValueOrDefault(target));
        public Task<bool> DeleteAsync(string target, CancellationToken cancellationToken = default)
        {
            DeletedTargets.Add(target);
            return Task.FromResult(_stored.Remove(target));
        }
    }
}
