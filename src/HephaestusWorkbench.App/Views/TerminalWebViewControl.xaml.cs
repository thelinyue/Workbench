using System.IO;
using System.Text;
using System.Windows;
using HephaestusWorkbench.App.Ssh;
using HephaestusWorkbench.App.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace HephaestusWorkbench.App.Views;

/// <summary>
/// Host 内置终端的固定 WebView2 封装。每个标签使用独立 user data 目录，只映射随应用发布的离线终端资产，
/// 并在 Web Message 进入协议层前校验来源；网络、跨源导航、弹窗、下载、权限和外部 scheme 全部拒绝。
/// </summary>
public partial class TerminalWebViewControl : System.Windows.Controls.UserControl, ITerminalSurface
{
    private const string ResourceFilter = "*";
    private CoreWebView2Environment? _environment;
    private TerminalTabViewModel? _tab;
    private bool _eventsAttached;
    private bool _initialized;
    private int _disposed;

    public TerminalWebViewControl()
    {
        InitializeComponent();
        Browser.AllowExternalDrop = TerminalBrowserSecurityPolicy.AllowExternalDrop;
        Loaded += OnLoaded;
    }

    public event EventHandler<string>? MessageReceived;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized || DataContext is not TerminalTabViewModel tab) return;
        _initialized = true;
        _tab = tab;
        try
        {
            Directory.CreateDirectory(tab.CacheDirectory);
            _environment = await CoreWebView2Environment.CreateAsync(userDataFolder: tab.CacheDirectory);
            await Browser.EnsureCoreWebView2Async(_environment);
            Configure(Browser.CoreWebView2);
            AttachEvents(Browser.CoreWebView2);
            var assets = Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal");
            if (!File.Exists(Path.Combine(assets, "index.html")))
                throw new FileNotFoundException("缺少内置终端页面，请重新安装应用。", Path.Combine(assets, "index.html"));
            Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
                TerminalBrowserSecurityPolicy.VirtualHostName,
                assets,
                TerminalBrowserSecurityPolicy.HostResourceAccessKind);
            var uri = $"{TerminalBrowserSecurityPolicy.VirtualOrigin}/index.html?fontFamily={Uri.EscapeDataString(tab.FontFamily)}&fontSize={tab.FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            await NavigateAndWaitAsync(Browser.CoreWebView2, uri);
            await tab.AttachSurfaceAsync(this);
        }
        catch (Exception exception)
        {
            ShowError($"无法启动内置终端：{Describe(exception)}");
        }
    }

    private static async Task NavigateAndWaitAsync(CoreWebView2 core, string uri)
    {
        var completion = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, CoreWebView2NavigationCompletedEventArgs args) => completion.TrySetResult(args);
        core.NavigationCompleted += Handler;
        try
        {
            core.Navigate(uri);
            var result = await completion.Task;
            if (!result.IsSuccess)
                throw new InvalidOperationException($"内置终端页面加载失败：{result.WebErrorStatus}。");
        }
        finally
        {
            core.NavigationCompleted -= Handler;
        }
    }

    private static void Configure(CoreWebView2 core)
    {
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsWebMessageEnabled = true;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
    }

    private void AttachEvents(CoreWebView2 core)
    {
        core.NavigationStarting += OnNavigationStarting;
        core.FrameNavigationStarting += OnFrameNavigationStarting;
        core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.PermissionRequested += OnPermissionRequested;
        core.AddWebResourceRequestedFilter(ResourceFilter, CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.WebMessageReceived += OnWebMessageReceived;
        _eventsAttached = true;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e) =>
        e.Cancel = TerminalBrowserSecurityPolicy.ShouldCancelNavigation(e.Uri);

    private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e) =>
        e.Cancel = TerminalBrowserSecurityPolicy.ShouldCancelNavigation(e.Uri);

    private void OnLaunchingExternalUriScheme(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
    {
        e.Cancel = true;
    }

    private static void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e) => e.Handled = true;

    private static void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
        e.Handled = true;
    }

    private static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = CoreWebView2PermissionState.Deny;
        e.SavesInProfile = false;
        e.Handled = true;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (TerminalBrowserSecurityPolicy.IsAllowedResource(e.Request.Uri)) return;
        var content = new MemoryStream(Encoding.UTF8.GetBytes("SSH 终端已阻止非同源资源请求。"));
        e.Response = Browser.CoreWebView2.Environment.CreateWebResourceResponse(
            content,
            403,
            "Forbidden",
            "Content-Type: text/plain; charset=utf-8");
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!TerminalBrowserSecurityPolicy.IsTrustedMessageSource(e.Source)) return;
        try
        {
            var json = e.TryGetWebMessageAsString();
            _ = TerminalWebMessageProtocol.ParseInbound(json);
            MessageReceived?.Invoke(this, json);
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            ShowError($"终端消息被拒绝：{exception.Message}");
        }
    }

    public Task SendAsync(string json, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Browser.CoreWebView2 is null) throw new InvalidOperationException("终端页面尚未准备完成。");
            Browser.CoreWebView2.PostWebMessageAsString(json);
        }).Task;
    }

    private void ShowError(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => ShowError(message));
            return;
        }
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private static string Describe(Exception exception) => exception switch
    {
        FileNotFoundException => exception.Message,
        WebView2RuntimeNotFoundException => "未检测到 Microsoft Edge WebView2 Runtime，请安装后重试。",
        _ => exception.Message
    };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (Dispatcher.CheckAccess())
        {
            DisposeBrowser();
            return;
        }
        await Dispatcher.InvokeAsync(DisposeBrowser);
    }

    private void DisposeBrowser()
    {
        Loaded -= OnLoaded;
        var core = Browser.CoreWebView2;
        if (core is not null && _eventsAttached)
        {
            core.NavigationStarting -= OnNavigationStarting;
            core.FrameNavigationStarting -= OnFrameNavigationStarting;
            core.LaunchingExternalUriScheme -= OnLaunchingExternalUriScheme;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.DownloadStarting -= OnDownloadStarting;
            core.PermissionRequested -= OnPermissionRequested;
            core.WebResourceRequested -= OnWebResourceRequested;
            core.WebMessageReceived -= OnWebMessageReceived;
            core.RemoveWebResourceRequestedFilter(ResourceFilter, CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.ClearVirtualHostNameToFolderMapping(TerminalBrowserSecurityPolicy.VirtualHostName);
        }
        MessageReceived = null;
        Browser.Dispose();
        _environment = null;
        _tab = null;
    }}
