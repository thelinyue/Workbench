using System.IO;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;

namespace HephaestusWorkbench.Tests;

public sealed class SqliteSshRepositoryTests
{
    [Fact]
    public async Task DeviceRepository_CRUD_PersistsStableAuthenticationNameAndNullableFields()
    {
        var root = CreateRoot();
        try
        {
            var factory = await CreateFactoryAsync(root);
            var repository = new SqliteSshDeviceRepository(factory);
            var older = DateTime.UtcNow.AddMinutes(-2);
            var newer = DateTime.UtcNow.AddMinutes(-1);

            await repository.UpsertAsync(new SshDevice
            {
                Id = "device-b",
                Name = "密码设备",
                Host = "10.0.0.2",
                Port = 22,
                Username = "root",
                AuthenticationMethod = SshAuthenticationMethod.Password,
                PrivateKeyPath = null,
                CredentialTarget = null,
                CreatedAt = older,
                UpdatedAt = newer
            });
            await repository.UpsertAsync(new SshDevice
            {
                Id = "device-a",
                Name = "密钥设备",
                Host = "10.0.0.1",
                Port = 2222,
                Username = "admin",
                AuthenticationMethod = SshAuthenticationMethod.PrivateKey,
                PrivateKeyPath = @"C:\keys\server.key",
                CredentialTarget = "HephaestusWorkbench/ssh/device-a",
                CreatedAt = older,
                UpdatedAt = newer
            });

            var listed = await repository.ListAsync();
            Assert.Equal(new[] { "device-a", "device-b" }, listed.Select(item => item.Id));
            Assert.Null((await repository.GetAsync("device-b"))!.PrivateKeyPath);
            Assert.Null((await repository.GetAsync("device-b"))!.CredentialTarget);

            await using (var connection = await factory.OpenAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT authentication_method FROM ssh_devices WHERE id = 'device-a'";
                Assert.Equal(nameof(SshAuthenticationMethod.PrivateKey), await command.ExecuteScalarAsync());
            }

            await repository.UpsertAsync(listed[0] with
            {
                Name = "更新后的设备",
                UpdatedAt = newer.AddMinutes(1)
            });
            Assert.Equal("更新后的设备", (await repository.GetAsync("device-a"))!.Name);

            await repository.DeleteAsync("device-a");
            Assert.Null(await repository.GetAsync("device-a"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task DeviceRepository_ListOrdersByUpdatedAtDescendingThenId()
    {
        var root = CreateRoot();
        try
        {
            var factory = await CreateFactoryAsync(root);
            var repository = new SqliteSshDeviceRepository(factory);
            var now = DateTime.UtcNow;
            await repository.UpsertAsync(NewDevice("older", now.AddMinutes(-1)));
            await repository.UpsertAsync(NewDevice("same-b", now));
            await repository.UpsertAsync(NewDevice("same-a", now));

            var items = await repository.ListAsync();

            Assert.Equal(new[] { "same-a", "same-b", "older" }, items.Select(item => item.Id));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task HostKeyRepository_UpsertPreservesStoredFirstSeenAt()
    {
        var root = CreateRoot();
        try
        {
            var factory = await CreateFactoryAsync(root);
            var repository = new SqliteSshHostKeyRepository(factory);
            var firstSeen = DateTime.UtcNow.AddDays(-2);
            await repository.UpsertAsync(new SshHostKey
            {
                Host = "server.example.com",
                Port = 22,
                KeyAlgorithm = "ssh-ed25519",
                Fingerprint = "SHA256:first",
                FirstSeenAt = firstSeen,
                LastSeenAt = firstSeen
            });

            var lastSeen = DateTime.UtcNow;
            await repository.UpsertAsync(new SshHostKey
            {
                Host = "server.example.com",
                Port = 22,
                KeyAlgorithm = "rsa-sha2-512",
                Fingerprint = "SHA256:updated",
                FirstSeenAt = DateTime.UtcNow.AddYears(1),
                LastSeenAt = lastSeen
            });

            var saved = await repository.GetAsync("server.example.com", 22);
            Assert.NotNull(saved);
            Assert.Equal("rsa-sha2-512", saved.KeyAlgorithm);
            Assert.Equal("SHA256:updated", saved.Fingerprint);
            Assert.Equal(firstSeen, saved.FirstSeenAt.ToUniversalTime());
            Assert.Equal(lastSeen, saved.LastSeenAt.ToUniversalTime());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ConnectionHistoryRepository_InsertCompleteAndListRecentOnlyUpdatesCompletionFields()
    {
        var root = CreateRoot();
        try
        {
            var factory = await CreateFactoryAsync(root);
            var repository = new SqliteSshConnectionHistoryRepository(factory);
            var connectedAt = DateTime.UtcNow.AddMinutes(-2);
            await repository.InsertAsync(new SshConnectionHistory
            {
                Id = "history-1",
                Host = "10.0.0.10",
                Port = 22,
                Username = "root",
                ConnectedAt = connectedAt,
                Outcome = SshConnectionOutcome.Connected
            });
            await repository.InsertAsync(new SshConnectionHistory
            {
                Id = "history-2",
                Host = "10.0.0.11",
                Port = 2222,
                Username = "admin",
                ConnectedAt = connectedAt.AddMinutes(1),
                Outcome = SshConnectionOutcome.Connected
            });

            var disconnectedAt = DateTime.UtcNow;
            await repository.CompleteAsync(
                "history-1",
                disconnectedAt,
                SshConnectionOutcome.AuthenticationFailed,
                "认证失败");

            var items = await repository.ListRecentAsync(2);
            Assert.Equal(new[] { "history-2", "history-1" }, items.Select(item => item.Id));
            var completed = items.Single(item => item.Id == "history-1");
            Assert.Equal("10.0.0.10", completed.Host);
            Assert.Equal(22, completed.Port);
            Assert.Equal("root", completed.Username);
            Assert.Equal(connectedAt, completed.ConnectedAt.ToUniversalTime());
            Assert.Equal(disconnectedAt, completed.DisconnectedAt!.Value.ToUniversalTime());
            Assert.Equal(SshConnectionOutcome.AuthenticationFailed, completed.Outcome);
            Assert.Equal("认证失败", completed.ErrorMessage);
            Assert.Single(await repository.ListRecentAsync(1));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.ListRecentAsync(0));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Repositories_UnknownPersistedEnumsFailClosedWithChineseErrors()
    {
        var root = CreateRoot();
        try
        {
            var factory = await CreateFactoryAsync(root);
            await using (var connection = await factory.OpenAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO ssh_devices
                        (id, name, host, port, username, authentication_method, private_key_path, credential_target, created_at, updated_at)
                    VALUES
                        ('bad-device', '异常设备', 'host', 22, 'root', 'UnknownAuth', NULL, NULL, $now, $now);
                    INSERT INTO ssh_connection_history
                        (id, device_id, host, port, username, connected_at, disconnected_at, outcome, error_message)
                    VALUES
                        ('bad-history', NULL, 'host', 22, 'root', $now, NULL, 'UnknownOutcome', NULL);
                    """;
                command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync();
            }

            var deviceError = await Assert.ThrowsAsync<InvalidDataException>(
                () => new SqliteSshDeviceRepository(factory).GetAsync("bad-device"));
            Assert.Contains("未知", deviceError.Message);
            Assert.Contains("认证方式", deviceError.Message);

            var historyError = await Assert.ThrowsAsync<InvalidDataException>(
                () => new SqliteSshConnectionHistoryRepository(factory).ListRecentAsync(10));
            Assert.Contains("未知", historyError.Message);
            Assert.Contains("连接结果", historyError.Message);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static SshDevice NewDevice(string id, DateTime updatedAt) => new()
    {
        Id = id,
        Name = id,
        Host = "localhost",
        Port = 22,
        Username = "root",
        AuthenticationMethod = SshAuthenticationMethod.Password,
        CreatedAt = updatedAt.AddMinutes(-1),
        UpdatedAt = updatedAt
    };

    private static async Task<SqliteConnectionFactory> CreateFactoryAsync(string root)
    {
        var factory = new SqliteConnectionFactory(new DataPaths(root));
        await new DatabaseInitializer(factory).InitializeAsync();
        return factory;
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
