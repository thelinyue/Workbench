# 闭源分发说明

Hephaestus Workbench 的源码仓库保持私有。安装包、官方插件、校验文件和必要文档发布到 `thelinyue/Hephaestus-Workbench-Releases`；在线插件目录、Schema、投稿模板和校验工具发布到 `thelinyue/Hephaestus-Workbench-Plugins`。两个公开仓库都不得包含产品源码、PDB、测试日志、内部路径或构建配置。

## 正式构建

正式构建由 GitHub Actions 从官方插件 Releases 仓库自动获取最新正式版本，并显式注入授权的插件二进制：

```powershell
.\installer\build-installer.ps1 -Configuration Release -Version 1.2.1 -PluginBinaryPath '<CI 下载的 log_analyzer.exe>'
```

脚本在 `installer/dist` 仅生成名称带版本号的标准离线安装包 `Hephaestus工作台_v<版本号>.exe` 及其校验文件。安装包内含 self-contained 主程序和日志分析插件；规则编辑器通过应用商店独立安装和更新。没有日志分析插件二进制时，普通源码构建仍然可执行，但正式打包会立即返回中文错误。

## 主程序发布前的应用更新门禁

主程序发布前必须先检查 `Hephaestus-Workbench-Plugin-Sources` 中日志分析和规则编辑器的源码、manifest、版本及发布清单变化。仅文档或测试变化不视为应用更新。

- 发现应用代码或发布清单存在本地更新、但尚未发布到应用商店时，必须暂停主程序发布并先确认是否发布应用更新。
- 用户确认后，先完成对应插件 Release 和 `Hephaestus-Workbench-Plugins/catalog.json` 更新，并确认目录、版本、下载地址、大小和 SHA-256 有效。
- 应用更新完成后，才允许创建主程序版本标签并触发主程序 Release。
- 没有未发布的应用更新时，可直接继续主程序构建和发布。

主程序安装包只内置日志分析；规则编辑器通过应用商店独立安装。升级已有安装时不得主动删除用户数据目录中的规则编辑器。

## 公开仓库内容

私有规则源码仓库只用于 CI 构建、测试和签名；客户端从公开 Release/CDN 下载签名规则包和 catalog.json，不携带访问 Token。

规则发布清单包含 ruleSetId、version、minimumPluginVersion、packageUrl、packageSize、sha256、signature、keyId 和 releaseNotesUrl。客户端先校验 HTTPS、大小、SHA-256、Ed25519 签名、规则版本和 Schema，成功后原子替换 Rules/Official/main.json 并重建 Rules/Active/active.json；失败时保留上一份有效规则。

签名私钥只由 CI Secret 持有，客户端只内置公钥。规则可被已安装客户端读取和提取；若必须隐藏规则，应改为服务端执行。

- Plugins 仓库根目录 `catalog.json`：v1.1.1 及以后客户端读取的版本化插件目录。
- Plugins 仓库的 Schema、模板和校验脚本：社区投稿与 CI 校验使用，不存放插件二进制。
- Releases 仓库 `README.md` 与 `DISTRIBUTION-LICENSE.md`：用户下载、校验和二进制许可说明。
- Releases 仓库中 `plugin-log-analyzer-v*` 的最高正式 Release：GitHub Actions 会自动选择最高版本并将其内置到工作台安装包。插件版本使用分段数字格式，不要混用 `1.6` 和 `1.60`。
- Releases 仓库 Release `plugin-log-rule-editor-v1.0.0`：官方规则编辑器 ZIP。
- Releases 仓库 Release `v1.2.1`：单个离线 Setup、`SHA256SUMS.txt` 和发行说明。

发布前必须复核公开提交和 Release 资产，禁止上传源码、PDB、`.tgz`、密钥、令牌、内部日志和未授权文件。发布后使用未登录请求验证目录与所有资产，并重新计算插件包 SHA-256 与目录值比较。
