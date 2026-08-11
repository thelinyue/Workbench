# Hephaestus Workbench Releases

本仓库仅用于发布赫菲斯托斯工程工作台的 Windows 安装包、官方插件、SHA-256 校验文件和公开使用文档。

本仓库不包含产品源码，也不代表产品以开源许可证发布。请只从本仓库的 Releases 页面下载安装包和插件。

## 安装

从 `v1.1.0` Release 下载 `HephaestusWorkbench_Setup.exe`。升级现有安装可使用 `HephaestusWorkbench_Update.exe`，卸载入口为 `HephaestusWorkbench_Uninstall.exe`。

下载后可使用 PowerShell 验证文件：

```powershell
Get-FileHash -Algorithm SHA256 .\HephaestusWorkbench_Setup.exe
```

结果应与同一 Release 中的 `SHA256SUMS.txt` 一致。

## 官方插件目录

应用读取 `marketplace/catalog.json` 获取官方插件。目录中的安装包地址必须使用 HTTPS，客户端会强制校验包大小、SHA-256、压缩包路径和插件清单。

遇到下载、安装或校验错误时，请保留工作台 `Logs/workbench.log` 中的中文错误信息用于排查。
