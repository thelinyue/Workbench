namespace HephaestusWorkbench.Services;

/// <summary>在线下载与离线安装共同遵守的扩展 ZIP 大小上限。</summary>
public static class ExtensionPackageLimits
{
    public const long MaximumPackageBytes = 200L * 1024 * 1024;
}
