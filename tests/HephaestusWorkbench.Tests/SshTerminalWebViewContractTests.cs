using System.Xml.Linq;
using HephaestusWorkbench.App.Ssh;
using HephaestusWorkbench.Core.Models;

namespace HephaestusWorkbench.Tests;

public sealed class SshTerminalWebViewContractTests
{
    [Fact]
    public void OfflineTerminalAssets_ArePinnedAndContainNoCdnReferences()
    {
        var assets = Path.Combine(FindRepositoryRoot(), "src", "HephaestusWorkbench.App", "Assets", "Terminal");
        var required = new[] { "index.html", "terminal.js", "xterm.js", "xterm.css", "addon-fit.js", "LICENSE.xterm", "LICENSE.addon-fit" };

        Assert.All(required, file => Assert.True(File.Exists(Path.Combine(assets, file)), $"缺少离线终端资产：{file}"));
        var content = string.Join("\n", Directory.EnumerateFiles(assets).Select(File.ReadAllText));
        Assert.DoesNotContain("https://cdn", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unpkg.com", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("jsdelivr", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@xterm/xterm 6.0.0", content, StringComparison.Ordinal);
        Assert.Contains("@xterm/addon-fit 0.11.0", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://terminal.hephaestus.invalid/")]
    [InlineData("https://terminal.hephaestus.invalid/index.html")]
    public void SecurityPolicy_AllowsOnlyBuiltInOrigin(string uri)
    {
        Assert.False(TerminalBrowserSecurityPolicy.ShouldCancelNavigation(uri));
        Assert.True(TerminalBrowserSecurityPolicy.IsTrustedMessageSource(uri));
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("http://terminal.hephaestus.invalid/")]
    [InlineData("file:///C:/temp/index.html")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://terminal.hephaestus.invalid.evil.test/")]
    public void SecurityPolicy_BlocksNetworkFileExternalSchemeAndLookalikeOrigins(string uri)
    {
        Assert.True(TerminalBrowserSecurityPolicy.ShouldCancelNavigation(uri));
        Assert.False(TerminalBrowserSecurityPolicy.IsTrustedMessageSource(uri));
        Assert.False(TerminalBrowserSecurityPolicy.IsAllowedResource(uri));
    }

    [Fact]
    public void TerminalPage_ProvidesChineseAccessibleConnectionAndTabControls()
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", "HephaestusWorkbench.App", "Views", "SshTerminalPage.xaml");
        var document = XDocument.Load(path);
        var text = document.ToString();

        Assert.Contains("新建 SSH 连接", text, StringComparison.Ordinal);
        Assert.Contains("保存设备", text, StringComparison.Ordinal);
        Assert.Contains("保存凭据", text, StringComparison.Ordinal);
        Assert.Contains("密码认证", text, StringComparison.Ordinal);
        Assert.Contains("私钥认证", text, StringComparison.Ordinal);
        Assert.Contains("关闭终端标签", text, StringComparison.Ordinal);
        Assert.DoesNotContain("IsEnabled=\"False\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalWebViewControl_EnforcesSecurityWaitsForNavigationAndAvoidsUiShutdownDeadlock()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "src", "HephaestusWorkbench.App", "Views", "TerminalWebViewControl.xaml.cs"));
        var bridge = File.ReadAllText(Path.Combine(root, "src", "HephaestusWorkbench.App", "Assets", "Terminal", "terminal.js"));
        var controller = File.ReadAllText(Path.Combine(root, "src", "HephaestusWorkbench.App", "Ssh", "TerminalSessionController.cs"));

        Assert.Contains("SetVirtualHostNameToFolderMapping", code, StringComparison.Ordinal);
        Assert.Contains("NavigationStarting", code, StringComparison.Ordinal);
        Assert.Contains("FrameNavigationStarting", code, StringComparison.Ordinal);
        Assert.Contains("LaunchingExternalUriScheme", code, StringComparison.Ordinal);
        Assert.Contains("DownloadStarting", code, StringComparison.Ordinal);
        Assert.Contains("PermissionRequested", code, StringComparison.Ordinal);
        Assert.Contains("WebResourceRequested", code, StringComparison.Ordinal);
        Assert.Contains("IsTrustedMessageSource(e.Source)", code, StringComparison.Ordinal);
        Assert.Contains("NavigationCompleted", code, StringComparison.Ordinal);
        Assert.True(code.IndexOf("await NavigateAndWaitAsync", StringComparison.Ordinal) < code.IndexOf("AttachSurfaceAsync", StringComparison.Ordinal));
        Assert.Contains("Dispatcher.CheckAccess()", code, StringComparison.Ordinal);
        Assert.Contains("BoundedChannelOptions(OutputQueueCapacity)", controller, StringComparison.Ordinal);
        Assert.Contains("terminal.write(bytes, () => post({ type: 'ack'", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("NavigateToString", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPage_ExposesMinimalSshTerminalPreferences()
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", "HephaestusWorkbench.App", "Views", "SettingsPage.xaml");
        var text = File.ReadAllText(path);

        Assert.Contains("SSH 与终端", text, StringComparison.Ordinal);
        Assert.Contains("默认端口", text, StringComparison.Ordinal);
        Assert.Contains("终端字体", text, StringComparison.Ordinal);
        Assert.Contains("终端字号", text, StringComparison.Ordinal);
        Assert.Contains("暂态断线自动重连三次", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AppSettings_DefinesSshTerminalDefaultsWithoutCredentialProperties()
    {
        var config = new AppSettingsConfig();
        var json = System.Text.Json.JsonSerializer.Serialize(config);

        Assert.Equal(22, config.Ssh.DefaultPort);
        Assert.Equal("Cascadia Mono", config.Terminal.FontFamily);
        Assert.InRange(config.Terminal.FontSize, 10, 24);
        Assert.Equal(SshReconnectBehavior.AutomaticThreeAttempts, config.ReconnectBehavior);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("passphrase", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HephaestusWorkbench.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("无法定位测试仓库根目录。");
    }
}
