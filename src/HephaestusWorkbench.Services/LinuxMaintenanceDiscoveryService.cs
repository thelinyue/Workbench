using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Services;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 通过 ICommandExecutionService 的独立 exec connection 执行固定、只读的 Linux/OpenSSH 发现命令。
/// 所有 executable 和 argument 都是宿主常量；不使用 sh -c、模板拼接或交互终端，凭据只原样传给命令服务。
/// </summary>
public sealed class LinuxMaintenanceDiscoveryService : IMaintenanceDiscoveryService
{
    private const int MaximumCommandOutputCharacters = 1024 * 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);
    private static readonly string[] LsblkArguments = ["--json", "--bytes", "--output", "NAME,KNAME,PATH,TYPE,MAJ:MIN,UUID"];
    private static readonly string[] LvsArguments = ["--reportformat", "json", "--units", "b", "--nosuffix", "--options", "lv_name,vg_name,lv_uuid,lv_path"];

    private readonly ICommandExecutionService _commands;

    public LinuxMaintenanceDiscoveryService(ICommandExecutionService commands) =>
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));

    public async Task<PreflightResult> DiscoverAsync(
        string targetType,
        SshConnectionRequest connection,
        SshCredentialSecret? credential,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(targetType, "linux-open-ssh", StringComparison.Ordinal))
            throw new ArgumentException("维护发现只支持 linux-open-ssh 目标。", nameof(targetType));
        ArgumentNullException.ThrowIfNull(connection);

        var errors = new List<string>();
        var warnings = new List<string>();
        var system = new Dictionary<string, string>(StringComparer.Ordinal);
        var discovery = new Dictionary<string, string>(StringComparer.Ordinal);
        var targets = new Dictionary<string, StableMaintenanceTarget>(StringComparer.Ordinal);

        var userName = connection.Username;
        var user = await RunAsync(connection, credential, "id", ["-un"], cancellationToken);
        if (RequireSuccess(user, "读取远端用户名", errors))
        {
            var value = user.Stdout.Trim();
            if (value.Length == 0) errors.Add("读取远端用户名成功，但返回内容为空。");
            else userName = value;
        }

        var isRoot = false;
        var uid = await RunAsync(connection, credential, "id", ["-u"], cancellationToken);
        if (RequireSuccess(uid, "读取远端用户 UID", errors))
        {
            var value = uid.Stdout.Trim();
            if (!int.TryParse(value, out var numericUid)) errors.Add("远端用户 UID 格式无效，无法确认 root 身份。");
            else isRoot = numericUid == 0;
        }

        var sudo = await RunAsync(connection, credential, "sudo", ["-n", "true"], cancellationToken);
        var sudoAvailable = sudo.Completed && sudo.ExitCode == 0;
        if (!sudoAvailable)
            warnings.Add("sudo -n 不可用；自动维护将被策略拒绝，手动只读发现仍可继续。");

        await ReadSystemValueAsync("kernelName", "内核名称", "-s");
        await ReadSystemValueAsync("kernelRelease", "内核版本", "-r");
        await ReadSystemValueAsync("architecture", "系统架构", "-m");

        var lsblk = await RunAsync(connection, credential, "lsblk", LsblkArguments, cancellationToken);
        if (RequireSuccess(lsblk, "执行只读 lsblk 发现", errors))
        {
            discovery["lsblk.json"] = lsblk.Stdout;
            ParseLsblk(lsblk.Stdout, targets, errors);
        }

        var lvs = await RunAsync(connection, credential, "lvs", LvsArguments, cancellationToken);
        if (lvs.Completed && lvs.ExitCode == 0)
        {
            discovery["lvm.lvs.json"] = lvs.Stdout;
            ParseLvs(lvs.Stdout, targets, errors);
        }
        else if (IsOptionalToolMissing(lvs))
        {
            warnings.Add("LVM 只读发现工具 lvs 未安装，已跳过逻辑卷发现。");
        }
        else
        {
            warnings.Add(lvs.Completed
                ? $"LVM 只读发现不可用（Exit Code {lvs.ExitCode}），已跳过逻辑卷发现。"
                : "LVM 只读发现命令未能完成，已跳过逻辑卷发现。");
        }

        if (targets.Count == 0)
            errors.Add("未发现包含 UUID、LV UUID 或 major:minor 的稳定目标，已拒绝生成可执行计划。");

        return new PreflightResult
        {
            TargetType = targetType,
            RemoteUsername = userName,
            IsRoot = isRoot,
            IsPasswordlessSudoAvailable = sudoAvailable,
            SystemInformation = new ReadOnlyDictionary<string, string>(system),
            DiscoveryValues = new ReadOnlyDictionary<string, string>(discovery),
            StableTargets = Array.AsReadOnly(targets.Values.OrderBy(item => item.StableIdentity, StringComparer.Ordinal).ToArray()),
            Errors = Array.AsReadOnly(errors.ToArray()),
            Warnings = Array.AsReadOnly(warnings.ToArray())
        };

        async Task ReadSystemValueAsync(string key, string description, string argument)
        {
            var result = await RunAsync(connection, credential, "uname", [argument], cancellationToken);
            if (!RequireSuccess(result, $"读取{description}", errors)) return;
            var value = result.Stdout.Trim();
            if (value.Length == 0) errors.Add($"读取{description}成功，但返回内容为空。");
            else system[key] = value;
        }
    }

    private async Task<CommandCapture> RunAsync(
        SshConnectionRequest connection,
        SshCredentialSecret? credential,
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        try
        {
            var result = await _commands.ExecuteAsync(
                new RemoteCommandRequest(connection, executable, arguments, CommandTimeout),
                credential,
                (chunk, _) =>
                {
                    var target = chunk.Stream == RemoteCommandOutputStream.Stdout ? stdout : stderr;
                    if (target.Length + chunk.Text.Length > MaximumCommandOutputCharacters)
                        throw new InvalidDataException($"只读发现命令 {executable} 的输出超过 {MaximumCommandOutputCharacters} 字符限制。");
                    target.Append(chunk.Text);
                    return ValueTask.CompletedTask;
                },
                cancellationToken);
            return new CommandCapture(true, result.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 异常详情可能由远端或底层库携带敏感内容，Preflight 只返回固定中文摘要。
            return new CommandCapture(false, null, string.Empty, string.Empty);
        }
    }

    private static bool RequireSuccess(CommandCapture capture, string description, List<string> errors)
    {
        if (!capture.Completed)
        {
            errors.Add($"{description}失败：命令未能完成。");
            return false;
        }
        if (capture.ExitCode != 0)
        {
            errors.Add($"{description}失败：Exit Code {capture.ExitCode}。");
            return false;
        }
        return true;
    }

    private static bool IsOptionalToolMissing(CommandCapture capture) =>
        capture.Completed && (capture.ExitCode == 127 ||
            capture.Stderr.Contains("command not found", StringComparison.OrdinalIgnoreCase) ||
            capture.Stderr.Contains("No such file", StringComparison.OrdinalIgnoreCase));

    private static void ParseLsblk(
        string json,
        Dictionary<string, StableMaintenanceTarget> targets,
        List<string> errors)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("blockdevices", out var devices) || devices.ValueKind != JsonValueKind.Array)
                throw new JsonException("缺少 blockdevices 数组。");
            foreach (var device in devices.EnumerateArray()) ParseBlockDevice(device, targets, errors);
        }
        catch (JsonException exception)
        {
            errors.Add($"lsblk 返回的 JSON 无法解析：{exception.Message}");
        }
    }

    private static void ParseBlockDevice(
        JsonElement device,
        Dictionary<string, StableMaintenanceTarget> targets,
        List<string> errors)
    {
        var displayName = ReadString(device, "name") ?? ReadString(device, "path") ?? "未命名块设备";
        var kind = ReadString(device, "type") ?? "block-device";
        var foundIdentity = false;
        var uuid = ReadString(device, "uuid");
        if (!string.IsNullOrWhiteSpace(uuid))
        {
            AddTarget(targets, errors, new StableMaintenanceTarget(kind, displayName, "uuid:" + uuid));
            foundIdentity = true;
        }
        var majorMinor = ReadString(device, "maj:min");
        if (!string.IsNullOrWhiteSpace(majorMinor))
        {
            AddTarget(targets, errors, new StableMaintenanceTarget(kind, displayName, "major:minor:" + majorMinor));
            foundIdentity = true;
        }
        if (!foundIdentity)
            errors.Add($"块设备 {displayName} 缺少稳定身份（UUID 或 major:minor）。");

        if (device.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray()) ParseBlockDevice(child, targets, errors);
    }

    private static void ParseLvs(
        string json,
        Dictionary<string, StableMaintenanceTarget> targets,
        List<string> errors)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("report", out var reports) || reports.ValueKind != JsonValueKind.Array)
                throw new JsonException("缺少 report 数组。");
            foreach (var report in reports.EnumerateArray())
            {
                if (!report.TryGetProperty("lv", out var volumes) || volumes.ValueKind != JsonValueKind.Array) continue;
                foreach (var volume in volumes.EnumerateArray())
                {
                    var lvName = ReadString(volume, "lv_name") ?? "未命名逻辑卷";
                    var vgName = ReadString(volume, "vg_name");
                    var displayName = string.IsNullOrWhiteSpace(vgName) ? lvName : vgName + "/" + lvName;
                    var uuid = ReadString(volume, "lv_uuid");
                    if (string.IsNullOrWhiteSpace(uuid))
                        errors.Add($"逻辑卷 {displayName} 缺少稳定身份 LV UUID。");
                    else
                        AddTarget(targets, errors, new StableMaintenanceTarget("lvm-logical-volume", displayName, "lv-uuid:" + uuid));
                }
            }
        }
        catch (JsonException exception)
        {
            errors.Add($"LVM lvs 返回的 JSON 无法解析：{exception.Message}");
        }
    }

    private static void AddTarget(
        Dictionary<string, StableMaintenanceTarget> targets,
        List<string> errors,
        StableMaintenanceTarget target)
    {
        if (!targets.TryAdd(target.StableIdentity, target))
            errors.Add($"发现重复稳定身份 {target.StableIdentity}，已拒绝继续使用该发现结果。");
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : value.ToString().Trim();
    }

    private sealed record CommandCapture(bool Completed, int? ExitCode, string Stdout, string Stderr);
}
