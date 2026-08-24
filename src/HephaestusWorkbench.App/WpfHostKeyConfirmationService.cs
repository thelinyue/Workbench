using System.Windows;
using HephaestusWorkbench.App.Ssh;
using HephaestusWorkbench.Core.Models;

namespace HephaestusWorkbench.App;

/// <summary>使用宿主模态对话框执行首次 Host Key TOFU；指纹变化由协调器直接拒绝，不进入本服务。</summary>
internal sealed class WpfHostKeyConfirmationService : IHostKeyConfirmationService
{
    public Task<bool> ConfirmAsync(SshHostKeyObservation observation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = $"这是首次连接该 SSH 主机，请核对并确认 Host Key。\n\n" +
                      $"主机：{observation.Host}\n" +
                      $"端口：{observation.Port}\n" +
                      $"算法：{observation.KeyAlgorithm}\n" +
                      $"SHA256 指纹：{observation.Fingerprint}\n\n" +
                      "仅在你确认指纹可信时选择“是”。";
        var result = System.Windows.MessageBox.Show(
            message,
            "确认 SSH Host Key",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        return Task.FromResult(result == MessageBoxResult.Yes);
    }
}
