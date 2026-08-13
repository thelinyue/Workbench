using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class GitHubDownloadMirrorTemplateTests
{
    [Fact]
    public void ValidateAndNormalize_AllowsHttpsTemplateAndEmptyValue()
    {
        Assert.Equal(string.Empty, GitHubDownloadMirrorTemplate.ValidateAndNormalize("  "));
        Assert.Equal("https://mirror.example/{url}", GitHubDownloadMirrorTemplate.ValidateAndNormalize(" https://mirror.example/{url} "));
    }

    [Theory]
    [InlineData("http://mirror.example/{url}")]
    [InlineData("https://mirror.example/plugin.zip")]
    [InlineData("https://mirror.example/{url}/{url}")]
    public void ValidateAndNormalize_RejectsInvalidTemplate(string template)
    {
        Assert.Throws<ArgumentException>(() => GitHubDownloadMirrorTemplate.ValidateAndNormalize(template));
    }

    [Fact]
    public void BuildUri_OnlyBuildsForGitHubPackageUrls()
    {
        var template = "https://mirror.example/{url}";
        var github = GitHubDownloadMirrorTemplate.BuildUri(template, new Uri("https://github.com/example/plugin.zip"));
        var other = GitHubDownloadMirrorTemplate.BuildUri(template, new Uri("https://example.com/plugin.zip"));

        Assert.Equal("https://mirror.example/https://github.com/example/plugin.zip", github?.AbsoluteUri);
        Assert.Null(other);
    }
}
