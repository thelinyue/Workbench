using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class GitHubRuleRepositoryServiceTests
{
    [Fact]
    public async Task CreatePullRequest_CreatesBranchAndVersionedRuleFileWithoutPersistingToken()
    {
        var handler = new RecordingHandler();
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var logger = new WorkbenchLogger(root);
        var service = new GitHubRuleRepositoryService(
            logger,
            new HttpClient(handler),
            new RuleRepositoryOptions("owner", "rules", "main", "rules/log-analyzer/versions"));

        var result = await service.CreatePullRequestAsync(
            new RuleSet { Files = new() },
            "2026.08.14",
            "补充规则",
            "github_pat_secret");

        Assert.Equal(7, result.Number);
        Assert.Equal("https://github.com/owner/rules/pull/7", result.Url);
        Assert.Contains(handler.RequestUris, value => Uri.UnescapeDataString(value).Contains("rules/log-analyzer/versions/2026.08.14.json", StringComparison.Ordinal));
        Assert.DoesNotContain("github_pat_secret", string.Join("\n", handler.RequestBodies));
        Assert.All(handler.AuthorizationHeaders, header => Assert.Equal("github_pat_secret", header?.Parameter));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> RequestUris { get; } = new();
        public List<string> RequestBodies { get; } = new();
        public List<AuthenticationHeaderValue?> AuthorizationHeaders { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!.ToString());
            RequestBodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            AuthorizationHeaders.Add(request.Headers.Authorization);

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/git/ref/heads/main", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"object\":{\"sha\":\"base-sha\"}}");
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.Contains("/contents/"))
                return Json(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}");
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/git/refs", StringComparison.Ordinal))
                return Json(HttpStatusCode.Created, "{\"ref\":\"refs/heads/rule-maintenance/test\"}");
            if (request.Method == HttpMethod.Put && request.RequestUri.AbsolutePath.Contains("/contents/"))
                return Json(HttpStatusCode.Created, "{\"content\":{\"sha\":\"new-sha\"}}");
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/pulls", StringComparison.Ordinal))
                return Json(HttpStatusCode.Created, "{\"number\":7,\"html_url\":\"https://github.com/owner/rules/pull/7\"}");

            return Json(HttpStatusCode.NotFound, "{\"message\":\"Unexpected request\"}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string content)
            => new(status)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            };
    }
}
