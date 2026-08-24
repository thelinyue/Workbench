using HephaestusWorkbench.Core.Models;

namespace HephaestusWorkbench.Core.Services;

/// <summary>
/// 使用独立结构化命令连接执行 Linux/OpenSSH 只读 Discovery 与 Preflight。
/// 实现不得借用交互终端，也不得把凭据写入结果、日志或持久化模型。
/// </summary>
public interface IMaintenanceDiscoveryService
{
    Task<PreflightResult> DiscoverAsync(
        string targetType,
        SshConnectionRequest connection,
        SshCredentialSecret? credential,
        CancellationToken cancellationToken = default);
}
