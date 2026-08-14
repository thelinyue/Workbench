using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HephaestusWorkbench.Core.Models;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 将维护者确认后的主规则快照提交到公开 GitHub 规则仓库。
/// Token 由调用方按次传入，本服务不缓存、不写盘，也不会把 Token 写入日志。
/// </summary>
public interface IRuleRepositoryPublisher
{
    Task<RulePullRequestResult> CreatePullRequestAsync(
        RuleSet rules,
        string version,
        string message,
        string token,
        CancellationToken cancellationToken = default);
}

public sealed record RulePullRequestResult(string BranchName, int Number, string Url);

/// <summary>GitHub Contents API 配置。默认仓库仅作为开发兼容值，正式环境可通过环境变量覆盖。</summary>
public sealed record RuleRepositoryOptions(string Owner, string Repository, string Branch, string RulesPath)
{
    public static RuleRepositoryOptions FromEnvironment()
    {
        var repository = Environment.GetEnvironmentVariable("HEPHAESTUS_RULE_REPOSITORY")?.Trim();
        var parts = repository?.Split('/', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var owner = parts is { Length: 2 } ? parts[0] : "thelinyue";
        var name = parts is { Length: 2 } ? parts[1] : "Hephaestus-Workbench-Plugins";
        return new RuleRepositoryOptions(
            owner,
            name,
            Environment.GetEnvironmentVariable("HEPHAESTUS_RULE_BRANCH")?.Trim() is { Length: > 0 } branch ? branch : "main",
            Environment.GetEnvironmentVariable("HEPHAESTUS_RULE_SOURCE_PATH")?.Trim() is { Length: > 0 } path
                ? path
                : "rules/log-analyzer/versions");
    }

    public static RuleRepositoryOptions FromSettings(MaintainerSettings settings)
        => settings.ToRepositoryOptions();
}

public sealed class GitHubRuleRepositoryService : IRuleRepositoryPublisher
{
    private readonly WorkbenchLogger _logger;
    private readonly HttpClient _http;
    private readonly RuleRepositoryOptions _options;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public GitHubRuleRepositoryService(WorkbenchLogger logger, HttpClient? httpClient = null, RuleRepositoryOptions? options = null)
    {
        _logger = logger;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        _options = options ?? RuleRepositoryOptions.FromEnvironment();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HephaestusWorkbench-RuleEditor/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<RulePullRequestResult> CreatePullRequestAsync(
        RuleSet rules,
        string version,
        string message,
        string token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("请输入 GitHub Fine-grained Token。");
        if (string.IsNullOrWhiteSpace(version)) throw new InvalidDataException("规则版本不能为空。");
        if (!System.Text.RegularExpressions.Regex.IsMatch(version.Trim(), "^[0-9A-Za-z][0-9A-Za-z._-]*$"))
            throw new InvalidDataException("规则版本只能包含字母、数字、点、下划线和短横线。");
        if (string.IsNullOrWhiteSpace(message)) throw new InvalidDataException("提交说明不能为空。");

        rules.Version = version.Trim();
        var source = JsonSerializer.Serialize(rules, new JsonSerializerOptions { WriteIndented = true });
        var baseSha = await GetBranchShaAsync(token, cancellationToken);
        var branchName = $"rule-maintenance/{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..48];
        await CreateBranchAsync(token, branchName, baseSha, cancellationToken);

        var rulesPath = $"{_options.RulesPath.TrimEnd('/')}/{version.Trim()}.json";
        var currentFile = await GetFileAsync(token, rulesPath, _options.Branch, cancellationToken);
        await PutFileAsync(token, branchName, rulesPath, source, currentFile?.Sha, message.Trim(), cancellationToken);
        var pullRequest = await CreatePullRequestAsync(token, branchName, message.Trim(), cancellationToken);
        _logger.Info($"维护者规则 PR 已创建：#{pullRequest.Number}");
        return pullRequest with { BranchName = branchName };
    }

    private async Task<string> GetBranchShaAsync(string token, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"git/ref/heads/{Uri.EscapeDataString(_options.Branch)}", token, cancellationToken);
        using var document = await ReadJsonAsync(response, cancellationToken);
        return document.RootElement.GetProperty("object").GetProperty("sha").GetString()
            ?? throw new InvalidDataException("GitHub 主分支没有有效提交。");
    }

    private async Task CreateBranchAsync(string token, string branchName, string sha, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { @ref = $"refs/heads/{branchName}", sha }, _json);
        using var response = await SendAsync(HttpMethod.Post, "git/refs", token, cancellationToken, payload);
        response.Dispose();
    }

    private async Task<GitHubFile?> GetFileAsync(string token, string path, string branch, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"contents/{EscapePath(path)}?ref={Uri.EscapeDataString(branch)}", token, cancellationToken, allowNotFound: true);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        using var document = await ReadJsonAsync(response, cancellationToken);
        var root = document.RootElement;
        return new GitHubFile(root.GetProperty("sha").GetString() ?? string.Empty);
    }

    private async Task PutFileAsync(string token, string branch, string path, string content, string? sha, string message, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["message"] = message,
            ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
            ["branch"] = branch
        };
        if (!string.IsNullOrWhiteSpace(sha)) payload["sha"] = sha;
        var json = JsonSerializer.Serialize(payload, _json);
        using var response = await SendAsync(HttpMethod.Put, $"contents/{EscapePath(path)}", token, cancellationToken, json);
        response.Dispose();
    }

    private async Task<RulePullRequestResult> CreatePullRequestAsync(string token, string branch, string title, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { title, head = branch, @base = _options.Branch, body = "由规则编辑器创建，请在合并前确认 GitHub Actions 校验结果。" }, _json);
        using var response = await SendAsync(HttpMethod.Post, "pulls", token, cancellationToken, payload);
        using var document = await ReadJsonAsync(response, cancellationToken);
        var root = document.RootElement;
        return new RulePullRequestResult(
            branch,
            root.GetProperty("number").GetInt32(),
            root.GetProperty("html_url").GetString() ?? string.Empty);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, CancellationToken cancellationToken, string? body = null, bool allowNotFound = false)
    {
        using var request = new HttpRequestMessage(method, $"https://api.github.com/repos/{_options.Owner}/{_options.Repository}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/vnd.github+json");
        var response = await _http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode || (allowNotFound && response.StatusCode == System.Net.HttpStatusCode.NotFound)) return response;
        var statusCode = response.StatusCode;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        response.Dispose();
        throw new InvalidOperationException($"GitHub 操作失败：HTTP {(int)statusCode}。{ExtractMessage(detail)}");
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        => await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

    private static string ExtractMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("message", out var message) ? message.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException) { return string.Empty; }
    }

    private static string EscapePath(string path)
        => string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    private sealed record GitHubFile(string Sha);
}
