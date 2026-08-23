using System.Text.Json;
using System.Text.RegularExpressions;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 统一管理维护者主规则、用户规则和实际生效规则。
/// 主规则只读，用户规则独立保存；active.json 始终由两者合并生成，避免编辑器覆盖维护者内容。
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
    public string OfficialRulesFile => _paths.OfficialRulesFile;
    public string LocalRulesDirectory => _paths.LocalRulesDirectory;
    public string LocalAdditionsFile => _paths.LocalAdditionsFile;
    public string ActiveRulesPath => _paths.ActiveRulesFile;
    public bool HasActiveRules => File.Exists(_paths.ActiveRulesFile) && HasRules(_paths.ActiveRulesFile);

    public async Task<RuleSet> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var rules = await JsonSerializer.DeserializeAsync<RuleSet>(stream, _options, cancellationToken)
                ?? throw new InvalidDataException("规则文件为空。");
            ThrowIfInvalid(rules, path);
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

    public async Task<RuleSet?> ReadOfficialAsync(CancellationToken cancellationToken = default)
        => File.Exists(_paths.OfficialRulesFile) ? await ReadAsync(_paths.OfficialRulesFile, cancellationToken) : null;

    public async Task<UserRuleSet> ReadUserAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.LocalAdditionsFile))
        {
            return new UserRuleSet { BaseVersion = (await ReadOfficialAsync(cancellationToken))?.Version };
        }

        try
        {
            await using var stream = File.OpenRead(_paths.LocalAdditionsFile);
            var result = await JsonSerializer.DeserializeAsync<UserRuleSet>(stream, _options, cancellationToken)
                ?? new UserRuleSet();
            result.Rules ??= new List<UserRuleRecord>();
            result.Categories ??= DeriveCategories(result.Rules);
            return result;
        }
        catch (JsonException ex)
        {
            _logger.Error("本地用户规则 JSON 格式错误。", ex);
            throw new InvalidDataException("本地用户规则 JSON 格式错误。", ex);
        }
    }

    public async Task<RuleEditorState> ReadEditorStateAsync(CancellationToken cancellationToken = default)
    {
        var official = await ReadOfficialAsync(cancellationToken) ?? new RuleSet { Version = "尚未同步" };
        var user = await ReadUserAsync(cancellationToken);
        var active = File.Exists(_paths.ActiveRulesFile)
            ? await ReadAsync(_paths.ActiveRulesFile, cancellationToken)
            : Merge(official, user);
        return new RuleEditorState
        {
            Official = official,
            User = user,
            Active = active,
            State = BuildState(official, user)
        };
    }

    public async Task SaveUserAsync(UserRuleSet user, CancellationToken cancellationToken = default)
    {
        user.SchemaVersion = 1;
        user.Rules ??= new List<UserRuleRecord>();
        user.Categories ??= DeriveCategories(user.Rules);
        var issue = ValidateUserRules(user).FirstOrDefault(x => x.IsError);
        if (issue is not null) throw new InvalidDataException(issue.Message);
        user.Categories = user.Categories.Select(category => category.Trim()).ToList();
        user.BaseVersion ??= (await ReadOfficialAsync(cancellationToken))?.Version;
        await WriteJsonAtomicAsync(_paths.LocalAdditionsFile, user, cancellationToken);
        await RebuildActiveAsync(cancellationToken);
        _logger.Info("用户规则已保存并重新生成激活规则。");
    }

    public async Task<RuleStateSnapshot> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var official = await ReadOfficialAsync(cancellationToken);
        return BuildState(official, await ReadUserAsync(cancellationToken));
    }

    public async Task ApplyOfficialAsync(RuleSet official, CancellationToken cancellationToken = default)
    {
        ThrowIfInvalid(official, "云端主规则");
        var snapshot = await CaptureFilesAsync(cancellationToken);
        var previous = await ReadOfficialAsync(cancellationToken);
        try
        {
            if (previous is not null && !string.IsNullOrWhiteSpace(previous.Version))
            {
                var historyPath = Path.Combine(_paths.RulesHistoryDirectory, $"main-{MakeSafeFileName(previous.Version)}.json");
                if (!File.Exists(historyPath)) await WriteJsonAtomicAsync(historyPath, previous, cancellationToken);
            }

            await WriteJsonAtomicAsync(_paths.OfficialRulesFile, official, cancellationToken);
            var user = await ReadUserAsync(cancellationToken);
            user.BaseVersion = official.Version;
            RefreshUserStatuses(user, official);
            await WriteJsonAtomicAsync(_paths.LocalAdditionsFile, user, cancellationToken);
            await RebuildActiveAsync(cancellationToken);
            _logger.Info($"维护者主规则已更新：{official.Version}");
        }
        catch
        {
            await RestoreFilesAsync(snapshot);
            throw;
        }
    }

    public async Task<IReadOnlyList<LocalRuleFile>> ListLocalAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<LocalRuleFile>();
        foreach (var path in Directory.EnumerateFiles(_paths.LocalRulesDirectory, "*.json", SearchOption.TopDirectoryOnly).Where(x => !string.Equals(x, _paths.LocalAdditionsFile, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x))
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
        var name = MakeSafeFileName(Path.GetFileName(sourcePath));
        if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name += ".json";
        var target = Path.Combine(_paths.LocalRulesDirectory, name);
        await WriteJsonAtomicAsync(target, rules, cancellationToken);
        _logger.Info($"规则已添加到本地规则目录：{target}");
        return new LocalRuleFile(name, target, rules.Version, File.GetLastWriteTime(target));
    }

    public async Task ActivateAsync(string path, CancellationToken cancellationToken = default)
    {
        var rules = await ReadAsync(path, cancellationToken);
        await WriteJsonAtomicAsync(_paths.ActiveRulesFile, rules, cancellationToken);
        _logger.Info($"规则已激活：{path}");
    }

    public async Task<RuleSet?> ReadActiveAsync(CancellationToken cancellationToken = default)
        => File.Exists(_paths.ActiveRulesFile) ? await ReadAsync(_paths.ActiveRulesFile, cancellationToken) : null;

    public async Task<string> EnsureActiveAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.ActiveRulesFile)) await RebuildActiveAsync(cancellationToken);
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
                if (!ruleKeys.Add(RuleKey(rule, includeResult: true))) issues.Add(new("warning", $"{ruleLocation}：与同文件中的规则重复。"));
            }
        }
        return issues;
    }

    private async Task RebuildActiveAsync(CancellationToken cancellationToken)
    {
        var official = await ReadOfficialAsync(cancellationToken);
        if (official is null)
        {
            // 尚未同步维护者主规则时，不生成空的 active.json，让分析器继续使用内置 config.json。
            await PersistStateAsync(cancellationToken);
            return;
        }
        var user = await ReadUserAsync(cancellationToken);
        RefreshUserStatuses(user, official);
        await WriteJsonAtomicAsync(_paths.LocalAdditionsFile, user, cancellationToken);
        await WriteJsonAtomicAsync(_paths.ActiveRulesFile, Merge(official, user), cancellationToken);
        await PersistStateAsync(cancellationToken);
    }

    private async Task PersistStateAsync(CancellationToken cancellationToken)
    {
        var state = BuildState(await ReadOfficialAsync(cancellationToken), await ReadUserAsync(cancellationToken));
        await WriteJsonAtomicAsync(_paths.RulesStateFile, state, cancellationToken);
    }

    private RuleSet Merge(RuleSet official, UserRuleSet user)
    {
        var result = Clone(official);
        var keys = result.Files.SelectMany(file => file.Keywords.Select(rule => (File: file.Name, Rule: rule)))
            .ToDictionary(x => RuleKey(x.File, x.Rule, false), _ => true, StringComparer.Ordinal);
        foreach (var item in user.Rules.Where(x => x.Status is not ("conflict" or "merged")))
        {
            var file = result.Files.FirstOrDefault(x => string.Equals(x.Name, item.File, StringComparison.OrdinalIgnoreCase));
            if (file is null)
            {
                file = new RuleFile { Name = item.File, Category = item.Category };
                result.Files.Add(file);
            }
            var key = RuleKey(item.File, item.Rule, false);
            if (keys.ContainsKey(key)) continue;
            file.Keywords.Add(Clone(item.Rule));
            keys[key] = true;
        }
        return result;
    }

    private static void RefreshUserStatuses(UserRuleSet user, RuleSet official)
    {
        foreach (var item in user.Rules)
        {
            var matches = official.Files.FirstOrDefault(x => string.Equals(x.Name, item.File, StringComparison.OrdinalIgnoreCase))?.Keywords
                .Where(x => RuleKey(x, false) == RuleKey(item.Rule, false)).ToList() ?? new List<RuleDefinition>();
            if (matches.Count == 0)
            {
                if (item.Status == "merged") item.Status = "draft";
                item.ConflictMessage = null;
                continue;
            }
            if (matches.Any(x => RuleKey(x, true) == RuleKey(item.Rule, true)))
            {
                item.Status = "merged";
                item.ConflictMessage = "该规则已合并到维护者主规则。";
            }
            else
            {
                item.Status = "conflict";
                item.ConflictMessage = "主规则中存在相同匹配条件但内容不同的规则。";
            }
        }
    }

    private RuleStateSnapshot BuildState(RuleSet? official, UserRuleSet user)
        => new()
        {
            OfficialVersion = official?.Version,
            LocalRuleCount = user.Rules.Count(x => x.Status is not "merged"),
            ConflictRuleCount = user.Rules.Count(x => x.Status == "conflict")
        };

    public IReadOnlyList<RuleValidationIssue> ValidateUserRules(UserRuleSet user)
    {
        var categories = (user.Categories ?? DeriveCategories(user.Rules)).Select(category => category.Trim()).ToList();
        if (categories.Any(string.IsNullOrWhiteSpace))
            return new[] { new RuleValidationIssue("error", "分类名称不能为空。", Field: "category") };

        var duplicateCategory = categories
            .GroupBy(category => category, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateCategory is not null)
            return new[] { new RuleValidationIssue("error", $"分类“{duplicateCategory.Key}”已存在，不能重复创建。", Field: "category") };

        var rules = new RuleSet { Version = user.BaseVersion ?? "local" };
        var issues = new List<RuleValidationIssue>();
        var categorySet = categories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var record in user.Rules)
        {
            var category = record.Category.Trim();
            if (string.IsNullOrWhiteSpace(category))
                issues.Add(new RuleValidationIssue("error", "规则必须归属一个已创建的分类。", new[] { record.LocalId }, "category"));
            else if (!categorySet.Contains(category))
                issues.Add(new RuleValidationIssue("error", $"分类“{category}”尚未创建，请先创建分类。", new[] { record.LocalId }, "category"));
        }

        if (issues.Any(issue => issue.IsError))
            return issues;

        foreach (var group in user.Rules.GroupBy(x => (x.File, x.Category), StringTupleComparer.Instance))
        {
            rules.Files.Add(new RuleFile { Name = group.Key.File, Category = group.Key.Category, Keywords = group.Select(x => x.Rule).ToList() });
            var groupRecords = group.ToList();
            var groupIssues = Validate(new RuleSet
            {
                Version = rules.Version,
                Files = new List<RuleFile>
                {
                    new() { Name = group.Key.File, Category = group.Key.Category, Keywords = groupRecords.Select(x => x.Rule).ToList() }
                }
            });

            foreach (var issue in groupIssues)
            {
                var localIds = ResolveLocalIds(issue.Message, groupRecords);
                issues.Add(issue with
                {
                    LocalIds = localIds,
                    Field = ResolveField(issue.Message)
                });
            }
        }

        var versionIssue = Validate(rules).FirstOrDefault(x => x.Message.Contains("version", StringComparison.OrdinalIgnoreCase));
        if (versionIssue is not null)
            issues.Add(versionIssue with { Field = "version" });

        return issues;
    }

    // 兼容旧版本地文件：旧数据没有独立分类清单时，先从已有规则恢复分类；
    // 新数据则以 UserRuleSet.Categories 为准，允许分类在没有规则时单独存在。
    private static List<string> DeriveCategories(IEnumerable<UserRuleRecord> rules)
        => rules.Select(record => record.Category?.Trim())
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Select(category => category!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<string>? ResolveLocalIds(string message, IReadOnlyList<UserRuleRecord> records)
    {
        var marker = "规则 ";
        var start = message.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            // 文件级错误没有规则序号，但当前分组中的规则都受该文件/分类字段影响，
            // 前端仍应能把问题定位到可编辑的规则卡片，而不是只弹出一条全局通知。
            return message.Contains("文件名", StringComparison.Ordinal)
                || message.Contains("分类", StringComparison.Ordinal)
                ? records.Select(x => x.LocalId).ToList()
                : null;
        }
        start += marker.Length;
        var end = message.IndexOf('：', start);
        if (end < 0) end = message.Length;
        if (!int.TryParse(message[start..end], out var index) || index < 1 || index > records.Count) return null;

        if (message.Contains("重复", StringComparison.Ordinal))
        {
            var current = records[index - 1].Rule;
            var duplicateIds = records
                .Where(x => RuleKey(x.Rule, includeResult: true) == RuleKey(current, includeResult: true))
                .Select(x => x.LocalId)
                .ToList();
            return duplicateIds.Count > 1 ? duplicateIds : new[] { records[index - 1].LocalId };
        }

        return new[] { records[index - 1].LocalId };
    }

    private static string? ResolveField(string message)
    {
        if (message.Contains("版本", StringComparison.OrdinalIgnoreCase) || message.Contains("version", StringComparison.OrdinalIgnoreCase)) return "version";
        if (message.Contains("文件名", StringComparison.Ordinal)) return "file";
        if (message.Contains("分类", StringComparison.Ordinal)) return "category";
        if (message.Contains("关键词", StringComparison.Ordinal) || message.Contains("正则表达式", StringComparison.Ordinal)) return "term";
        if (message.Contains("问题描述", StringComparison.Ordinal)) return "result";
        if (message.Contains("上下文行数", StringComparison.Ordinal)) return "context_lines";
        if (message.Contains("上下文方向", StringComparison.Ordinal)) return "context_direction";
        if (message.Contains("搜索方向", StringComparison.Ordinal)) return "search_direction";
        if (message.Contains("严重程度", StringComparison.Ordinal)) return "severity";
        return null;
    }

    private void ThrowIfInvalid(RuleSet rules, string source)
    {
        var issue = Validate(rules).FirstOrDefault(x => x.IsError);
        if (issue is not null) throw new InvalidDataException($"{source}：{issue.Message}");
    }

    private async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, value, _options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<IReadOnlyDictionary<string, byte[]?>> CaptureFilesAsync(CancellationToken cancellationToken)
    {
        var files = new[] { _paths.OfficialRulesFile, _paths.LocalAdditionsFile, _paths.ActiveRulesFile, _paths.RulesStateFile };
        var snapshot = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshot[file] = File.Exists(file)
                ? await File.ReadAllBytesAsync(file, cancellationToken)
                : null;
        }
        return snapshot;
    }

    private static async Task RestoreFilesAsync(IReadOnlyDictionary<string, byte[]?> snapshot)
    {
        foreach (var pair in snapshot)
        {
            if (pair.Value is null)
            {
                if (File.Exists(pair.Key)) File.Delete(pair.Key);
                continue;
            }

            await File.WriteAllBytesAsync(pair.Key, pair.Value);
        }
    }

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

    private static RuleSet Clone(RuleSet value) => JsonSerializer.Deserialize<RuleSet>(JsonSerializer.Serialize(value)) ?? new RuleSet();
    private static RuleDefinition Clone(RuleDefinition value) => JsonSerializer.Deserialize<RuleDefinition>(JsonSerializer.Serialize(value)) ?? new RuleDefinition();
    private static string RuleKey(RuleDefinition rule, bool includeResult) => RuleKey(string.Empty, rule, includeResult);
    private static string RuleKey(string file, RuleDefinition rule, bool includeResult)
        => $"{file}\0{rule.Term}\0{rule.Regex}\0{(includeResult ? rule.Result : string.Empty)}";
    private static string MakeSafeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? $"rules-{DateTime.Now:yyyyMMddHHmmss}" : value;
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string File, string Category)>
    {
        public static readonly StringTupleComparer Instance = new();
        public bool Equals((string File, string Category) x, (string File, string Category) y)
            => string.Equals(x.File, y.File, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Category, y.Category, StringComparison.Ordinal);
        public int GetHashCode((string File, string Category) obj) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.File), obj.Category.GetHashCode(StringComparison.Ordinal));
    }
}
