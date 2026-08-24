using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 对待激活扩展执行一次正式加载验证。实现方应走与真实任务一致的加载路径，
/// 验证失败时抛出包含明确原因的异常，由 Registry 负责回滚 current.json。
/// </summary>
public interface IExtensionHealthChecker
{
    Task CheckAsync(ExtensionManifest manifest, CancellationToken cancellationToken = default);
}

/// <summary>
/// 正式版扩展的最小类型健康检查。Manifest 的结构和路径边界已由 PluginSDK 统一校验；
/// 此处只验证运行时真正需要的入口已经随版本目录落盘，避免把缺少入口的包切换为 healthy。
/// </summary>
public sealed class ExtensionHealthChecker : IExtensionHealthChecker
{
    public Task CheckAsync(ExtensionManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        cancellationToken.ThrowIfCancellationRequested();
        ExtensionContractValidator.ValidateManifest(manifest);

        if (manifest.Runtime.Kind is ExtensionRuntimeKind.Process or ExtensionRuntimeKind.Web)
        {
            var entryPath = manifest.EntryPath;
            if (string.IsNullOrWhiteSpace(entryPath) || !File.Exists(entryPath))
                throw new InvalidOperationException($"扩展 {manifest.Id} {manifest.Version} 的入口文件不存在：{entryPath ?? "未声明"}");
        }

        return Task.CompletedTask;
    }
}
