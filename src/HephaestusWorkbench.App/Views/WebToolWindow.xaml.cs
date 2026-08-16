using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Services;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using WinSaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace HephaestusWorkbench.App.Views;

/// <summary>
/// 承载静态 Web 工具插件的独立窗口。
/// Web 工具不是分析报告，也不进入报告 Tab；工作台只负责提供本地 WebView2 容器和安全保存桥接。
/// </summary>
public partial class WebToolWindow : Window
{
    private static readonly string WebView2UserDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HephaestusWorkbench",
        "WebView2");
    private static readonly Lazy<Task<CoreWebView2Environment>> WebView2Environment = new(
        CreateWebView2EnvironmentAsync);

    private readonly PluginManifest _manifest;
    private readonly WorkbenchLogger _logger;
    private readonly RuleSetService? _rules;
    private readonly IRulePublisher? _publisher;
    private MaintainerModeService? _maintainerMode;
    private IRuleRepositoryPublisher? _ruleRepository;
    private MaintainerSettingsStore? _maintainerSettingsStore;
    private MaintainerSettings? _maintainerSettings;
    private RuleRepositoryOptions? _repositoryOptions;
    private string? _githubToken;
    private static readonly JsonSerializerOptions RuleMessageJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private bool _initialized;

    public WebToolWindow(PluginManifest manifest, WorkbenchLogger logger, RuleSetService? rules = null, IRulePublisher? publisher = null)
    {
        _manifest = manifest;
        _logger = logger;
        _rules = rules;
        _publisher = publisher;
        Title = manifest.Name;
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            if (_manifest.Type != PluginType.Web)
                throw new InvalidDataException("当前插件不是 Web 工具，不能使用 WebView2 启动。");
            if (!File.Exists(_manifest.EntryPath))
                throw new FileNotFoundException("Web 工具入口文件不存在。", _manifest.EntryPath);

            if (_manifest.Supports("rule-editor")) InitializeMaintainerConfiguration();

            await Browser.EnsureCoreWebView2Async(await WebView2Environment.Value);
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.NavigationStarting += OnNavigationStarting;
            Browser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            Browser.CoreWebView2.Navigate(new Uri(_manifest.EntryPath).AbsoluteUri);
        }
        catch (Exception ex)
        {
            ShowError($"工具启动失败：{DescribeError(ex)}");
            _logger.Error($"Web 工具启动失败：{_manifest.Id}", ex);
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)
            || !IsWithinPluginDirectory(uri.LocalPath))
        {
            e.Cancel = true;
            _logger.Error($"Web 工具阻止了非本地导航：{e.Uri}");
        }
    }

    private bool IsWithinPluginDirectory(string path)
    {
        var root = Path.GetFullPath(_manifest.DirectoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Uri.UnescapeDataString(path));
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = ParseWebMessage(e.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type)) return;
            var messageType = type.GetString();
            if (_manifest.Supports("rule-editor") && messageType is "getRuleState" or "saveUserRules" or "validateUserRules" or "submitSelectedRules" or "exportRules" or "getMaintainerSetupState" or "configureMaintainer" or "unlockMaintainer" or "getMaintainerState" or "setMaintainerToken" or "submitMaintainerRules" or "exitMaintainerMode")
            {
                await HandleRuleMessageAsync(messageType!, root);
                return;
            }
            if (!string.Equals(messageType, "saveFile", StringComparison.Ordinal)) return;

            var fileName = root.TryGetProperty("fileName", out var fileNameNode) ? fileNameNode.GetString() : null;
            var content = root.TryGetProperty("content", out var contentNode) ? contentNode.GetString() : null;
            var overwriteRequested = root.TryGetProperty("overwriteRequested", out var overwriteNode) && overwriteNode.GetBoolean();
            if (string.IsNullOrWhiteSpace(fileName) || content is null || Path.GetFileName(fileName) != fileName)
                throw new InvalidDataException("保存文件名或文件内容无效。");

            var dialog = new WinSaveFileDialog
            {
                Title = "保存 VG 清理结果",
                FileName = fileName,
                Filter = "VG 配置文件 (*.vg)|*.vg|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                DefaultExt = ".vg",
                AddExtension = true,
                OverwritePrompt = false
            };
            if (dialog.ShowDialog(this) != true)
            {
                PostMessage(new WebToolMessage("saveCanceled"));
                return;
            }
            if (File.Exists(dialog.FileName) && !overwriteRequested)
                throw new IOException("目标文件已存在。请勾选允许覆盖，或选择其他文件名。");
            if (File.Exists(dialog.FileName)
                && System.Windows.MessageBox.Show(this, $"文件已存在，确定覆盖吗？\n{dialog.FileName}", "确认覆盖", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                PostMessage(new WebToolMessage("saveCanceled"));
                return;
            }

            await File.WriteAllTextAsync(dialog.FileName, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            PostMessage(new WebToolMessage("saveSucceeded", dialog.FileName));
        }
        catch (Exception ex)
        {
            PostMessage(new WebToolMessage("error", Message: DescribeError(ex)));
            _logger.Error($"Web 工具保存结果失败：{_manifest.Id}", ex);
        }
    }

    private async Task HandleRuleMessageAsync(string messageType, JsonElement root)
    {
        if (_rules is null) throw new InvalidOperationException("规则服务未初始化。");
        switch (messageType)
        {
            case "getMaintainerSetupState":
                PostRuleMessage(_maintainerMode is null ? "maintainerSetupRequired" : "maintainerConfigured", new
                {
                    repository = BuildRepositoryPayload(_repositoryOptions ?? RuleRepositoryOptions.FromEnvironment())
                });
                return;
            case "configureMaintainer":
                ConfigureMaintainer(root);
                PostRuleMessage("maintainerUnlocked", await BuildMaintainerStateAsync());
                return;
            case "unlockMaintainer":
                var key = root.TryGetProperty("key", out var keyNode) ? keyNode.GetString() : null;
                if (_maintainerMode is null)
                {
                    PostRuleMessage("maintainerSetupRequired", new { repository = BuildRepositoryPayload(_repositoryOptions ?? RuleRepositoryOptions.FromEnvironment()) });
                    return;
                }
                if (!_maintainerMode.TryUnlock(key))
                {
                    var remaining = _maintainerMode?.GetLockoutRemaining() ?? TimeSpan.Zero;
                    if (remaining > TimeSpan.Zero) throw new InvalidOperationException($"维护者模式已暂时锁定，请 {Math.Ceiling(remaining.TotalSeconds)} 秒后重试。");
                    throw new InvalidOperationException("维护者密钥不正确或维护者模式未配置。");
                }
                PostRuleMessage("maintainerUnlocked", await BuildMaintainerStateAsync());
                return;
            case "getMaintainerState":
                EnsureMaintainerUnlocked();
                PostRuleMessage("maintainerState", await BuildMaintainerStateAsync());
                return;
            case "setMaintainerToken":
                EnsureMaintainerUnlocked();
                var token = root.TryGetProperty("token", out var tokenNode) ? tokenNode.GetString()?.Trim() : null;
                if (string.IsNullOrWhiteSpace(token)) throw new InvalidDataException("GitHub Token 不能为空。");
                _githubToken = token;
                PostRuleMessage("maintainerTokenAccepted", new { connected = true });
                return;
            case "validateMaintainerRules":
                EnsureMaintainerUnlocked();
                var maintainerCandidate = root.TryGetProperty("rules", out var maintainerCandidateNode)
                    ? maintainerCandidateNode.Deserialize<UserRuleSet>(RuleMessageJsonOptions)
                    : null;
                if (maintainerCandidate is null) throw new InvalidDataException("维护者规则内容为空。");
                var maintainerValidation = _rules.ValidateUserRules(maintainerCandidate);
                PostRuleMessage("validationResult", maintainerValidation);
                return;
            case "submitMaintainerRules":
                EnsureMaintainerUnlocked();
                if (_ruleRepository is null) throw new InvalidOperationException("规则仓库提交通道未初始化。");
                if (string.IsNullOrWhiteSpace(_githubToken)) throw new InvalidOperationException("请先输入 GitHub Fine-grained Token。");
                var maintainerRules = root.TryGetProperty("rules", out var maintainerRulesNode)
                    ? maintainerRulesNode.Deserialize<UserRuleSet>(RuleMessageJsonOptions)
                    : null;
                if (maintainerRules is null) throw new InvalidDataException("维护者规则内容为空。");
                var version = root.TryGetProperty("version", out var versionNode) ? versionNode.GetString()?.Trim() : null;
                var message = root.TryGetProperty("message", out var messageNode) ? messageNode.GetString()?.Trim() : null;
                var release = BuildRuleSet(maintainerRules, version);
                var releaseIssues = _rules.Validate(release);
                var issue = releaseIssues.FirstOrDefault(x => x.IsError);
                if (issue is not null)
                {
                    PostRuleMessage("validationResult", releaseIssues);
                    return;
                }
                var pullRequest = await _ruleRepository.CreatePullRequestAsync(release, release.Version!, message ?? string.Empty, _githubToken, CancellationToken.None);
                PostRuleMessage("maintainerSubmissionSucceeded", new { pullRequest.Number, pullRequest.Url, pullRequest.BranchName });
                return;
            case "exitMaintainerMode":
                _maintainerMode?.Clear();
                _githubToken = null;
                PostRuleMessage("maintainerExited", new { });
                return;
            case "getRuleState":
                PostRuleMessage("ruleState", await ReadRuleEditorStateAsync());
                PostRuleMessage("hostInfo", BuildHostInfoPayload());
                return;
            case "saveUserRules":
                var user = root.TryGetProperty("user", out var userNode)
                    ? userNode.Deserialize<UserRuleSet>(RuleMessageJsonOptions)
                    : null;
                if (user is null) throw new InvalidDataException("用户规则内容为空。");
                await _rules.SaveUserAsync(user);
                PostRuleMessage("saveSucceeded", await ReadRuleEditorStateAsync());
                return;
            case "validateUserRules":
                var candidate = root.TryGetProperty("user", out var candidateNode)
                    ? candidateNode.Deserialize<UserRuleSet>(RuleMessageJsonOptions)
                    : null;
                if (candidate is null) throw new InvalidDataException("用户规则内容为空。");
                PostRuleMessage("validationResult", _rules.ValidateUserRules(candidate));
                return;
            case "submitSelectedRules":
                var currentUser = root.TryGetProperty("user", out var currentUserNode)
                    ? currentUserNode.Deserialize<UserRuleSet>(RuleMessageJsonOptions)
                    : await _rules.ReadUserAsync();
                if (currentUser is null) throw new InvalidDataException("用户规则内容为空。");
                if (!currentUser.Rules.Any(x => x.Selected && x.Status is ("draft" or "rejected")))
                {
                    PostRuleMessage("submissionBlocked", new { message = "请先选择至少一条草稿或已退回的规则。" });
                    return;
                }

                var userIssues = _rules.ValidateUserRules(currentUser);
                if (userIssues.Any(x => x.IsError))
                {
                    PostRuleMessage("validationResult", userIssues);
                    return;
                }

                if (_publisher is null)
                {
                    PostRuleMessage("submissionBlocked", new { message = "当前未配置用户规则审核发布通道。请先保存草稿，配置发布通道后再提交。" });
                    return;
                }

                if (root.TryGetProperty("user", out _)) await _rules.SaveUserAsync(currentUser);
                if (!currentUser.Rules.Any(x => x.Selected && x.Status is ("draft" or "rejected")))
                {
                    PostRuleMessage("submissionBlocked", new { message = "主规则已发生变化，选中的规则现在存在冲突或已合并。请刷新后处理冲突，再重试提交。" });
                    return;
                }
                var submission = await _rules.BuildSubmissionAsync(currentUser);
                var submissionId = await _publisher.PublishAsync(submission);
                await _rules.MarkSubmittedAsync(submission, submissionId);
                PostRuleMessage("submissionSucceeded", new { submissionId, state = await ReadRuleEditorStateAsync() });
                return;
            case "exportRules":
                var exportState = await _rules.ReadEditorStateAsync();
                PostRuleMessage("exportData", exportState.User);
                return;
        }
    }

    private void EnsureMaintainerUnlocked()
    {
        if (_maintainerMode?.IsUnlocked != true) throw new InvalidOperationException("维护者模式尚未解锁。");
    }

    private async Task<object> BuildMaintainerStateAsync()
    {
        var official = await _rules!.ReadOfficialAsync() ?? new RuleSet { Version = "尚未同步" };
        return new
        {
            version = official.Version,
            categories = official.Files.Select(file => file.Category)
                .Where(category => !string.IsNullOrWhiteSpace(category))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            rules = official.Files.SelectMany(file => file.Keywords.Select((rule, index) => new UserRuleRecord
            {
                LocalId = $"{file.Name}:{index}",
                File = file.Name,
                Category = file.Category,
                Rule = rule,
                Status = "draft"
            })).ToList(),
            repository = BuildRepositoryPayload(_repositoryOptions ?? RuleRepositoryOptions.FromEnvironment())
        };
    }

    private async Task<RuleEditorState> ReadRuleEditorStateAsync()
    {
        var state = await _rules!.ReadEditorStateAsync();
        state.State.SubmissionAvailable = _publisher is not null;
        state.State.SubmissionUnavailableReason = _publisher is null
            ? "当前未配置用户规则审核发布通道。"
            : null;
        return state;
    }

    private object BuildHostInfoPayload()
        => new
        {
            appVersion = AppVersionInfo.DisplayVersion,
            submissionAvailable = _publisher is not null,
            submissionUnavailableReason = _publisher is null ? "当前未配置用户规则审核发布通道。" : null
        };

    private void InitializeMaintainerConfiguration()
    {
        _maintainerSettingsStore = new MaintainerSettingsStore();
        try
        {
            _maintainerSettings = _maintainerSettingsStore.Load();
        }
        catch (Exception ex)
        {
            _logger.Error("读取维护者配置失败，将等待重新初始化。", ex);
            _maintainerSettings = null;
        }

        if (_maintainerSettings is null)
        {
            var environmentKey = Environment.GetEnvironmentVariable("HEPHAESTUS_MAINTAINER_KEY");
            if (!string.IsNullOrWhiteSpace(environmentKey))
                _maintainerSettings = MaintainerSettings.FromEnvironment(environmentKey);
        }

        if (_maintainerSettings is not null)
        {
            _maintainerMode = new MaintainerModeService(_maintainerSettings.Key);
            _repositoryOptions = MaintainerSettingsStoreOptions(_maintainerSettings);
            _ruleRepository = new GitHubRuleRepositoryService(_logger, options: _repositoryOptions);
        }
    }

    private void ConfigureMaintainer(JsonElement root)
    {
        var key = ReadRequiredString(root, "key");
        var confirmation = ReadRequiredString(root, "confirmation");
        if (!string.Equals(key, confirmation, StringComparison.Ordinal))
            throw new InvalidDataException("两次输入的维护者密钥不一致。");

        var repository = ReadRequiredString(root, "repository");
        var parts = repository.Split('/', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) throw new InvalidDataException("GitHub 仓库必须填写为 owner/repository。");
        var settings = new MaintainerSettings(
            key,
            parts[0],
            parts[1],
            ReadRequiredString(root, "branch"),
            ReadRequiredString(root, "rulesPath"));

        if (_maintainerSettingsStore is null) throw new InvalidOperationException("维护者配置存储未初始化。");
        _maintainerSettingsStore.Save(settings);
        _maintainerSettings = settings;
        _repositoryOptions = MaintainerSettingsStoreOptions(settings);
        _maintainerMode = new MaintainerModeService(settings.Key);
        _ruleRepository = new GitHubRuleRepositoryService(_logger, options: _repositoryOptions);
        _maintainerMode.TryUnlock(key);
    }

    private static RuleRepositoryOptions MaintainerSettingsStoreOptions(MaintainerSettings settings)
        => RuleRepositoryOptions.FromSettings(settings);

    private static object BuildRepositoryPayload(RuleRepositoryOptions options)
        => new { owner = options.Owner, repository = options.Repository, branch = options.Branch, rulesPath = options.RulesPath };

    private static string ReadRequiredString(JsonElement root, string name)
    {
        var value = root.TryGetProperty(name, out var node) ? node.GetString()?.Trim() : null;
        return string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException($"维护者配置项 {name} 不能为空。") : value;
    }

    private static RuleSet BuildRuleSet(UserRuleSet user, string? version)
    {
        var result = new RuleSet { Version = string.IsNullOrWhiteSpace(version) ? user.BaseVersion ?? DateTime.UtcNow.ToString("yyyy.MM.dd") : version };
        foreach (var group in user.Rules.GroupBy(x => (x.File, x.Category)))
        {
            result.Files.Add(new RuleFile
            {
                Name = group.Key.File,
                Category = group.Key.Category,
                Keywords = group.Select(x => x.Rule).ToList()
            });
        }
        return result;
    }

    private void PostRuleMessage(string type, object payload)
    {
        PostMessage(new WebToolMessage(type, Data: payload));
    }

    /// <summary>
    /// 兼容 WebView2 的两种消息形态：页面直接发送对象，或先 JSON.stringify 后发送字符串。
    /// 后一种形态在 WebView2 中会被包装成 JSON 字符串，直接调用 TryGetProperty 会触发类型异常。
    /// </summary>
    internal static JsonDocument ParseWebMessage(string message)
    {
        var envelope = JsonDocument.Parse(message);
        if (envelope.RootElement.ValueKind != JsonValueKind.String) return envelope;

        var content = envelope.RootElement.GetString();
        envelope.Dispose();
        if (string.IsNullOrWhiteSpace(content)) throw new JsonException("WebView 消息内容为空。");
        return JsonDocument.Parse(content);
    }

    private void PostMessage(WebToolMessage message)
    {
        if (Browser.CoreWebView2 is null) return;
        Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _maintainerMode?.Clear();
        _githubToken = null;
        Browser.Dispose();
    }

    private static Task<CoreWebView2Environment> CreateWebView2EnvironmentAsync()
    {
        Directory.CreateDirectory(WebView2UserDataFolder);
        return CoreWebView2Environment.CreateAsync(userDataFolder: WebView2UserDataFolder);
    }

    private static string DescribeError(Exception ex) => ex is UnauthorizedAccessException
        ? "没有权限访问目标文件或 WebView2 数据目录。"
        : ex.Message;

    private sealed record WebToolMessage(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("path")] string? Path = null,
        [property: JsonPropertyName("message")] string? Message = null,
        [property: JsonPropertyName("data")] object? Data = null);
}
