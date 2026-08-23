namespace HephaestusWorkbench.Services;

/// <summary>
/// 在线下载与离线安装共同遵守的扩展 ZIP 大小上限。安装事务需要为不可变验签快照保留多份字节数组，
/// 因此限制为 64 MiB，将多份快照的内存峰值限制在可控范围。
/// </summary>
public static class ExtensionPackageLimits
{
    public const long MaximumPackageBytes = 64L * 1024 * 1024;
}
