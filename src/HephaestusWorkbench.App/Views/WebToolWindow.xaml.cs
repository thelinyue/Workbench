using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using WinSaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace HephaestusWorkbench.App.Views;

/// <summary>
/// 承载静态 Web 工具插件的独立窗口。
/// Web 工具不是分析报告，也不进入报告 Tab；工作台只负责提供本地 WebView2 容器和安全保存桥接。
/// </summary>
public partial class WebToolWindow : Window
{
    private static readonly string WebView2UserDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HephaestusWorkbench",
        "WebView2");
    private static readonly Lazy<Task<CoreWebView2Environment>> WebView2Environment = new(
        CreateWebView2EnvironmentAsync);

    private readonly PluginManifest _manifest;
    private readonly WorkbenchLogger _logger;
    private bool _initialized;

    public WebToolWindow(PluginManifest manifest, WorkbenchLogger logger)
    {
        _manifest = manifest;
        _logger = logger;
        Title = manifest.Name;
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            if (_manifest.Type != PluginType.Web)
                throw new InvalidDataException("当前插件不是 Web 工具，不能使用 WebView2 启动。");
            if (!File.Exists(_manifest.EntryPath))
                throw new FileNotFoundException("Web 工具入口文件不存在。", _manifest.EntryPath);

            await Browser.EnsureCoreWebView2Async(await WebView2Environment.Value);
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.NavigationStarting += OnNavigationStarting;
            Browser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            Browser.CoreWebView2.Navigate(new Uri(_manifest.EntryPath).AbsoluteUri);
        }
        catch (Exception ex)
        {
            ShowError($"工具启动失败：{DescribeError(ex)}");
            _logger.Error($"Web 工具启动失败：{_manifest.Id}", ex);
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)
            || !IsWithinPluginDirectory(uri.LocalPath))
        {
            e.Cancel = true;
            _logger.Error($"Web 工具阻止了非本地导航：{e.Uri}");
        }
    }

    private bool IsWithinPluginDirectory(string path)
    {
        var root = Path.GetFullPath(_manifest.DirectoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Uri.UnescapeDataString(path));
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || !string.Equals(type.GetString(), "saveFile", StringComparison.Ordinal)) return;

            var fileName = root.TryGetProperty("fileName", out var fileNameNode) ? fileNameNode.GetString() : null;
            var content = root.TryGetProperty("content", out var contentNode) ? contentNode.GetString() : null;
            var overwriteRequested = root.TryGetProperty("overwriteRequested", out var overwriteNode) && overwriteNode.GetBoolean();
            if (string.IsNullOrWhiteSpace(fileName) || content is null || Path.GetFileName(fileName) != fileName)
                throw new InvalidDataException("保存文件名或文件内容无效。");

            var dialog = new WinSaveFileDialog
            {
                Title = "保存 VG 清理结果",
                FileName = fileName,
                Filter = "VG 配置文件 (*.vg)|*.vg|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                DefaultExt = ".vg",
                AddExtension = true,
                OverwritePrompt = false
            };
            if (dialog.ShowDialog(this) != true)
            {
                PostMessage(new WebToolMessage("saveCanceled"));
                return;
            }
            if (File.Exists(dialog.FileName) && !overwriteRequested)
                throw new IOException("目标文件已存在。请勾选允许覆盖，或选择其他文件名。");
            if (File.Exists(dialog.FileName)
                && System.Windows.MessageBox.Show(this, $"文件已存在，确定覆盖吗？\n{dialog.FileName}", "确认覆盖", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                PostMessage(new WebToolMessage("saveCanceled"));
                return;
            }

            await File.WriteAllTextAsync(dialog.FileName, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            PostMessage(new WebToolMessage("saveSucceeded", dialog.FileName));
        }
        catch (Exception ex)
        {
            PostMessage(new WebToolMessage("saveFailed", null, DescribeError(ex)));
            _logger.Error($"Web 工具保存结果失败：{_manifest.Id}", ex);
        }
    }

    private void PostMessage(WebToolMessage message)
    {
        if (Browser.CoreWebView2 is null) return;
        Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Browser.Dispose();
    }

    private static Task<CoreWebView2Environment> CreateWebView2EnvironmentAsync()
    {
        Directory.CreateDirectory(WebView2UserDataFolder);
        return CoreWebView2Environment.CreateAsync(userDataFolder: WebView2UserDataFolder);
    }

    private static string DescribeError(Exception ex) => ex is UnauthorizedAccessException
        ? "没有权限访问目标文件或 WebView2 数据目录。"
        : ex.Message;

    private sealed record WebToolMessage(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("path")] string? Path = null,
        [property: JsonPropertyName("message")] string? Message = null);
}
