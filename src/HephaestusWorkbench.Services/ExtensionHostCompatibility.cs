namespace HephaestusWorkbench.Services;

/// <summary>
/// 使用与扩展中心完全相同的 SemVer 实现判断扩展最低宿主版本。宿主版本由组合根传入，
/// 避免界面兼容状态与实际运行链路分别读取或硬编码不同版本。
/// </summary>
public sealed class ExtensionHostCompatibility
{
    private readonly SemanticVersion _hostVersion;

    public ExtensionHostCompatibility(string hostVersion)
    {
        if (!SemanticVersion.TryParse(hostVersion, out _hostVersion))
            throw new ArgumentException("宿主版本必须是有效的语义化版本。", nameof(hostVersion));
    }

    /// <summary>判断扩展声明的最低宿主版本是否不高于当前注入的宿主版本。</summary>
    public bool IsCompatible(string minHostVersion)
        => SemanticVersion.TryParse(minHostVersion, out var requiredVersion) &&
           requiredVersion.CompareTo(_hostVersion) <= 0;
}
