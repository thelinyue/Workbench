# Hephaestus Workbench Releases

本仓库仅用于发布赫菲斯托斯工程工作台的 Windows 安装包、官方插件、SHA-256 校验文件和公开使用文档。

本仓库不包含产品源码，也不代表产品以开源许可证发布。请只从本仓库的 Releases 页面下载安装包和插件。

## 安装

从最新 Release 下载 `HephaestusWorkbench_Setup.exe`。它是包含 .NET 8 运行时和官方内置插件的单文件离线安装包，提供标准 Windows 安装向导；同一个 Setup 也用于后续升级和修复。卸载请使用 Windows 设置或控制面板中的“已安装的应用”。

下载后可使用 PowerShell 验证文件：

```powershell
Get-FileHash -Algorithm SHA256 .\HephaestusWorkbench_Setup.exe
```

结果应与同一 Release 中的 `SHA256SUMS.txt` 一致。

## 官方插件目录

从 v1.1.1 开始，应用读取独立公开仓库 `thelinyue/Hephaestus-Workbench-Plugins` 根目录的 `catalog.json` 获取在线插件。插件 ZIP 仍由各插件的 GitHub Release 托管；客户端会强制校验 HTTPS、包大小、SHA-256、压缩包路径和插件清单。v1.1.0 保留使用本仓库中的旧目录。

遇到下载、安装或校验错误时，请保留工作台 `Logs/workbench.log` 中的中文错误信息用于排查。
