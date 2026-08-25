# Hephaestus Workbench Releases

本仓库仅用于发布赫菲斯托斯工程工作台的 Electron Windows 安装包、SHA-256 校验文件和公开使用文档。

## 安装

从最新 Release 下载名称中带版本号的 `HephaestusWorkbench_v<版本号>.exe`。安装器提供标准 Windows 安装向导，支持后续升级和卸载。卸载请使用 Windows 设置或控制面板中的“已安装的应用”。

Electron 安装包自带运行所需的 Chromium 和 Node.js 运行时，目标机器无需额外安装桌面运行组件。

下载后可使用 PowerShell 验证文件：

```powershell
Get-FileHash -Algorithm SHA256 .\HephaestusWorkbench_v0.1.0.exe
```

结果应与同一 Release 中的 `SHA256SUMS.txt` 一致。

遇到安装或运行错误时，请保留工作台界面中的中文错误信息，以及用户数据目录中的日志用于排查。
