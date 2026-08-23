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
