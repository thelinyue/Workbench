namespace HephaestusWorkbench.Services;

/// <summary>
/// 在线下载与离线安装共同遵守的扩展 ZIP 大小上限。安装事务需要为不可变验签快照保留多份字节数组，
/// 因此将正式发布包上限冻结为 209,715,200 字节，所有在线、离线和交接路径必须保持一致。
/// </summary>
public static class ExtensionPackageLimits
{
    public const long MaximumPackageBytes = 209_715_200L;
}
