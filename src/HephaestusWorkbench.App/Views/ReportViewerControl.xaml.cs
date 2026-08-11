using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using HephaestusWorkbench.App.ViewModels;
using HephaestusWorkbench.Services;
using Microsoft.Web.WebView2.Core;

namespace HephaestusWorkbench.App.Views;

/// <summary>承载单个只读报告，并通过 WebMessage 保存节流后的页面滚动位置。</summary>
public partial class ReportViewerControl : System.Windows.Controls.UserControl
{
    // WebView2 默认把用户数据目录放在 EXE 所在目录；安装版 EXE 位于 Program Files 时该目录不可写。
    // 统一改到当前用户的 LocalAppData，避免初始化阶段因 UDF 无法创建而返回 E_ACCESSDENIED。
    private static readonly string WebView2UserDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HephaestusWorkbench",
        "WebView2");
    private static readonly Lazy<Task<CoreWebView2Environment>> WebView2Environment = new(
        CreateWebView2EnvironmentAsync);

    private ReportTabViewModel? _tab;
    private bool _initialized;

    public WorkbenchLogger? Logger { get; set; }

    public ReportViewerControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_tab is not null) _tab.DisposeRequested -= OnDisposeRequested;
        _tab = e.NewValue as ReportTabViewModel;
        if (_tab is not null) _tab.DisposeRequested += OnDisposeRequested;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized || _tab is null) return;
        _initialized = true;
        try
        {
            if (!File.Exists(_tab.ReportFile)) throw new FileNotFoundException("报告文件不存在。", _tab.ReportFile);
            await Browser.EnsureCoreWebView2Async(await WebView2Environment.Value);
            Browser.CoreWebView2.WebMessageReceived += (_, args) => ReceiveScroll(args.WebMessageAsJson);
            Browser.CoreWebView2.NavigationCompleted += async (_, args) =>
            {
                if (_tab is null) return;
                if (!args.IsSuccess)
                {
                    _tab.LoadError = $"报告加载失败：{args.WebErrorStatus}";
                    Logger?.Error($"报告导航失败：{_tab.ReportFile}，状态：{args.WebErrorStatus}");
                    ErrorPanel.Visibility = Visibility.Visible;
                    return;
                }

                try
                {
                    await Browser.ExecuteScriptAsync($"window.scrollTo(0, {_tab.ScrollPosition.ToString(System.Globalization.CultureInfo.InvariantCulture)});");
                }
                catch (Exception ex)
                {
                    _tab.LoadError = $"报告加载失败：{DescribeLoadError(ex)}";
                    Logger?.Error($"报告恢复滚动位置失败：{_tab.ReportFile}", ex);
                    ErrorPanel.Visibility = Visibility.Visible;
                }
            };
            await Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("""
                (() => {
                  let timer;
                  window.addEventListener('scroll', () => {
                    clearTimeout(timer);
                    timer = setTimeout(() => chrome.webview.postMessage({ type: 'scroll', value: window.scrollY || 0 }), 400);
                  }, { passive: true });
                })();
                """);
            Browser.CoreWebView2.Navigate(new Uri(_tab.ReportFile).AbsoluteUri);
        }
        catch (Exception ex)
        {
            if (_tab is not null)
            {
                _tab.LoadError = $"报告加载失败：{DescribeLoadError(ex)}";
                Logger?.Error($"报告加载失败：{_tab.ReportFile}", ex);
            }
            ErrorPanel.Visibility = Visibility.Visible;
        }
    }

    private static Task<CoreWebView2Environment> CreateWebView2EnvironmentAsync()
    {
        Directory.CreateDirectory(WebView2UserDataFolder);
        return CoreWebView2Environment.CreateAsync(userDataFolder: WebView2UserDataFolder);
    }

    private static string DescribeLoadError(Exception exception)
    {
        return exception is UnauthorizedAccessException || exception.HResult == unchecked((int)0x80070005)
            ? "WebView2 用户数据目录没有写入权限，请检查当前用户对本地应用数据目录的访问权限。"
            : exception.Message;
    }

    private void ReceiveScroll(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("type", out var type) && type.GetString() == "scroll" &&
                document.RootElement.TryGetProperty("value", out var value) && _tab is not null)
                _tab.ScrollPosition = value.GetDouble();
        }
        catch (JsonException) { }
    }

    private void OnDisposeRequested(object? sender, EventArgs e)
    {
        Browser.Dispose();
        if (_tab is not null) _tab.DisposeRequested -= OnDisposeRequested;
    }
}
