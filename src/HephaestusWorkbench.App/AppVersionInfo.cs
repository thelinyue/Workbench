using System.Reflection;

namespace HephaestusWorkbench.App;

/// <summary>
/// 统一读取并格式化当前客户端版本，避免窗口标题、页眉和后续诊断入口各自维护版本常量。
/// </summary>
internal static class AppVersionInfo
{
    public static string DisplayVersion { get; } = ToDisplayVersion(
        typeof(AppVersionInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        typeof(AppVersionInfo).Assembly.GetName().Version);

    internal static string ToDisplayVersion(string? informationalVersion, Version? assemblyVersion)
    {
        var normalized = informationalVersion?.Split('+', 2)[0].Trim();
        if (string.IsNullOrWhiteSpace(normalized) && assemblyVersion is not null)
            normalized = $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{Math.Max(assemblyVersion.Build, 0)}";
        if (string.IsNullOrWhiteSpace(normalized)) normalized = "0.0.0";
        return normalized.StartsWith('v') ? normalized : $"v{normalized}";
    }
}
