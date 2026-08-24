using System.Text.Json;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Services;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class LinuxMaintenanceDiscoveryServiceTests
{
    [Fact]
    public async Task DiscoverAsync_UsesOnlyStructuredReadOnlyCommandsAndBuildsStableTargets()
    {
        var commands = SuccessfulCommands();
        var service = new LinuxMaintenanceDiscoveryService(commands);
        var secret = new SshCredentialSecret("TOP-SECRET-CREDENTIAL");

        var result = await service.DiscoverAsync("linux-open-ssh", Connection(), secret);

        Assert.Equal("root", result.RemoteUsername);
        Assert.True(result.IsRoot);
        Assert.True(result.IsPasswordlessSudoAvailable);
        Assert.Equal("Linux", result.SystemInformation["kernelName"]);
        Assert.Equal("6.8.0", result.SystemInformation["kernelRelease"]);
        Assert.Equal("x86_64", result.SystemInformation["architecture"]);
        Assert.Contains(result.StableTargets, target => target.StableIdentity == "uuid:disk-uuid" && target.Kind == "disk");
        Assert.Contains(result.StableTargets, target => target.StableIdentity == "major:minor:8:0");
        Assert.Contains(result.StableTargets, target => target.StableIdentity == "lv-uuid:lv-uuid-1");
        Assert.Empty(result.Errors);
        Assert.All(commands.Requests, request =>
        {
            Assert.DoesNotContain("sh", Path.GetFileName(request.Executable), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("-c", request.Arguments);
            Assert.DoesNotContain("TOP-SECRET-CREDENTIAL", request.Executable, StringComparison.Ordinal);
            Assert.All(request.Arguments, argument => Assert.DoesNotContain("TOP-SECRET-CREDENTIAL", argument, StringComparison.Ordinal));
        });
        Assert.DoesNotContain("TOP-SECRET-CREDENTIAL", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverAsync_RejectsUnsupportedTargetWithoutExecutingCommands()
    {
        var commands = SuccessfulCommands();
        var service = new LinuxMaintenanceDiscoveryService(commands);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.DiscoverAsync("network-device", Connection(), null));

        Assert.Contains("linux-open-ssh", error.Message, StringComparison.Ordinal);
        Assert.Empty(commands.Requests);
    }

    [Fact]
    public async Task DiscoverAsync_OptionalToolAbsenceAndSudoDenialAreWarningsNotBlockingErrors()
    {
        var commands = SuccessfulCommands();
        commands.Set("sudo", ["-n", "true"], 1, "", "sudo: a password is required");
        commands.Set("lvs", LvsArguments(), 127, "", "lvs: command not found");

        var result = await new LinuxMaintenanceDiscoveryService(commands)
            .DiscoverAsync("linux-open-ssh", Connection(), null);

        Assert.False(result.IsPasswordlessSudoAvailable);
        Assert.Empty(result.Errors);
        Assert.Contains(result.Warnings, warning => warning.Contains("sudo -n", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("LVM", StringComparison.Ordinal) && warning.Contains("未安装", StringComparison.Ordinal));
        Assert.Contains(result.StableTargets, target => target.StableIdentity == "uuid:disk-uuid");
    }

    [Fact]
    public async Task DiscoverAsync_BlockingCommandFailureUsesChineseErrorAndDoesNotMixStderrIntoDiscoveryValues()
    {
        var commands = SuccessfulCommands();
        commands.Set("lsblk", LsblkArguments(), 2, "partial-stdout", "TOP-SECRET-CREDENTIAL stderr");

        var result = await new LinuxMaintenanceDiscoveryService(commands)
            .DiscoverAsync("linux-open-ssh", Connection(), new SshCredentialSecret("TOP-SECRET-CREDENTIAL"));

        Assert.Contains(result.Errors, error => error.Contains("lsblk", StringComparison.Ordinal) && error.Contains("Exit Code 2", StringComparison.Ordinal));
        Assert.DoesNotContain("partial-stdout", result.DiscoveryValues.Values);
        Assert.DoesNotContain("TOP-SECRET-CREDENTIAL", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverAsync_MissingOrDuplicateStableIdentityFailsClosed()
    {
        var commands = SuccessfulCommands();
        commands.Set("lsblk", LsblkArguments(), 0, """
            {"blockdevices":[
              {"name":"sda","type":"disk","maj:min":null,"uuid":null},
              {"name":"sdb","type":"disk","maj:min":"8:16","uuid":"same"},
              {"name":"sdc","type":"disk","maj:min":"8:32","uuid":"same"}
            ]}
            """, "");
        commands.Set("lvs", LvsArguments(), 0, """
            {"report":[{"lv":[
              {"lv_name":"one","vg_name":"vg","lv_uuid":"duplicate","lv_path":"/dev/vg/one"},
              {"lv_name":"two","vg_name":"vg","lv_uuid":"duplicate","lv_path":"/dev/vg/two"}
            ]}]}
            """, "");

        var result = await new LinuxMaintenanceDiscoveryService(commands)
            .DiscoverAsync("linux-open-ssh", Connection(), null);

        Assert.Contains(result.Errors, error => error.Contains("缺少稳定身份", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("重复", StringComparison.Ordinal));
    }

    private static FakeCommandExecutionService SuccessfulCommands()
    {
        var commands = new FakeCommandExecutionService();
        commands.Set("id", ["-un"], 0, "root\n", "");
        commands.Set("id", ["-u"], 0, "0\n", "");
        commands.Set("sudo", ["-n", "true"], 0, "", "");
        commands.Set("uname", ["-s"], 0, "Linux\n", "");
        commands.Set("uname", ["-r"], 0, "6.8.0\n", "");
        commands.Set("uname", ["-m"], 0, "x86_64\n", "");
        commands.Set("lsblk", LsblkArguments(), 0, """
            {"blockdevices":[{"name":"sda","kname":"sda","path":"/dev/sda","type":"disk","maj:min":"8:0","uuid":"disk-uuid"}]}
            """, "");
        commands.Set("lvs", LvsArguments(), 0, """
            {"report":[{"lv":[{"lv_name":"root","vg_name":"vg0","lv_uuid":"lv-uuid-1","lv_path":"/dev/vg0/root"}]}]}
            """, "");
        return commands;
    }

    private static string[] LsblkArguments() => ["--json", "--bytes", "--output", "NAME,KNAME,PATH,TYPE,MAJ:MIN,UUID"];
    private static string[] LvsArguments() => ["--reportformat", "json", "--units", "b", "--nosuffix", "--options", "lv_name,vg_name,lv_uuid,lv_path"];

    private static SshConnectionRequest Connection() => new(
        "device-1", "server.example.com", 22, "configured-user",
        SshAuthenticationMethod.Password, null, "HephaestusWorkbench/ssh/device-1");

    private sealed class FakeCommandExecutionService : ICommandExecutionService
    {
        private readonly Dictionary<string, Response> _responses = new(StringComparer.Ordinal);
        public List<RemoteCommandRequest> Requests { get; } = [];

        public void Set(string executable, IReadOnlyList<string> arguments, int exitCode, string stdout, string stderr) =>
            _responses[Key(executable, arguments)] = new Response(exitCode, stdout, stderr);

        public async Task<RemoteCommandResult> ExecuteAsync(
            RemoteCommandRequest request,
            SshCredentialSecret? credential,
            Func<RemoteCommandOutputChunk, CancellationToken, ValueTask> onOutput,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (!_responses.TryGetValue(Key(request.Executable, request.Arguments), out var response))
                throw new InvalidOperationException($"测试未配置命令：{request.Executable} {string.Join(' ', request.Arguments)}");
            if (response.Stdout.Length > 0)
                await onOutput(new RemoteCommandOutputChunk(1, RemoteCommandOutputStream.Stdout, response.Stdout), cancellationToken);
            if (response.Stderr.Length > 0)
                await onOutput(new RemoteCommandOutputChunk(2, RemoteCommandOutputStream.Stderr, response.Stderr), cancellationToken);
            return new RemoteCommandResult(response.ExitCode, TimeSpan.FromMilliseconds(10));
        }

        private static string Key(string executable, IReadOnlyList<string> arguments) => executable + "\0" + string.Join("\0", arguments);
        private sealed record Response(int ExitCode, string Stdout, string Stderr);
    }
}
