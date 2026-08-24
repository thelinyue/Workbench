using Microsoft.Web.WebView2.Core;

namespace HephaestusWorkbench.App.Ssh;

/// <summary>
/// 内置终端页的浏览器安全边界。只允许固定 HTTPS 虚拟源读取本地映射资产，
/// 其余网络、文件、外部 scheme、跨源消息、弹窗、下载和权限由控件统一拒绝。
/// </summary>
internal static class TerminalBrowserSecurityPolicy
{
    internal const string VirtualHostName = "terminal.hephaestus.invalid";
    internal const string VirtualOrigin = "https://terminal.hephaestus.invalid";
    internal static CoreWebView2HostResourceAccessKind HostResourceAccessKind => CoreWebView2HostResourceAccessKind.DenyCors;
    internal static bool AllowExternalDrop => false;

    internal static bool ShouldCancelNavigation(string? uri) => !IsAllowedResource(uri);
    internal static bool IsTrustedMessageSource(string? source) => HasExactOrigin(source);
    internal static bool IsAllowedResource(string? uri) => HasExactOrigin(uri);

    private static bool HasExactOrigin(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(uri.Host, VirtualHostName, StringComparison.OrdinalIgnoreCase) &&
               uri.Port == 443 &&
               string.IsNullOrEmpty(uri.UserInfo);
    }
}
