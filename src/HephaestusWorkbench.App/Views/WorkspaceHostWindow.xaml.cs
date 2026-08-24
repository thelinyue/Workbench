using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;
using Microsoft.Web.WebView2.Core;

namespace HephaestusWorkbench.App.Views;

/// <summary>
/// 固定承载 manifest v2 Workspace Web 扩展的受控窗口。
/// 宿主只映射当前扩展版本目录，默认拒绝网络、跨源导航、弹窗、浏览器权限和全部 Bridge 方法，
/// 避免本地静态页面借 WebView2 获得文件、Shell、进程或系统能力。
/// </summary>
public partial class WorkspaceHostWindow : Window
{
    internal const string VirtualHostName = WorkspaceBrowserSecurityPolicy.VirtualHostName;
    internal const string VirtualOrigin = WorkspaceBrowserSecurityPolicy.VirtualOrigin;

    private const string ResourceFilter = "*";
    private const string UnknownRequestId = "unknown";

    private readonly ExtensionManifest _manifest;
    private readonly string _cacheDirectory;
    private readonly WorkbenchLogger _logger;
    private readonly ExtensionVersionLease _versionLease;
    private CoreWebView2Environment? _environment;
    private bool _initialized;
    private bool _eventsAttached;

    public WorkspaceHostWindow(
        ExtensionVersionLease versionLease,
        string cacheDirectory,
        WorkbenchLogger logger)
    {
        _manifest = ValidateWorkspaceLease(versionLease);
        _cacheDirectory = string.IsNullOrWhiteSpace(cacheDirectory)
            ? throw new ArgumentException("Workspace Host 缓存目录不能为空。", nameof(cacheDirectory))
            : cacheDirectory;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _versionLease = versionLease;

        InitializeComponent();
        // 禁止把宿主文件拖入不受信任页面，避免页面借浏览器默认行为读取本地文件。
        Browser.AllowExternalDrop = WorkspaceBrowserSecurityPolicy.AllowExternalDrop;
        Title = _manifest.Name;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>
    /// Workspace Host 只能消费 Registry 创建的同一份版本租约，不能分别接收 manifest 和授权对象。
    /// 即使测试或未来宿主代码构造了不一致对象，也会在创建 WebView2 前按当前授权快照 fail-closed。
    /// </summary>
    internal static ExtensionManifest ValidateWorkspaceLease(ExtensionVersionLease versionLease)
    {
        ArgumentNullException.ThrowIfNull(versionLease);
        var manifest = versionLease.Manifest;
        var authorization = versionLease.Authorization;

        if (string.IsNullOrWhiteSpace(authorization.KeyId))
            throw new InvalidDataException("Workspace 扩展租约缺少受信任签名密钥身份。");
        if (!string.Equals(authorization.PublisherId, manifest.PublisherId, StringComparison.Ordinal))
            throw new InvalidDataException("Workspace 扩展租约的发布者与运行时授权不一致。");
        if (!authorization.AllowedKinds.Contains(manifest.Kind))
            throw new InvalidDataException("Workspace 扩展租约的类型超出运行时授权范围。");

        var allowedPermissions = new HashSet<string>(authorization.Permissions, StringComparer.Ordinal);
        foreach (var permission in manifest.Permissions)
        {
            if (!allowedPermissions.Contains(permission))
                throw new InvalidDataException($"Workspace 扩展权限 {permission} 超出运行时授权范围。");
        }

        return manifest;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            var entryPath = ResolveEntryPath(_manifest);
            var userDataFolder = BuildUserDataFolder(_manifest, _cacheDirectory);
            Directory.CreateDirectory(userDataFolder);

            _environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await Browser.EnsureCoreWebView2Async(_environment);

            ConfigureBrowser(Browser.CoreWebView2);
            AttachBrowserEvents(Browser.CoreWebView2);
            Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
                VirtualHostName,
                _manifest.DirectoryPath,
                WorkspaceBrowserSecurityPolicy.HostResourceAccessKind);
            Browser.CoreWebView2.Navigate(BuildVirtualEntryUri(_manifest.DirectoryPath, entryPath).AbsoluteUri);
        }
        catch (Exception exception)
        {
            var message = $"Workspace 扩展启动失败：{DescribeError(exception)}";
            ShowError(message);
            _logger.Error($"Workspace 扩展 {_manifest.Id} 启动失败", exception);
        }
    }

    /// <summary>
    /// 在创建 WebView2 前完成最后一道宿主校验。即使 manifest 来自内存对象而非标准解析器，
    /// 也只能启动 schema v2 的 workspace/web + workspace.page，并且入口必须真实位于版本目录内。
    /// </summary>
    internal static string ResolveEntryPath(ExtensionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.SchemaVersion != 2)
            throw new InvalidDataException("Workspace Host 只接受 schemaVersion 为 2 的扩展清单。");
        if (manifest.Kind != ExtensionKind.Workspace || manifest.Runtime?.Kind != ExtensionRuntimeKind.Web)
            throw new InvalidDataException("Workspace Host 只接受 workspace/web 扩展。");
        if (!manifest.Capabilities.Contains("workspace.page", StringComparer.Ordinal))
            throw new InvalidDataException("Workspace 扩展必须声明 workspace.page 能力。");

        try
        {
            ExtensionContractValidator.ValidateManifest(manifest);
        }
        catch (ExtensionContractException exception)
        {
            throw new InvalidDataException($"Workspace 扩展清单无效：{exception.Message}", exception);
        }

        try
        {
            var root = Path.GetFullPath(manifest.DirectoryPath);
            var entry = manifest.Runtime.Entry;
            if (string.IsNullOrWhiteSpace(entry) || Path.IsPathRooted(entry))
                throw new InvalidDataException("Workspace 扩展入口必须位于扩展版本目录内。");

            var resolved = Path.GetFullPath(Path.Combine(root, entry));
            if (!IsWithinDirectory(root, resolved))
                throw new InvalidDataException("Workspace 扩展入口解析后不在扩展版本目录内。");
            if (!File.Exists(resolved))
                throw new FileNotFoundException("Workspace 扩展入口文件不存在。", resolved);

            return resolved;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Workspace 扩展入口路径无效：{exception.Message}", exception);
        }
    }

    /// <summary>每个扩展版本使用独立 WebView2 数据目录，避免 Cookie、缓存和权限状态跨扩展泄漏。</summary>
    internal static string BuildUserDataFolder(ExtensionManifest manifest, string cacheDirectory)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(cacheDirectory))
            throw new ArgumentException("Workspace Host 缓存目录不能为空。", nameof(cacheDirectory));

        EnsureSafeFolderSegment(manifest.Id, "扩展 ID");
        EnsureSafeFolderSegment(manifest.Version, "扩展版本");

        var root = Path.GetFullPath(Path.Combine(cacheDirectory, "WebView2", "Workspace"));
        var result = Path.GetFullPath(Path.Combine(root, manifest.Id, manifest.Version));
        if (!IsWithinDirectory(root, result))
            throw new InvalidDataException("Workspace WebView2 数据目录越过了缓存根目录。");
        return result;
    }

    /// <summary>导航、资源请求和 Web Message 共用同一严格来源判定，避免各事件采用不同口径。</summary>
    internal static bool IsCurrentVirtualOrigin(string? source)
        => !WorkspaceBrowserSecurityPolicy.ShouldCancelNavigation(source);

    /// <summary>
    /// v2.0.0 没有获批的 Workspace Bridge 方法。合法请求统一返回 methodNotAllowed；
    /// 畸形、未知字段或错误协议请求统一返回 invalidRequest，且绝不执行任何宿主操作。
    /// </summary>
    internal static WorkspaceBridgeResponse CreateBridgeResponse(string message)
    {
        try
        {
            var request = WorkspaceBridgeProtocol.ParseRequest(message);
            return CreateErrorResponse(
                request.RequestId,
                "methodNotAllowed",
                "v2.0.0 当前尚未开放任何 Workspace Bridge 方法，宿主未执行任何操作。");
        }
        catch (ExtensionContractException)
        {
            return CreateErrorResponse(
                UnknownRequestId,
                "invalidRequest",
                "Workspace Bridge 请求无效，宿主未执行任何操作。");
        }
    }

    private static WorkspaceBridgeResponse CreateErrorResponse(string requestId, string code, string message)
        => new()
        {
            ProtocolVersion = WorkspaceBridgeProtocol.Version,
            RequestId = requestId,
            Error = new WorkspaceBridgeError
            {
                Code = code,
                Message = message
            }
        };

    private static Uri BuildVirtualEntryUri(string root, string entryPath)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(entryPath));
        var escapedPath = string.Join(
            '/',
            relative.Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
        return new Uri($"{VirtualOrigin}/{escapedPath}", UriKind.Absolute);
    }

    private static bool IsWithinDirectory(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return !Path.IsPathRooted(relative)
               && !relative.Equals("..", StringComparison.Ordinal)
               && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
               && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static void EnsureSafeFolderSegment(string? value, string displayName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value is "." or ".."
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains(Path.DirectorySeparatorChar)
            || value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidDataException($"{displayName} 不能用于隔离的 WebView2 数据目录。");
        }
    }

    private static void ConfigureBrowser(CoreWebView2 core)
    {
        var settings = core.Settings;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = WorkspaceBrowserSecurityPolicy.AreDefaultScriptDialogsEnabled;
        settings.AreHostObjectsAllowed = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsWebMessageEnabled = true;
    }

    private void AttachBrowserEvents(CoreWebView2 core)
    {
        core.NavigationStarting += OnNavigationStarting;
        core.FrameNavigationStarting += OnFrameNavigationStarting;
        core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
        core.DownloadStarting += OnDownloadStarting;
        core.AddWebResourceRequestedFilter(
            ResourceFilter,
            CoreWebView2WebResourceContext.All,
            CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.NewWindowRequested += OnNewWindowRequested;
        core.PermissionRequested += OnPermissionRequested;
        core.WebMessageReceived += OnWebMessageReceived;
        _eventsAttached = true;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        e.Cancel = WorkspaceBrowserSecurityPolicy.ShouldCancelNavigation(e.Uri);
        if (e.Cancel) _logger.Error($"Workspace 扩展阻止了非同源导航：{e.Uri}");
    }

    /// <summary>子框架与顶层页面共用固定虚拟源策略，禁止 iframe 绕过顶层导航边界。</summary>
    private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        e.Cancel = WorkspaceBrowserSecurityPolicy.ShouldCancelNavigation(e.Uri);
        if (e.Cancel) _logger.Error($"Workspace 扩展阻止了子框架非同源导航：{e.Uri}");
    }

    /// <summary>任何外部 URI Scheme 都不得离开 WebView2 交给操作系统处理。</summary>
    private void OnLaunchingExternalUriScheme(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
    {
        e.Cancel = WorkspaceBrowserSecurityPolicy.ShouldCancelExternalUriScheme(e.Uri);
        _logger.Error($"Workspace 扩展阻止了外部 URI Scheme：{e.Uri}");
    }

    /// <summary>Workspace 页面只允许读取映射内静态资源，不允许向本机文件系统写入下载内容。</summary>
    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var decision = WorkspaceBrowserSecurityPolicy.DecideDownload();
        e.Cancel = decision.Cancel;
        e.Handled = decision.Handled;
        _logger.Error("Workspace 扩展阻止了下载请求。");
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (IsCurrentVirtualOrigin(e.Request.Uri)) return;

        var content = new MemoryStream(Encoding.UTF8.GetBytes("Workspace Host 已阻止非同源资源请求。"));
        e.Response = _environment?.CreateWebResourceResponse(
            content,
            403,
            "Forbidden",
            "Content-Type: text/plain; charset=utf-8");
        _logger.Error($"Workspace 扩展阻止了非同源资源请求：{e.Request.Uri}");
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        _logger.Error($"Workspace 扩展阻止了弹窗请求：{e.Uri}");
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = CoreWebView2PermissionState.Deny;
        e.SavesInProfile = false;
        e.Handled = true;
        _logger.Error($"Workspace 扩展阻止了浏览器权限请求：{e.PermissionKind}");
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsCurrentVirtualOrigin(e.Source))
        {
            _logger.Error($"Workspace 扩展阻止了非同源 Web Message：{e.Source}");
            return;
        }

        var response = CreateBridgeResponse(e.WebMessageAsJson);
        Browser.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(response));
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // 版本租约由窗口本身持有，不能依赖外部 Closed 处理器的注册顺序。
        // 即使 WebView2 解绑或释放抛出异常，finally 边界也必须归还租约。
        CompleteClose(() =>
        {
            Loaded -= OnLoaded;
            Closed -= OnClosed;

            try
            {
                var core = Browser.CoreWebView2;
                if (core is not null && _eventsAttached)
                {
                    core.NavigationStarting -= OnNavigationStarting;
                    core.FrameNavigationStarting -= OnFrameNavigationStarting;
                    core.LaunchingExternalUriScheme -= OnLaunchingExternalUriScheme;
                    core.DownloadStarting -= OnDownloadStarting;
                    core.WebResourceRequested -= OnWebResourceRequested;
                    core.NewWindowRequested -= OnNewWindowRequested;
                    core.PermissionRequested -= OnPermissionRequested;
                    core.WebMessageReceived -= OnWebMessageReceived;
                    core.RemoveWebResourceRequestedFilter(
                        ResourceFilter,
                        CoreWebView2WebResourceContext.All,
                        CoreWebView2WebResourceRequestSourceKinds.All);
                    core.ClearVirtualHostNameToFolderMapping(VirtualHostName);
                    _eventsAttached = false;
                }
            }
            finally
            {
                Browser.Dispose();
            }
        }, _versionLease);
    }

    /// <summary>确保窗口内部清理失败时，扩展版本租约仍会被释放。</summary>
    internal static void CompleteClose(Action browserCleanup, IDisposable versionLease)
    {
        try
        {
            browserCleanup();
        }
        finally
        {
            versionLease.Dispose();
        }
    }

    private static string DescribeError(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "没有权限访问扩展文件或 WebView2 数据目录。",
        _ => exception.Message
    };
}
