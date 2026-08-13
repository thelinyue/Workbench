using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HephaestusWorkbench.Core.Models;

namespace HephaestusWorkbench.Services;

/// <summary>隔离用户规则提交通道；主规则和 active.json 永远不会作为整体上传。</summary>
public interface IRulePublisher
{
    bool IsConfigured { get; }
    Task<string?> PublishAsync(RuleSubmission submission, CancellationToken cancellationToken = default);
}

/// <summary>远程用户规则审核客户端。Token 由宿主注入，不写入普通 JSON 配置文件。</summary>
public sealed class HttpRulePublisher : IRulePublisher
{
    private readonly Uri? _endpoint;
    private readonly string? _token;
    private readonly HttpClient _http;
    private readonly WorkbenchLogger _logger;

    public HttpRulePublisher(string? endpoint, string? token, WorkbenchLogger logger, HttpClient? httpClient = null, string? protectedTokenPath = null)
    {
        _endpoint = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
        _token = string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(protectedTokenPath) && File.Exists(protectedTokenPath) ? ReadProtectedToken(protectedTokenPath, logger) : string.IsNullOrWhiteSpace(token) ? null : token;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _logger = logger;
        if (!string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(protectedTokenPath))
        {
            try { DpapiSecretStore.ProtectToFile(protectedTokenPath, token); } catch (Exception ex) { logger.Error("保存规则发布令牌密文失败", ex); }
        }
    }

    public bool IsConfigured => _endpoint is not null;

    private static string? ReadProtectedToken(string path, WorkbenchLogger logger)
    {
        try { return DpapiSecretStore.ReadFromFile(path); } catch (Exception ex) { logger.Error("读取规则发布令牌密文失败", ex); return null; }
    }

    public async Task<string?> PublishAsync(RuleSubmission submission, CancellationToken cancellationToken = default)
    {
        if (_endpoint is null) throw new InvalidOperationException("未配置 HTTPS 规则发布地址。");
        if (submission.Changes.Count == 0) throw new InvalidOperationException("没有可提交的用户规则。");
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(submission), Encoding.UTF8, "application/json")
        };
        if (_token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = TryReadSubmissionId(body);
                _logger.Info("用户规则提交成功，已进入维护者审核流程。");
                return result;
            }
            if (response.StatusCode == HttpStatusCode.Conflict) throw new InvalidOperationException("主规则版本已变化，请先更新主规则后重新提交。");
            throw new InvalidOperationException($"规则提交失败，服务器返回 {(int)response.StatusCode}：{response.ReasonPhrase}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.Error("规则提交失败", ex); throw; }
    }

    private static string? TryReadSubmissionId(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("submissionId", out var id) ? id.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
