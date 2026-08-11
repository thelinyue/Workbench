# 闭源分发说明

Hephaestus Workbench 的源码仓库保持私有。安装包、官方插件、校验文件和必要文档发布到 `thelinyue/Hephaestus-Workbench-Releases`；在线插件目录、Schema、投稿模板和校验工具发布到 `thelinyue/Hephaestus-Workbench-Plugins`。两个公开仓库都不得包含产品源码、PDB、测试日志、内部路径或构建配置。

## 正式构建

正式构建必须显式提供已获授权的插件二进制：

```powershell
.\installer\build-installer.ps1 -Configuration Release -Version 1.1.2 -PluginBinaryPath '.\插件\log_analyzer.exe'
```

脚本在 `installer/dist` 生成名称带版本号的单个标准离线安装包 `HephaestusWorkbench_Setup_v<版本号>.exe`、`log-analyzer-1.50-win-x64.zip`、市场目录和校验文件。Setup 内含 self-contained 主程序，不依赖联网下载应用载荷。没有插件二进制时，普通源码构建仍然可执行，但正式打包会立即返回中文错误。

## 公开仓库内容

- Plugins 仓库根目录 `catalog.json`：v1.1.1 及以后客户端读取的版本化插件目录。
- Plugins 仓库的 Schema、模板和校验脚本：社区投稿与 CI 校验使用，不存放插件二进制。
- Releases 仓库 `README.md` 与 `DISTRIBUTION-LICENSE.md`：用户下载、校验和二进制许可说明。
- Releases 仓库 Release `plugin-log-analyzer-v1.50`：官方插件 ZIP。
- Releases 仓库 Release `v1.1.2`：单个离线 Setup、`SHA256SUMS.txt` 和发行说明。

发布前必须复核公开提交和 Release 资产，禁止上传源码、PDB、`.tgz`、密钥、令牌、内部日志和未授权文件。发布后使用未登录请求验证目录与所有资产，并重新计算插件包 SHA-256 与目录值比较。
