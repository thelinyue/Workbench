using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HephaestusWorkbench.Core.Models;

namespace HephaestusWorkbench.Services;

/// <summary>隔离规则上传通道，未配置服务时仍允许本地规则工作流独立运行。</summary>
public interface IRulePublisher
{
    bool IsConfigured { get; }
    Task PublishAsync(RuleSet rules, CancellationToken cancellationToken = default);
}

/// <summary>远程规则发布客户端。Token 由宿主注入，不写入普通 JSON 配置文件。</summary>
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
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
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

    public async Task PublishAsync(RuleSet rules, CancellationToken cancellationToken = default)
    {
        if (_endpoint is null) throw new InvalidOperationException("未配置 HTTPS 规则发布地址。");
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(rules), Encoding.UTF8, "application/json")
        };
        if (_token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) { _logger.Info("规则上传成功。"); return; }
            if (response.StatusCode == HttpStatusCode.Conflict) throw new InvalidOperationException("规则版本冲突，请刷新规则后重试。");
            throw new InvalidOperationException($"规则上传失败，服务器返回 {(int)response.StatusCode}：{response.ReasonPhrase}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.Error("规则上传失败", ex); throw; }
    }
}
