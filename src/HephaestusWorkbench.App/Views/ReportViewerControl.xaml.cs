using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using HephaestusWorkbench.App.ViewModels;

namespace HephaestusWorkbench.App.Views;

/// <summary>承载单个只读报告，并通过 WebMessage 保存节流后的页面滚动位置。</summary>
public partial class ReportViewerControl : System.Windows.Controls.UserControl
{
    private ReportTabViewModel? _tab;
    private bool _initialized;

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
            await Browser.EnsureCoreWebView2Async();
            Browser.CoreWebView2.WebMessageReceived += (_, args) => ReceiveScroll(args.WebMessageAsJson);
            Browser.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                if (_tab is null) return;
                await Browser.ExecuteScriptAsync($"window.scrollTo(0, {_tab.ScrollPosition.ToString(System.Globalization.CultureInfo.InvariantCulture)});");
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
            if (_tab is not null) _tab.LoadError = $"报告加载失败：{ex.Message}";
            ErrorPanel.Visibility = Visibility.Visible;
        }
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
