# 闭源分发说明

Hephaestus Workbench 的源码仓库保持私有。面向用户公开的安装包、官方插件、校验文件和必要文档发布到独立仓库 `thelinyue/Hephaestus-Workbench-Releases`，公开仓库不包含源码、PDB、测试日志、内部路径或构建配置。

## 正式构建

正式构建必须显式提供已获授权的插件二进制：

```powershell
.\installer\build-installer.ps1 -Configuration Release -Version 1.1.0 -PluginBinaryPath '.\插件\log_analyzer.exe'
```

脚本在 `installer/dist` 生成三个轻量联网安装入口、self-contained 主程序 ZIP、`log-analyzer-1.50-win-x64.zip` 和 `SHA256SUMS.txt`。安装器在构建时固化主程序 ZIP 的大小与 SHA-256，首次安装和升级需要联网下载该 ZIP。没有插件二进制时，普通源码构建仍然可执行，但正式打包会立即返回中文错误。

## 公开仓库内容

- `marketplace/catalog.json`：客户端读取的版本化官方目录。
- `README.md`：用户下载与校验说明。
- `DISTRIBUTION-LICENSE.md`：仅约束公开文档和二进制发行物，不授予源码许可。
- GitHub Release `plugin-log-analyzer-v1.50`：官方插件 ZIP。
- GitHub Release `v1.1.0`：安装、升级、卸载入口及 SHA-256 清单。

发布前必须复核公开提交和 Release 资产，禁止上传源码、PDB、`.tgz`、密钥、令牌、内部日志和未授权文件。发布后使用未登录请求验证目录与所有资产，并重新计算插件包 SHA-256 与目录值比较。
