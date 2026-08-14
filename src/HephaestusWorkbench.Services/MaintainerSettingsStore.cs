using System.Text.Json;
using System.Text.RegularExpressions;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 维护者模式的本机配置。文件由当前 Windows 用户的 DPAPI 保护，普通用户不需要也不应该直接编辑它。
/// GitHub Token 不属于此配置，始终只在当前窗口内存中存在。
/// </summary>
public sealed record MaintainerSettings(
    string Key,
    string Owner,
    string Repository,
    string Branch,
    string RulesPath)
{
    public RuleRepositoryOptions ToRepositoryOptions()
        => new(Owner, Repository, Branch, RulesPath);

    public static MaintainerSettings FromEnvironment(string key)
    {
        var repository = Environment.GetEnvironmentVariable("HEPHAESTUS_RULE_REPOSITORY")?.Trim();
        var parts = repository?.Split('/', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new MaintainerSettings(
            key,
            parts is { Length: 2 } ? parts[0] : "thelinyue",
            parts is { Length: 2 } ? parts[1] : "Hephaestus-Workbench-Plugins",
            Environment.GetEnvironmentVariable("HEPHAESTUS_RULE_BRANCH")?.Trim() is { Length: > 0 } branch ? branch : "main",
            Environment.GetEnvironmentVariable("HEPHAESTUS_RULE_SOURCE_PATH")?.Trim() is { Length: > 0 } path ? path : "rules/log-analyzer/versions");
    }
}

/// <summary>以 DPAPI 读写维护者配置，并对用户输入做最小边界校验。</summary>
public sealed class MaintainerSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public MaintainerSettingsStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HephaestusWorkbench",
            "Config",
            "maintainer.dat");
    }

    public string Path { get; }
    public bool Exists => File.Exists(Path);

    public MaintainerSettings? Load()
    {
        if (!File.Exists(Path)) return null;
        try
        {
            var json = DpapiSecretStore.ReadFromFile(Path);
            var settings = JsonSerializer.Deserialize<MaintainerSettings>(json, JsonOptions);
            Validate(settings);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidDataException("维护者配置无法读取，请重新初始化维护者模式。", ex);
        }
    }

    public void Save(MaintainerSettings settings)
    {
        Validate(settings);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        DpapiSecretStore.ProtectToFile(Path, json, "Hephaestus Workbench maintainer configuration");
    }

    private static void Validate(MaintainerSettings? settings)
    {
        if (settings is null) throw new InvalidDataException("维护者配置为空。");
        if (settings.Key.Length < 8) throw new InvalidDataException("维护者密钥至少需要 8 个字符。");
        if (!Regex.IsMatch(settings.Owner, "^[A-Za-z0-9_.-]+$") || !Regex.IsMatch(settings.Repository, "^[A-Za-z0-9_.-]+$"))
            throw new InvalidDataException("GitHub 仓库名称格式无效。");
        if (string.IsNullOrWhiteSpace(settings.Branch)) throw new InvalidDataException("GitHub 分支不能为空。");
        if (string.IsNullOrWhiteSpace(settings.RulesPath)
            || System.IO.Path.IsPathRooted(settings.RulesPath)
            || settings.RulesPath.Split('/', '\\').Any(x => x == ".."))
            throw new InvalidDataException("规则目录必须是仓库内的相对路径。");
    }
}
