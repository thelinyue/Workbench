using System.Reflection;
using System.Text.Json;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Core.Services;

namespace HephaestusWorkbench.Tests;

public sealed class SshCoreContractTests
{
    [Fact]
    public void SshDtos_RoundTripThroughJson_WithStableStringEnums()
    {
        var device = new SshDevice
        {
            Id = "device-1",
            Name = "测试服务器",
            Host = "192.0.2.10",
            Port = 22,
            Username = "root",
            AuthenticationMethod = SshAuthenticationMethod.PrivateKey,
            PrivateKeyPath = @"C:\keys\id_ed25519",
            CredentialTarget = "HephaestusWorkbench/ssh/device-1/private-key-passphrase",
            CreatedAt = new DateTime(2026, 8, 24, 1, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 8, 24, 2, 0, 0, DateTimeKind.Utc)
        };

        var json = JsonSerializer.Serialize(device);
        var restored = JsonSerializer.Deserialize<SshDevice>(json);

        Assert.Contains("\"PrivateKey\"", json, StringComparison.Ordinal);
        Assert.Equal(device, restored);
    }

    [Fact]
    public void CommandContracts_KeepExecutableArgumentsAndOutputStreamsSeparate()
    {
        var connection = new SshConnectionRequest(
            "device-1",
            "server.example.com",
            22,
            "ops",
            SshAuthenticationMethod.Password,
            null,
            "HephaestusWorkbench/ssh/device-1/password");
        var request = new RemoteCommandRequest(connection, "lsblk", ["--json", "--output", "NAME,UUID"], TimeSpan.FromSeconds(30));
        var stdout = new RemoteCommandOutputChunk(1, RemoteCommandOutputStream.Stdout, "{\"blockdevices\":[]}");
        var stderr = new RemoteCommandOutputChunk(2, RemoteCommandOutputStream.Stderr, "warning");

        Assert.Equal("lsblk", request.Executable);
        Assert.Equal(["--json", "--output", "NAME,UUID"], request.Arguments);
        Assert.NotEqual(stdout.Stream, stderr.Stream);
        Assert.Equal(1, stdout.Sequence);
        Assert.Equal(2, stderr.Sequence);
    }

    [Fact]
    public void SshInterfaces_DoNotExposeWpfSqliteOrSshNetTypes()
    {
        var contractTypes = new[]
        {
            typeof(ISshTerminalService),
            typeof(ITerminalSession),
            typeof(IInteractiveChannel),
            typeof(ICommandExecutionService),
            typeof(ISshDeviceRepository),
            typeof(ISshHostKeyRepository),
            typeof(ISshConnectionHistoryRepository),
            typeof(ICredentialStore)
        };

        var exposedTypes = contractTypes
            .SelectMany(GetPublicSignatureTypes)
            .Where(type => type.Namespace is not null)
            .ToArray();

        Assert.DoesNotContain(exposedTypes, type => type.Namespace!.StartsWith("System.Windows", StringComparison.Ordinal));
        Assert.DoesNotContain(exposedTypes, type => type.Namespace!.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal));
        Assert.DoesNotContain(exposedTypes, type => type.Namespace!.StartsWith("Renci.SshNet", StringComparison.Ordinal));
    }

    [Fact]
    public void CredentialSecret_DoesNotExposeSecretThroughToString()
    {
        var credential = new SshCredentialSecret("sensitive-value");

        Assert.DoesNotContain("sensitive-value", credential.ToString(), StringComparison.Ordinal);
        Assert.Equal("sensitive-value", credential.Value);
    }

    private static IEnumerable<Type> GetPublicSignatureTypes(Type contractType)
    {
        yield return contractType;
        foreach (var property in contractType.GetProperties())
            yield return property.PropertyType;
        foreach (var method in contractType.GetMethods())
        {
            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters())
                yield return parameter.ParameterType;
        }
    }
}
