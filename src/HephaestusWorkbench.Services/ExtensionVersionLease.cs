using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 固定一次任务所使用的扩展版本。Registry 切换 current.json 后，已取得租约的任务仍持有原 manifest；
/// 只有所有租约释放后，该版本才可能进入清理判断。
/// </summary>
public sealed class ExtensionVersionLease : IDisposable
{
    private Action? _release;

    internal ExtensionVersionLease(ExtensionManifest manifest, Action release)
    {
        Manifest = manifest;
        _release = release;
    }

    public ExtensionManifest Manifest { get; }

    public string Id => Manifest.Id;

    public string Version => Manifest.Version;

    public void Dispose()
    {
        Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
