# 赫菲斯托斯工程工作台安装包

正式发行使用 Inno Setup 6 生成标准 Windows 单文件离线安装包，默认安装目录为：

```text
C:\Program Files\HephaestusWorkbench
```

安装包制作命令：

```powershell
.\installer\build-installer.ps1 -Configuration Release -Version 1.2.1 -PluginBinaryPath '<CI 下载的 log_analyzer.exe>'
```

构建机必须安装 Inno Setup 6。也可通过 `-InnoCompilerPath` 显式传入 `ISCC.exe`。脚本会完成以下工作：

- 发布包含 .NET 8 运行时的 Windows x64 主程序。
- 将显式传入且已获授权的 `log_analyzer.exe` 写入安装包内的 PluginSeed。
- 规则编辑器不再随安装包提供，用户通过应用商店安装和更新。
- 只生成名称带版本号的 `Hephaestus工作台_v<版本号>.exe` 和对应的 `SHA256SUMS.txt`；官方插件 ZIP 由各自独立 Release 发布。
- 不把插件 EXE、PDB、源码、测试日志或构建缓存写入源码仓库。

安装包提供欢迎、许可协议、安装目录、开始菜单、桌面快捷方式、确认、进度和完成页面。同一个 Setup 可用于首次安装、覆盖升级和修复；卸载入口由 Windows 控制面板统一管理，不再单独发布 Update 或 Uninstall EXE。

应用数据默认位于用户文档目录，并通过 LocalAppData 中的引导文件记录，不在程序安装目录内。升级和卸载只处理程序文件，默认保留用户数据库、日志、案例和报告。
