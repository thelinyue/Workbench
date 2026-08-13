namespace HephaestusWorkbench.Services;

/// <summary>
/// 校验和生成 GitHub 插件下载备用地址。
/// 该类只处理地址模板，不负责网络请求，保证目录下载、插件包下载和规则下载互不串用。
/// </summary>
public static class GitHubDownloadMirrorTemplate
{
    public const string Placeholder = "{url}";

    public static string ValidateAndNormalize(string? template)
    {
        var normalized = template?.Trim() ?? string.Empty;
        if (normalized.Length == 0) return string.Empty;

        if (normalized.Count(x => x == '{') != 1 || normalized.Count(x => x == '}') != 1 || !normalized.Contains(Placeholder, StringComparison.Ordinal))
            throw new ArgumentException("GitHub 加速地址必须包含且只能包含一个 {url}。", nameof(template));

        var sample = normalized.Replace(Placeholder, "https://github.com/example/plugin.zip", StringComparison.Ordinal);
        if (!Uri.TryCreate(sample, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("GitHub 加速地址必须是 HTTPS 绝对地址。", nameof(template));

        return normalized;
    }

    public static bool IsConfigured(string? template) => !string.IsNullOrWhiteSpace(template);

    public static Uri? BuildUri(string? template, Uri original)
    {
        if (!IsGitHubUri(original) || !IsConfigured(template)) return null;
        var normalized = ValidateAndNormalize(template);
        var value = normalized.Replace(Placeholder, original.AbsoluteUri, StringComparison.Ordinal);
        return Uri.TryCreate(value, UriKind.Absolute, out var result) && result.Scheme == Uri.UriSchemeHttps ? result : null;
    }

    private static bool IsGitHubUri(Uri uri)
        => uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
}
