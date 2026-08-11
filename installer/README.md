# 赫工（Hephaestus Workbench）安装器

安装器采用小型 .NET 8 Windows Forms 联网引导程序，默认目标目录为：

```text
C:\Program Files\HephaestusWorkbench
```

安装包制作：

```powershell
.\installer\build-installer.ps1 -Configuration Release -Version 1.1.0 -PluginBinaryPath '.\插件\log_analyzer.exe'
```

脚本会先发布 self-contained WPF 主程序并生成独立 ZIP，再发布不内嵌主程序的轻量安装器并复制三个入口：

- `HephaestusWorkbench_Setup.exe`：首次安装或修复安装
- `HephaestusWorkbench_Update.exe --update`：升级现有安装
- `HephaestusWorkbench_Uninstall.exe --uninstall`：卸载安装目录

安装器本身需要 .NET 8 Desktop Runtime，并会联网下载约 75 MB 的主程序 ZIP、校验固定 SHA-256 后安装。安装完成后的主程序是 self-contained；安装器还会检查 Windows 10/11 x64 和 WebView2 Runtime。

首次安装会显示传统安装器风格的“选择安装位置”页面。目标文件夹是可直接编辑的完整路径，默认值为 C:\Program Files\HephaestusWorkbench；也可以输入其他盘符路径，例如 D:\Apps\HephaestusWorkbench。全新安装只使用 HephaestusWorkbench 的新程序、数据和 Bootstrap 路径，不读取或迁移旧版产品数据。

正式包如需离线安装 WebView2，将官方 x64 安装程序放到：

```text
installer\dependencies\MicrosoftEdgeWebView2RuntimeInstallerX64.exe
```

升级前会将当前 HephaestusWorkbench 用户数据目录中的数据库备份到 `Backups\upgrade-<timestamp>`，程序升级不会覆盖 `HephaestusWorkbenchData`。卸载默认保留用户数据，只有明确确认后才删除日志、案例和报告。
