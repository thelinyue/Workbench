using Microsoft.Web.WebView2.Core;

namespace HephaestusWorkbench.App.Views;

/// <summary>
/// Workspace WebView2 的最小安全决策边界。这里不持有浏览器对象，只返回固定的 fail-closed 决策，
/// 使窗口事件处理器只负责把决策写回 WebView2，同时允许测试直接执行真实安全逻辑。
/// </summary>
internal static class WorkspaceBrowserSecurityPolicy
{
    internal const string VirtualHostName = "workspace.hephaestus.invalid";
    internal const string VirtualOrigin = $"https://{VirtualHostName}";
    internal const bool AllowExternalDrop = false;
    internal const bool AreDefaultScriptDialogsEnabled = false;

    internal static CoreWebView2HostResourceAccessKind HostResourceAccessKind
        => CoreWebView2HostResourceAccessKind.Deny;

    /// <summary>顶层与子框架导航只允许固定 HTTPS 虚拟源，其他输入全部取消。</summary>
    internal static bool ShouldCancelNavigation(string? source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return true;
        return !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
               || !string.Equals(uri.Host, VirtualHostName, StringComparison.OrdinalIgnoreCase)
               || !uri.IsDefaultPort
               || !string.IsNullOrEmpty(uri.UserInfo);
    }

    /// <summary>任何外部 URI Scheme 都不得交给操作系统处理。</summary>
    internal static bool ShouldCancelExternalUriScheme(string? _) => true;

    /// <summary>下载必须同时取消并标记已处理，避免保存对话框或文件写入。</summary>
    internal static (bool Cancel, bool Handled) DecideDownload() => (Cancel: true, Handled: true);
}
