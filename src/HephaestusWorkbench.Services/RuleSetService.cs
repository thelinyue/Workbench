using System.Text.Json;
using System.Text.RegularExpressions;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 统一管理规则 JSON 的导入、校验、保存和激活。
/// 规则文件属于用户数据，不写入程序安装目录；所有替换均通过临时文件完成。
/// </summary>
public sealed class RuleSetService
{
    private readonly DataPaths _paths;
    private readonly WorkbenchLogger _logger;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public RuleSetService(DataPaths paths, WorkbenchLogger logger)
    {
        _paths = paths;
        _logger = logger;
        _paths.EnsureCreated();
    }

    public string RulesDirectory => _paths.RulesDirectory;
    public string LocalRulesDirectory => _paths.LocalRulesDirectory;
    public string ActiveRulesPath => _paths.ActiveRulesFile;
    public string RulePublisherTokenPath => _paths.RulePublisherTokenFile;
    public bool HasActiveRules => File.Exists(_paths.ActiveRulesFile) && HasRules(_paths.ActiveRulesFile);

    public async Task<RuleSet> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var rules = await JsonSerializer.DeserializeAsync<RuleSet>(stream, _options, cancellationToken)
                ?? throw new InvalidDataException("规则文件为空。");
            var error = Validate(rules).FirstOrDefault(x => x.IsError);
            if (error is not null) throw new InvalidDataException(error.Message);
            return rules;
        }
        catch (JsonException ex)
        {
            var message = $"规则 JSON 格式错误：{path}";
            _logger.Error(message, ex);
            throw new InvalidDataException(message, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error($"读取规则文件失败：{path}", ex);
            throw new InvalidOperationException($"读取规则文件失败：{ex.Message}", ex);
        }
    }

    public async Task<IReadOnlyList<LocalRuleFile>> ListLocalAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<LocalRuleFile>();
        foreach (var path in Directory.EnumerateFiles(_paths.LocalRulesDirectory, "*.json", SearchOption.TopDirectoryOnly).OrderBy(x => x))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var rules = await ReadAsync(path, cancellationToken);
                result.Add(new LocalRuleFile(Path.GetFileName(path), path, rules.Version, File.GetLastWriteTime(path)));
            }
            catch (Exception ex) { _logger.Error($"忽略无效本地规则文件：{path}", ex); }
        }
        return result;
    }

    public async Task<LocalRuleFile> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var rules = await ReadAsync(sourcePath, cancellationToken);
        var name = Path.GetFileName(sourcePath);
        if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name += ".json";
        name = MakeSafeFileName(name);
        var target = Path.Combine(_paths.LocalRulesDirectory, name);
        await WriteAtomicAsync(target, rules, cancellationToken);
        _logger.Info($"规则已添加到本地规则目录：{target}");
        return new LocalRuleFile(name, target, rules.Version, File.GetLastWriteTime(target));
    }

    public async Task ActivateAsync(string path, CancellationToken cancellationToken = default)
    {
        var rules = await ReadAsync(path, cancellationToken);
        await WriteAtomicAsync(_paths.ActiveRulesFile, rules, cancellationToken);
        _logger.Info($"规则已激活：{path}");
    }

    public async Task<RuleSet?> ReadActiveAsync(CancellationToken cancellationToken = default)
        => File.Exists(_paths.ActiveRulesFile) ? await ReadAsync(_paths.ActiveRulesFile, cancellationToken) : null;

    private static bool HasRules(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("files", out var files)
                && files.ValueKind == JsonValueKind.Array
                && files.EnumerateArray().Any(file => file.TryGetProperty("keywords", out var keywords) && keywords.ValueKind == JsonValueKind.Array && keywords.GetArrayLength() > 0);
        }
        catch { return false; }
    }

    public async Task<string> EnsureActiveAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.ActiveRulesFile))
            await WriteAtomicAsync(_paths.ActiveRulesFile, new RuleSet { Version = DateTime.Now.ToString("yyyy.MM.dd") }, cancellationToken);
        return _paths.ActiveRulesFile;
    }

    public IReadOnlyList<RuleValidationIssue> Validate(RuleSet? rules)
    {
        var issues = new List<RuleValidationIssue>();
        if (rules is not null && string.IsNullOrWhiteSpace(rules.Version)) issues.Add(new("error", "version 不能为空。"));
        if (rules is null) return new[] { new RuleValidationIssue("error", "规则集不能为空。") };
        if (rules.Files is null) return new[] { new RuleValidationIssue("error", "files 必须是数组。") };
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var fileIndex = 0; fileIndex < rules.Files.Count; fileIndex++)
        {
            var file = rules.Files[fileIndex];
            var location = $"文件项 {fileIndex + 1}";
            if (string.IsNullOrWhiteSpace(file.Name)) issues.Add(new("error", $"{location}：文件名前缀不能为空。"));
            if (!string.IsNullOrWhiteSpace(file.Name) && !fileNames.Add(file.Name.Trim())) issues.Add(new("warning", $"{location}：文件名前缀重复。"));
            if (string.IsNullOrWhiteSpace(file.Category)) issues.Add(new("warning", $"{location}：建议填写分类。"));
            if (file.Keywords is null) { issues.Add(new("error", $"{location}：keywords 必须是数组。")); continue; }
            var ruleKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var ruleIndex = 0; ruleIndex < file.Keywords.Count; ruleIndex++)
            {
                var rule = file.Keywords[ruleIndex];
                var ruleLocation = $"{location} / 规则 {ruleIndex + 1}";
                if (string.IsNullOrWhiteSpace(rule.Term)) issues.Add(new("error", $"{ruleLocation}：关键词不能为空。"));
                if (string.IsNullOrWhiteSpace(rule.Result)) issues.Add(new("warning", $"{ruleLocation}：问题描述为空。"));
                if (rule.ContextLines < 0) issues.Add(new("error", $"{ruleLocation}：上下文行数不能为负数。"));
                if (rule.ContextDirection is not ("up" or "down")) issues.Add(new("error", $"{ruleLocation}：上下文方向只能是 up 或 down。"));
                if (rule.SearchDirection is not ("up" or "down")) issues.Add(new("error", $"{ruleLocation}：搜索方向只能是 up 或 down。"));
                if (rule.Severity is not (null or "info" or "warning" or "critical")) issues.Add(new("error", $"{ruleLocation}：严重程度无效。"));
                if (rule.Regex)
                {
                    try { _ = new Regex(rule.Term); }
                    catch (Exception ex) { issues.Add(new("error", $"{ruleLocation}：正则表达式无效：{ex.Message}")); }
                }
                if (!ruleKeys.Add($"{rule.Term}\0{rule.Regex}\0{rule.Result}")) issues.Add(new("warning", $"{ruleLocation}：与同文件中的规则重复。"));
            }
        }
        return issues;
    }

    private async Task WriteAtomicAsync(string path, RuleSet rules, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, rules, _options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string MakeSafeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? $"rules-{DateTime.Now:yyyyMMddHHmmss}.json" : name;
    }
}
