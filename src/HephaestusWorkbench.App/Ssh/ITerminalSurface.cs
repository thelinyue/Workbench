namespace HephaestusWorkbench.App.Ssh;

/// <summary>隔离 WebView2 细节的终端表面，便于在没有 WebView2 Runtime 的测试环境验证协议和背压。</summary>
internal interface ITerminalSurface : IAsyncDisposable
{
    event EventHandler<string>? MessageReceived;
    Task SendAsync(string json, CancellationToken cancellationToken);
}
