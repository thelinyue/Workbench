using System.Text;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Core.Services;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class SshNetServiceTests
{
    [Fact]
    public async Task TerminalConnectAsync_CreatesIndependentPasswordClientsAndXtermPty()
    {
        var factory = new FakeSshClientFactory();
        var service = CreateTerminalService(factory, KnownHostKeys());
        var request = Connection(SshAuthenticationMethod.Password);

        await using var first = await service.ConnectAsync(request, new SshCredentialSecret("password-1"));
        await using var second = await service.ConnectAsync(request, new SshCredentialSecret("password-2"));

        Assert.Equal(2, factory.Clients.Count);
        Assert.NotSame(factory.Clients[0], factory.Clients[1]);
        Assert.Equal("password-1", factory.Configurations[0].Secret);
        Assert.Equal("password-2", factory.Configurations[1].Secret);
        Assert.All(factory.Clients, client => Assert.Equal(("xterm-256color", 80, 24), client.OpenedPty));
        Assert.Equal(new SshConnectionIdentity("device-1", "server.example", 22, "root"), first.ConnectionIdentity);
    }

    [Fact]
    public async Task TerminalConnectAsync_ResolvesPrivateKeyPassphraseFromCredentialTarget()
    {
        var factory = new FakeSshClientFactory();
        var credentials = new FakeCredentialStore(new SshStoredCredential("root", new SshCredentialSecret("key-passphrase")));
        var service = CreateTerminalService(factory, KnownHostKeys(), credentials);
        var request = Connection(SshAuthenticationMethod.PrivateKey) with
        {
            PrivateKeyPath = @"C:\keys\id_ed25519",
            CredentialTarget = "HephaestusWorkbench/ssh/device-1/private-key"
        };

        await using var session = await service.ConnectAsync(request, null);

        Assert.Equal([request.CredentialTarget], credentials.ReadTargets);
        Assert.Equal("key-passphrase", factory.Configurations.Single().Secret);
        Assert.Equal(request.PrivateKeyPath, factory.Configurations.Single().Request.PrivateKeyPath);
    }

    [Fact]
    public async Task TerminalConnectAsync_UnknownHostKeyFailsClosedWithoutPersistingTrust()
    {
        var factory = new FakeSshClientFactory();
        var repository = new FakeHostKeyRepository();
        var service = CreateTerminalService(factory, repository);

        var error = await Assert.ThrowsAsync<SshHostKeyValidationException>(() =>
            service.ConnectAsync(Connection(SshAuthenticationMethod.Password), new SshCredentialSecret("secret")));

        Assert.Equal(SshHostKeyFailureReason.Unknown, error.Reason);
        Assert.Equal("ssh-ed25519", error.Observation.KeyAlgorithm);
        Assert.Equal("SHA256:test-fingerprint", error.Observation.Fingerprint);
        Assert.Contains("尚未信任", error.Message, StringComparison.Ordinal);
        Assert.Empty(repository.Upserted);
        Assert.True(factory.Clients.Single().Disposed);
    }

    [Fact]
    public async Task TerminalConnectAsync_ChangedHostKeyFailsClosed()
    {
        var repository = KnownHostKeys(fingerprint: "SHA256:old-fingerprint");
        var service = CreateTerminalService(new FakeSshClientFactory(), repository);

        var error = await Assert.ThrowsAsync<SshHostKeyValidationException>(() =>
            service.ConnectAsync(Connection(SshAuthenticationMethod.Password), new SshCredentialSecret("secret")));

        Assert.Equal(SshHostKeyFailureReason.Changed, error.Reason);
        Assert.Equal("SHA256:old-fingerprint", error.ExpectedFingerprint);
        Assert.Contains("发生变化", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InteractiveChannel_ForwardsReadWriteResizeAndDispose()
    {
        var factory = new FakeSshClientFactory();
        factory.NextShellRead = Encoding.UTF8.GetBytes("终端输出");
        var service = CreateTerminalService(factory, KnownHostKeys());
        var session = await service.ConnectAsync(Connection(SshAuthenticationMethod.Password), new SshCredentialSecret("secret"));
        var buffer = new byte[64];

        var read = await session.InteractiveChannel.ReadAsync(buffer);
        await session.InteractiveChannel.WriteAsync(Encoding.UTF8.GetBytes("ls\n"));
        await session.InteractiveChannel.ResizeAsync(132, 40);
        await session.DisposeAsync();

        Assert.Equal("终端输出", Encoding.UTF8.GetString(buffer, 0, read));
        Assert.Equal("ls\n", Encoding.UTF8.GetString(factory.Clients.Single().Shell.Written.ToArray()));
        Assert.Equal((132, 40), factory.Clients.Single().Shell.LastResize);
        Assert.True(factory.Clients.Single().Shell.Disposed);
        Assert.True(factory.Clients.Single().Disconnected);
        Assert.True(factory.Clients.Single().Disposed);
    }

    [Fact]
    public async Task CommandExecuteAsync_UsesIndependentClientAndSafelyQuotesEveryPosixToken()
    {
        var factory = new FakeSshClientFactory();
        var terminal = CreateTerminalService(factory, KnownHostKeys());
        var commands = CreateCommandService(factory, KnownHostKeys());
        await using var session = await terminal.ConnectAsync(Connection(SshAuthenticationMethod.Password), new SshCredentialSecret("secret"));
        var request = new RemoteCommandRequest(
            Connection(SshAuthenticationMethod.Password),
            "/usr/bin/tool",
            ["a; b", "x|y", ">file", "$(id)", "`uname`", "with space", "it's"],
            TimeSpan.FromSeconds(5));

        await commands.ExecuteAsync(request, new SshCredentialSecret("secret"), (_, _) => ValueTask.CompletedTask);

        Assert.Equal(2, factory.Clients.Count);
        Assert.Equal("'/usr/bin/tool' 'a; b' 'x|y' '>file' '$(id)' '`uname`' 'with space' 'it'\"'\"'s'", factory.Clients[1].CreatedCommand);
    }

    [Fact]
    public async Task CommandExecuteAsync_RejectsShDashC()
    {
        var factory = new FakeSshClientFactory();
        var service = CreateCommandService(factory, KnownHostKeys());
        var request = new RemoteCommandRequest(Connection(SshAuthenticationMethod.Password), "/bin/sh", ["-c", "id"], TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExecuteAsync(request, new SshCredentialSecret("secret"), (_, _) => ValueTask.CompletedTask));

        Assert.Contains("sh -c", error.Message, StringComparison.Ordinal);
        Assert.Empty(factory.Clients);
    }

    [Fact]
    public async Task CommandExecuteAsync_SeparatesChunksWaitsForCallbackAndReturnsExitStatus()
    {
        var factory = new FakeSshClientFactory
        {
            NextCommand = new FakeCommandChannel("第一块\n第二块", "警告", 17, chunkSize: 6)
        };
        var service = CreateCommandService(factory, KnownHostKeys());
        var firstCallbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chunks = new List<RemoteCommandOutputChunk>();

        var execution = service.ExecuteAsync(
            new RemoteCommandRequest(Connection(SshAuthenticationMethod.Password), "tool", [], TimeSpan.FromSeconds(5)),
            new SshCredentialSecret("secret"),
            async (chunk, cancellationToken) =>
            {
                chunks.Add(chunk);
                if (chunks.Count == 1)
                {
                    firstCallbackStarted.SetResult();
                    await releaseFirstCallback.Task.WaitAsync(cancellationToken);
                }
            });

        await firstCallbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        Assert.True(factory.NextCommand.TotalReadCalls <= 2, $"回调阻塞时读取次数不应继续增长，实际为 {factory.NextCommand.TotalReadCalls}。\n");
        releaseFirstCallback.SetResult();
        var result = await execution;

        Assert.Equal(17, result.ExitCode);
        Assert.True(result.Duration >= TimeSpan.Zero);
        Assert.Contains(chunks, chunk => chunk.Stream == RemoteCommandOutputStream.Stdout && chunk.Text.Contains("第一", StringComparison.Ordinal));
        Assert.Contains(chunks, chunk => chunk.Stream == RemoteCommandOutputStream.Stderr && chunk.Text.Contains("警告", StringComparison.Ordinal));
        Assert.Equal(Enumerable.Range(1, chunks.Count).Select(value => (long)value), chunks.Select(chunk => chunk.Sequence));
    }

    [Fact]
    public async Task CommandExecuteAsync_CallbackFailureCancelsCommandAndDisposesConnectionPromptly()
    {
        var factory = new FakeSshClientFactory { NextCommand = FakeCommandChannel.BlockingWithOutput("chunk") };
        var service = CreateCommandService(factory, KnownHostKeys());
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(
            new RemoteCommandRequest(Connection(SshAuthenticationMethod.Password), "tool", [], TimeSpan.FromSeconds(3)),
            new SshCredentialSecret("secret"),
            (_, _) => throw new InvalidOperationException("callback failed")));

        stopwatch.Stop();
        Assert.Equal("callback failed", error.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"输出回调失败后应立即取消命令，实际耗时 {stopwatch.Elapsed}。");
        Assert.True(factory.Clients.Single().Disconnected);
        Assert.True(factory.Clients.Single().Disposed);
        Assert.True(factory.NextCommand.Disposed);
        Assert.Equal(1, factory.NextCommand.ExecuteCount);
    }
    [Fact]
    public async Task CommandExecuteAsync_CancellationDisposesConnectionWithoutReplay()
    {
        var factory = new FakeSshClientFactory { NextCommand = FakeCommandChannel.Blocking() };
        var service = CreateCommandService(factory, KnownHostKeys());
        using var cancellation = new CancellationTokenSource();
        var execution = service.ExecuteAsync(
            new RemoteCommandRequest(Connection(SshAuthenticationMethod.Password), "sleep", ["60"], TimeSpan.FromMinutes(1)),
            new SshCredentialSecret("secret"),
            (_, _) => ValueTask.CompletedTask,
            cancellation.Token);

        await factory.NextCommand.ExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        Assert.Single(factory.Clients);
        Assert.True(factory.Clients.Single().Disconnected);
        Assert.True(factory.Clients.Single().Disposed);
        Assert.True(factory.NextCommand.Disposed);
        Assert.Equal(1, factory.NextCommand.ExecuteCount);
    }

    private static SshNetTerminalService CreateTerminalService(FakeSshClientFactory factory, FakeHostKeyRepository repository, FakeCredentialStore? credentials = null) =>
        new(repository, credentials ?? new FakeCredentialStore(), factory);

    private static SshNetCommandExecutionService CreateCommandService(FakeSshClientFactory factory, FakeHostKeyRepository repository, FakeCredentialStore? credentials = null) =>
        new(repository, credentials ?? new FakeCredentialStore(), factory);

    private static SshConnectionRequest Connection(SshAuthenticationMethod method) =>
        new("device-1", "server.example", 22, "root", method, null, null);

    private static FakeHostKeyRepository KnownHostKeys(string algorithm = "ssh-ed25519", string fingerprint = "SHA256:test-fingerprint") =>
        new(new SshHostKey
        {
            Host = "server.example",
            Port = 22,
            KeyAlgorithm = algorithm,
            Fingerprint = fingerprint,
            FirstSeenAt = DateTime.UnixEpoch,
            LastSeenAt = DateTime.UnixEpoch
        });

    private sealed class FakeCredentialStore(SshStoredCredential? stored = null) : ICredentialStore
    {
        public List<string> ReadTargets { get; } = [];
        public Task WriteAsync(string target, string userName, SshCredentialSecret secret, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<SshStoredCredential?> ReadAsync(string target, CancellationToken cancellationToken = default)
        {
            ReadTargets.Add(target);
            return Task.FromResult(stored);
        }
        public Task<bool> DeleteAsync(string target, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class FakeHostKeyRepository(SshHostKey? stored = null) : ISshHostKeyRepository
    {
        public List<SshHostKey> Upserted { get; } = [];
        public Task<SshHostKey?> GetAsync(string host, int port, CancellationToken cancellationToken = default) => Task.FromResult(stored);
        public Task UpsertAsync(SshHostKey hostKey, CancellationToken cancellationToken = default)
        {
            Upserted.Add(hostKey);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSshClientFactory : ISshNetClientFactory
    {
        public List<SshClientConfiguration> Configurations { get; } = [];
        public List<FakeSshClient> Clients { get; } = [];
        public byte[] NextShellRead { get; set; } = [];
        public FakeCommandChannel NextCommand { get; set; } = new("", "", 0);

        public ISshNetClient Create(SshClientConfiguration configuration)
        {
            Configurations.Add(configuration);
            var client = new FakeSshClient(NextShellRead, NextCommand);
            Clients.Add(client);
            return client;
        }
    }

    private sealed class FakeSshClient(byte[] shellRead, FakeCommandChannel command) : ISshNetClient
    {
        public FakeShellChannel Shell { get; } = new(shellRead);
        public (string Terminal, int Columns, int Rows)? OpenedPty { get; private set; }
        public string? CreatedCommand { get; private set; }
        public bool Disconnected { get; private set; }
        public bool Disposed { get; private set; }

        public Task ConnectAsync(Func<SshHostKeyCandidate, bool> validateHostKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!validateHostKey(new SshHostKeyCandidate("ssh-ed25519", "SHA256:test-fingerprint")))
                throw new InvalidOperationException("host key rejected");
            return Task.CompletedTask;
        }

        public ISshShellChannel OpenShell(string terminalName, int columns, int rows)
        {
            OpenedPty = (terminalName, columns, rows);
            return Shell;
        }

        public ISshCommandChannel CreateCommand(string commandText)
        {
            CreatedCommand = commandText;
            return command;
        }

        public void Disconnect() => Disconnected = true;
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeShellChannel(byte[] readData) : ISshShellChannel
    {
        private readonly MemoryStream _read = new(readData);
        public MemoryStream Written { get; } = new();
        public (int Columns, int Rows)? LastResize { get; private set; }
        public bool Disposed { get; private set; }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => _read.ReadAsync(buffer, cancellationToken);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => Written.WriteAsync(data, cancellationToken);
        public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastResize = (columns, rows);
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeCommandChannel : ISshCommandChannel
    {
        private readonly Stream _stdout;
        private readonly Stream _stderr;
        private bool _block;
        public FakeCommandChannel(string stdout, string stderr, int exitCode, int chunkSize = int.MaxValue)
        {
            _stdout = new CountingChunkStream(Encoding.UTF8.GetBytes(stdout), chunkSize, () => TotalReadCalls++);
            _stderr = new CountingChunkStream(Encoding.UTF8.GetBytes(stderr), chunkSize, () => TotalReadCalls++);
            ExitStatus = exitCode;
        }
        private FakeCommandChannel(bool block)
        {
            _block = block;
            _stdout = new MemoryStream();
            _stderr = new MemoryStream();
        }
        public static FakeCommandChannel Blocking() => new(true);
        public static FakeCommandChannel BlockingWithOutput(string stdout)
        {
            var command = new FakeCommandChannel(stdout, "", 0);
            command._block = true;
            return command;
        }
        public TaskCompletionSource ExecutionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ExecuteCount { get; private set; }
        public int TotalReadCalls { get; private set; }
        public Stream StandardOutput => _stdout;
        public Stream StandardError => _stderr;
        public int? ExitStatus { get; }
        public bool Disposed { get; private set; }
        public async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            ExecuteCount++;
            ExecutionStarted.TrySetResult();
            if (_block)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _stdout.Dispose();
            _stderr.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingChunkStream(byte[] data, int chunkSize, Action onRead) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onRead();
            await Task.Yield();
            if (_position >= data.Length)
                return 0;
            var count = Math.Min(Math.Min(buffer.Length, chunkSize), data.Length - _position);
            data.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
