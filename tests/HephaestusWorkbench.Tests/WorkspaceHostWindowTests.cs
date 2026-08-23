using System.Text.Json;
using HephaestusWorkbench.App.Views;
using HephaestusWorkbench.PluginSDK;
using Microsoft.Web.WebView2.Core;

namespace HephaestusWorkbench.Tests;

public sealed class WorkspaceHostWindowTests
{
    [Fact]
    public void CompleteClose_ReleasesVersionLeaseWhenBrowserCleanupFails()
    {
        var lease = new RecordingDisposable();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            WorkspaceHostWindow.CompleteClose(
                () => throw new InvalidOperationException("受控的浏览器清理故障"),
                lease));

        Assert.Equal("受控的浏览器清理故障", exception.Message);
        Assert.True(lease.IsDisposed);
    }

    [Fact]
    public void ResolveEntryPath_AcceptsOnlyExistingWorkspaceWebEntry()
    {
        using var environment = new WorkspaceManifestEnvironment();
        var manifest = environment.CreateManifest();

        var result = WorkspaceHostWindow.ResolveEntryPath(manifest);

        Assert.Equal(Path.GetFullPath(environment.EntryPath), result);
    }

    [Theory]
    [InlineData(ExtensionKind.Analysis, ExtensionRuntimeKind.Web)]
    [InlineData(ExtensionKind.Workspace, ExtensionRuntimeKind.Process)]
    public void ResolveEntryPath_RejectsWrongKindOrRuntime(
        ExtensionKind kind,
        ExtensionRuntimeKind runtimeKind)
    {
        using var environment = new WorkspaceManifestEnvironment();
        var manifest = environment.CreateManifest(kind: kind, runtimeKind: runtimeKind);

        var exception = Assert.Throws<InvalidDataException>(
            () => WorkspaceHostWindow.ResolveEntryPath(manifest));

        Assert.Contains("workspace/web", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveEntryPath_RejectsMissingWorkspacePageCapability()
    {
        using var environment = new WorkspaceManifestEnvironment();
        var manifest = environment.CreateManifest(capabilities: []);

        var exception = Assert.Throws<InvalidDataException>(
            () => WorkspaceHostWindow.ResolveEntryPath(manifest));

        Assert.Contains("workspace.page", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveEntryPath_RejectsPathTraversal()
    {
        using var environment = new WorkspaceManifestEnvironment();
        var outsidePath = Path.Combine(environment.RootPath, "outside.html");
        File.WriteAllText(outsidePath, "outside");
        var manifest = environment.CreateManifest(entry: "../outside.html");

        var exception = Assert.Throws<InvalidDataException>(
            () => WorkspaceHostWindow.ResolveEntryPath(manifest));

        Assert.Contains("扩展版本目录", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveEntryPath_RejectsMissingEntryFile()
    {
        using var environment = new WorkspaceManifestEnvironment();
        var manifest = environment.CreateManifest(entry: "missing.html");

        var exception = Assert.Throws<FileNotFoundException>(
            () => WorkspaceHostWindow.ResolveEntryPath(manifest));

        Assert.Contains("入口文件不存在", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUserDataFolder_IsolatesEachExtensionVersionWithinRoot()
    {
        using var environment = new WorkspaceManifestEnvironment();
        var baseFolder = Path.Combine(environment.RootPath, "webview-data");
        var first = WorkspaceHostWindow.BuildUserDataFolder(
            environment.CreateManifest(id: "rule-editor", version: "2.0.0"),
            baseFolder);
        var second = WorkspaceHostWindow.BuildUserDataFolder(
            environment.CreateManifest(id: "rule-editor", version: "2.1.0"),
            baseFolder);
        var other = WorkspaceHostWindow.BuildUserDataFolder(
            environment.CreateManifest(id: "lvm-tool", version: "2.0.0"),
            baseFolder);

        Assert.NotEqual(first, second);
        Assert.NotEqual(first, other);
        Assert.True(IsWithin(baseFolder, first));
        Assert.True(IsWithin(baseFolder, second));
        Assert.True(IsWithin(baseFolder, other));
    }

    [Theory]
    [InlineData("https://workspace.hephaestus.invalid/index.html", true)]
    [InlineData("https://workspace.hephaestus.invalid/assets/app.js?version=2", true)]
    [InlineData("http://workspace.hephaestus.invalid/index.html", false)]
    [InlineData("https://workspace.hephaestus.invalid.evil.example/index.html", false)]
    [InlineData("https://workspace.hephaestus.invalid:444/index.html", false)]
    [InlineData("file:///C:/extension/index.html", false)]
    [InlineData("not-a-uri", false)]
    public void IsCurrentVirtualOrigin_RejectsNonSameOrigin(string source, bool expected)
    {
        Assert.Equal(expected, WorkspaceHostWindow.IsCurrentVirtualOrigin(source));
    }

    [Fact]
    public void BrowserSecurityPolicy_UsesLockedDownDefaults()
    {
        Assert.False(WorkspaceBrowserSecurityPolicy.AllowExternalDrop);
        Assert.False(WorkspaceBrowserSecurityPolicy.AreDefaultScriptDialogsEnabled);
        Assert.Equal(
            CoreWebView2HostResourceAccessKind.Deny,
            WorkspaceBrowserSecurityPolicy.HostResourceAccessKind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("mailto:security@example.com")]
    [InlineData("custom-scheme://open")]
    public void BrowserSecurityPolicy_AlwaysCancelsExternalUriScheme(string? uri)
    {
        Assert.True(WorkspaceBrowserSecurityPolicy.ShouldCancelExternalUriScheme(uri));
    }

    [Fact]
    public void BrowserSecurityPolicy_CancelsAndHandlesDownloads()
    {
        var decision = WorkspaceBrowserSecurityPolicy.DecideDownload();

        Assert.True(decision.Cancel);
        Assert.True(decision.Handled);
    }

    [Theory]
    [InlineData("https://workspace.hephaestus.invalid/index.html", false)]
    [InlineData("https://workspace.hephaestus.invalid/assets/app.js?version=2", false)]
    [InlineData("http://workspace.hephaestus.invalid/index.html", true)]
    [InlineData("https://workspace.hephaestus.invalid.evil.example/index.html", true)]
    [InlineData("https://workspace.hephaestus.invalid:444/index.html", true)]
    [InlineData("file:///C:/extension/index.html", true)]
    [InlineData("not-a-uri", true)]
    public void BrowserSecurityPolicy_FrameNavigationAllowsOnlyFixedVirtualOrigin(
        string source,
        bool expectedCancel)
    {
        Assert.Equal(expectedCancel, WorkspaceBrowserSecurityPolicy.ShouldCancelNavigation(source));
    }

    [Theory]
    [InlineData("LaunchingExternalUriScheme", "OnLaunchingExternalUriScheme")]
    [InlineData("DownloadStarting", "OnDownloadStarting")]
    [InlineData("FrameNavigationStarting", "OnFrameNavigationStarting")]
    public void BrowserSecurityContract_AttachesAndDetachesSecurityEvents(
        string eventName,
        string handlerName)
    {
        var source = LoadWorkspaceHostSource();

        Assert.Contains($"core.{eventName} += {handlerName};", source, StringComparison.Ordinal);
        Assert.Contains($"core.{eventName} -= {handlerName};", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserSecurityContract_WindowAppliesExecutablePolicy()
    {
        var source = LoadWorkspaceHostSource();
        const string navigationDecision =
            "e.Cancel = WorkspaceBrowserSecurityPolicy.ShouldCancelNavigation(e.Uri);";

        Assert.Contains(
            "Browser.AllowExternalDrop = WorkspaceBrowserSecurityPolicy.AllowExternalDrop;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "settings.AreDefaultScriptDialogsEnabled = WorkspaceBrowserSecurityPolicy.AreDefaultScriptDialogsEnabled;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "WorkspaceBrowserSecurityPolicy.HostResourceAccessKind);",
            source,
            StringComparison.Ordinal);
        Assert.Equal(2, source.Split(navigationDecision, StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "e.Cancel = WorkspaceBrowserSecurityPolicy.ShouldCancelExternalUriScheme(e.Uri);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "var decision = WorkspaceBrowserSecurityPolicy.DecideDownload();",
            source,
            StringComparison.Ordinal);
        Assert.Contains("e.Cancel = decision.Cancel;", source, StringComparison.Ordinal);
        Assert.Contains("e.Handled = decision.Handled;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateBridgeResponse_RejectsUnknownMethodWithoutExecutingIt()
    {
        var response = WorkspaceHostWindow.CreateBridgeResponse("""
            {
              "protocolVersion": "workspace-bridge-v1",
              "requestId": "bridge-1",
              "method": "workspace.readText",
              "params": { "path": "secret.txt" }
            }
            """);

        Assert.Equal("bridge-1", response.RequestId);
        Assert.Equal("methodNotAllowed", response.Error!.Code);
        Assert.Contains("尚未开放", response.Error.Message, StringComparison.Ordinal);
        Assert.Null(response.Result);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"protocolVersion\":\"workspace-bridge-v1\",\"requestId\":\"bridge-1\",\"method\":\"x\",\"params\":{},\"extra\":true}")]
    [InlineData("{\"protocolVersion\":\"legacy\",\"requestId\":\"bridge-1\",\"method\":\"x\",\"params\":{}}")]
    public void CreateBridgeResponse_ReturnsStructuredInvalidRequestForMalformedMessage(string message)
    {
        var response = WorkspaceHostWindow.CreateBridgeResponse(message);

        Assert.Equal(WorkspaceBridgeProtocol.Version, response.ProtocolVersion);
        Assert.Equal("invalidRequest", response.Error!.Code);
        Assert.Contains("无效", response.Error.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(response.RequestId));
    }

    private static bool IsWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return !Path.IsPathRooted(relative)
               && !relative.Equals("..", StringComparison.Ordinal)
               && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
               && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string LoadWorkspaceHostSource()
        => File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "HephaestusWorkbench.App",
            "Views",
            "WorkspaceHostWindow.xaml.cs"));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HephaestusWorkbench.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private sealed class WorkspaceManifestEnvironment : IDisposable
    {
        public WorkspaceManifestEnvironment()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
            ExtensionPath = Path.Combine(RootPath, "extension");
            EntryPath = Path.Combine(ExtensionPath, "web", "index.html");
            Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
            File.WriteAllText(EntryPath, "<!doctype html><title>workspace</title>");
        }

        public string RootPath { get; }
        public string ExtensionPath { get; }
        public string EntryPath { get; }

        public ExtensionManifest CreateManifest(
            string id = "rule-editor",
            string version = "2.0.0",
            ExtensionKind kind = ExtensionKind.Workspace,
            ExtensionRuntimeKind runtimeKind = ExtensionRuntimeKind.Web,
            string entry = "web/index.html",
            IReadOnlyList<string>? capabilities = null)
            => new()
            {
                SchemaVersion = 2,
                Id = id,
                Name = "规则编辑器",
                Version = version,
                Kind = kind,
                PublisherId = "thelinyue",
                HostApiVersion = "1.0",
                MinHostVersion = "2.0.0",
                Runtime = new ExtensionRuntime
                {
                    Kind = runtimeKind,
                    Protocol = runtimeKind == ExtensionRuntimeKind.Process
                        ? AnalysisProcessProtocol.Version
                        : null,
                    Entry = entry
                },
                Capabilities = capabilities ?? ["workspace.page"],
                Permissions = [],
                Dependencies = [],
                DirectoryPath = ExtensionPath
            };

        public void Dispose()
        {
            if (Directory.Exists(RootPath)) Directory.Delete(RootPath, recursive: true);
        }
    }

    private sealed class RecordingDisposable : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}
