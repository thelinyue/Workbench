namespace HephaestusWorkbench.App.ViewModels;

/// <summary>
/// SSH 终端页面的阶段性 Shell 模型。阶段 5 将在该固定页面内接入设备、连接和多标签终端，
/// 页面本身属于 Host，SSH 扩展不能替换或注册新的导航入口。
/// </summary>
public sealed class SshTerminalViewModel : ViewModelBase
{
    public string EmptyTitle => "尚未建立 SSH 连接";
    public string EmptyDescription => "连接 Linux/OpenSSH 主机后，可在独立标签中使用交互终端。";
}
